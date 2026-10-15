using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// "Which objects in the world cost the most?" A short recording of its own: the per-frame methods the
    /// monitor already measures (game update loops such as Character.CustomFixedUpdate, and mods' own
    /// Update/LateUpdate/FixedUpdate) get a timer that also notes which object the call was for. Afterwards the
    /// time is added up per object (a creature, a fire, a mod's manager) and per prefab (all loaded torches
    /// together), with an instanceId that ValAgentBridge's debug tools and perf/ab_test can use.
    ///
    /// It runs during a Baseline capture, so the normal probes are off; its own timers come off again at the end.
    /// That capture is not saved as a run: the timers slow the game a little, so it is no clean Baseline.
    /// Times are self time (a timed method called from another timed method counts once) and include the mod
    /// patches on that method. The timers add a little to each call, so compare objects with each other rather
    /// than reading the numbers as exact.
    /// </summary>
    internal sealed class ObjectCosts
    {
        private enum Phase { Hooking, Warmup, Measuring, Unhooking, Done }

        private sealed class Acc
        {
            public Component Owner;
            public MethodBase Method;
            public long Ticks, Calls;
        }

        public sealed class ObjectRow
        {
            public string Name, Prefab, Path;
            public int InstanceId;
            public double Ms, CallsPerFrame, DistanceM;
            public Vector3 Position;
            public List<(string part, double ms)> Parts = new List<(string, double)>();
            public List<string> Mods = new List<string>();
        }

        public sealed class PrefabRow
        {
            public string Prefab;
            public int Objects;
            public double Ms, MaxMs;
            public List<string> Mods = new List<string>();
        }

        public static ObjectCosts Running, Last;
        public const string CaptureLabel = "object costs";

        public readonly float Seconds;
        public string Error;
        public readonly List<ObjectRow> Objects = new List<ObjectRow>();
        public readonly List<PrefabRow> Prefabs = new List<PrefabRow>();
        public int MethodsTimed, Frames;
        public double TotalMs;
        public bool Done => _phase == Phase.Done;

        private readonly Harmony _harmony;
        private readonly SafetyGuard _guard;
        private readonly ManualLogSource _log;
        private readonly ModRegistry _registry;
        private readonly Queue<MethodInfo> _toHook = new Queue<MethodInfo>();
        private readonly List<MethodInfo> _hooked = new List<MethodInfo>();
        private Phase _phase = Phase.Hooking;
        private float _phaseUntil;
        private long _measureStart;

        // Shared with the static timer methods (one recording at a time, main thread only).
        private static ObjectCosts _cur;
        private static bool _measuring;
        private static readonly Dictionary<MethodBase, int> MethodIndex = new Dictionary<MethodBase, int>();
        private static readonly List<MethodBase> Methods = new List<MethodBase>();
        private static readonly Dictionary<long, Acc> Accs = new Dictionary<long, Acc>();
        private static Acc[] _stack = new Acc[64];
        private static long[] _start = new long[64], _child = new long[64];
        private static int _depth;

        private static readonly HashSet<string> LoopNames = new HashSet<string>
            { "Update", "LateUpdate", "FixedUpdate", "CustomUpdate", "CustomFixedUpdate", "CustomLateUpdate", "UpdateAI" };

        public ObjectCosts(float seconds, Instrumenter instrumenter, ModRegistry registry, SafetyGuard guard, string harmonyId, ManualLogSource log)
        {
            Seconds = Mathf.Clamp(seconds, 5f, 120f);
            _registry = registry;
            _guard = guard;
            _log = log;
            _harmony = new Harmony(harmonyId);
            // Methods the monitor already hooked without trouble this session: per-frame, per-object (instance) methods.
            foreach (var s in instrumenter.AllSources)
            {
                if (s.FirstHookSeq < 0 || !(s.Method is MethodInfo m) || m.IsStatic || !LoopNames.Contains(m.Name)) continue;
                if (s.Kind != "Game" && !LoopNames.Contains(s.Kind)) continue;
                if (m.DeclaringType == null || !typeof(Component).IsAssignableFrom(m.DeclaringType) || _guard.IsSkipped(m)) continue;
                _toHook.Enqueue(m);
            }
            if (_toHook.Count == 0)
                Error = "no per-frame methods to time yet: the monitor attaches its probes a few seconds after entering a world; try again once a recording has run";
        }

        public string Status => _phase switch
        {
            Phase.Hooking => $"Object costs: attaching timers ({_toHook.Count} left)",
            Phase.Warmup => "Object costs: warming up",
            Phase.Measuring => $"Object costs: measuring, {Math.Max(0, _phaseUntil - Time.realtimeSinceStartup):0} s left",
            Phase.Unhooking => "Object costs: removing timers",
            _ => "Object costs: done",
        };

        /// <summary>Every frame from the plugin. Returns true when it did heavy work this frame (hooking/unhooking).</summary>
        public bool Tick(float budgetMs)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            switch (_phase)
            {
                case Phase.Hooking:
                    if (_cur == null) Begin();
                    while (_toHook.Count > 0)
                    {
                        var m = _toHook.Dequeue();
                        try
                        {
                            _guard.Before('O', m);
                            _harmony.Patch(m, prefix: Prefix, finalizer: Finalizer);
                            _hooked.Add(m);
                        }
                        catch (Exception e) { _log.LogDebug($"Object costs: could not time {Instrumenter.Describe(m)}: {e.Message}"); }
                        if (Stopwatch.GetTimestamp() > deadline) return true;
                    }
                    _guard.AllDone();
                    MethodsTimed = _hooked.Count;
                    _phase = Phase.Warmup;
                    _phaseUntil = Time.realtimeSinceStartup + 3f; // newly patched methods compile on first use
                    return true;
                case Phase.Warmup:
                    if (Time.realtimeSinceStartup < _phaseUntil) return false;
                    Accs.Clear();
                    _depth = 0;
                    Frames = 0;
                    _measureStart = Stopwatch.GetTimestamp();
                    _measuring = true;
                    _phase = Phase.Measuring;
                    _phaseUntil = Time.realtimeSinceStartup + Seconds;
                    return false;
                case Phase.Measuring:
                    Frames++;
                    if (Time.realtimeSinceStartup < _phaseUntil) return false;
                    _measuring = false;
                    Summarize();
                    _phase = Phase.Unhooking;
                    return true;
                case Phase.Unhooking:
                    while (_hooked.Count > 0)
                    {
                        var m = _hooked[_hooked.Count - 1];
                        _hooked.RemoveAt(_hooked.Count - 1);
                        try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); }
                        catch (Exception e) { _log.LogWarning($"Object costs: could not remove the timer on {Instrumenter.Describe(m)}: {e.Message}"); }
                        if (Stopwatch.GetTimestamp() > deadline) return true;
                    }
                    End();
                    return true;
            }
            return false;
        }

        /// <summary>Stopped early: remove every timer now, keep nothing.</summary>
        public void Abort()
        {
            _measuring = false;
            foreach (var m in _hooked)
                try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); } catch { }
            _hooked.Clear();
            _guard.AllDone();
            End();
            if (Error == null) Error = "stopped early";
        }

        private void Begin()
        {
            _cur = this;
            MethodIndex.Clear();
            Methods.Clear();
            Accs.Clear();
            _depth = 0;
        }

        // Timed calls still running (this can be called from inside one, e.g. a bridge command) find an empty
        // stack when they finish and do nothing.
        private void End()
        {
            _phase = Phase.Done;
            _measuring = false;
            if (_cur == this) _cur = null;
            _depth = 0;
            Accs.Clear();
            MethodIndex.Clear();
            Methods.Clear();
        }

        // ---- Timers (main thread only) ----------------------------------------------------------

        private static readonly HarmonyMethod Prefix = new HarmonyMethod(AccessTools.Method(typeof(ObjectCosts), nameof(Enter))) { priority = Priority.First };
        private static readonly HarmonyMethod Finalizer = new HarmonyMethod(AccessTools.Method(typeof(ObjectCosts), nameof(Leave))) { priority = Priority.Last };

        // Both run inside game and mod code: they must never throw.
        private static void Enter(object __instance, MethodBase __originalMethod)
        {
            if (!_measuring || Thread.CurrentThread.ManagedThreadId != Probe.MainThreadId) return;
            try
            {
                var owner = __instance as Component;
                if (ReferenceEquals(owner, null)) return;
                if (!MethodIndex.TryGetValue(__originalMethod, out int mi))
                {
                    mi = Methods.Count;
                    MethodIndex[__originalMethod] = mi;
                    Methods.Add(__originalMethod);
                }
                long key = ((long)owner.GetInstanceID() << 16) ^ mi;
                if (!Accs.TryGetValue(key, out var acc))
                    Accs[key] = acc = new Acc { Owner = owner, Method = __originalMethod };
                if (_depth == _stack.Length)
                {
                    Array.Resize(ref _stack, _depth * 2);
                    Array.Resize(ref _start, _depth * 2);
                    Array.Resize(ref _child, _depth * 2);
                }
                _stack[_depth] = acc;
                _child[_depth] = 0;
                _start[_depth++] = Stopwatch.GetTimestamp();
            }
            catch { }
        }

        private static void Leave(object __instance, MethodBase __originalMethod)
        {
            if (_depth == 0 || Thread.CurrentThread.ManagedThreadId != Probe.MainThreadId) return;
            try
            {
                var acc = _stack[_depth - 1];
                if (acc == null || !ReferenceEquals(acc.Owner, __instance) || acc.Method != __originalMethod) return;
                int d = --_depth;
                long elapsed = Stopwatch.GetTimestamp() - _start[d];
                acc.Ticks += elapsed - _child[d];
                acc.Calls++;
                if (d > 0) _child[d - 1] += elapsed;
            }
            catch { }
        }

        // ---- Results ----------------------------------------------------------------------------

        private void Summarize()
        {
            int frames = Math.Max(1, Frames);
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            var player = Player.m_localPlayer;
            Vector3? here = player != null ? player.transform.position : (Vector3?)null;
            var byObject = new Dictionary<GameObject, ObjectRow>();
            var parts = new Dictionary<GameObject, Dictionary<string, double>>();
            foreach (var acc in Accs.Values)
            {
                if (acc.Ticks <= 0 || acc.Owner == null) continue; // destroyed meanwhile
                // The object as a whole: the networked object (creature, piece, item) it belongs to, else its root.
                var view = acc.Owner.GetComponentInParent<ZNetView>();
                var go = view != null ? view.gameObject : acc.Owner.transform.root.gameObject;
                if (!byObject.TryGetValue(go, out var row))
                {
                    // The game's central update loop calls most objects' per-frame code; what it does itself (and for
                    // code not timed here) can't be split per object.
                    string prefab = go.GetComponent<MonoUpdaters>() != null ? "Game update loop (MonoUpdaters, not split per object)" : go.name.Replace("(Clone)", "").Trim();
                    byObject[go] = row = new ObjectRow
                    {
                        Name = go.name, Prefab = prefab, Path = PathOf(go.transform), InstanceId = go.GetInstanceID(), Position = go.transform.position,
                        // Only world objects have a meaningful distance; managers and UI sit wherever they were created.
                        DistanceM = here.HasValue && view != null ? Math.Round(Vector3.Distance(here.Value, go.transform.position), 0) : double.NaN,
                    };
                    parts[go] = new Dictionary<string, double>();
                }
                double ms = acc.Ticks * msPerTick / frames;
                row.Ms += ms;
                row.CallsPerFrame += acc.Calls / (double)frames;
                var method = acc.Method;
                string part = $"{method.DeclaringType?.Name}.{method.Name}";
                parts[go][part] = (parts[go].TryGetValue(part, out double p) ? p : 0) + ms;
                var mod = method.DeclaringType != null ? _registry.ForPluginAssembly(method.DeclaringType.Assembly) : null;
                if (mod != null && mod.IsPlugin && !row.Mods.Contains(mod.Name)) row.Mods.Add(mod.Name);
            }
            foreach (var kv in byObject)
                kv.Value.Parts = parts[kv.Key].OrderByDescending(x => x.Value).Take(5).Select(x => (x.Key, x.Value)).ToList();
            Objects.AddRange(byObject.Values.OrderByDescending(r => r.Ms));
            TotalMs = Objects.Sum(r => r.Ms);
            Prefabs.AddRange(Objects.GroupBy(r => r.Prefab).Select(g => new PrefabRow
            {
                Prefab = g.Key, Objects = g.Count(), Ms = g.Sum(r => r.Ms), MaxMs = g.Max(r => r.Ms),
                Mods = g.SelectMany(r => r.Mods).Distinct().ToList(),
            }).OrderByDescending(r => r.Ms));
        }

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != null; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        public string Csv()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("kind,name,objects,ms_per_frame,max_ms_one_object,calls_per_frame,distance_m,instance_id,path,parts,mods\n");
            foreach (var p in Prefabs)
                sb.Append("prefab,").Append(ValPerfMonitor.Csv.Q(p.Prefab)).Append(',').Append(p.Objects.ToString(inv)).Append(',')
                  .Append(ValPerfMonitor.Csv.F(p.Ms, "0.0000")).Append(',').Append(ValPerfMonitor.Csv.F(p.MaxMs, "0.0000")).Append(",,,,,,")
                  .Append(ValPerfMonitor.Csv.Q(string.Join("; ", p.Mods.ToArray()))).Append('\n');
            foreach (var o in Objects)
                sb.Append("object,").Append(ValPerfMonitor.Csv.Q(o.Name)).Append(",1,").Append(ValPerfMonitor.Csv.F(o.Ms, "0.0000")).Append(",,")
                  .Append(ValPerfMonitor.Csv.F(o.CallsPerFrame, "0.0")).Append(',').Append(ValPerfMonitor.Csv.F(o.DistanceM, "0")).Append(',')
                  .Append(o.InstanceId.ToString(inv)).Append(',').Append(ValPerfMonitor.Csv.Q(o.Path)).Append(',')
                  .Append(ValPerfMonitor.Csv.Q(string.Join("; ", o.Parts.Select(x => $"{x.part} {x.ms:0.0000}").ToArray()))).Append(',')
                  .Append(ValPerfMonitor.Csv.Q(string.Join("; ", o.Mods.ToArray()))).Append('\n');
            return sb.ToString();
        }
    }
}
