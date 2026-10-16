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

        public const string SelfPatches = "ValPerfMonitor probes (this monitor)";
        public const string NoPatches = "no patches left (rebuilt by this monitor, or patches removed)";

        public static MemoryCensus Running, Last;
        private static MemoryCensus _lastOk; // the census the next one is compared with

        // Kept after the walk so the next census this session can show what grew.
        public Dictionary<string, (long count, long bytes)> OwnerTotals, RootTotals;
        public DateTime FinishedAt;
        public bool HasPrevious;
        public double PrevMinutesAgo, PrevGcMs;
        public long PrevObjects, PrevUsedAfterGc;
        public List<(string name, long count, long bytes)> OwnerGrowth = new List<(string, long, long)>(), RootGrowth = new List<(string, long, long)>();

        // Harmony's copies of patched methods (MonoMod's ILHook map), split over the mods patching each method.
        public int PatchedMethods;
        public long PatchObjects, PatchBytes;
        public List<(string mod, long count, long bytes, int methods)> HarmonyByMod = new List<(string, long, long, int)>();

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
        private Bucket _curRoot, _curOwner, _curPatch;
        private List<(string[] owners, Bucket b)> _patchEntries = new List<(string[], Bucket)>();
        private Dictionary<Type, Bucket> _curRootTypes;
        private string _curRootName;

        public MemoryCensus(int budgetMs, ModRegistry registry)
        {
            BudgetMs = Math.Max(5, Math.Min(500, budgetMs));
            _registry = registry;
            _steps = Run();
            // Never count the census itself, wherever a static field leads to it.
            _seen.Add(this); _seen.Add(_seen); _seen.Add(_stack); _seen.Add(_plans); _seen.Add(_owners);
            _seen.Add(_roots); _seen.Add(_types); _seen.Add(_ownerNames); _seen.Add(_steps); _seen.Add(_patchEntries);
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
                var roots = new List<(int order, string key, Type type, FieldInfo field)>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }
                    int order = OrderOf(asm);
                    string asmName = asm.GetName().Name;
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
                            bool special;
                            try { special = f.IsDefined(typeof(ThreadStaticAttribute), false) || f.IsDefined(typeof(ContextStaticAttribute), false); }
                            catch { continue; } // attributes that cannot be loaded: leave the field out
                            if (special) { ThreadStaticsSkipped++; continue; }
                            // Harmony's patch records first, so they are always counted under them (and split by mod).
                            bool hookMap = f.Name == "_Map" && t.FullName == "MonoMod.RuntimeDetour.ILHook";
                            roots.Add((hookMap ? -1 : order, asmName + "|" + t.FullName + "|" + f.Name, t, f));
                        }
                    }
                    if (OutOfTime) { yield return null; }
                }
                // A fixed order: each object counts under the first root that reaches it, so the same root must win every time.
                roots.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.key, b.key));
                StaticRoots = roots.Count;
                var notSetUp = new HashSet<Type>();
                for (int i = 0; i < roots.Count; i++)
                {
                    var (_, _, t, f) = roots[i];
                    object v;
                    var r = MonoStatics.Read(t, f, out v);
                    if (r == MonoStatics.Result.NotSetUp) { notSetUp.Add(t); continue; }
                    if (v == null) continue;
                    SetRoot(OwnerOf(t.Assembly), "static " + TypeName(t) + "." + f.Name);
                    if (v is IDictionary map && f.Name == "_Map" && t.FullName == "MonoMod.RuntimeDetour.ILHook")
                    {
                        // One entry per patched method: walk each on its own so its objects can go to the mods patching it.
                        var entries = new List<(MethodBase method, object ctx)>();
                        lock (map) { foreach (DictionaryEntry e in map) entries.Add((e.Key as MethodBase, e.Value)); }
                        PatchedMethods = entries.Count;
                        for (int k = 0; k < entries.Count; k++)
                        {
                            _curPatch = new Bucket();
                            _patchEntries.Add((PatchOwners(entries[k].method), _curPatch));
                            Push(entries[k].ctx);
                            while (_stack.Count > 0 || _arrayLeft != null)
                            {
                                Step();
                                if (OutOfTime) { Status = $"Harmony patches {k + 1:N0}/{entries.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                            }
                            if (OutOfTime) { Status = $"Harmony patches {k + 1:N0}/{entries.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                        }
                        _curPatch = null;
                        entries = null;
                    }
                    Push(v);
                    while (_stack.Count > 0 || _arrayLeft != null)
                    {
                        Step();
                        if (OutOfTime) { Status = $"static fields {i + 1:N0}/{roots.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                    }
                    if (OutOfTime) { Status = $"static fields {i + 1:N0}/{roots.Count:N0}, {_seen.Count:N0} objects"; yield return null; }
                }
                TypesNotSetUp = notSetUp.Count;
                roots = null; notSetUp = null; // iterator locals live on: drop them before the timed collections
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
            // Within one group, by type name, so the same component type reaches shared objects first every time.
            var typeNames = new Dictionary<Type, string>();
            string NameOf(UnityEngine.Object x) { var ty = x.GetType(); if (!typeNames.TryGetValue(ty, out var nm)) typeNames[ty] = nm = ty.FullName ?? ""; return nm; }
            foreach (var b in buckets) if (b != null) objs.AddRange(b.OrderBy(NameOf, StringComparer.Ordinal));
            typeNames = null;
            buckets = null; orderOf = null;
            ComponentObjects = objs.Count;
            yield return null;
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                if (o == null) continue; // destroyed meanwhile
                var t = o.GetType();
                SetRoot(OwnerOf(t.Assembly), "component " + TypeName(t));
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
            FinishedAt = DateTime.Now;
            CompareWith(_lastOk);
            _lastOk = this;
            Status = "done";
        }

        // ---- Growth since the previous census (leak finder) ------------------------------------------

        private void CompareWith(MemoryCensus prev)
        {
            if (prev == null || prev.RootTotals == null) return;
            HasPrevious = true;
            PrevMinutesAgo = (FinishedAt - prev.FinishedAt).TotalMinutes;
            PrevObjects = prev.Objects;
            PrevGcMs = prev.GcMedianMs;
            PrevUsedAfterGc = prev.MonoUsedAfterGc;
            OwnerGrowth = Growth(OwnerTotals, prev.OwnerTotals, 15);
            // Objects can move from one root to another of the same owner between censuses (first root to reach
            // them wins); that is not growth. So a root shows at most what its owner grew in total.
            var ownerGrew = OwnerGrowth.Count == 0 ? new Dictionary<string, long>() : Growth(OwnerTotals, prev.OwnerTotals, int.MaxValue).ToDictionary(x => x.Item1, x => x.Item2);
            RootGrowth = Growth(RootTotals, prev.RootTotals, int.MaxValue)
                .Select(x =>
                {
                    long c = Math.Min(x.Item2, ownerGrew.TryGetValue(SplitKey(x.Item1).owner, out var g) ? g : 0);
                    return (x.Item1, c, x.Item2 > 0 ? x.Item3 * c / x.Item2 : 0); // MB scaled down with the count
                })
                .Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(30).ToList();
            prev.OwnerTotals = prev.RootTotals = null; // only the latest census is compared with
        }

        private static List<(string, long, long)> Growth(Dictionary<string, (long count, long bytes)> now, Dictionary<string, (long count, long bytes)> before, int n)
        {
            var l = new List<(string name, long count, long bytes)>();
            foreach (var kv in now)
            {
                before.TryGetValue(kv.Key, out var b);
                long dc = kv.Value.count - b.count;
                if (dc > 0) l.Add((kv.Key, dc, kv.Value.bytes - b.bytes));
            }
            return l.OrderByDescending(x => x.count).Take(n).Select(x => (x.name, x.count, x.bytes)).ToList();
        }

        private void Release()
        {
            _seen = null; _stack = null; _plans = null; _owners = null; _roots = null; _types = null; _ownerNames = null;
            _curRoot = _curOwner = _curPatch = null; _curRootTypes = null; _arrayLeft = null; _arrayPlan = null; _patchEntries = null;
        }

        /// <summary>The mods with a patch on this method (each named once); the monitor's own probes on their own line.</summary>
        private string[] PatchOwners(MethodBase m)
        {
            var names = new List<string>();
            HarmonyLib.Patches info = null;
            try { if (m != null) info = HarmonyLib.Harmony.GetPatchInfo(m); } catch { }
            if (info != null)
            {
                foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers, info.ILManipulators })
                {
                    if (list == null) continue;
                    foreach (var p in list)
                    {
                        string n;
                        try
                        {
                            var asm = p.PatchMethod?.DeclaringType?.Assembly;
                            n = asm == _selfAsm ? SelfPatches : _registry?.Resolve(asm, p.owner)?.Name ?? p.owner ?? "(unknown)";
                        }
                        catch { n = p.owner ?? "(unknown)"; }
                        if (!names.Contains(n)) names.Add(n);
                    }
                }
            }
            if (names.Count == 0) names.Add(m == null ? "(unknown method)" : NoPatches);
            return names.ToArray();
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
                    string where = from is FieldInfo f ? $"{f.DeclaringType?.FullName}.{f.Name}" : from is Type t ? "element of " + TypeName(t) : "?";
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
            if (_curPatch != null) { _curPatch.Bytes += bytes; _curPatch.Count++; }
            if (!_types.TryGetValue(t, out var b)) _types[t] = b = new Bucket();
            b.Bytes += bytes; b.Count++;
            if (!_curRootTypes.TryGetValue(t, out var rb)) _curRootTypes[t] = rb = new Bucket();
            rb.Bytes += bytes; rb.Count++;
            if (bytes >= 4L * 1024 * 1024) Big.Add(($"{TypeName(t)} in {_curRootName}", bytes));
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
            OwnerTotals = new Dictionary<string, (long, long)>();
            RootTotals = new Dictionary<string, (long, long)>();
            foreach (var kv in _owners)
            {
                var row = new Row { Owner = kv.Key, Name = kv.Key, Bytes = kv.Value.Bytes, Count = kv.Value.Count };
                if (kv.Key == SelfName) { Self = row; continue; }
                ByOwner.Add(row);
                OwnerTotals[kv.Key] = (kv.Value.Count, kv.Value.Bytes);
            }
            ByOwner.Sort((a, b) => b.Count.CompareTo(a.Count));
            foreach (var kv in _roots)
            {
                if (kv.Value.owner == SelfName || kv.Value.b.Count == 0) continue;
                RootTotals[kv.Key] = (kv.Value.b.Count, kv.Value.b.Bytes);
                ByRoot.Add(new Row
                {
                    Owner = kv.Value.owner, Name = kv.Key.Substring(kv.Value.owner.Length + 3), Bytes = kv.Value.b.Bytes, Count = kv.Value.b.Count,
                    Types = kv.Value.types.OrderByDescending(x => x.Value.Count).Take(5).Select(x => (TypeName(x.Key), x.Value.Bytes, x.Value.Count)).ToList(),
                });
            }
            ByRoot.Sort((a, b) => b.Count.CompareTo(a.Count));
            if (ByRoot.Count > 500) ByRoot.RemoveRange(500, ByRoot.Count - 500);
            ByType = _types.OrderByDescending(x => x.Value.Count).Take(200).Select(x => (TypeName(x.Key), x.Value.Bytes, x.Value.Count)).ToList();
            // Several big objects of one type under one root make one line ("5 x ..."), with their total size.
            Big = Big.GroupBy(x => x.what).Select(g => (g.Count() > 1 ? $"{g.Count()} x {g.Key}" : g.Key, g.Sum(x => x.bytes)))
                .OrderByDescending(x => x.Item2).Take(50).ToList();
            CountedBytes = ByOwner.Sum(r => r.Bytes);

            // A method patched by several mods: its copies are shared out evenly between them.
            var byMod = new Dictionary<string, (double count, double bytes, int methods)>();
            foreach (var (owners, b) in _patchEntries)
            {
                PatchObjects += b.Count; PatchBytes += b.Bytes;
                foreach (var o in owners)
                {
                    byMod.TryGetValue(o, out var s);
                    byMod[o] = (s.count + (double)b.Count / owners.Length, s.bytes + (double)b.Bytes / owners.Length, s.methods + 1);
                }
            }
            HarmonyByMod = byMod.Select(kv => (kv.Key, (long)Math.Round(kv.Value.count), (long)Math.Round(kv.Value.bytes), kv.Value.methods))
                .OrderByDescending(x => x.Item2).ToList();
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

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", Inv);
        internal static double MbNum(long bytes) => Math.Round(bytes / 1048576.0, 1);
        internal static string N(long n) => n.ToString("N0", Inv);
        internal static string Signed(long n) => (n >= 0 ? "+" : "") + n.ToString("N0", Inv);
        internal static string SignedMb(long bytes) => (bytes >= 0 ? "+" : "") + Mb(bytes);
        internal static string Millions(long n) => n >= 1000000 ? (n / 1e6).ToString("0.0", Inv) + " million" : N(n);

        /// <summary>A type as code writes it ("System.Collections.Generic.Dictionary&lt;Int32, ZDO&gt;.Entry[]"), not .NET's
        /// assembly-qualified form; generic arguments without their namespace.</summary>
        internal static string TypeName(Type t) => TypeName(t, true);

        private static string TypeName(Type t, bool ns)
        {
            if (t == null) return "?";
            try
            {
                if (t.IsArray) return TypeName(t.GetElementType(), ns) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
                if (t.IsGenericParameter) return t.Name;
                string name = t.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                var args = t.IsGenericType ? t.GetGenericArguments() : Type.EmptyTypes;
                string prefix;
                if (t.IsNested && t.DeclaringType != null)
                {
                    // A nested type of a generic type carries its parent's arguments first.
                    var parent = t.DeclaringType;
                    int pn = parent.IsGenericTypeDefinition ? parent.GetGenericArguments().Length : 0;
                    if (pn > 0 && args.Length >= pn && !args.Take(pn).Any(a => a.IsGenericParameter))
                    {
                        try { parent = parent.MakeGenericType(args.Take(pn).ToArray()); } catch { }
                    }
                    args = args.Skip(Math.Min(pn, args.Length)).ToArray();
                    prefix = TypeName(parent, ns) + ".";
                }
                else prefix = ns && !string.IsNullOrEmpty(t.Namespace) ? t.Namespace + "." : "";
                if (args.Length > 0) name += "<" + string.Join(", ", args.Select(a => TypeName(a, false)).ToArray()) + ">";
                return prefix + name;
            }
            catch { return t.FullName ?? t.Name; }
        }

        internal static string Ago(double minutes) => minutes < 1 ? "less than a minute ago" : minutes.ToString("0", Inv) + " min ago";

        /// <summary>Owner name and root name from a RootTotals key ("owner | root").</summary>
        internal static (string owner, string root) SplitKey(string key)
        {
            int i = key.IndexOf(" | ", StringComparison.Ordinal);
            return i < 0 ? ("", key) : (key.Substring(0, i), key.Substring(i + 3));
        }

        /// <summary>A few plain-English sentences: what fills memory, what a full collection costs, what grew.</summary>
        public List<string> Plain()
        {
            var l = new List<string>();
            if (Error != null) { l.Add("The memory census did not finish: " + Error); return l; }
            string garbage = MonoUsedBefore > MonoUsedAfterGc ? "; the difference was garbage waiting to be collected" : "";
            l.Add($"Live managed memory after a full clean-up: {Mb(MonoUsedAfterGc)} MB (before: {Mb(MonoUsedBefore)} MB{garbage}). The census reached {Millions(Objects)} objects, about {Mb(CountedBytes)} MB.");
            if (GcMs.Count > 0)
            {
                string perMillion = Objects >= 100000 ? $", here about {(GcMedianMs / (Objects / 1e6)).ToString("0", Inv)} ms per million" : "";
                l.Add($"A full garbage collection takes about {GcMedianMs.ToString("0", Inv)} ms right now (runs: {string.Join(", ", GcMs.Select(x => x.ToString("0", Inv)).ToArray())} ms). That time grows with the number of live objects{perMillion}, and hardly with megabytes.");
            }
            var top = ByOwner.Take(4).Select(r => $"{r.Name} ({Millions(r.Count)} objects, {Mb(r.Bytes)} MB)").ToArray();
            if (top.Length > 0) l.Add("Most live objects are kept by: " + string.Join("; ", top) + ".");
            var mods = ByOwner.Where(r => IsMod(r.Name)).Take(3).Select(r => $"{r.Name} ({Millions(r.Count)} objects, {Mb(r.Bytes)} MB)").ToArray();
            if (mods.Length > 0) l.Add("Mods keeping the most: " + string.Join("; ", mods) + ".");
            var roots = ByRoot.Take(3).Select(r => $"{r.Name} [{r.Owner}] ({Millions(r.Count)} objects, {Mb(r.Bytes)} MB)").ToArray();
            if (roots.Length > 0) l.Add("Biggest single holders: " + string.Join("; ", roots) + ".");
            if (PatchedMethods > 0)
            {
                var hp = HarmonyByMod.Take(4).Select(x => $"{x.mod} ({Millions(x.count)} objects, {x.methods} methods)").ToArray();
                l.Add($"Harmony's copies of {N(PatchedMethods)} patched methods keep {Millions(PatchObjects)} objects ({Mb(PatchBytes)} MB). Biggest shares: {string.Join("; ", hp)}.");
                var self = HarmonyByMod.FirstOrDefault(x => x.mod == SelfPatches);
                if (self.mod != null && self.count > 0)
                    l.Add($"Of those, {Millions(self.count)} objects come from this monitor's own probes; they go away when the monitor is removed.");
            }
            if (HasPrevious)
            {
                l.Add($"Compared with the census {Ago(PrevMinutesAgo)}: live objects {Signed(Objects - PrevObjects)}, live memory {SignedMb(MonoUsedAfterGc - PrevUsedAfterGc)} MB, full garbage collection {PrevGcMs.ToString("0", Inv)} -> {GcMedianMs.ToString("0", Inv)} ms.");
                var grew = OwnerGrowth.Take(3).Select(x => $"{x.name} {Signed(x.count)} objects").ToArray();
                if (grew.Length > 0) l.Add("Grew most: " + string.Join("; ", grew) + ". Something that grows in every census while you stay in one place is likely a leak; one-off changes are usually objects loading as you move.");
                else l.Add("Nothing grew since then.");
            }
            if (Self != null) l.Add($"This monitor itself keeps {Millions(Self.Count)} objects ({Mb(Self.Bytes)} MB), left out above.");
            if (!StaticsRead) l.Add("Static fields were NOT read (" + StaticsNote + "); only components were counted.");
            return l;
        }

        private bool IsMod(string name) => _registry?.Mods.Any(m => m.IsPlugin && m.Name == name) == true;

        public string Text()
        {
            var sb = new StringBuilder();
            void Line(string s = "") => sb.AppendLine(s);
            string Row(long count, long bytes, string name) => $"{N(count),12} {Mb(bytes),9}  {name}";
            string GrowRow(long count, long bytes, string name) => $"{Signed(count),12} {SignedMb(bytes),9}  {name}";
            const string Header = "     Objects        MB  Name";

            Line("ValPerfMonitor memory census, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            Line();
            Line("== Summary ==");
            foreach (var s in Plain()) Line(s);
            Line();
            Line("== Details ==");
            Line($"Mono used memory (MB):  before {Mb(MonoUsedBefore)}, after full GC {Mb(MonoUsedAfterGc)}");
            Line($"Mono heap size (MB):    before {Mb(MonoHeapBefore)}, after {Mb(MonoHeapAfter)}");
            Line($"Full GC times (ms):     {string.Join(", ", GcMs.Select(x => x.ToString("0.0", Inv)).ToArray())}");
            Line($"Objects reached:        {N(Objects)} ({Mb(CountedBytes)} MB, approximate sizes)");
            Line($"Static fields read:     {(StaticsRead ? "yes" : "no: " + StaticsNote)}");
            Line($"Static reference fields {N(StaticRoots)}; types not set up yet (read as empty, nothing run) {N(TypesNotSetUp)}; skipped struct statics {N(StructStaticsSkipped)}, thread statics {N(ThreadStaticsSkipped)}");
            Line($"Components and ScriptableObjects: {N(ComponentObjects)}");
            Line($"Harmony patched methods: {N(PatchedMethods)}");
            Line($"Census work:            {(SliceMsTotal / 1000).ToString("0.0", Inv)} s over {Frames} frames (up to {BudgetMs} ms each), longest frame {MaxSliceMs.ToString("0", Inv)} ms ({MaxSliceAt}); {GcsDuringWalk} garbage collections during the walk");
            Line($"Read errors:            {Errors}; unreadable values skipped {Unreadable}{(UnreadableSamples.Count > 0 ? " (" + string.Join(", ", UnreadableSamples.ToArray()) + ")" : "")}");
            foreach (var e in ErrorSamples) Line("  " + e);
            Line();
            Line("Sizes are approximate. Each object counts once, under the first root that reaches it (game and engine first,");
            Line("then libraries, then mods). Lists are sorted by object count: that, not MB, is what makes a full garbage collection slow.");

            if (HasPrevious)
            {
                Line();
                Line($"== Growth since the census {Ago(PrevMinutesAgo)}, by owner (only what grew) ==");
                Line(Header);
                foreach (var g in OwnerGrowth) Line(GrowRow(g.count, g.bytes, g.name));
                Line();
                Line("== Growth by root (only what grew; never more than its owner grew, since objects can move between roots of one owner) ==");
                Line(Header);
                foreach (var g in RootGrowth) { var (o, r) = SplitKey(g.name); Line(GrowRow(g.count, g.bytes, $"[{o}] {r}")); }
            }

            Line();
            Line("== By owner (objects first reached from its static fields and components) ==");
            Line(Header);
            foreach (var r in ByOwner) Line(Row(r.Count, r.Bytes, r.Name));
            if (Self != null) Line(Row(Self.Count, Self.Bytes, Self.Name + " (left out of the totals)"));

            if (PatchedMethods > 0)
            {
                Line();
                Line($"== Harmony's copies of patched methods, by patching mod ({N(PatchedMethods)} methods; a method patched by several mods is shared out evenly) ==");
                Line("     Objects        MB  Methods  Mod");
                foreach (var x in HarmonyByMod) Line($"{N(x.count),12} {Mb(x.bytes),9}  {x.methods,7}  {x.mod}");
            }

            Line();
            Line("== By root (one static field, or all components of one type), with its 5 most common object types ==");
            Line(Header);
            foreach (var r in ByRoot)
            {
                Line(Row(r.Count, r.Bytes, $"[{r.Owner}] {r.Name}"));
                foreach (var t in r.Types) Line($"    {N(t.count),12} {Mb(t.bytes),9}  {t.type}");
            }
            Line();
            Line("== By object type ==");
            Line(Header);
            foreach (var t in ByType) Line(Row(t.count, t.bytes, t.type));
            Line();
            Line("== Single objects of 4 MB or more (several of one type under one root on one line, with their total) ==");
            Line("       MB  Object and where it was reached");
            foreach (var b in Big) Line($"{Mb(b.bytes),9}  {b.what}");
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
