using System;
using System.Linq;

namespace ValPerfMonitor
{
    /// <summary>
    /// Commands for ValAgentBridge (an AI-agent test bridge, separate mod), so a test can be run and read without
    /// pressing keys. ValAgentBridge finds methods with this attribute by name; this mod doesn't reference it, and
    /// without ValAgentBridge installed (or when the game wasn't started through it) nothing here is ever called.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class BridgeToolAttribute : Attribute
    {
        public BridgeToolAttribute(string name, string description) { Name = name; Description = description; }
        public string Name { get; }
        public string Description { get; }
    }

    internal static class BridgeCommands
    {
        private static Plugin P => Plugin.Instance;

        [BridgeTool("perf/status", "What ValPerfMonitor is doing: capture, mode, suite step, seconds counted, warm-up")]
        private static object Status()
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var c = p.Current;
            return new
            {
                success = true,
                recording = c != null,
                capture = c?.Number ?? 0,
                mode = c?.Mode.ToString() ?? "",
                label = c?.Label ?? "",
                secondsCounted = c == null ? 0 : Math.Round(c.MeasuredSeconds, 1),
                suite = p.Suite == null ? "" : $"step {p.Suite.Step + 1}/{p.Suite.Steps.Count}",
                abTest = p.Ab == null ? "" : p.Ab.Status,
                warmingUp = p.WarmingUp,
                worldReady = p.WorldReady,
                results = p.Results.Count,
            };
        }

        [BridgeTool("perf/start_suite", "Run the full suite (Baseline, Timing, Allocations, Trace) at the current spot with this label")]
        private static object StartSuite(string label = "", float seconds = 60f)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            if (!p.WorldReady) return new { success = false, error = "not in a world yet (or the world is still loading)" };
            if (label.Length > 0) p.CfgLabel.Value = label;
            if (seconds >= 15) p.CfgSuiteSeconds.Value = seconds;
            p.StartTimed(fullSuite: true, CaptureMode.Timing);
            return new { success = p.Suite != null, label = p.CfgLabel.Value, secondsPerMode = p.CfgSuiteSeconds.Value };
        }

        [BridgeTool("perf/split_startup", "On the next launch, split these mods' startup into their own methods (comma-separated names or GUIDs) and/or the N slowest mods of this launch; empty and 0 = off. Results in perf/load_times after restart")]
        private static object SplitStartup(string mods = "", int slowest = 0)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            p.CfgSplitStartupMods.Value = mods ?? "";
            p.CfgSplitStartupSlowest.Value = Math.Max(0, Math.Min(20, slowest));
            return new { success = true, mods = p.CfgSplitStartupMods.Value, slowest = p.CfgSplitStartupSlowest.Value, note = "takes effect on the next game launch" };
        }

        [BridgeTool("perf/stop", "Stop whatever is recording and save it")]
        private static object Stop()
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            p.StopTest();
            return new { success = true };
        }

        [BridgeTool("perf/results", "Captures finished this session: mode, label, FPS, 1% low, heaviest mod, report path")]
        private static object Results()
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            return new
            {
                success = true,
                captures = p.Results.Select(r => new
                {
                    r.Number, mode = r.Mode.ToString(), r.Label, seconds = Math.Round(r.Seconds, 0),
                    fps = Math.Round(r.Fps, 1), fps1Low = Math.Round(r.Fps1Low, 1),
                    modsPerFrame = double.IsNaN(r.ModsPerFrame) ? (double?)null : Math.Round(r.ModsPerFrame, 3), r.Unit, heaviest = r.Top, r.Report,
                    numbers = r.Mode == CaptureMode.Allocations ? (AllocCounter.Exact ? "exact" : "estimates: see 'How sure' in the report") : "measured",
                }).ToList(),
                compare = System.IO.Path.Combine(p.RootFolder, "compare.html"),
            };
        }

        [BridgeTool("perf/load_times", "Startup and world load times this session, with the slowest mods and steps")]
        private static object LoadTimes()
        {
            var l = P?.Loads;
            if (l == null) return new { success = false, error = "monitor not started" };
            return new
            {
                success = true,
                startupDone = l.StartupDone,
                totalToMenuSeconds = Round(l.TotalToMenu), modLoadingSeconds = Round(l.ModLoading), modsToMenuSeconds = Round(l.ModsToMenu),
                slowestMods = l.Mods.OrderByDescending(m => m.Seconds).Take(10).Select(m => new { m.Name, seconds = Round(m.Seconds) }).ToList(),
                startupSteps = l.StartupSteps.Take(10).Select(s => new { step = s.label, s.calls, seconds = Round(s.seconds) }).ToList(),
                startupModsInSteps = l.StartupStepMods.Take(20).Select(s => new { s.step, s.mod, s.calls, seconds = Round(s.seconds) }).ToList(),
                startupSplit = StartupDetail.Mods.Select(m => new
                {
                    mod = m.Name, timedMethods = m.Methods,
                    slowest = StartupDetail.Results.Where(r => r.Mod == m.Name).Take(15)
                        .Select(r => new { method = r.Name, r.Calls, totalSeconds = Round(StartupDetail.Seconds(r.Total)), ownSeconds = Round(StartupDetail.Seconds(r.Self)) }).ToList(),
                }).ToList(),
                startupSplitPending = StartupDetail.Active && !StartupDetail.Finished,
                worldLoads = l.WorldLoads.Select(w => new
                {
                    w.When, spawnSeconds = Round(w.SpawnSeconds), screenGoneSeconds = Round(w.ScreenSeconds), hitchesStoppedSeconds = Round(w.SmoothSeconds), w.Hitches,
                    steps = w.Steps.Take(10).Select(s => new { step = s.label, s.calls, seconds = Round(s.seconds) }).ToList(),
                    modsInSteps = w.StepMods.Take(20).Select(s => new { s.step, s.mod, s.calls, seconds = Round(s.seconds) }).ToList(),
                }).ToList(),
            };
        }

        [BridgeTool("perf/patches_on", "Which mods patch a game method (type and method name, e.g. ZNetScene / Awake): prefixes, postfixes, transpilers, finalizers")]
        private static object PatchesOn(string type, string method)
        {
            var t = HarmonyLib.AccessTools.TypeByName(type);
            if (t == null) return new { success = false, error = $"type {type} not found" };
            var methods = HarmonyLib.AccessTools.GetDeclaredMethods(t).Where(m => m.Name == method).ToList();
            if (methods.Count == 0) return new { success = false, error = $"{type}.{method} not found" };
            var reg = P?.Registry;
            object Describe(HarmonyLib.Patch x) => new
            {
                mod = reg?.Resolve(x.PatchMethod?.DeclaringType?.Assembly, x.owner)?.Name ?? x.owner,
                patch = $"{x.PatchMethod?.DeclaringType?.FullName}.{x.PatchMethod?.Name}",
                x.priority,
            };
            return new
            {
                success = true,
                overloads = methods.Select(m =>
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(m);
                    return new
                    {
                        signature = m.ToString(),
                        prefixes = info?.Prefixes.Select(Describe).ToList(),
                        postfixes = info?.Postfixes.Select(Describe).ToList(),
                        transpilers = info?.Transpilers.Select(Describe).ToList(),
                        finalizers = info?.Finalizers.Select(Describe).ToList(),
                    };
                }).ToList(),
            };
        }

        [BridgeTool("perf/alloc_check", "Confirm a suspected garbage source: call a method (type and method name, e.g. ZDOMan / ReleaseNearbyZDOS) many times right now and report bytes and microseconds per call. Arguments are filled from the live game; floats use timeArg: now, future or a number. The method really runs.")]
        private static object AllocCheckTool(string type, string method, int calls = 2000, string timeArg = "now")
        {
            var r = AllocCheck.Run(type, method, calls, timeArg);
            return new
            {
                success = r.Error == null,
                error = r.Error,
                r.Method, r.Instance, r.Calls, r.GcRuns,
                bytesPerCall = double.IsNaN(r.BytesPerCall) ? (double?)null : Math.Round(r.BytesPerCall, 1),
                microsPerCall = Math.Round(r.MicrosPerCall, 2),
            };
        }

        [BridgeTool("perf/worldgen_cost", "How much a world-generation mod (default Riverheim) adds to the game's alternative biome check during world load: times a sample of the same world-generator calls with the mod's world-generator patches on, then briefly off, and scales up to the full map. Patches are put back straight away")]
        private static object WorldGenCostTool(string mod = "Riverheim", int points = 100000)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var r = WorldGenCost.Run(mod, points, p.Registry, Plugin.Guid);
            double measured = double.NaN;
            var load = p.Loads?.WorldLoads.LastOrDefault();
            if (load != null)
            {
                var step = load.Steps.FirstOrDefault(s => s.label.StartsWith("Alternative biome check", StringComparison.Ordinal));
                if (step.label != null) measured = step.seconds;
            }
            return new
            {
                success = r.Error == null, error = r.Error, r.Mod, patchesTakenOff = r.PatchesTakenOff, patches = r.Patches,
                pointsSampled = r.Points, pointsInFullCheck = r.FullPoints,
                microsPerPointWithMod = Math.Round(r.MicrosWith, 3), microsPerPointWithout = double.IsNaN(r.MicrosWithout) ? (double?)null : Math.Round(r.MicrosWithout, 3),
                estimatedSecondsWithMod = Round(r.SecondsWith), estimatedSecondsWithout = Round(r.SecondsWithout),
                measuredAtLastWorldLoad = Round(measured),
                note = (r.Note + " Estimates cover only the per-point world-generator calls; the check also groups the points into regions afterwards.").Trim(),
            };
        }

        [BridgeTool("perf/objects", "Objects loaded right now, grouped by prefab, most common first, with mods whose components are on them and how many are stacked exactly on a copy of themselves. Shows what fills the scene")]
        private static object Objects(int count = 25)
        {
            var list = ObjectLoads.Loaded(Math.Max(1, count), out int total, out int stacked);
            if (list == null) return new { success = false, error = "not in a world yet" };
            return new
            {
                success = true, loaded = total, stacked,
                objects = list.Select(x => new { x.prefab, scriptsFrom = x.mods, x.count, x.stacked }).ToList(),
                note = "stacked: copies sitting exactly on another object of the same prefab (wasted).",
            };
        }

        [BridgeTool("perf/slow_objects", "Objects (prefabs) that took longest to spawn this session: count, total, average and worst ms, and mods whose components are on them. byMax=true sorts by the worst single spawn")]
        private static object SlowObjects(int count = 20, bool byMax = false)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            p.WriteSlowObjects();
            return new
            {
                success = true,
                spawned = ObjectLoads.Objects,
                objects = ObjectLoads.Top(Math.Max(1, count), byMax).Select(r => new
                {
                    r.Prefab, scriptsFrom = r.ModHint, r.Count, totalMs = Math.Round(r.TotalMs, 1), avgMs = Math.Round(r.AvgMs, 3), maxMs = Math.Round(r.MaxMs, 2), r.Over5Ms,
                    splitSpawns = r.SplitSpawns,
                    unityAndOtherAvgMs = r.SplitSpawns == 0 ? (double?)null : Math.Round(r.OtherAvgMs, 3),
                    parts = r.Parts.Take(8).Select(x => new { part = x.name, avgMs = Math.Round(x.avgMs, 3) }).ToList(),
                }).ToList(),
                note = "parts: own time per spawn of each script's startup code and each mod patch on it, only for spawns after the object was first seen slow (splitSpawns). unityAndOtherAvgMs: the rest (Unity copying the object, game code not split out).",
            };
        }

        [BridgeTool("perf/ab_test", "Find out if a mod (or one of its methods) really costs frame time: take its Harmony patches off (mod, optionally only those on patchesOn = Type:Method) or skip one mod method (skip = Namespace.Type:Method), and put them back, in turns, measuring each turn with no probes. Stand still; read the result with perf/ab_result")]
        private static object AbTestTool(string mod = "", string patchesOn = "", string skip = "", float seconds = 20f, int rounds = 3, string label = "")
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var t = p.StartAb(mod, patchesOn, skip, seconds, rounds, label);
            if (t.Error != null) return new { success = false, error = t.Error };
            return new
            {
                success = true, what = t.What, turns = t.Plan.Count, secondsPerTurn = t.Seconds,
                patchesTakenOff = t.PatchCount, methodsSkipped = t.SkipCount, list = t.Describe().Take(30).ToList(),
                expectedMinutes = Math.Round(t.Plan.Count * (t.Seconds + p.CfgSwitchWarmup.Value + 3) / 60.0, 1),
            };
        }

        [BridgeTool("perf/ab_result", "Result of the running or last A/B test: each turn and the verdict (real cost or not)")]
        private static object AbResult()
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var t = p.Ab ?? p.LastAb;
            if (t == null) return new { success = false, error = "no A/B test this session" };
            return new
            {
                success = true, what = t.What, done = t.Done, running = p.Ab != null, status = t.Status,
                turns = t.Phases.Select(x => new
                {
                    state = x.On ? "normal" : "off", x.Capture,
                    cpuMainMs = double.IsNaN(x.CpuMainMs) ? (double?)null : Math.Round(x.CpuMainMs, 3),
                    frameMs = Math.Round(x.FrameMs, 3),
                    garbageKbPerFrame = double.IsNaN(x.GarbageKbPerFrame) ? (double?)null : Math.Round(x.GarbageKbPerFrame, 2), fps1Low = Math.Round(x.Fps1Low, 1), gcPerMin = Math.Round(x.GcPerMin, 1), slowFramesPerMin = Math.Round(x.SlowPerMin, 1),
                }).ToList(),
                verdict = t.Verdict, details = t.VerdictLines,
            };
        }

        private static double? Round(double v) => double.IsNaN(v) ? (double?)null : Math.Round(v, 2);
    }
}
