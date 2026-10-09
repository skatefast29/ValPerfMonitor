using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Splits the time of a slow object spawn into its parts, so a slow object isn't blamed on whichever mod
    /// happens to have a script on it. Once an object takes longer than the threshold to spawn, the startup
    /// methods (Awake, OnEnable) of every script on it are timed from then on, and so is every mod patch on
    /// those methods. Each part is charged only its own time; what is left is Unity copying the object
    /// (meshes, colliders, child objects) plus game code that isn't split out.
    ///
    /// Only scripts on objects that already exist are hooked: their classes are loaded and their startup code
    /// has already run, so hooking them is safe. Hooking writes the same crash breadcrumb as the main probes.
    /// The hooks stay for the session and cost one check per call outside spawns.
    /// </summary>
    internal static class SpawnParts
    {
        public static bool Enabled = true;
        public static float ThresholdMs = 2f;

        private static Harmony _harmony;
        private static SafetyGuard _guard;
        private static ModRegistry _registry;
        private static ManualLogSource _log;
        private static int _mainThread;
        private static Assembly _game, _self;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private static readonly Dictionary<MethodBase, int> Ids = new Dictionary<MethodBase, int>();
        internal static readonly List<string> PartNames = new List<string>();
        private static readonly HashSet<Type> SeenTypes = new HashSet<Type>();
        private static readonly HashSet<MethodBase> Queued = new HashSet<MethodBase>();
        private static readonly Queue<(MethodBase method, bool patchMethod, string name)> Pending = new Queue<(MethodBase, bool, string)>();
        private static readonly List<ObjectLoads.Stat> Waiting = new List<ObjectLoads.Stat>();
        private static int _hooked, _failed;

        // The spawn in progress: own time per part, in ticks.
        internal static int SpawnDepth;
        private static long[] _spawnTicks = new long[256];
        private static int[] _spawnCalls = new int[256];
        private static int[] _touched = new int[256];
        private static int _touchedCount;

        private const int MaxDepth = 64;
        private static readonly long[] StackStart = new long[MaxDepth];
        private static readonly long[] StackChild = new long[MaxDepth];
        private static int _depth;

        private static readonly string[] StartupMethods = { "Awake", "OnEnable" };

        public static void Init(string harmonyId, ModRegistry registry, SafetyGuard guard, ManualLogSource log)
        {
            _harmony = new Harmony(harmonyId);
            _registry = registry;
            _guard = guard;
            _log = log;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _game = typeof(ZNet).Assembly;
            _self = typeof(SpawnParts).Assembly;
        }

        private static void Prefix(MethodBase __originalMethod, out int __state)
        {
            __state = -1;
            if (SpawnDepth == 0 || _depth >= MaxDepth || Thread.CurrentThread.ManagedThreadId != _mainThread)
                return;
            if (!Ids.TryGetValue(__originalMethod, out int id))
                return;
            __state = id;
            int d = _depth++;
            StackChild[d] = 0;
            StackStart[d] = Stopwatch.GetTimestamp();
        }

        // A finalizer so the stack stays balanced when the method throws (the exception is rethrown).
        private static void Finalizer(int __state)
        {
            if (__state < 0)
                return;
            long now = Stopwatch.GetTimestamp();
            int d = --_depth;
            long incl = now - StackStart[d];
            if (d > 0) StackChild[d - 1] += incl;
            if (_spawnCalls[__state]++ == 0)
                _touched[_touchedCount++] = __state;
            _spawnTicks[__state] += incl - StackChild[d];
        }

        /// <summary>The outermost spawn ended (ObjectLoads): file its parts, or queue the object for splitting.</summary>
        internal static void EndSpawn(ObjectLoads.Stat s, double ms, GameObject result)
        {
            if (s != null)
            {
                if (s.SplitReady)
                {
                    s.SplitSpawns++;
                    s.SplitMs += ms;
                    if (s.Parts == null) s.Parts = new Dictionary<int, double>();
                    for (int i = 0; i < _touchedCount; i++)
                    {
                        int id = _touched[i];
                        s.Parts.TryGetValue(id, out double v);
                        s.Parts[id] = v + _spawnTicks[id] * MsPerTick;
                    }
                }
                else if (Enabled && !s.SplitQueued && ms >= ThresholdMs && result != null)
                    QueueObject(s, result);
            }
            for (int i = 0; i < _touchedCount; i++)
            {
                int id = _touched[i];
                _spawnTicks[id] = 0;
                _spawnCalls[id] = 0;
            }
            _touchedCount = 0;
            _depth = 0;
        }

        private static void QueueObject(ObjectLoads.Stat s, GameObject go)
        {
            s.SplitQueued = true;
            Waiting.Add(s);
            MonoBehaviour[] scripts;
            try { scripts = go.GetComponentsInChildren<MonoBehaviour>(true); }
            catch { return; }
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var c in scripts)
            {
                if (c == null) continue;
                for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (!SeenTypes.Add(t) || t.ContainsGenericParameters || t.Assembly == _self || t.Assembly.GetName().Name.StartsWith("UnityEngine") || t.Assembly.GetName().Name.StartsWith("Unity."))
                        continue;
                    foreach (var name in StartupMethods)
                    {
                        MethodInfo m;
                        try { m = t.GetMethod(name, declared, null, Type.EmptyTypes, null); }
                        catch { continue; }
                        if (m == null || m.GetMethodBody() == null || !Queued.Add(m)) continue;
                        string where = $"{t.Name}.{name}";
                        // Mod patches on it first: hooking the startup method afterwards rebuilds its wrapper, so
                        // small patch methods are called (and timed) instead of being inlined into it.
                        Patches info = null;
                        try { info = Harmony.GetPatchInfo(m); } catch { }
                        if (info != null)
                            foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                            {
                                var pm = p.PatchMethod;
                                if (pm == null || pm.DeclaringType?.Assembly == _self || !Queued.Add(pm)) continue;
                                var mod = _registry.Resolve(pm.DeclaringType?.Assembly, p.owner);
                                Pending.Enqueue((pm, true, $"{mod?.Name ?? p.owner}: patch {Instrumenter.Describe(pm)} on {where}"));
                            }
                        Pending.Enqueue((m, false, $"{OwnerName(t)}: {where}"));
                    }
                }
            }
        }

        private static string OwnerName(Type t)
        {
            var asm = t.Assembly;
            if (asm == _game || asm.GetName().Name.StartsWith("assembly_")) return "Valheim";
            var mod = _registry.ForPluginAssembly(asm) ?? _registry.Resolve(asm, null);
            return mod?.Name ?? asm.GetName().Name;
        }

        /// <summary>Attach queued hooks until the time budget runs out. Returns true if anything was done.</summary>
        public static bool Drain(double budgetMs)
        {
            if (Pending.Count == 0 && Waiting.Count == 0)
                return false;
            long deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            while (Pending.Count > 0)
            {
                var (m, patchMethod, name) = Pending.Dequeue();
                Hook(m, patchMethod, name);
                if (Stopwatch.GetTimestamp() > deadline)
                    return true;
            }
            foreach (var s in Waiting) s.SplitReady = true;
            Waiting.Clear();
            _guard?.AllDone();
            _log.LogDebug($"Spawn splitting: {_hooked} startup methods timed ({_failed} could not be).");
            return true;
        }

        private static void Hook(MethodBase m, bool patchMethod, string name)
        {
            if (_guard != null && (_guard.IsSkipped(m) || (patchMethod && !_guard.Validate(m))))
                return;
            if (!Ids.ContainsKey(m))
            {
                int id = PartNames.Count;
                PartNames.Add(name);
                if (id >= _spawnTicks.Length)
                {
                    Array.Resize(ref _spawnTicks, id * 2);
                    Array.Resize(ref _spawnCalls, id * 2);
                    Array.Resize(ref _touched, id * 2);
                }
                Ids[m] = id;
            }
            try
            {
                _guard?.Before('S', m);
                _harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(SpawnParts), nameof(Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SpawnParts), nameof(Finalizer)) { priority = Priority.Last });
                _hooked++;
            }
            catch (Exception e)
            {
                _failed++;
                _log.LogDebug($"Spawn splitting: could not time {name}: {e.Message}");
            }
        }
    }
}
