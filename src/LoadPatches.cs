using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// Splits the big loading steps (translations, item database, network setup, prefab registry ...) by
    /// mod: every mod's patch on a timed loading step gets its own timer, so a step's time can be shown as
    /// "mod A 1.2 s, mod B 0.4 s, the game's own code the rest".
    ///
    /// Attached when the monitor starts (after every mod's Awake, before the main menu loads), and once more
    /// at the main menu for patches added late. Patch methods that also patch the main menu (FejdStartup)
    /// are never touched: hooking those is what crashed the game. The same methods are hooked in the world
    /// by the normal probes every session, and the safety guard (breadcrumb, skip list, class check) covers
    /// them here too. These patches only run while loading, saving or logging out, so the timers cost
    /// nothing during play.
    /// </summary>
    internal static class LoadPatches
    {
        public struct Event { public string Step, Mod; public long Start; public double SelfMs; }

        private static Harmony _harmony;
        private static ModRegistry _registry;
        private static SafetyGuard _guard;
        private static ManualLogSource _log;
        private static string _monitorPrefix;
        private static readonly Dictionary<MethodBase, string> ModOf = new Dictionary<MethodBase, string>();
        private static readonly HashSet<MethodBase> Seen = new HashSet<MethodBase>();
        private static readonly List<Event> Events = new List<Event>();
        private static readonly long[] PStart = new long[64];
        private static readonly long[] PNested = new long[64];
        private static readonly int[] PStepDepth = new int[64];
        private static int _depth;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        /// <summary>True once the main-menu pass has run.</summary>
        public static bool Armed { get; private set; }
        public static int Timed => ModOf.Count;

        public static void Init(string harmonyId, string monitorGuid, ModRegistry registry, SafetyGuard guard, ManualLogSource log)
        {
            _harmony = new Harmony(harmonyId);
            _monitorPrefix = monitorGuid;
            _registry = registry;
            _guard = guard;
            _log = log;
        }

        /// <summary>
        /// Attach timers to the mod patches on the loading steps not timed yet. atMainMenu: the second pass,
        /// for patches added after startup. Returns true if any work was done.
        /// </summary>
        public static bool Arm(bool atMainMenu)
        {
            if (_harmony == null || Armed) return false;
            if (atMainMenu) Armed = true;
            long t0 = Stopwatch.GetTimestamp();
            int before = ModOf.Count, failed = 0, skipped = 0;
            var menuPatches = MainMenuPatches();
            foreach (var step in LoadSteps.Steps.ToList())
            {
                var original = step.Key;
                if (original.DeclaringType == typeof(FejdStartup)) continue;
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch { continue; }
                if (info == null) continue;
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    var m = p.PatchMethod;
                    if (m == null || p.owner.StartsWith(_monitorPrefix, StringComparison.Ordinal) || !Seen.Add(m)) continue;
                    if (menuPatches.Contains(m) || m.IsAbstract || m.ContainsGenericParameters || _guard.IsSkipped(m) || !_guard.Validate(m, runClassConstructor: atMainMenu)) { skipped++; continue; }
                    string mod = _registry.Resolve(m.DeclaringType?.Assembly, p.owner)?.Name ?? p.owner;
                    try
                    {
                        _guard.Before('P', m);
                        _harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(LoadPatches), nameof(Prefix)) { priority = Priority.First },
                            finalizer: new HarmonyMethod(typeof(LoadPatches), nameof(Finalizer)) { priority = Priority.Last });
                        ModOf[m] = mod;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        _log.LogDebug($"Loading patch {Instrumenter.Describe(m)} not timed: {e.Message}");
                    }
                }
            }
            _guard.AllDone();
            int added = ModOf.Count - before;
            if (added > 0 || skipped + failed > 0)
                _log.LogInfo($"Timing {added} {(atMainMenu ? "more " : "")}mod patches on the loading steps, so loading can be split by mod " +
                             $"({(Stopwatch.GetTimestamp() - t0) * MsPerTick:0} ms{(skipped + failed > 0 ? $"; {skipped} skipped as unsafe or main-menu patches, {failed} could not be timed" : "")}).");
            return true;
        }

        /// <summary>Take the timers off the mod patches on these methods (the server's frame loops once startup is done).</summary>
        public static void Disarm(IEnumerable<MethodBase> originals)
        {
            if (_harmony == null) return;
            var off = new List<MethodBase>();
            foreach (var original in originals)
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch { continue; }
                if (info == null) continue;
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    var m = p.PatchMethod;
                    if (m == null || !ModOf.ContainsKey(m)) continue;
                    try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); off.Add(m); } catch { }
                    ModOf.Remove(m);
                }
            }
            HarmonyCopies.FreeUnused(off, _log);
        }

        /// <summary>Every patch method that patches a main menu (FejdStartup) method: never hooked here.</summary>
        private static HashSet<MethodBase> MainMenuPatches()
        {
            var set = new HashSet<MethodBase>();
            try
            {
                foreach (var original in Harmony.GetAllPatchedMethods())
                {
                    if (original.DeclaringType != typeof(FejdStartup)) continue;
                    var info = Harmony.GetPatchInfo(original);
                    if (info == null) continue;
                    foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers))
                        if (p.PatchMethod != null) set.Add(p.PatchMethod);
                }
            }
            catch (Exception e) { _log.LogDebug($"Could not list main menu patches: {e.Message}"); }
            return set;
        }
        private static void Prefix(out bool __state)
        {
            __state = LoadSteps.OnMainThread && _depth < PStart.Length;
            if (!__state) return;
            int sd = Math.Min(LoadSteps.Depth, LoadSteps.DoneAtDepth.Length - 1);
            PStepDepth[_depth] = sd;
            PNested[_depth] = LoadSteps.DoneAtDepth[sd];
            PStart[_depth++] = Stopwatch.GetTimestamp();
        }

        private static void Finalizer(MethodBase __originalMethod, bool __state)
        {
            if (!__state) return;
            int d = --_depth;
            long end = Stopwatch.GetTimestamp();
            // Loading steps called from inside this patch are timed as their own steps: take them out.
            long nested = LoadSteps.DoneAtDepth[PStepDepth[d]] - PNested[d];
            double self = (end - PStart[d] - nested) * MsPerTick;
            if (self < 0.5 && LoadSteps.InFrameStep) return; // a mod patch on a frame loop: only the slow calls are kept
            if (Events.Count < 20000 && ModOf.TryGetValue(__originalMethod, out var mod))
                Events.Add(new Event { Step = LoadSteps.CurrentLabel ?? "outside the timed steps", Mod = mod, Start = PStart[d], SelfMs = Math.Max(0, self) });
        }

        /// <summary>Mod patch time per step and mod for patches that started in [from, to), slowest first.</summary>
        public static List<(string step, string mod, int calls, double seconds)> Summarize(long from, long to) =>
            Events.Where(e => e.Start >= from && e.Start < to)
                  .GroupBy(e => (e.Step, e.Mod))
                  .Select(g => (step: g.Key.Step, mod: g.Key.Mod, calls: g.Count(), seconds: g.Sum(e => e.SelfMs) / 1000))
                  .OrderByDescending(x => x.seconds).ToList();

        public const string CsvHeader = "phase,time,step,mod,calls,seconds\n";

        public static string Csv(string phase, string when, List<(string step, string mod, int calls, double seconds)> rows)
        {
            var sb = new StringBuilder();
            foreach (var (step, mod, calls, seconds) in rows)
                sb.Append(phase).Append(',').Append(when).Append(',').Append(ValPerfMonitor.Csv.Q(step)).Append(',').Append(ValPerfMonitor.Csv.Q(mod)).Append(',')
                  .Append(calls).Append(',').Append(ValPerfMonitor.Csv.F(seconds, "0.000")).Append('\n');
            return sb.ToString();
        }
    }
}
