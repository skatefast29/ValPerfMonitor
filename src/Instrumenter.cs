using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Finds every method worth measuring and attaches the Probe to it. Patching is queued and
    /// drained a few milliseconds per frame; frames where that happens are excluded from the results.
    /// </summary>
    internal sealed class Instrumenter
    {
        private static readonly string[] UnityCallbacks = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

        // Valheim's per-frame entry points: Unity callbacks plus the MonoUpdaters batch callbacks.
        private static readonly string[] GameLoopNames =
            { "Update", "LateUpdate", "FixedUpdate", "OnGUI", "CustomUpdate", "CustomFixedUpdate", "CustomLateUpdate", "UpdateAI" };

        // Core vanilla work outside MonoBehaviour update loops (these classes are always in use in a world).
        private static readonly string[] GameExtras =
        {
            "WearNTear:UpdateSupport", "ZNetScene:CreateDestroyObjects", "ZDOMan:Update", "ZDOMan:SendZDOs",
            "ZRoutedRpc:HandleRoutedRPC", "ZRpc:HandlePackage", "ZSteamSocket:Update",
            "ZoneSystem:CreateLocalZones", "ZoneSystem:CreateGhostZones", "Heightmap:Regenerate",
            // The parts inside the hitches seen while moving (each measured as its own self-time).
            "ZoneSystem:SpawnZone", "ZoneSystem:PlaceLocations", "ZoneSystem:PlaceVegetation",
            "ZNetScene:CreateObjectsSorted", "ZNetScene:CreateObject", "ZNetScene:RemoveObjects",
            "ZDOMan:ReleaseNearbyZDOS", "ClutterSystem:GenerateVegPatch",
        };

        private readonly Harmony _harmony;
        private readonly ModRegistry _registry;
        private readonly ManualLogSource _log;
        private readonly Assembly _self;
        private readonly HashSet<string> _excludedGuids;
        private readonly SafetyGuard _guard;

        private readonly Queue<SourceInfo> _pending = new Queue<SourceInfo>();
        private readonly Dictionary<MethodBase, SourceInfo> _known = new Dictionary<MethodBase, SourceInfo>();
        private readonly List<SourceInfo> _all = new List<SourceInfo>();
        private readonly HashSet<(MethodBase, MethodBase)> _seenTranspilers = new HashSet<(MethodBase, MethodBase)>();
        private readonly HashSet<MethodBase> _seenTargets = new HashSet<MethodBase>();
        private readonly Queue<MethodBase> _rebuild = new Queue<MethodBase>();
        private readonly HashSet<MethodBase> _rebuildQueued = new HashSet<MethodBase>();
        // When each target was last rebuilt (sequence number). A rebuild made after a patch method was first
        // probed stays valid for good: the detour marks that patch method non-inlinable, and the mark is never
        // removed, even when the probe is taken off for Baseline. So a re-hook doesn't rebuild it again.
        private readonly Dictionary<MethodBase, long> _rebuiltAt = new Dictionary<MethodBase, long>();
        private long _seq;
        // Hooking statistics for the log line when a round of hooking finishes.
        private long _hookStart, _attachTicks, _rebuildTicks;
        private int _hookFrames, _attachCount, _rebuildCount, _rebuildsNotNeeded;
        private bool _includeUnity, _includeGame, _scannedGameExtras, _scannedCoroutines;

        /// <summary>Also measure mods' coroutines (set before the first Scan).</summary>
        public bool IncludeCoroutines;

        /// <summary>"Namespace.Type:Method" entries, comma-separated, to measure as well (set before the first Scan).</summary>
        public string ExtraMethods = "";
        private bool _scannedExtraMethods;
        private readonly HashSet<Type> _seenBehaviourTypes = new HashSet<Type>();
        private readonly List<string> _failedNames = new List<string>();

        /// <summary>
        /// Probes are attached only once the player is in a loaded world (see Plugin). Before that, scans
        /// just collect what to measure. Hooking makes Mono compile each method; doing that during startup,
        /// before the game has run the code itself, can abort the game.
        /// </summary>
        public bool Hooked { get; private set; }
        private bool _calibrationHooked;
        public int Instrumented => _all.Count(s => s.Attached);
        public int Failed { get; private set; }
        public int Unsafe { get; private set; }
        public int MenuOnly { get; private set; }
        public int Pending => _pending.Count + _rebuild.Count;
        public int PatchedTargets => _seenTargets.Count;
        public int GameMethods => _all.Count(s => s.Mod.IsGame);
        /// <summary>Everything found to measure, hooked or not (FirstHookSeq >= 0 once a probe went on without trouble).</summary>
        public IEnumerable<SourceInfo> AllSources => _all;
        public int LastPatchedMethodCount { get; private set; } = -1;

        // Calibration results, per capture mode.
        /// <summary>Total time one probed call costs (for correcting frame time), in Stopwatch ticks.</summary>
        public readonly double[] CallOverheadTicks = new double[4];
        /// <summary>Part of a probed call that lands inside its caller's measurement (ticks, or bytes in Allocations mode).</summary>
        public readonly double[] ChildOverhead = new double[4];

        public Instrumenter(Harmony harmony, ModRegistry registry, ManualLogSource log, Assembly self, IEnumerable<string> excludedGuids, SafetyGuard guard)
        {
            _guard = guard;
            _harmony = harmony;
            _registry = registry;
            _log = log;
            _self = self;
            _excludedGuids = new HashSet<string>(excludedGuids, StringComparer.OrdinalIgnoreCase);
        }

        // ---- Discovery --------------------------------------------------------------------------

        /// <summary>Queue every not-yet-measured patch method (and, first time only, Unity callbacks and game loops).</summary>
        public int Scan(bool includeUnityCallbacks, bool includeGame)
        {
            int before = _all.Count;
            List<MethodBase> targets;
            try { targets = Harmony.GetAllPatchedMethods().ToList(); }
            catch (Exception e) { _log.LogError($"Could not list Harmony patches: {e.Message}"); return 0; }

            foreach (var target in targets)
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(target); }
                catch { continue; }
                if (info == null)
                    continue;

                bool external = false;
                foreach (var p in info.Prefixes) external |= Consider(p, "Prefix", target);
                foreach (var p in info.Postfixes) external |= Consider(p, "Postfix", target);
                foreach (var p in info.Finalizers) external |= Consider(p, "Finalizer", target);
                foreach (var p in info.Transpilers)
                {
                    if (p.owner == _harmony.Id || p.PatchMethod == null)
                        continue;
                    external = true;
                    if (_seenTranspilers.Add((p.PatchMethod, target)))
                        _registry.Resolve(p.PatchMethod.DeclaringType?.Assembly, p.owner).Transpilers++;
                }
                if (external)
                    _seenTargets.Add(target);
            }
            LastPatchedMethodCount = targets.Count;

            _includeUnity = includeUnityCallbacks;
            _includeGame = includeGame;
            if (includeGame && !_scannedGameExtras)
            {
                _scannedGameExtras = true;
                ScanGameExtras();
            }
            if (includeUnityCallbacks && IncludeCoroutines && !_scannedCoroutines)
            {
                _scannedCoroutines = true;
                ScanCoroutines();
            }
            if (!_scannedExtraMethods)
            {
                _scannedExtraMethods = true;
                ScanExtraMethods();
            }
            ScanLiveBehaviours();
            return _all.Count - before;
        }

        /// <summary>Cheap check used for periodic rescans: did anyone add patches since the last scan?</summary>
        public bool PatchSetChanged()
        {
            try { return Harmony.GetAllPatchedMethods().Count() != LastPatchedMethodCount; }
            catch { return false; }
        }

        private bool Consider(Patch p, string kind, MethodBase target)
        {
            if (p.owner == _harmony.Id)
                return false;
            var method = p.PatchMethod;
            if (method == null || method.DeclaringType?.Assembly == _self)
                return false; // this monitor's own patches (e.g. the panel's input block) are not results

            if (_known.TryGetValue(method, out var existing))
            {
                AddTarget(existing, target);
                return true;
            }

            var mod = _registry.Resolve(method.DeclaringType?.Assembly, p.owner);
            switch (kind)
            {
                case "Prefix": mod.Prefixes++; break;
                case "Postfix": mod.Postfixes++; break;
                default: mod.Finalizers++; break;
            }
            if (_excludedGuids.Contains(mod.Guid) || !CanPatch(method))
            {
                _known[method] = null;
                return true;
            }

            var source = new SourceInfo { Mod = mod, Method = method, Kind = kind, Name = Describe(method) };
            AddTarget(source, target);
            Queue(source);
            return true;
        }

        /// <summary>
        /// Update-style methods are only hooked for classes Unity has actually created objects of (including
        /// prefabs and inactive objects). Those classes are loaded and their callbacks already run, so they
        /// are safe to hook; classes that never get instantiated never run, so nothing real is missed.
        /// (Hooking a never-loaded class whose dependencies are missing makes Mono abort the game.)
        /// Call again after scene changes to pick up newly created kinds of objects.
        /// </summary>
        public int ScanLiveBehaviours()
        {
            if (!_includeUnity && !_includeGame)
                return 0;
            UnityEngine.Object[] objects;
            try { objects = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)); }
            catch (Exception e) { _log.LogWarning($"Could not list live objects: {e.Message}"); return 0; }
            return ScanLiveBehaviours(objects);
        }

        /// <summary>Same, with a list of live MonoBehaviours the caller already has.</summary>
        public int ScanLiveBehaviours(UnityEngine.Object[] objects)
        {
            if ((!_includeUnity && !_includeGame) || objects == null)
                return 0;
            int before = _all.Count;

            Assembly game = typeof(ZNet).Assembly;
            var names = new HashSet<string>(GameLoopNames);
            var newTypes = new HashSet<Type>();
            foreach (var o in objects)
            {
                if (o == null) continue;
                var t = o.GetType();
                if (_seenBehaviourTypes.Add(t)) newTypes.Add(t);
            }
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var visited = new HashSet<Type>();
            foreach (var leaf in newTypes)
            {
                for (var t = leaf; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (!visited.Add(t) || t.ContainsGenericParameters)
                        continue;
                    var asm = t.Assembly;
                    if (asm == _self) continue;
                    if (asm == game)
                    {
                        if (!_includeGame) continue;
                        MethodInfo[] methods;
                        try { methods = t.GetMethods(declared); } catch { continue; }
                        foreach (var m in methods)
                            if (names.Contains(m.Name))
                                QueueGame(t, m);
                        continue;
                    }
                    var mod = _registry.ForPluginAssembly(asm);
                    if (mod == null || !_includeUnity || _excludedGuids.Contains(mod.Guid))
                        continue;
                    foreach (var name in UnityCallbacks)
                    {
                        var m = DeclaredNoArgs(t, name);
                        if (m == null || _known.ContainsKey(m) || !CanPatch(m))
                            continue;
                        mod.UnityCallbacks++;
                        var source = new SourceInfo { Mod = mod, Method = m, Kind = name, Name = Describe(m), LiveClass = true };
                        source.Targets.Add("(Unity callback)");
                        Queue(source);
                    }
                }
            }
            return _all.Count - before;
        }

        /// <summary>
        /// Mods' coroutines: methods that "yield return" and are run a step at a time by Unity
        /// (StartCoroutine). The C# compiler turns each into a hidden class whose MoveNext() runs one step;
        /// probing MoveNext times every step. Only iterators that return IEnumerator are taken (coroutines);
        /// ones returning IEnumerable are ordinary loops over data (LINQ-style, Harmony transpilers).
        /// These classes may never have been used yet, so they go through the safety check like patch methods.
        /// </summary>
        private void ScanCoroutines()
        {
            foreach (var asm in _registry.PluginAssemblies)
            {
                if (asm == _self) continue;
                var mod = _registry.ForPluginAssembly(asm);
                if (mod == null || _excludedGuids.Contains(mod.Guid)) continue;
                foreach (var t in SafeTypes(asm))
                {
                    try
                    {
                        if (!t.IsNested || !t.Name.StartsWith("<") || t.ContainsGenericParameters || t.IsValueType) continue;
                        // Iterators that are local functions inside a lambda's closure class (ServerSync's
                        // "sendAsync", bundled in ~30 mods) fail to compile when patched, and the failed patch
                        // can't be removed again. Skip coroutines whose outer class is itself compiler-generated.
                        if (t.DeclaringType == null || t.DeclaringType.Name.StartsWith("<")) continue;
                        if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t) || typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) continue;
                        var m = DeclaredNoArgs(t, "MoveNext");
                        if (m == null || m.ReturnType != typeof(bool) || _known.ContainsKey(m) || !CanPatch(m)) continue;
                        mod.Coroutines++;
                        var source = new SourceInfo { Mod = mod, Method = m, Kind = "Coroutine", Name = CoroutineName(t) };
                        source.Targets.Add("(coroutine)");
                        Queue(source);
                    }
                    catch { }
                }
            }
        }

        // "<SpawnLoop>d__12" nested in "MyMod.Spawner" -> "Spawner.SpawnLoop"
        // A local function inside a method: "<<Start>g__Loop|3_0>d" -> "Spawner.Start/Loop"
        private static string CoroutineName(Type t)
        {
            string n = t.Name;
            string method;
            if (n.StartsWith("<<"))
            {
                int end = n.IndexOf('>');
                int g = n.IndexOf("g__", StringComparison.Ordinal), bar = n.IndexOf('|');
                string outer = end > 2 ? n.Substring(2, end - 2) : n;
                method = g > 0 && bar > g ? outer + "/" + n.Substring(g + 3, bar - g - 3) : outer;
            }
            else
            {
                int end = n.IndexOf('>');
                method = end > 1 ? n.Substring(1, end - 1) : n;
            }
            return $"{t.DeclaringType?.Name}.{method}";
        }

        private void ScanGameExtras()
        {
            Assembly game = typeof(ZNet).Assembly;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var extra in GameExtras)
            {
                var parts = extra.Split(':');
                var type = game.GetType(parts[0], false);
                if (type == null) continue;
                foreach (var m in type.GetMethods(all))
                    if (m.Name == parts[1])
                        QueueGame(type, m);
            }
        }

        /// <summary>
        /// Methods named in the "Extra methods to measure" setting ("Namespace.Type:Method", comma-separated),
        /// in the game or in any mod, so the inside of a slow mod method can be split into parts. Each is
        /// reported under the mod that contains it. Mod methods go through the same safety check as patches.
        /// </summary>
        private void ScanExtraMethods()
        {
            if (string.IsNullOrWhiteSpace(ExtraMethods)) return;
            Assembly game = typeof(ZNet).Assembly;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var raw in ExtraMethods.Split(','))
            {
                var entry = raw.Trim();
                int colon = entry.LastIndexOf(':');
                if (colon <= 0 || colon == entry.Length - 1) { _log.LogWarning($"Extra method '{entry}': expected Type:Method"); continue; }
                Type type = null;
                try { type = AccessTools.TypeByName(entry.Substring(0, colon)); } catch { }
                if (type == null) { _log.LogWarning($"Extra method '{entry}': type not found"); continue; }
                bool isGame = type.Assembly == game;
                var mod = isGame ? _registry.ForGameType(type) : _registry.ForPluginAssembly(type.Assembly) ?? _registry.Resolve(type.Assembly, null);
                if (mod == null || (!isGame && _excludedGuids.Contains(mod.Guid))) continue;
                int found = 0;
                foreach (var m in type.GetMethods(all))
                {
                    if (m.Name != entry.Substring(colon + 1) || m.IsAbstract || m.ContainsGenericParameters || _known.ContainsKey(m) || !CanPatch(m)) continue;
                    Queue(new SourceInfo { Mod = mod, Method = m, Kind = isGame ? "Game" : "Extra", Name = Describe(m), LiveClass = isGame });
                    found++;
                }
                if (found == 0) _log.LogWarning($"Extra method '{entry}': no measurable method by that name");
            }
        }

        private void QueueGame(Type type, MethodInfo m)
        {
            if (_known.ContainsKey(m) || !CanPatch(m))
                return;
            Queue(new SourceInfo { Mod = _registry.ForGameType(type), Method = m, Kind = "Game", Name = Describe(m), LiveClass = true });
        }

        private void Queue(SourceInfo source)
        {
            _known[source.Method] = source;
            _all.Add(source);
            if (Hooked)
                _pending.Enqueue(source);
        }

        // ---- Attaching --------------------------------------------------------------------------

        /// <summary>Patch queued methods until the time budget runs out. Returns true if anything was done.</summary>
        public bool Drain(double budgetMs)
        {
            if (Pending == 0)
            {
                // The previous call may have used up its budget on the very last item.
                _guard?.AllDone();
                return false;
            }
            long start = Stopwatch.GetTimestamp();
            if (_hookStart == 0) _hookStart = start;
            _hookFrames++;
            long deadline = start + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            while (_pending.Count > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                Attach(_pending.Dequeue());
                long t1 = Stopwatch.GetTimestamp();
                _attachTicks += t1 - t0;
                _attachCount++;
                if (t1 > deadline)
                    return true;
            }
            while (_rebuild.Count > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                Rebuild(_rebuild.Dequeue());
                long t1 = Stopwatch.GetTimestamp();
                _rebuildTicks += t1 - t0;
                _rebuildCount++;
                if (t1 > deadline)
                    return true;
            }
            _guard?.AllDone();
            FullRound = false;
            double freq = Stopwatch.Frequency;
            _log.LogInfo($"Hooking took {(Stopwatch.GetTimestamp() - _hookStart) / freq:0.0} s over {_hookFrames} frames: {_attachCount} probes attached in {_attachTicks / freq:0.0} s, " +
                         $"{_rebuildCount} patched game methods rebuilt in {_rebuildTicks / freq:0.0} s" +
                         (_rebuildsNotNeeded > 0 ? $" ({_rebuildsNotNeeded} rebuilds skipped, already done earlier)." : "."));
            _hookStart = _attachTicks = _rebuildTicks = 0;
            _hookFrames = _attachCount = _rebuildCount = _rebuildsNotNeeded = 0;
            _log.LogInfo($"Measuring {Instrumented} methods: {_registry.Visible.Count(m => !m.IsGame && m.Instrumented > 0)} mods, " +
                         $"{_registry.Visible.Count(m => m.IsGame && m.Instrumented > 0)} game systems ({Failed} could not be patched, {Unsafe} skipped as unsafe, {MenuOnly} main-menu-only patches left out).");
            if (_failedNames.Count > 0)
            {
                foreach (var f in _failedNames.Take(10))
                    _log.LogInfo("  could not be measured: " + f);
                if (_failedNames.Count > 10) _log.LogInfo($"  ...and {_failedNames.Count - 10} more.");
                _failedNames.Clear();
            }
            if (_guard != null && _guard.Rejected.Count > 0)
            {
                foreach (var r in _guard.Rejected.Take(10))
                    _log.LogInfo("  skipped (would not compile safely): " + r);
                if (_guard.Rejected.Count > 10) _log.LogInfo($"  ...and {_guard.Rejected.Count - 10} more.");
                _guard.Rejected.Clear();
            }
            return true;
        }

        private void Attach(SourceInfo source)
        {
            if (source.Attached)
                return;
            if (source.TargetMethods.Count > 0 && source.TargetMethods.All(t => t.DeclaringType == typeof(FejdStartup)))
            {
                // Main-menu-only patches can't affect in-world performance, and hooking them (large setup
                // methods in bundled libraries such as ItemManager) has crashed the game.
                MenuOnly++;
                _known[source.Method] = null;
                _all.Remove(source);
                return;
            }
            if (_guard != null && (_guard.IsSkipped(source.Method) || (!source.LiveClass && !_guard.Validate(source.Method))))
            {
                // Its class can't fully load (usually integration code for a mod that isn't installed).
                // Compiling it would crash the game, and it can never run anyway, so it costs nothing.
                Unsafe++;
                _known[source.Method] = null;
                _all.Remove(source);
                return;
            }
            // Register before patching so the very first call after the patch lands is recorded.
            source.Id = Probe.Register(source.Method, source.Mod.Index);
            _registry.Store(source);
            try
            {
                _guard?.Before('P', source.Method);
                PatchProbe(source.Method);
                source.Attached = true;
                source.Mod.Instrumented++;
                if (source.FirstHookSeq < 0)
                    source.FirstHookSeq = ++_seq;
                if (MayBeInlined(source.Method))
                    foreach (var target in source.TargetMethods)
                        QueueRebuild(source, target);
            }
            catch (Exception e)
            {
                Failed++;
                source.Mod.Failed++;
                _known[source.Method] = null;
                _all.Remove(source);
                _failedNames.Add($"{source.Mod.Name}: {source.Kind} {source.Name} ({e.Message})");
            }
        }

        private void PatchProbe(MethodBase method)
        {
            _harmony.Patch(method,
                prefix: new HarmonyMethod(Probe.PrefixMethod) { priority = Priority.First },
                finalizer: new HarmonyMethod(Probe.FinalizerMethod) { priority = Priority.Last });
        }

        /// <summary>Remove every probe (Baseline mode). The game runs exactly as without the monitor's hooks.</summary>
        public void Unhook()
        {
            if (!Hooked)
                return;
            _pending.Clear();
            _rebuild.Clear();
            _rebuildQueued.Clear();
            FullRound = false;
            _hookStart = _attachTicks = _rebuildTicks = 0; // an unfinished round of hooking is not reported
            _hookFrames = _attachCount = _rebuildCount = _rebuildsNotNeeded = 0;
            // One method at a time: removing a probe re-runs other mods' transpilers on that method, and if
            // that fails for one method the rest must still be removed (UnpatchSelf would stop there).
            // A probe that can't be removed stays attached and is not attached a second time later.
            var stuck = new HashSet<MethodBase>();
            List<MethodBase> patched;
            try { patched = _harmony.GetPatchedMethods().ToList(); }
            catch (Exception e) { _log.LogError($"Could not list the probes to remove: {e.Message}"); patched = new List<MethodBase>(); }
            foreach (var m in patched)
            {
                try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); }
                catch (Exception e)
                {
                    stuck.Add(m);
                    if (stuck.Count <= 5)
                        _log.LogWarning($"Could not remove the probe from {Describe(m)} ({e.Message}); it stays attached for this Baseline.");
                }
            }
            _calibrationHooked = false;
            foreach (var s in _all)
            {
                if (!s.Attached || stuck.Contains(s.Method)) continue;
                s.Mod.Instrumented--;
                s.Attached = false;
            }
            Hooked = false;
            _log.LogInfo(stuck.Count == 0 ? "All probes removed." : $"Probes removed, except on {stuck.Count} method(s) listed above.");
        }

        /// <summary>Re-attach every probe after Baseline.</summary>
        public void Rehook()
        {
            if (Hooked)
                return;
            Hooked = true;
            FullRound = true;
            RehookCalibration();
            foreach (var s in _all)
                _pending.Enqueue(s);
        }

        /// <summary>
        /// Take the probe off these methods for a moment, for a measurement the probe would add to (perf/worldgen_cost).
        /// Returns the methods taken off; PutProbesBack puts them on again. Methods without a probe are left alone.
        /// </summary>
        public List<MethodBase> TakeProbesOff(IEnumerable<MethodBase> methods)
        {
            var off = new List<MethodBase>();
            foreach (var m in methods.Distinct())
            {
                if (m == null || !_known.TryGetValue(m, out var s) || s == null || !s.Attached) continue;
                try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); off.Add(m); }
                catch (Exception e) { _log.LogWarning($"Could not take the probe off {Describe(m)} for a moment: {e.Message}"); }
            }
            return off;
        }

        public void PutProbesBack(List<MethodBase> off)
        {
            foreach (var m in off)
            {
                try { PatchProbe(m); }
                catch (Exception e)
                {
                    _log.LogWarning($"Could not put the probe back on {Describe(m)}: {e.Message}");
                    if (_known.TryGetValue(m, out var s) && s != null && s.Attached) { s.Attached = false; s.Mod.Instrumented--; }
                }
            }
        }

        /// <summary>True while every probe is being attached at once (entering a world, after Baseline), as opposed to small top-ups.</summary>
        public bool FullRound { get; private set; }

        private void AddTarget(SourceInfo source, MethodBase target)
        {
            if (source == null || source.TargetMethods.Contains(target))
                return;
            source.TargetMethods.Add(target);
            source.Targets.Add(Describe(target));
            if (source.Attached && MayBeInlined(source.Method))
                QueueRebuild(source, target); // patch method already measured, now also used on a new target
        }

        /// <summary>
        /// Mono only inlines tiny methods (under 20 bytes of IL by default) without exception handlers.
        /// Only targets of such patches need their wrapper rebuilt; rebuilding the rest would just re-run
        /// other mods' transpilers for nothing. 32 bytes leaves a safety margin.
        /// </summary>
        private static bool MayBeInlined(MethodBase m)
        {
            try
            {
                var body = m.GetMethodBody();
                return body != null && body.ExceptionHandlingClauses.Count == 0 && body.GetILAsByteArray().Length <= 32;
            }
            catch { return true; }
        }

        private void QueueRebuild(SourceInfo source, MethodBase target)
        {
            if (_rebuiltAt.TryGetValue(target, out long at) && at > source.FirstHookSeq)
            {
                _rebuildsNotNeeded++;
                return;
            }
            if (_rebuildQueued.Add(target))
                _rebuild.Enqueue(target);
        }

        /// <summary>
        /// Regenerate a patched game method. Small patch methods get inlined by the JIT into the game
        /// method's Harmony wrapper, so the probe on them never fires. Once a method is detoured, MonoMod
        /// marks it non-inlinable; rebuilding the wrapper makes it call the (now probed) patch method.
        /// Patching with no patches just rebuilds; it re-runs other mods' transpilers like any re-patch does.
        /// </summary>
        private void Rebuild(MethodBase target)
        {
            _rebuildQueued.Remove(target);
            if (_guard != null && _guard.IsSkipped(target))
                return;
            _guard?.Before('R', target);
            try
            {
                _harmony.Patch(target);
                _rebuiltAt[target] = ++_seq;
            }
            catch (Exception e) { _log.LogDebug($"Could not rebuild {Describe(target)}: {e.Message}"); }
        }

        // ---- Helpers ----------------------------------------------------------------------------

        private static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }

        private static MethodInfo DeclaredNoArgs(Type type, string name)
        {
            try { return type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null); }
            catch { return null; }
        }

        private static bool CanPatch(MethodBase m)
        {
            try
            {
                if (m.IsAbstract || m.ContainsGenericParameters || m.IsGenericMethodDefinition)
                    return false;
                var t = m.DeclaringType;
                if (t == null || t.ContainsGenericParameters)
                    return false;
                return m.GetMethodBody() != null;
            }
            catch { return false; }
        }

        internal static string Describe(MethodBase m)
        {
            if (m == null)
                return "?";
            var t = m.DeclaringType;
            return t == null ? m.Name : $"{t.Name}.{m.Name}";
        }

        // ---- Calibration ------------------------------------------------------------------------

        private const int ChildrenPerParent = 32;

        [MethodImpl(MethodImplOptions.NoInlining)] private static int CalLeafProbed(int x) => x + 1;
        [MethodImpl(MethodImplOptions.NoInlining)] private static int CalLeafPlain(int x) => x + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CalParentProbedKids(int x)
        {
            for (int i = 0; i < ChildrenPerParent; i++) x = CalLeafProbed(x);
            return x;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CalParentPlainKids(int x)
        {
            for (int i = 0; i < ChildrenPerParent; i++) x = CalLeafPlain(x);
            return x;
        }

        private int _calParentA = -1, _calParentB = -1;

        /// <summary>
        /// Measures, for each capture mode, what the probe itself costs:
        ///  - per call in total (probed call minus identical unprobed call), to correct frame time;
        ///  - the part a child call adds to its caller (a parent calling probed vs. unprobed children),
        ///    which the probe then subtracts from every caller so it is never billed to a mod or the game.
        /// </summary>
        public void Calibrate()
        {
            try
            {
                const BindingFlags bf = BindingFlags.Static | BindingFlags.NonPublic;
                var leaf = typeof(Instrumenter).GetMethod(nameof(CalLeafProbed), bf);
                var parentA = typeof(Instrumenter).GetMethod(nameof(CalParentProbedKids), bf);
                var parentB = typeof(Instrumenter).GetMethod(nameof(CalParentPlainKids), bf);
                Probe.Register(leaf, _registry.Calibration.Index);
                _calParentA = Probe.Register(parentA, _registry.Calibration.Index);
                _calParentB = Probe.Register(parentB, _registry.Calibration.Index);
                RehookCalibration();

                bool wasRecording = Probe.Recording;
                var wasMode = Probe.Mode;
                Probe.Recording = true;
                Probe.ChildOverheadFx = 0;
                foreach (var mode in new[] { CaptureMode.Timing, CaptureMode.Allocations, CaptureMode.Trace })
                {
                    if (mode == CaptureMode.Allocations && !AllocCounter.Available)
                        continue;
                    Probe.Mode = mode;
                    CallOverheadTicks[(int)mode] = MeasureCallOverhead();
                    ChildOverhead[(int)mode] = MeasureChildOverhead();
                }
                Probe.Mode = wasMode;
                Probe.Recording = wasRecording;
                Probe.CommitSources(false);
                Probe.ResetSources();
                Probe.ClearFrame();

                double us = 1e6 / Stopwatch.Frequency;
                _log.LogInfo($"Probe overhead per call: timing {CallOverheadTicks[1] * us:0.000} µs (caller share {ChildOverhead[1] * us:0.000} µs), " +
                             $"allocations {CallOverheadTicks[2] * us:0.000} µs (caller share {ChildOverhead[2]:0.0} bytes), " +
                             $"trace {CallOverheadTicks[3] * us:0.000} µs (caller share {ChildOverhead[3] * us:0.000} µs).");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Overhead calibration failed, results will not be overhead-corrected: {e.Message}");
            }
        }

        private static double MeasureCallOverhead()
        {
            const int n = 20000;
            int acc = 0;
            for (int i = 0; i < 2000; i++) { acc += CalLeafProbed(i); acc += CalLeafPlain(i); }
            var samples = new List<long>();
            for (int round = 0; round < 7; round++)
            {
                Probe.TraceCount = 0;
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++) acc += CalLeafProbed(i);
                long t1 = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++) acc += CalLeafPlain(i);
                long t2 = Stopwatch.GetTimestamp();
                samples.Add((t1 - t0) - (t2 - t1));
            }
            Probe.TraceCount = 0;
            GC.KeepAlive(acc);
            samples.Sort();
            return Math.Max(0, (double)samples[samples.Count / 2] / n);
        }

        private double MeasureChildOverhead()
        {
            const int n = 1000;
            var perCall = new List<double>();
            for (int round = 0; round < 7; round++)
            {
                Probe.ResetSources();
                Probe.TraceCount = 0;
                int acc = 0;
                for (int i = 0; i < n; i++) { acc += CalParentProbedKids(i); acc += CalParentPlainKids(i); }
                GC.KeepAlive(acc);
                Probe.CommitSources(true);
                double a = (double)Probe.SourceSelf[_calParentA] / Math.Max(1, Probe.SourceCalls[_calParentA]);
                double b = (double)Probe.SourceSelf[_calParentB] / Math.Max(1, Probe.SourceCalls[_calParentB]);
                perCall.Add((a - b) / ChildrenPerParent);
            }
            Probe.TraceCount = 0;
            perCall.Sort();
            return Math.Max(0, perCall[perCall.Count / 2]);
        }

        private void RehookCalibration()
        {
            if (_calibrationHooked) return;
            _calibrationHooked = true;
            try
            {
                const BindingFlags bf = BindingFlags.Static | BindingFlags.NonPublic;
                PatchProbe(typeof(Instrumenter).GetMethod(nameof(CalLeafProbed), bf));
                PatchProbe(typeof(Instrumenter).GetMethod(nameof(CalParentProbedKids), bf));
                PatchProbe(typeof(Instrumenter).GetMethod(nameof(CalParentPlainKids), bf));
            }
            catch (Exception e) { _log.LogDebug($"Calibration probes not attached: {e.Message}"); }
        }
    }
}
