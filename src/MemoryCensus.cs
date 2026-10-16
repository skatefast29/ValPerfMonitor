using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValPerfMonitor
{
    /// <summary>
    /// "What keeps the managed heap full?" Walks everything reachable from static fields and from every
    /// MonoBehaviour/ScriptableObject (scene objects and prefabs), adds up approximate sizes and object counts per
    /// mod, per root (one static field, or all components of one type) and per object type, then times a full
    /// blocking garbage collection. A full collection's time grows with the number of live objects, not megabytes.
    ///
    /// Static fields are read straight from Mono's static memory, never through FieldInfo.GetValue: GetValue runs
    /// a type's static constructor if it has not run yet, and running one early broke a mod (logout then threw
    /// TypeInitializationException). Reading the memory runs no code at all; a type not set up yet reads as empty.
    /// A self-test checks that on every run and the statics part stands down if it fails.
    ///
    /// Runs in slices of a few tens of ms per frame. Those frames are marked heavy, so captures leave them out.
    /// Objects first reached from an earlier root count there: game and engine roots come first, then mods, then
    /// this monitor (shown on its own line, left out of the rankings). The census's own bookkeeping is never counted
    /// and is thrown away before the timed collections, after one untimed collection that clears it.
    /// </summary>
    internal sealed class MemoryCensus
    {
        public const string SelfName = "ValPerfMonitor (this monitor)";

        public sealed class Bucket { public long Bytes, Count; }

        public sealed class Row
        {
            public string Owner, Name;
            public long Bytes, Count;
            public List<(string type, long bytes, long count)> Types = new List<(string, long, long)>();
        }

        public static MemoryCensus Running, Last;

        public readonly int BudgetMs;
        public string Error, Status = "starting", File;
        public bool Done;
        public bool StaticsRead;          // false when the self-test failed: components only
        public string StaticsNote;
        public long MonoUsedBefore, MonoHeapBefore, MonoUsedAfterGc, MonoHeapAfter;
        public long Objects, CountedBytes;
        public int Frames, Errors, Unreadable, GcsDuringWalk, StaticRoots, ComponentObjects, TypesNotSetUp, StructStaticsSkipped, ThreadStaticsSkipped;
        public double WalkSeconds, SliceMsTotal, MaxSliceMs;
        public string MaxSliceAt;
        public readonly List<string> UnreadableSamples = new List<string>();
        public readonly List<double> GcMs = new List<double>();
        public readonly List<string> ErrorSamples = new List<string>();
        public List<Row> ByOwner = new List<Row>(), ByRoot = new List<Row>();
        public List<(string type, long bytes, long count)> ByType = new List<(string, long, long)>();
        public List<(string what, long bytes)> Big = new List<(string, long)>();
        public Row Self;

        private readonly ModRegistry _registry;
        private readonly Assembly _selfAsm = typeof(MemoryCensus).Assembly;
        private readonly Stopwatch _slice = new Stopwatch();
        private readonly Stopwatch _walk = new Stopwatch();
        private IEnumerator _steps;

        // Walk state; dropped (set to null) before the timed collections.
        private HashSet<object> _seen = new HashSet<object>(new RefEq());
        private Stack<object> _stack = new Stack<object>();
        private Dictionary<Type, Plan> _plans = new Dictionary<Type, Plan>();
        private Dictionary<string, Bucket> _owners = new Dictionary<string, Bucket>();
        private Dictionary<string, (string owner, Bucket b, Dictionary<Type, Bucket> types)> _roots = new Dictionary<string, (string, Bucket, Dictionary<Type, Bucket>)>();
        private Dictionary<Type, Bucket> _types = new Dictionary<Type, Bucket>();
        private Dictionary<Assembly, string> _ownerNames = new Dictionary<Assembly, string>();
        private Bucket _curRoot, _curOwner;
        private Dictionary<Type, Bucket> _curRootTypes;
        private string _curRootName;

        public MemoryCensus(int budgetMs, ModRegistry registry)
        {
            BudgetMs = Math.Max(5, Math.Min(500, budgetMs));
            _registry = registry;
            _steps = Run();
            // Never count the census itself, wherever a static field leads to it.
            _seen.Add(this); _seen.Add(_seen); _seen.Add(_stack); _seen.Add(_plans); _seen.Add(_owners);
            _seen.Add(_roots); _seen.Add(_types); _seen.Add(_ownerNames); _seen.Add(_steps);
        }

        /// <summary>Every frame from the plugin while running: one slice of work.</summary>
        public void Tick()
        {
            if (Done) return;
            Frames++;
            _slice.Restart();
            string before = Status;
            try
            {
                if (!_steps.MoveNext()) Done = true;
            }
            catch (Exception e)
            {
                Error = "census failed: " + e.Message;
                Release();
                Done = true;
            }
            double ms = _slice.Elapsed.TotalMilliseconds;
            SliceMsTotal += ms;
            if (ms > MaxSliceMs) { MaxSliceMs = ms; MaxSliceAt = before + " -> " + Status; }
        }

        public void Abort()
        {
            if (Done) return;
            Error = "stopped early";
            Release();
            Done = true;
        }

        private bool OutOfTime => _slice.ElapsedMilliseconds >= BudgetMs;

        private IEnumerator Run()
        {
            MonoUsedBefore = Profiler.GetMonoUsedSizeLong();
            MonoHeapBefore = Profiler.GetMonoHeapSizeLong();
            _walk.Start();
            int gc0 = GC.CollectionCount(0);

            // ---- Static fields: game, engine and libraries first, then mods, then this monitor ----
            StaticsNote = MonoStatics.SelfTest();
            StaticsRead = StaticsNote == null;
            if (StaticsRead)
            {
                Status = "listing static fields";
                var roots = new List<(int order, Type type, FieldInfo field)>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }
                    int order = OrderOf(asm);
                    foreach (var t in types)
                    {
                        if (t.ContainsGenericParameters) continue; // open generics have no statics of their own
                        FieldInfo[] fs;
                        try { fs = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                        catch { continue; }
                        foreach (var f in fs)
                        {
                            if (f.IsLiteral) continue;
                            Type ft;
                            try { ft = f.FieldType; } catch { continue; }
                            if (ft.IsPointer || ft.IsPrimitive || ft.IsEnum || ft == typeof(IntPtr) || ft == typeof(UIntPtr)) continue;
                            if (ft.IsValueType) { StructStaticsSkipped++; continue; }
                            if (f.IsDefined(typeof(ThreadStaticAttribute), false) || f.IsDefined(typeof(ContextStaticAttribute), false)) { ThreadStaticsSkipped++; continue; }
                            roots.Add((order, t, f));
                        }
                    }
                    if (OutOfTime) { yield return null; }
                }
                roots.Sort((a, b) => a.order.CompareTo(b.order));
                StaticRoots = roots.Count;
                var notSetUp = new HashSet<Type>();
                for (int i = 0; i < roots.Count; i++)
                {
                    var (_, t, f) = roots[i];
                    object v;
                    var r = MonoStatics.Read(t, f, out v);
                    if (r == MonoStatics.Result.NotSetUp) { notSetUp.Add(t); continue; }
                    if (v == null) continue;
                    SetRoot(OwnerOf(t.Assembly), "static " + t.FullName + "." + f.Name);
                    Push(v);
                    while (_stack.Count > 0 || _arrayLeft != null)
                    {
                        Step();
                        if (OutOfTime) { Status = $"static fields {i + 1:N0}/{roots.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                    }
                    if (OutOfTime) { Status = $"static fields {i + 1:N0}/{roots.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                }
                TypesNotSetUp = notSetUp.Count;
            }

            // ---- Every MonoBehaviour and ScriptableObject (scene objects and prefabs) ----
            Status = "listing components";
            yield return null;
            var found = new List<UnityEngine.Object>();
            found.AddRange(Resources.FindObjectsOfTypeAll<MonoBehaviour>());
            found.AddRange(Resources.FindObjectsOfTypeAll<ScriptableObject>());
            yield return null;
            // Same order as the statics (game and engine first); each assembly's order is worked out once.
            var orderOf = new Dictionary<Assembly, int>();
            var buckets = new List<UnityEngine.Object>[10];
            foreach (var x in found)
            {
                var asm = x.GetType().Assembly;
                if (!orderOf.TryGetValue(asm, out int ord)) orderOf[asm] = ord = OrderOf(asm);
                (buckets[ord] ?? (buckets[ord] = new List<UnityEngine.Object>())).Add(x);
            }
            found = null;
            var objs = new List<UnityEngine.Object>();
            foreach (var b in buckets) if (b != null) objs.AddRange(b);
            buckets = null;
            ComponentObjects = objs.Count;
            yield return null;
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                if (o == null) continue; // destroyed meanwhile
                var t = o.GetType();
                SetRoot(OwnerOf(t.Assembly), "component " + t.FullName);
                var plan = GetPlan(t);
                if (_seen.Add(o)) Count(o, plan.Shallow);
                WalkFields(o, plan); // components are walked here, once, even when reached earlier
                while (_stack.Count > 0 || _arrayLeft != null)
                {
                    Step();
                    if (OutOfTime) { Status = $"components {i + 1:N0}/{objs.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                }
                if (OutOfTime) { Status = $"components {i + 1:N0}/{objs.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
            }
            objs = null;
            _walk.Stop();
            GcsDuringWalk = GC.CollectionCount(0) - gc0;
            WalkSeconds = _walk.Elapsed.TotalSeconds;
            Objects = _seen.Count;

            Status = "adding up";
            yield return null;
            Summarize();
            Release();

            // ---- Full collections: one untimed (clears the census's own garbage), then timed ones, one per frame ----
            Status = "clearing the census's own memory";
            yield return null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 0; i < 3; i++)
            {
                Status = $"timing full garbage collection {i + 1}/3";
                yield return null;
                var sw = Stopwatch.StartNew();
                GC.Collect();
                sw.Stop();
                GcMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            MonoUsedAfterGc = Profiler.GetMonoUsedSizeLong();
            MonoHeapAfter = Profiler.GetMonoHeapSizeLong();
            Status = "done";
        }

        private void Release()
        {
            _seen = null; _stack = null; _plans = null; _owners = null; _roots = null; _types = null; _ownerNames = null;
            _curRoot = _curOwner = null; _curRootTypes = null; _arrayLeft = null; _arrayPlan = null;
        }

        // ---- Who owns what -----------------------------------------------------------------------

        private int OrderOf(Assembly asm)
        {
            if (asm == _selfAsm) return 9;
            var mod = _registry?.ForPluginAssembly(asm);
            if (mod != null && mod.IsPlugin) return 5;
            string n = asm.GetName().Name;
            if (n == "mscorlib" || n == "System" || n.StartsWith("System.") || n == "netstandard") return 0;
            if (n.StartsWith("UnityEngine") || n.StartsWith("Unity.")) return 1;
            if (n.StartsWith("assembly_")) return 2;
            return 3; // BepInEx, Harmony and other libraries
        }

        private string OwnerOf(Assembly asm)
        {
            if (_ownerNames.TryGetValue(asm, out var s)) return s;
            string n = asm.GetName().Name;
            var mod = _registry?.ForPluginAssembly(asm);
            if (asm == _selfAsm) s = SelfName;
            else if (mod != null && mod.IsPlugin) s = mod.Name;
            else if (n.StartsWith("assembly_")) s = "Valheim (game)";
            else if (n.StartsWith("UnityEngine") || n.StartsWith("Unity.")) s = "Unity engine";
            else if (n == "0Harmony" || n.StartsWith("MonoMod") || n.StartsWith("Mono.Cecil")) s = "Harmony / MonoMod (mod patching)";
            else if (n.StartsWith("BepInEx")) s = "BepInEx";
            else if (n == "mscorlib" || n == "System" || n.StartsWith("System.") || n.StartsWith("Mono.") || n.StartsWith("Microsoft.") || n == "netstandard") s = ".NET runtime";
            else s = n + " (library)";
            _ownerNames[asm] = s;
            return s;
        }

        private void SetRoot(string owner, string root)
        {
            _curRootName = owner + " | " + root;
            if (!_roots.TryGetValue(_curRootName, out var r))
                _roots[_curRootName] = r = (owner, new Bucket(), new Dictionary<Type, Bucket>());
            _curRoot = r.b;
            _curRootTypes = r.types;
            if (!_owners.TryGetValue(owner, out _curOwner)) _owners[owner] = _curOwner = new Bucket();
        }

        // ---- The walk ------------------------------------------------------------------------------

        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        private sealed class Plan
        {
            public long Shallow;
            public FieldInfo[] RefFields = new FieldInfo[0];
            public FieldInfo[] StructFields = new FieldInfo[0];
            public Plan[] StructPlans = new Plan[0];
            public bool HasRefs;
        }

        private void Push(object o, object from = null)
        {
            if (o == null) return;
            bool real;
            try { var t0 = o.GetType(); real = t0 != null && t0.Name != null && t0.GetHashCode() != int.MinValue; } catch { real = false; }
            if (!real)
            {
                // A field that holds something that is not a real object (Mono keeps native data in a few). Skip it.
                Unreadable++;
                if (UnreadableSamples.Count < 10)
                {
                    string where = from is FieldInfo f ? $"{f.DeclaringType?.FullName}.{f.Name}" : from is Type t ? "element of " + t.FullName : "?";
                    if (!UnreadableSamples.Contains(where)) UnreadableSamples.Add(where);
                }
                return;
            }
            if (o is UnityEngine.Object)
            {
                // Only the managed wrapper; components are walked as roots of their own.
                if (_seen.Add(o)) Count(o, 24);
                return;
            }
            if (_seen.Add(o)) _stack.Push(o);
        }

        private void Count(object o, long bytes)
        {
            var t = o.GetType();
            _curRoot.Bytes += bytes; _curRoot.Count++;
            _curOwner.Bytes += bytes; _curOwner.Count++;
            if (!_types.TryGetValue(t, out var b)) _types[t] = b = new Bucket();
            b.Bytes += bytes; b.Count++;
            if (!_curRootTypes.TryGetValue(t, out var rb)) _curRootTypes[t] = rb = new Bucket();
            rb.Bytes += bytes; rb.Count++;
            if (bytes >= 4L * 1024 * 1024) Big.Add(($"{t.FullName} in {_curRootName}", bytes));
        }

        // Only reads fields and array slots; never calls a method on the object (delegates, Regex and the like
        // are walked through their fields like anything else).
        private void Visit(object o)
        {
            try
            {
                var t = o.GetType();
                if (o is string s) { Count(o, Align(22 + 2L * s.Length)); return; }
                if (o is Array arr)
                {
                    var et = t.GetElementType();
                    Count(o, Align(32 + ElemSize(et) * arr.LongLength));
                    if (et == null || et.IsPointer || et.IsPrimitive || et.IsEnum || et == typeof(IntPtr) || et == typeof(UIntPtr)) return;
                    Plan ep = null;
                    if (et.IsValueType)
                    {
                        ep = GetPlan(Nullable.GetUnderlyingType(et) ?? et);
                        if (!ep.HasRefs) return;
                    }
                    // Its elements are taken a few thousand at a time (Step), so a huge array spreads over frames.
                    _arrayLeft = arr.GetEnumerator();
                    _arrayType = t;
                    _arrayPlan = ep;
                    return;
                }
                var plan = GetPlan(t);
                Count(o, plan.Shallow);
                WalkFields(o, plan);
            }
            catch (Exception e) { Fail(o, e); }
        }

        private IEnumerator _arrayLeft;
        private Type _arrayType;
        private Plan _arrayPlan;

        /// <summary>One unit of walk work: the next 4096 elements of the array being walked, else the next object.</summary>
        private void Step()
        {
            if (_arrayLeft == null) { Visit(_stack.Pop()); return; }
            try
            {
                for (int i = 0; i < 4096; i++)
                {
                    if (!_arrayLeft.MoveNext()) { _arrayLeft = null; _arrayPlan = null; return; }
                    var e = _arrayLeft.Current;
                    if (_arrayPlan == null) Push(e, _arrayType);
                    else if (e != null) WalkFields(e, _arrayPlan);
                }
            }
            catch (Exception ex) { Fail(_arrayType, ex); _arrayLeft = null; _arrayPlan = null; }
        }

        private void WalkFields(object holder, Plan p)
        {
            if (!p.HasRefs) return;
            var rf = p.RefFields;
            for (int i = 0; i < rf.Length; i++)
            {
                object v;
                try { v = rf[i].GetValue(holder); } catch (Exception e) { Fail(holder, e); continue; }
                Push(v, rf[i]);
            }
            var sf = p.StructFields;
            for (int i = 0; i < sf.Length; i++)
            {
                object v;
                try { v = sf[i].GetValue(holder); } catch (Exception e) { Fail(holder, e); continue; }
                if (v != null) WalkFields(v, p.StructPlans[i]);
            }
        }

        private void Fail(object o, Exception e)
        {
            Errors++;
            if (ErrorSamples.Count < 10)
            {
                string type = "?";
                try { var t = o?.GetType(); type = t == null ? "null" : (t.Namespace ?? "") + "." + t.Name + " (" + t.Assembly.GetName().Name + ")"; } catch (Exception te) { type = "type unknown: " + te.GetType().Name; }
                string at = (e.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
                ErrorSamples.Add($"{_curRootName}: {type}: {e.GetType().Name} {e.Message} at {at}");
            }
        }

        private static long Align(long n) => (n + 7) & ~7L;

        private long ElemSize(Type t)
        {
            if (t == null || !t.IsValueType) return 8;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t.IsPrimitive) return PrimSize(t);
            return GetPlan(t).Shallow;
        }

        private static long PrimSize(Type t)
        {
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(int) || t == typeof(uint) || t == typeof(float)) return 4;
            return 8;
        }

        private Plan GetPlan(Type t)
        {
            if (_plans.TryGetValue(t, out var p)) return p;
            p = new Plan();
            _plans[t] = p; // also stops recursion through self-referencing structs
            bool skip;
            try { skip = IsRuntimeInternal(t); } catch { skip = true; }
            if (skip)
            {
                // Mono keeps native pointers in some object-typed fields of these: count them, never read them.
                p.Shallow = t.IsValueType ? 8 : 64;
                return p;
            }
            var refs = new List<FieldInfo>(); var structs = new List<FieldInfo>(); var plans = new List<Plan>();
            long size = 0;
            for (var c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
            {
                FieldInfo[] fs;
                try { fs = c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var f in fs)
                {
                    Type ft;
                    try { ft = f.FieldType; } catch { continue; }
                    if (ft == null) { size += 8; continue; }
                    if (ft.IsPointer || ft == typeof(IntPtr) || ft == typeof(UIntPtr)) { size += 8; continue; }
                    if (!ft.IsValueType) { size += 8; refs.Add(f); continue; }
                    var et = ft.IsEnum ? Enum.GetUnderlyingType(ft) : ft;
                    if (et.IsPrimitive) { size += PrimSize(et); continue; }
                    if (et == t) { size += 8; continue; }
                    var inner = GetPlan(et);
                    size += inner.Shallow;
                    // A nullable struct reads back boxed as its inner value, so walk it with the inner type's plan.
                    var walk = Nullable.GetUnderlyingType(et) is Type u ? GetPlan(u) : inner;
                    if (walk.HasRefs) { structs.Add(f); plans.Add(walk); }
                }
            }
            p.RefFields = refs.ToArray(); p.StructFields = structs.ToArray(); p.StructPlans = plans.ToArray();
            p.HasRefs = refs.Count > 0 || structs.Count > 0;
            p.Shallow = t.IsValueType ? Math.Max(1, Align(size)) : Align(16 + size);
            return p;
        }

        private static bool IsRuntimeInternal(Type t)
        {
            if (typeof(MemberInfo).IsAssignableFrom(t) || typeof(Assembly).IsAssignableFrom(t) || typeof(Module).IsAssignableFrom(t)) return true;
            if (typeof(System.Threading.Thread).IsAssignableFrom(t) || typeof(AppDomain).IsAssignableFrom(t)) return true;
            if (t.Assembly != typeof(object).Assembly) return false;
            var ns = t.Namespace ?? "";
            return ns.StartsWith("System.Reflection") || ns.StartsWith("System.Runtime") || ns.StartsWith("Mono")
                || ns.StartsWith("System.Security") || (ns == "System.Threading" && !t.Name.Contains("Lazy"))
                || t.Name.StartsWith("Runtime") || t.Name.StartsWith("Mono");
        }

        // ---- Results -------------------------------------------------------------------------------

        private void Summarize()
        {
            foreach (var kv in _owners)
            {
                var row = new Row { Owner = kv.Key, Name = kv.Key, Bytes = kv.Value.Bytes, Count = kv.Value.Count };
                if (kv.Key == SelfName) Self = row; else ByOwner.Add(row);
            }
            ByOwner.Sort((a, b) => b.Count.CompareTo(a.Count));
            foreach (var kv in _roots)
            {
                if (kv.Value.owner == SelfName || kv.Value.b.Count == 0) continue;
                ByRoot.Add(new Row
                {
                    Owner = kv.Value.owner, Name = kv.Key.Substring(kv.Value.owner.Length + 3), Bytes = kv.Value.b.Bytes, Count = kv.Value.b.Count,
                    Types = kv.Value.types.OrderByDescending(x => x.Value.Count).Take(5).Select(x => (x.Key.FullName, x.Value.Bytes, x.Value.Count)).ToList(),
                });
            }
            ByRoot.Sort((a, b) => b.Count.CompareTo(a.Count));
            if (ByRoot.Count > 500) ByRoot.RemoveRange(500, ByRoot.Count - 500);
            ByType = _types.OrderByDescending(x => x.Value.Count).Take(200).Select(x => (x.Key.FullName, x.Value.Bytes, x.Value.Count)).ToList();
            Big = Big.OrderByDescending(x => x.bytes).Take(50).ToList();
            CountedBytes = ByOwner.Sum(r => r.Bytes);
        }

        public double GcMedianMs
        {
            get
            {
                if (GcMs.Count == 0) return double.NaN;
                var s = GcMs.OrderBy(x => x).ToList();
                return s[s.Count / 2];
            }
        }

        internal static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        internal static string Millions(long n) => n >= 1000000 ? (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " million" : n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>A few plain-English sentences: what fills memory and what a full collection costs.</summary>
        public List<string> Plain()
        {
            var l = new List<string>();
            if (Error != null) { l.Add("The memory census did not finish: " + Error); return l; }
            l.Add($"Live managed memory after a full clean-up: {Mb(MonoUsedAfterGc)} MB (it was {Mb(MonoUsedBefore)} MB before, the rest was garbage waiting to be collected). The census reached {Millions(Objects)} objects, about {Mb(CountedBytes)} MB.");
            if (GcMs.Count > 0)
                l.Add($"A full garbage collection takes about {GcMedianMs:0} ms right now (runs: {string.Join(", ", GcMs.Select(x => x.ToString("0", CultureInfo.InvariantCulture)).ToArray())} ms). That time grows with the number of live objects, roughly 5 ms per million, and hardly with megabytes.");
            var top = ByOwner.Take(4).Select(r => $"{r.Name} ({Millions(r.Count)} objects, {Mb(r.Bytes)} MB)").ToArray();
            if (top.Length > 0) l.Add("Most live objects are kept by: " + string.Join("; ", top) + ".");
            var mods = ByOwner.Where(r => _registry?.Mods.Any(m => m.IsPlugin && m.Name == r.Name) == true).Take(3).Select(r => $"{r.Name} ({Millions(r.Count)} objects, {Mb(r.Bytes)} MB)").ToArray();
            if (mods.Length > 0) l.Add("Mods keeping the most: " + string.Join("; ", mods) + ".");
            var roots = ByRoot.Take(3).Select(r => $"{r.Name} [{r.Owner}] ({Millions(r.Count)} objects, {Mb(r.Bytes)} MB)").ToArray();
            if (roots.Length > 0) l.Add("Biggest single holders: " + string.Join("; ", roots) + ".");
            if (Self != null) l.Add($"This monitor itself keeps {Millions(Self.Count)} objects ({Mb(Self.Bytes)} MB), left out above. Harmony's copies of patched methods also grow while the monitor's probes are on.");
            if (!StaticsRead) l.Add("Static fields were NOT read (" + StaticsNote + "); only components were counted.");
            return l;
        }

        public string Text()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("ValPerfMonitor memory census, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            foreach (var s in Plain()) sb.AppendLine(s);
            sb.AppendLine();
            sb.AppendLine($"mono used before {Mb(MonoUsedBefore)} MB, after full GC {Mb(MonoUsedAfterGc)} MB; mono heap before {Mb(MonoHeapBefore)} MB, after {Mb(MonoHeapAfter)} MB");
            sb.AppendLine($"objects {Objects:N0}, counted {Mb(CountedBytes)} MB (approximate sizes), walk {WalkSeconds:0.0} s over {Frames} frames of up to {BudgetMs} ms");
            sb.AppendLine($"static fields read: {(StaticsRead ? "yes" : "no: " + StaticsNote)}; {StaticRoots:N0} static reference fields, {TypesNotSetUp:N0} types not set up yet (read as empty, nothing run); skipped {StructStaticsSkipped:N0} struct statics, {ThreadStaticsSkipped:N0} thread statics; {ComponentObjects:N0} components and ScriptableObjects");
            sb.AppendLine($"errors {Errors}; unreadable values skipped {Unreadable}{(UnreadableSamples.Count > 0 ? " (" + string.Join(", ", UnreadableSamples.ToArray()) + ")" : "")}");
            sb.AppendLine($"census work {SliceMsTotal / 1000:0.0} s in {Frames} frames, longest frame {MaxSliceMs:0} ms ({MaxSliceAt}); {GcsDuringWalk} garbage collections during the walk");
            foreach (var e in ErrorSamples) sb.AppendLine("  " + e);
            sb.AppendLine();
            sb.AppendLine("== By owner (objects first reached from its static fields and components) ==");
            foreach (var r in ByOwner) sb.AppendLine($"{r.Count,12:N0} objects {Mb(r.Bytes),9} MB  {r.Name}");
            if (Self != null) sb.AppendLine($"{Self.Count,12:N0} objects {Mb(Self.Bytes),9} MB  {Self.Name} (left out)");
            sb.AppendLine();
            sb.AppendLine("== By root (one static field, or all components of one type) ==");
            foreach (var r in ByRoot)
            {
                sb.AppendLine($"{r.Count,12:N0} objects {Mb(r.Bytes),9} MB  [{r.Owner}] {r.Name}");
                foreach (var t in r.Types) sb.AppendLine($"              {t.count,12:N0} {Mb(t.bytes),9} MB  {t.type}");
            }
            sb.AppendLine();
            sb.AppendLine("== By object type ==");
            foreach (var t in ByType) sb.AppendLine($"{t.count,12:N0} objects {Mb(t.bytes),9} MB  {t.type}");
            sb.AppendLine();
            sb.AppendLine("== Single objects of 4 MB or more ==");
            foreach (var b in Big) sb.AppendLine($"{Mb(b.bytes),9} MB  {b.what}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Reads a static reference field without running the type's static constructor: Mono's own exported functions
    /// give the type's static memory if the type has been set up (else nothing), and the value is read from there.
    /// </summary>
    internal static class MonoStatics
    {
        private const string Mono = "mono-2.0-bdwgc";
        [DllImport(Mono)] private static extern IntPtr mono_domain_get();
        [DllImport(Mono)] private static extern IntPtr mono_class_from_mono_type(IntPtr type);
        [DllImport(Mono)] private static extern IntPtr mono_unity_class_try_get_vtable(IntPtr domain, IntPtr klass);
        [DllImport(Mono)] private static extern IntPtr mono_vtable_get_static_field_data(IntPtr vtable);
        [DllImport(Mono)] private static extern uint mono_field_get_offset(IntPtr field);
        [DllImport(Mono)] private static extern IntPtr mono_gchandle_new_v2(IntPtr obj, int pinned);

        public enum Result { Ok, NotSetUp, Skipped }

        private static IntPtr _domain;

        public static Result Read(Type t, FieldInfo f, out object value)
        {
            value = null;
            IntPtr klass = mono_class_from_mono_type(t.TypeHandle.Value);
            if (klass == IntPtr.Zero) return Result.Skipped;
            IntPtr vt = mono_unity_class_try_get_vtable(_domain, klass); // never creates one
            if (vt == IntPtr.Zero) return Result.NotSetUp;
            IntPtr data = mono_vtable_get_static_field_data(vt);
            if (data == IntPtr.Zero) return Result.NotSetUp;
            uint offset = mono_field_get_offset(f.FieldHandle.Value);
            if (offset == uint.MaxValue || offset > 1 << 24) return Result.Skipped; // thread/context statics live elsewhere
            IntPtr obj = Marshal.ReadIntPtr(data, (int)offset);
            if (obj == IntPtr.Zero) return Result.Ok;
            // The raw pointer on this thread's stack keeps the object alive until the handle holds it.
            var h = GCHandle.FromIntPtr(mono_gchandle_new_v2(obj, 0));
            value = h.Target;
            h.Free();
            return Result.Ok;
        }

        // Checked on every census: a value set from code reads back as the same object, and reading a field of a
        // type whose static constructor has not run does not run it.
        private static class SetFromCode { public static object Marker; }
        private static class NeverTouched { public static readonly object Value = Ran(); }
        private static bool _neverTouchedRan;
        private static object Ran() { _neverTouchedRan = true; return new object(); }

        /// <summary>Null when reading statics this way is safe here; else why not.</summary>
        public static string SelfTest()
        {
            try
            {
                _domain = mono_domain_get();
                if (_domain == IntPtr.Zero) return "no Mono domain";
                var marker = new object();
                SetFromCode.Marker = marker;
                var r = Read(typeof(SetFromCode), typeof(SetFromCode).GetField(nameof(SetFromCode.Marker)), out var back);
                if (r != Result.Ok || !ReferenceEquals(back, marker)) return "self-test: a static field did not read back correctly";
                Read(typeof(NeverTouched), typeof(NeverTouched).GetField(nameof(NeverTouched.Value)), out _);
                if (_neverTouchedRan) return "self-test: reading a static field ran its type's static constructor";
                return null;
            }
            catch (Exception e) { return "self-test failed: " + e.GetType().Name + " " + e.Message; }
        }
    }
}
