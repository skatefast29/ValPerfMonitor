using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// "Does this mod (or this one method, or this object) actually cost anything?" answered without a restart:
    /// its Harmony patches are taken off (or one of its methods is skipped, or the objects are switched off) and
    /// put back, in turns, while Baseline
    /// captures (no probes) measure the game. Turns go on, off, off, on, ... so a slow drift (time of day,
    /// weather, memory growing) lands on both sides equally, and the spread between turns of the same kind
    /// shows how big a difference has to be before it is real.
    ///
    /// Patches are put back exactly as they were (same owner, priority and ordering). Nothing is written to
    /// disk, so a restart always brings everything back. Removing a mod's patches can make that mod misbehave
    /// for the duration (its own state may expect them), so this is a test tool: stand still and don't play
    /// during it.
    /// </summary>
    internal sealed class AbTest
    {
        private sealed class Removed
        {
            public MethodBase Original;
            public Patch Patch;
            public HarmonyPatchType Type;
        }

        public sealed class Phase
        {
            public bool On;
            public int Capture;
            public double FrameMs, CpuMainMs, Fps1Low, GcPerMin, SlowPerMin, Seconds, GarbageKbPerFrame;
            // What the world was like during the turn, to leave out rounds where it changed.
            public List<string> Weathers = new List<string>();
            public float MovedM;
            public int ObjectsMin = -1, ObjectsMax = -1;
            public bool HasPos;
            public UnityEngine.Vector3 StartPos;
            public string WeatherText => Weathers.Count == 0 ? "?" : string.Join(" -> ", Weathers);
        }

        // A round is left out when the world changed under it: weather, the player moving, or the loaded
        // objects changing by more than this share.
        private const float MaxMoveM = 10f;
        private const double MaxObjectChange = 0.15;
        public const int MaxRounds = 6;

        public readonly string What, Label;
        public readonly float Seconds;
        public readonly List<bool> Plan = new List<bool>();
        public readonly List<Phase> Phases = new List<Phase>();
        public string Error, Verdict = "";
        public readonly List<string> VerdictLines = new List<string>();
        public bool Done;
        public int Step = -1;
        public int RoundsUsed, RoundsLeftOut;
        private readonly int _requestedRounds;
        // Per-turn condition tracking (sampled every few frames; reads only, no allocations).
        private Phase _turn = new Phase();
        private int _sampleCountdown;

        private readonly List<Removed> _removed = new List<Removed>();
        private readonly List<MethodInfo> _skipped = new List<MethodInfo>();
        // Objects switched off instead (one object, or every loaded copy of a prefab). Only the ones that were on
        // are switched back on, and only while they still exist (the game may unload them in the meantime).
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<GameObject> _switchedOff = new List<GameObject>();
        private readonly Harmony _skipHarmony;
        private readonly ManualLogSource _log;
        private readonly string _crumbPath;
        private bool _off;
        // The frame cap is lifted during the test (restored after): at the cap, frames wait for the limiter and
        // a CPU saving can't show in frame time.
        private int _savedTarget, _savedVsync;
        private bool _uncapped;
        // Whole-game garbage during the current turn: heap growth over counted frames without a GC.
        private long _prevHeap = -1, _heapBytes;
        private int _heapFrames;
        // Main-thread CPU time during the current turn (CpuClock), over counted frames.
        private ulong _prevCycles, _cycles;
        private int _cpuFrames;

        public int PatchCount => _removed.Count;
        public int SkipCount => _skipped.Count;
        public int ObjectCount => _objects.Count;
        public IEnumerable<string> Describe() =>
            _removed.Select(r => $"{r.Type} {Instrumenter.Describe(r.Patch.PatchMethod)} on {Instrumenter.Describe(r.Original)}")
                    .Concat(_skipped.Select(m => $"skip {Instrumenter.Describe(m)}"))
                    .Concat(_objects.Where(o => o != null).Take(50).Select(o => $"switch off {PathOf(o.transform)} (instanceId {o.GetInstanceID()})"));

        private static readonly MethodInfo SkipVoid = AccessTools.Method(typeof(AbTest), nameof(SkipPrefix));
        private static readonly MethodInfo SkipBool = AccessTools.Method(typeof(AbTest), nameof(SkipPrefixKeepGoing));
        private static bool SkipPrefix() => false;
        // A skipped Harmony prefix returns true ("let the original run"), as if it did nothing.
        private static bool SkipPrefixKeepGoing(ref bool __result) { __result = true; return false; }

        /// <summary>
        /// mod: plugin name or GUID whose Harmony patches are taken off. patchesOn: only its patches on this
        /// game method ("Type:Method"). skip: instead, a mod method ("Namespace.Type:Method") to skip entirely.
        /// objectId / objectPath / prefab: instead, switch off one Unity object (with its children) or every
        /// loaded copy of a prefab.
        /// </summary>
        public AbTest(string mod, string patchesOn, string skip, int objectId, string objectPath, string prefab, float seconds, int rounds, string label,
                      ModRegistry registry, string harmonyId, string rootFolder, ManualLogSource log)
        {
            _log = log;
            _skipHarmony = new Harmony(harmonyId);
            _crumbPath = Path.Combine(rootFolder, "ab-test-in-progress.txt");
            Seconds = Math.Max(10f, seconds);
            rounds = Math.Max(1, Math.Min(MaxRounds, rounds));
            _requestedRounds = rounds;
            for (int i = 0; i < rounds; i++) AddRound();

            if (!string.IsNullOrWhiteSpace(skip))
            {
                What = "skip " + skip.Trim();
                FindSkip(skip.Trim(), registry);
            }
            else if (!string.IsNullOrWhiteSpace(mod))
            {
                var entry = FindMod(mod.Trim(), registry);
                if (entry == null) return;
                What = entry.Name + (string.IsNullOrWhiteSpace(patchesOn) ? " (all its patches)" : $" (its patches on {patchesOn.Trim()})");
                FindPatches(entry, patchesOn, registry, harmonyId);
            }
            else if (objectId != 0 || !string.IsNullOrWhiteSpace(objectPath) || !string.IsNullOrWhiteSpace(prefab))
                What = FindObjects(objectId, objectPath, prefab);
            else Error = "say which mod (mod), which mod method to skip (skip), or which object to switch off (objectId, objectPath or prefab)";
            Label = (string.IsNullOrWhiteSpace(label) ? "AB " + What : label.Trim());
            if (Error == null && _removed.Count == 0 && _skipped.Count == 0 && _objects.Count == 0)
                Error = "nothing to take off: no matching patches, methods or objects found";
        }

        private ModEntry FindMod(string name, ModRegistry registry)
        {
            var mods = registry.Visible.Where(m => !m.IsGame).ToList();
            var hit = mods.Where(m => string.Equals(m.Guid, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) hit = mods.Where(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count == 1) return hit[0];
            Error = hit.Count == 0 ? $"no mod called '{name}'" : $"'{name}' matches several mods: {string.Join(", ", hit.Take(8).Select(m => m.Name))}";
            return null;
        }

        private void FindPatches(ModEntry entry, string patchesOn, ModRegistry registry, string harmonyId)
        {
            HashSet<MethodBase> only = null;
            if (!string.IsNullOrWhiteSpace(patchesOn))
            {
                only = new HashSet<MethodBase>(ResolveMethods(patchesOn.Trim()));
                if (only.Count == 0) { Error = $"method '{patchesOn}' not found (use Type:Method)"; return; }
            }
            string monitor = harmonyId.Substring(0, harmonyId.LastIndexOf('.'));
            List<MethodBase> patched;
            try { patched = Harmony.GetAllPatchedMethods().ToList(); }
            catch (Exception e) { Error = "could not list patches: " + e.Message; return; }
            foreach (var original in patched)
            {
                if (only != null && !only.Contains(original)) continue;
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch { continue; }
                if (info == null) continue;
                void Take(IEnumerable<Patch> list, HarmonyPatchType type)
                {
                    foreach (var p in list)
                    {
                        if (p.PatchMethod == null || p.owner.StartsWith(monitor, StringComparison.Ordinal)) continue;
                        if (registry.Resolve(p.PatchMethod.DeclaringType?.Assembly, p.owner) != entry) continue;
                        _removed.Add(new Removed { Original = original, Patch = p, Type = type });
                    }
                }
                Take(info.Prefixes, HarmonyPatchType.Prefix);
                Take(info.Postfixes, HarmonyPatchType.Postfix);
                Take(info.Transpilers, HarmonyPatchType.Transpiler);
                Take(info.Finalizers, HarmonyPatchType.Finalizer);
                Take(info.ILManipulators, HarmonyPatchType.ILManipulator);
            }
        }

        private void FindSkip(string spec, ModRegistry registry)
        {
            Assembly game = typeof(ZNet).Assembly;
            foreach (var m in ResolveMethods(spec).OfType<MethodInfo>())
            {
                var asm = m.DeclaringType?.Assembly;
                if (asm == null || asm == game || asm.GetName().Name.StartsWith("assembly_") || asm.GetName().Name.StartsWith("UnityEngine"))
                { Error = "only mod methods can be skipped, not the game's own"; return; }
                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                if (m.ReturnType == typeof(void)) _skipped.Add(m);
                else if (m.ReturnType == typeof(bool) && IsHarmonyPrefix(m)) _skipped.Add(m);
            }
            if (_skipped.Count == 0 && Error == null)
                Error = $"'{spec}': no method that can be skipped safely (it must return nothing, or be a Harmony prefix)";
        }

        /// <summary>Finds the objects to switch off; returns what is tested, or null with Error set.</summary>
        private string FindObjects(int id, string path, string prefab)
        {
            if (!string.IsNullOrWhiteSpace(prefab))
            {
                string name = prefab.Trim();
                var scene = ZNetScene.instance;
                if (scene == null) { Error = "not in a world"; return null; }
                if (scene.GetPrefab(name) == null) { Error = $"no prefab called '{name}' (use the names perf/objects lists)"; return null; }
                var copies = GameContext.LoadedOfPrefab(name.GetStableHashCode());
                if (copies.Count == 0) { Error = $"no copies of {name} are loaded here"; return null; }
                foreach (var go in copies)
                {
                    string why = Refuse(go);
                    if (why != null) { Error = $"{name}: {why}"; return null; }
                }
                _objects.AddRange(copies);
                return $"prefab {name} ({copies.Count} loaded copies switched off)";
            }
            GameObject target = null;
            if (id != 0)
            {
                var found = Resources.InstanceIDToObject(id);
                target = found as GameObject;
                if (target == null && found is Component c && c != null) target = c.gameObject;
                if (target == null) { Error = $"no live object with instanceId {id}"; return null; }
            }
            else
            {
                target = FindPath(path.Trim().Trim('/'), out string error);
                if (target == null) { Error = error; return null; }
            }
            string refused = Refuse(target);
            if (refused != null) { Error = refused; return null; }
            _objects.Add(target);
            return $"object {PathOf(target.transform)}";
        }

        /// <summary>Why an object must not be switched off, or null.</summary>
        private static string Refuse(GameObject go)
        {
            var player = Player.m_localPlayer;
            if (player != null && (go == player.gameObject || player.transform.IsChildOf(go.transform)))
                return "that holds the player character";
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                // BepInEx_Manager holds every mod's plugin, this monitor and the bridge: nothing could switch it back on.
                if (c is BepInEx.BaseUnityPlugin || c.GetType().Assembly == typeof(Plugin).Assembly || c.GetType().Assembly.GetName().Name.StartsWith("ValAgentBridge"))
                    return "that holds mods' plugins (or this monitor or the bridge), which would stop them";
                if (c is ZNet || c is ZNetScene || c is Game || c is ZoneSystem || c is EnvMan)
                    return "that is one of the game's core objects; switching it off stops the world";
            }
            return null;
        }

        /// <summary>An object by its path of names from a root ("_GameMain/GUI"), in the loaded scenes or the kept-across-scenes one.</summary>
        private static GameObject FindPath(string path, out string error)
        {
            error = null;
            var parts = path.Split('/');
            if (parts.Length == 0 || parts[0].Length == 0) { error = "empty objectPath"; return null; }
            var roots = new List<GameObject>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (s.isLoaded) roots.AddRange(s.GetRootGameObjects());
            }
            var kept = Plugin.Instance != null ? Plugin.Instance.gameObject.scene : default;
            if (kept.IsValid() && kept.isLoaded) roots.AddRange(kept.GetRootGameObjects());
            var matches = roots.Where(r => r != null && r.name == parts[0]).Select(r => r.transform).ToList();
            for (int i = 1; i < parts.Length && matches.Count > 0; i++)
            {
                var next = new List<Transform>();
                foreach (var t in matches)
                    for (int k = 0; k < t.childCount; k++)
                        if (t.GetChild(k).name == parts[i]) next.Add(t.GetChild(k));
                matches = next;
            }
            if (matches.Count == 0) { error = $"no object at '{path}' (paths as debug/scene_tree shows them)"; return null; }
            if (matches.Count > 1) { error = $"'{path}' matches {matches.Count} objects; use objectId (from debug/find_objects) to pick one, or prefab for all copies"; return null; }
            return matches[0].gameObject;
        }

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var x = t; x != null; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static bool IsHarmonyPrefix(MethodInfo m)
        {
            try
            {
                return Harmony.GetAllPatchedMethods().Any(o =>
                {
                    var info = Harmony.GetPatchInfo(o);
                    return info != null && info.Prefixes.Any(p => p.PatchMethod == m);
                });
            }
            catch { return false; }
        }

        private void AddRound()
        {
            // on, off | off, on | on, off ...
            bool onFirst = (Plan.Count / 2) % 2 == 0;
            Plan.Add(onFirst);
            Plan.Add(!onFirst);
        }

        private static List<MethodBase> ResolveMethods(string spec)
        {
            int colon = spec.LastIndexOf(':');
            if (colon <= 0 || colon == spec.Length - 1) return new List<MethodBase>();
            Type t = null;
            try { t = AccessTools.TypeByName(spec.Substring(0, colon)); } catch { }
            if (t == null) return new List<MethodBase>();
            string name = spec.Substring(colon + 1);
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            return t.GetMethods(all).Where(m => m.Name == name).Cast<MethodBase>().ToList();
        }

        // ---- Running ----------------------------------------------------------------------------

        public string Status => Done ? "A/B test finished" : $"A/B test {What}: turn {Step + 1}/{Plan.Count} ({(Plan[Math.Max(0, Step)] ? "normal" : "taken off")})";

        public void Begin(Plugin p)
        {
            _savedTarget = UnityEngine.Application.targetFrameRate;
            _savedVsync = UnityEngine.QualitySettings.vSyncCount;
            _uncapped = true;
            StartPhase(p, 0);
        }

        private void Uncap()
        {
            if (!_uncapped) return;
            UnityEngine.QualitySettings.vSyncCount = 0;
            UnityEngine.Application.targetFrameRate = -1;
        }

        private void RestoreCap()
        {
            if (!_uncapped) return;
            _uncapped = false;
            UnityEngine.QualitySettings.vSyncCount = _savedVsync;
            UnityEngine.Application.targetFrameRate = _savedTarget;
        }

        /// <summary>Every frame while the test runs (Plugin's frame loop).</summary>
        public void Frame(bool use)
        {
            if (--_sampleCountdown <= 0)
            {
                _sampleCountdown = 30;
                SampleConditions();
            }
            if (CpuClock.Available)
            {
                ulong c = CpuClock.Read();
                if (use && _prevCycles != 0 && c >= _prevCycles)
                {
                    _cycles += c - _prevCycles;
                    _cpuFrames++;
                }
                _prevCycles = c;
            }
            long h;
            try { h = GC.GetTotalMemory(false); } catch { return; }
            if (use && _prevHeap >= 0 && h >= _prevHeap)
            {
                _heapBytes += h - _prevHeap;
                _heapFrames++;
            }
            _prevHeap = h;
        }

        public void Tick(Plugin p)
        {
            var c = p.Current;
            if (Done || c == null || c.MeasuredSeconds < Seconds)
                return;
            RecordPhase(p, c);
            if (Step + 1 == Plan.Count && Plan.Count / 2 < MaxRounds && _requestedRounds >= 2)
            {
                string why = WhyAnotherRound();
                if (why != null)
                {
                    AddRound();
                    _log.LogInfo($"A/B test: adding round {Plan.Count / 2} ({why}).");
                }
            }
            if (Step + 1 < Plan.Count) StartPhase(p, Step + 1);
            else Finish(p);
        }

        /// <summary>Weather, position and loaded-object count, read every 30 frames into the current turn.</summary>
        private void SampleConditions()
        {
            try
            {
                var env = EnvMan.instance;
                var setup = env != null ? env.GetCurrentEnvironment() : null;
                string w = setup != null ? setup.m_name : null;
                if (!string.IsNullOrEmpty(w) && (_turn.Weathers.Count == 0 || _turn.Weathers[_turn.Weathers.Count - 1] != w) && _turn.Weathers.Count < 6)
                    _turn.Weathers.Add(w);
            }
            catch { }
            var player = Player.m_localPlayer;
            if (player != null)
            {
                var pos = player.transform.position;
                if (!_turn.HasPos) { _turn.HasPos = true; _turn.StartPos = pos; }
                else _turn.MovedM = Math.Max(_turn.MovedM, UnityEngine.Vector3.Distance(_turn.StartPos, pos));
            }
            int n = GameContext.LoadedObjects();
            if (n >= 0)
            {
                _turn.ObjectsMin = _turn.ObjectsMin < 0 ? n : Math.Min(_turn.ObjectsMin, n);
                _turn.ObjectsMax = Math.Max(_turn.ObjectsMax, n);
            }
        }

        /// <summary>Why a round (two turns next to each other) can't be trusted, or null when it can.</summary>
        private static string RoundProblem(Phase a, Phase b)
        {
            var weathers = a.Weathers.Concat(b.Weathers).Distinct().ToList();
            if (weathers.Count > 1) return $"weather changed ({a.WeatherText} | {b.WeatherText})";
            if (a.HasPos && b.HasPos)
            {
                float moved = Math.Max(Math.Max(a.MovedM, b.MovedM), UnityEngine.Vector3.Distance(a.StartPos, b.StartPos));
                if (moved > MaxMoveM) return $"player moved {moved:0} m";
            }
            int min = Math.Min(a.ObjectsMin, b.ObjectsMin), max = Math.Max(a.ObjectsMax, b.ObjectsMax);
            if (min > 0 && (max - min) > MaxObjectChange * max) return $"loaded objects changed ({min} to {max})";
            return null;
        }

        /// <summary>After the last planned turn: a reason to run one more round, or null.</summary>
        private string WhyAnotherRound()
        {
            var m = Compare(MainMetric(out _));
            if (m.LeftOut > 0 && m.Pairs < _requestedRounds) return $"{m.LeftOut} round(s) left out because conditions changed";
            if (m.Pairs >= 2 && !m.Real && m.SameWay) return "every round agrees but the sizes vary";
            return null;
        }

        private Func<Phase, double> MainMetric(out string name)
        {
            bool cpu = Phases.All(x => !double.IsNaN(x.CpuMainMs));
            name = cpu ? "main-thread CPU" : "frame time";
            return cpu ? (x => x.CpuMainMs) : (Func<Phase, double>)(x => x.FrameMs);
        }

        private void StartPhase(Plugin p, int step)
        {
            Step = step;
            bool on = Plan[step];
            if (on) PutBack(); else TakeOff();
            Uncap(); // again each turn, in case the game re-applied its own setting
            _prevHeap = -1;
            _heapBytes = 0;
            _heapFrames = 0;
            _prevCycles = 0;
            _cycles = 0;
            _cpuFrames = 0;
            _turn = new Phase();
            _sampleCountdown = 0;
            p.StartCapture(CaptureMode.Baseline, $"{Label} {(on ? "normal" : "off")} {step + 1}");
            if (p.Current != null) p.Current.AbTurn = true;
        }

        private void RecordPhase(Plugin p, Capture c)
        {
            var s = Report.Summarize(c.Stats);
            double cpu = _cpuFrames > 0 ? CpuClock.ToMs(_cycles) / _cpuFrames : double.NaN;
            if (double.IsNaN(cpu) && p.Counters != null && p.Counters.FrameTimingSeen && p.Counters.TotTimingSamples > 0)
                cpu = p.Counters.TotCpuMain / p.Counters.TotTimingSamples;
            double secs = Math.Max(1, c.MeasuredSeconds);
            SampleConditions();
            var ph = _turn;
            ph.On = Plan[Step]; ph.Capture = c.Number; ph.FrameMs = s.Fps > 0 ? 1000 / s.Fps : double.NaN; ph.CpuMainMs = cpu;
            ph.Fps1Low = s.Fps1Low; ph.GcPerMin = s.GcPerMin; ph.SlowPerMin = c.Stats.SpikeCount / secs * 60; ph.Seconds = secs;
            ph.GarbageKbPerFrame = _heapFrames == 0 ? double.NaN : _heapBytes / 1024.0 / _heapFrames;
            Phases.Add(ph);
            _turn = new Phase();
        }

        private void TakeOff()
        {
            if (_off) return;
            _off = true;
            try { File.WriteAllText(_crumbPath, "A/B test had these taken off (only for that session):\n" + string.Join("\n", Describe()) + "\n"); } catch { }
            foreach (var r in _removed)
            {
                try { new Harmony(r.Patch.owner).Unpatch(r.Original, r.Patch.PatchMethod); }
                catch (Exception e) { _log.LogWarning($"A/B test: could not take off {Instrumenter.Describe(r.Patch.PatchMethod)}: {e.Message}"); }
            }
            foreach (var m in _skipped)
            {
                try { _skipHarmony.Patch(m, prefix: new HarmonyMethod(m.ReturnType == typeof(bool) ? SkipBool : SkipVoid) { priority = Priority.First }); }
                catch (Exception e) { _log.LogWarning($"A/B test: could not skip {Instrumenter.Describe(m)}: {e.Message}"); }
            }
            foreach (var go in _objects)
            {
                if (go == null || !go.activeSelf) continue;
                try { go.SetActive(false); _switchedOff.Add(go); }
                catch (Exception e) { _log.LogWarning($"A/B test: could not switch off {go.name}: {e.Message}"); }
            }
        }

        /// <summary>Put everything back as it was. Safe to call more than once.</summary>
        public void PutBack()
        {
            if (!_off) return;
            _off = false;
            int failed = 0;
            foreach (var m in _skipped)
            {
                try { _skipHarmony.Unpatch(m, HarmonyPatchType.All, _skipHarmony.Id); }
                catch (Exception e) { failed++; _log.LogError($"A/B test: could not un-skip {Instrumenter.Describe(m)}: {e.Message}"); }
            }
            foreach (var r in _removed)
            {
                var p = r.Patch;
                // Already back (another mod re-applied it)? Then leave it.
                try
                {
                    var info = Harmony.GetPatchInfo(r.Original);
                    if (info != null && All(info).Any(x => x.PatchMethod == p.PatchMethod)) continue;
                }
                catch { }
                var hm = new HarmonyMethod(p.PatchMethod) { priority = p.priority, before = p.before, after = p.after, debug = p.debug };
                try
                {
                    var h = new Harmony(p.owner);
                    switch (r.Type)
                    {
                        case HarmonyPatchType.Prefix: h.Patch(r.Original, prefix: hm); break;
                        case HarmonyPatchType.Postfix: h.Patch(r.Original, postfix: hm); break;
                        case HarmonyPatchType.Transpiler: h.Patch(r.Original, transpiler: hm); break;
                        case HarmonyPatchType.Finalizer: h.Patch(r.Original, finalizer: hm); break;
                        case HarmonyPatchType.ILManipulator: h.Patch(r.Original, ilmanipulator: hm); break;
                    }
                }
                catch (Exception e) { failed++; _log.LogError($"A/B test: could not put back {Instrumenter.Describe(p.PatchMethod)} on {Instrumenter.Describe(r.Original)}: {e.Message}"); }
            }
            foreach (var go in _switchedOff)
            {
                if (go == null) continue; // unloaded by the game meanwhile: it comes back as normal when reloaded
                try { go.SetActive(true); }
                catch (Exception e) { failed++; _log.LogError($"A/B test: could not switch {go.name} back on: {e.Message}"); }
            }
            _switchedOff.Clear();
            if (failed > 0) _log.LogError($"A/B test: {failed} patch(es) or object(s) could not be put back. Restart the game before playing on.");
            else try { File.Delete(_crumbPath); } catch { }
        }

        private static IEnumerable<Patch> All(Patches i) => i.Prefixes.Concat(i.Postfixes).Concat(i.Transpilers).Concat(i.Finalizers).Concat(i.ILManipulators);

        /// <summary>Stopped early: put everything back, keep whatever turns finished.</summary>
        public void Abort()
        {
            PutBack();
            RestoreCap();
            Done = true;
            if (Phases.Count > 0) Judge();
        }

        private void Finish(Plugin p)
        {
            PutBack();
            RestoreCap();
            Done = true;
            Judge();
            p.AbFinished(this);
        }

        // ---- Verdict ----------------------------------------------------------------------------

        private void Judge()
        {
            VerdictLines.Clear();
            var on = Phases.Where(x => x.On).ToList();
            var off = Phases.Where(x => !x.On).ToList();
            if (on.Count == 0 || off.Count == 0) { Verdict = "not enough turns to compare"; return; }
            var main = MainMetric(out string mainName);

            // Positive diff: the game is faster with it taken off.
            var m = Compare(main);
            RoundsUsed = m.Pairs;
            RoundsLeftOut = m.LeftOut;
            if (m.Pairs == 0 && m.LeftOut > 0)
            {
                Verdict = $"NO RESULT: conditions changed in every round ({string.Join("; ", m.Problems)}). Stand still in clear, pinned weather and run it again";
                VerdictLines.Add(Verdict);
                return;
            }
            double avgOn = on.Average(main);
            string pct = avgOn > 0 ? $" ({100 * Math.Abs(m.Diff) / avgOn:0.0}%)" : "";
            string pairs = $"each normal turn vs the off turn next to it: {string.Join(", ", m.PairDiffs.Select(d => Signed(d)))} ms";
            if (m.Pairs < 2)
                Verdict = $"{mainName} {Signed(m.Diff)} ms per frame lower with it off{pct}, but only one round: can't tell real from normal variation";
            else if (m.Real && Math.Abs(m.Diff) >= 0.05)
                Verdict = m.Diff > 0 ? $"REAL COST: taking it off saved {m.Diff:0.000} ms of {mainName} per frame{pct}; faster in every round ({pairs})"
                                     : $"REAL: the game used {-m.Diff:0.000} ms more {mainName} per frame with it off{pct}; slower in every round ({pairs})";
            else if (m.SameWay && m.Pairs >= 3 && Math.Abs(m.Diff) >= 0.05)
                Verdict = $"LEANS {(m.Diff > 0 ? "COST" : "SLOWER WITHOUT IT")}: every round pointed the same way ({Signed(m.Diff)} ms per frame on average{pct}), " +
                          $"but the rounds vary too much in size to be sure; run more rounds ({pairs})";
            else
                Verdict = $"NO CLEAR DIFFERENCE in {mainName}: {Signed(m.Diff)} ms per frame on average, but the rounds disagree or the difference is smaller than their spread ({pairs})";
            VerdictLines.Add(Verdict);
            if (m.LeftOut > 0)
                VerdictLines.Add($"Left out {m.LeftOut} round(s) because conditions changed: {string.Join("; ", m.Problems)}");

            if (Phases.All(x => !double.IsNaN(x.GarbageKbPerFrame)))
            {
                var g = Compare(x => x.GarbageKbPerFrame);
                VerdictLines.Add($"Garbage per frame (whole game): {on.Average(x => x.GarbageKbPerFrame):0.0} KB normal, {off.Average(x => x.GarbageKbPerFrame):0.0} KB off" +
                                 (g.Pairs < 2 ? "" : g.Real ? $" (real: {g.Diff:0.0} KB less per frame with it off in every round)" : " (rounds disagree: within normal variation)"));
            }
            VerdictLines.Add($"Garbage collections per minute: {on.Average(x => x.GcPerMin):0.0} normal, {off.Average(x => x.GcPerMin):0.0} off" +
                             (Seconds < 45 ? " (short turns hold only a few collections: rough)" : ""));
            VerdictLines.Add($"1% low FPS: {on.Average(x => x.Fps1Low):0.0} normal, {off.Average(x => x.Fps1Low):0.0} off; slow frames per minute: {on.Average(x => x.SlowPerMin):0.0} normal, {off.Average(x => x.SlowPerMin):0.0} off");
        }

        private sealed class Comparison
        {
            public double Diff;            // mean of (normal - off) over rounds
            public double Spread;          // max - min of the per-round differences
            public int Pairs;
            public int LeftOut;            // rounds not used because the world changed during them
            public List<string> Problems = new List<string>();
            public bool Real;             // every round agrees on the direction and the mean is bigger than the spread
            public bool SameWay;           // every round agrees on the direction
            public List<double> PairDiffs = new List<double>();
        }

        /// <summary>
        /// Each round is a normal turn and the off turn right next to it, so a slow drift over the test (time of
        /// day, memory growing) cancels out within the round. Rounds where the world changed (weather, player
        /// moved, objects loaded or unloaded) are left out. Real = every round points the same way, and the
        /// average difference is bigger than how much the rounds differ from each other.
        /// </summary>
        private Comparison Compare(Func<Phase, double> f)
        {
            var r = new Comparison();
            for (int i = 0; i + 1 < Phases.Count; i += 2)
            {
                var a = Phases[i];
                var b = Phases[i + 1];
                if (a.On == b.On) continue;
                string problem = RoundProblem(a, b);
                if (problem != null) { r.LeftOut++; r.Problems.Add($"round {i / 2 + 1}: {problem}"); continue; }
                r.PairDiffs.Add(a.On ? f(a) - f(b) : f(b) - f(a));
            }
            r.Pairs = r.PairDiffs.Count;
            if (r.Pairs == 0) return r;
            r.Diff = r.PairDiffs.Average();
            r.Spread = r.PairDiffs.Max() - r.PairDiffs.Min();
            r.SameWay = r.PairDiffs.All(d => d > 0) || r.PairDiffs.All(d => d < 0);
            r.Real = r.Pairs >= 2 && r.SameWay && Math.Abs(r.Diff) > r.Spread;
            return r;
        }

        private static string Signed(double v) => (v >= 0 ? "+" : "") + v.ToString("0.000");

        public string CsvLine(string session, ModRegistry registry)
        {
            var sb = new StringBuilder();
            sb.Append(Csv.Q(session)).Append(',').Append(DateTime.Now.ToString("HH:mm:ss", Csv.Inv)).Append(',').Append(Csv.Q(What)).Append(',')
              .Append(Seconds.ToString("0", Csv.Inv)).Append(',').Append(Phases.Count).Append(',');
            Func<bool, Func<Phase, double>, string> avg = (on, f) => { var l = Phases.Where(x => x.On == on).ToList(); return l.Count == 0 ? "" : Csv.F(l.Average(f), "0.000"); };
            sb.Append(avg(true, x => x.CpuMainMs)).Append(',').Append(avg(false, x => x.CpuMainMs)).Append(',')
              .Append(avg(true, x => x.GarbageKbPerFrame)).Append(',').Append(avg(false, x => x.GarbageKbPerFrame)).Append(',')
              .Append(avg(true, x => x.FrameMs)).Append(',').Append(avg(false, x => x.FrameMs)).Append(',')
              .Append(avg(true, x => x.GcPerMin)).Append(',').Append(avg(false, x => x.GcPerMin)).Append(',')
              .Append(Csv.Q(Verdict)).Append(',')
              .Append(RoundsUsed).Append(',').Append(RoundsLeftOut).Append(',')
              .Append(Csv.Q(RunStamp.GameVersion)).Append(',').Append(Csv.Q(RunStamp.ModsFingerprint(registry))).Append('\n');
            return sb.ToString();
        }

        public const string CsvHeader = "session,time,what,seconds_per_turn,turns,cpu_ms_normal,cpu_ms_off,garbage_kb_per_frame_normal,garbage_kb_per_frame_off,frame_ms_normal,frame_ms_off,gc_per_min_normal,gc_per_min_off,verdict," +
                                        "rounds_used,rounds_left_out,game_version,mods_fingerprint\n";

        /// <summary>At startup: say so if the game closed in the middle of an A/B test (nothing to repair).</summary>
        public static void CheckLeftover(string rootFolder, ManualLogSource log)
        {
            string path = Path.Combine(rootFolder, "ab-test-in-progress.txt");
            try
            {
                if (!File.Exists(path)) return;
                log.LogWarning("The game closed during an A/B test last time. Nothing needs repairing (patches are only taken off for that session); " +
                               "if it crashed, the cause may be what was taken off:\n" + File.ReadAllText(path));
                File.Delete(path);
            }
            catch { }
        }
    }
}
