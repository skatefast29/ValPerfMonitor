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

        /// <summary>
        /// For tools that do real work in the calling frame: that frame is left out of captures, and with
        /// refuse = true the tool must not run alongside a test. Returns the error, or null to go ahead.
        /// </summary>
        private static string Busy(Plugin p, bool refuse)
        {
            p.MarkHeavy();
            string test = p.TestRunning;
            return refuse && test != null ? $"{test} is running; wait for it to finish, or stop it first (perf/stop)" : null;
        }

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
            // For this suite only: the owner's own "Run label" and "Seconds per mode" settings stay as they are.
            p.StartTimed(fullSuite: true, CaptureMode.Timing, string.IsNullOrWhiteSpace(label) ? null : label, seconds >= 15 ? seconds : 0);
            return new { success = p.Suite != null, label = p.Suite?.Label ?? "", secondsPerMode = p.Suite?.Seconds ?? 0f };
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
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            string busy = Busy(p, refuse: true);
            if (busy != null) return new { success = false, error = busy };
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
            string busy = Busy(p, refuse: true);
            if (busy != null) return new { success = false, error = busy };
            var r = WorldGenCost.Run(mod, points, p.Registry, Plugin.Guid, p.Instrumenter);
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
            if (P != null) Busy(P, refuse: false);
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
            Busy(p, refuse: false);
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

        [BridgeTool("perf/ab_test", "Find out if a mod, one of its methods, or an object really costs frame time: take the mod's Harmony patches off (mod, optionally only those on patchesOn = Type:Method), skip one mod method (skip = Namespace.Type:Method), or switch off an object with its children (objectId from debug/find_objects, objectPath as debug/scene_tree shows it, or prefab = every loaded copy, names as in perf/objects), and put it back, in turns, measuring each turn with no probes. Stand still, or give script = a ValAgentBridge test script (a saved name as test/run_file takes it, or the script as JSON) that is run over and over in every turn (open the inventory, the map, place pieces...; it must leave things as it found them), so the cost is measured while doing something. Read the result with perf/ab_result")]
        private static object AbTestTool(string mod = "", string patchesOn = "", string skip = "", int objectId = 0, string objectPath = "", string prefab = "", float seconds = 20f, int rounds = 3, string label = "", string script = "")
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var t = p.StartAb(mod, patchesOn, skip, seconds, rounds, label, objectId, objectPath, prefab, script);
            if (t.Error != null) return new { success = false, error = t.Error };
            return new
            {
                success = true, what = t.What, turns = t.Plan.Count, secondsPerTurn = t.Seconds,
                patchesTakenOff = t.PatchCount, methodsSkipped = t.SkipCount, objectsSwitchedOff = t.ObjectCount, list = t.Describe().Take(30).ToList(),
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
                    scriptRuns = t.Script == null ? (int?)null : x.ScriptRuns,
                }).ToList(),
                script = t.Script == null ? null : new { spec = t.Script.Spec.Length > 200 ? t.Script.Spec.Substring(0, 200) + "..." : t.Script.Spec, runs = t.Script.Runs, failed = t.Script.Failed, lastFailure = t.Script.LastFailure },
                verdict = t.Verdict, details = t.VerdictLines,
            };
        }

        [BridgeTool("perf/object_costs", "Find the most expensive objects here: for a few seconds (default 20) time each object's per-frame code (game update loops and mods' Update, with the mod patches on them) per object, then rank objects and prefabs (all loaded copies together). Pauses the normal recording. Read with perf/object_costs_result; instanceIds work with debug/inspect_object and perf/ab_test objectId")]
        private static object ObjectCostsTool(float seconds = 20f)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var oc = p.StartObjectCosts(seconds);
            if (oc.Error != null) return new { success = false, error = oc.Error };
            return new { success = true, seconds = oc.Seconds, expectedSeconds = Math.Round(oc.Seconds + 8, 0), note = "read the result with perf/object_costs_result" };
        }

        [BridgeTool("perf/object_costs_result", "Result of the running or last perf/object_costs: most expensive prefabs (all copies together) and single objects, ms of main-thread time per frame, with their parts, distance and instanceId")]
        private static object ObjectCostsResult(int count = 20)
        {
            var oc = ObjectCosts.Running ?? ObjectCosts.Last;
            if (oc == null) return new { success = false, error = "no object costs recording this session" };
            if (!oc.Done) return new { success = true, done = false, status = oc.Status };
            int n = Math.Max(1, count);
            return new
            {
                success = oc.Error == null, error = oc.Error, done = true, methodsTimed = oc.MethodsTimed, frames = oc.Frames,
                totalMsPerFrame = Math.Round(oc.TotalMs, 3), objects = oc.Objects.Count,
                prefabs = oc.Prefabs.Take(n).Select(x => new { x.Prefab, copies = x.Objects, msPerFrame = Math.Round(x.Ms, 4), worstCopyMs = Math.Round(x.MaxMs, 4), modCode = x.Mods }).ToList(),
                single = oc.Objects.Take(n).Select(x => new
                {
                    x.Prefab, x.InstanceId, msPerFrame = Math.Round(x.Ms, 4), callsPerFrame = Math.Round(x.CallsPerFrame, 1),
                    distanceM = double.IsNaN(x.DistanceM) ? (double?)null : x.DistanceM, x.Path,
                    parts = x.Parts.Select(t => $"{t.part} {t.ms:0.0000}").ToList(), modCode = x.Mods,
                }).ToList(),
                note = "Self time of per-frame methods per object, including the mod patches on them; the timers add a little to every call, so compare objects with each other. Mod patches on game methods are inside the game method's part (perf/ab_test with a mod shows those).",
            };
        }

        [BridgeTool("perf/log_errors", "Errors and warnings in the BepInEx log this session, per mod (a mod's own log name, or for game/Unity errors the first mod in the stack trace), and the most repeated messages with their stack. Errors repeated every frame cost CPU")]
        private static object LogErrors(int count = 15)
        {
            var p = P;
            var w = LogWatch.Instance;
            if (p == null || w == null) return new { success = false, error = "monitor not started" };
            p.WriteLogErrors();
            double minutes = Math.Max(1.0 / 60, LogWatch.Now / 60);
            return new
            {
                success = true, errors = w.Errors, warnings = w.Warnings, errorsPerMinute = Math.Round(w.Errors / minutes, 2),
                byMod = w.ByMod(p.Registry).Take(Math.Max(1, count)).Select(r => new
                {
                    r.Mod, r.Errors, r.Warnings, r.Exceptions, lastSeenSecondsAgo = Math.Round(LogWatch.Now - r.LastSeen, 0),
                    example = r.Top?.Message,
                }).ToList(),
                messages = w.TopMessages(p.Registry, Math.Max(1, count)).Select(x => new
                {
                    x.mod, logName = x.entry.Source, x.entry.Level, x.count, message = x.entry.Message,
                    perMinute = Math.Round(x.count / minutes, 1),
                    stack = x.entry.Stack.Length == 0 ? null : x.entry.Stack.Split('\n').Take(5).ToList(),
                }).ToList(),
                distinctMessagesNotKept = w.Dropped,
                logLines = w.Lines,
                logWriteMsPerMinute = LogWatch.Timed ? Math.Round(LogWatch.TicksToMs(LogWatch.MainThreadTicks) / minutes, 1) : (double?)null,
                floods = w.Floods().Take(Math.Max(1, count)).Select(f => new
                {
                    mod = LogWatch.FloodOwner(f, p.Registry), logName = f.Source, level = f.Level, message = f.Message,
                    peakPerSecond = f.PeakPerSecond, secondsFlooding = f.FloodSeconds, lines = f.Total,
                    firstSecondsAgo = Math.Round(LogWatch.Now - f.FirstFlood, 0), lastSecondsAgo = Math.Round(LogWatch.Now - f.LastFlood, 0),
                }).ToList(),
                floodNote = $"A flood = one message written more than {LogWatch.FloodThreshold} times in one second. logWriteMsPerMinute = main-thread time BepInEx spent writing log lines (Unity's own cost of building errors comes on top).",
                file = System.IO.Path.Combine(p.SessionFolder, "log_errors.csv"),
            };
        }

        [BridgeTool("perf/relog_test", "Start a relog test here (in a world): this is cycle 0. Then log out and back in a few times (world/logout + world/enter, or examples/relog-test.json); after each login, once the world has settled (settleSeconds), a cycle is recorded: error counts, game objects, materials and (census = true) a memory census. Before logging out again, wait until perf/relog_result says busy = false. Read the result with perf/relog_result")]
        private static object RelogTestTool(bool census = true, int settleSeconds = 10)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            Busy(p, refuse: false);
            if (!p.WorldReady) return new { success = false, error = "not in a world yet (or the world is still loading)" };
            if (p.TestRunning != null) return new { success = false, error = $"{p.TestRunning} is running; stop it first (perf/stop)" };
            RelogTest.Running = new RelogTest(census, settleSeconds);
            return new { success = true, census, settleSeconds = RelogTest.Running.SettleSeconds, note = "cycle 0 is recorded now; wait for perf/relog_result busy = false, then log out and back in, wait again, and so on. Two relogs or more are needed to see what grows every cycle." };
        }

        [BridgeTool("perf/relog_result", "Result of the relog test: busy (true = a cycle is being recorded; don't log out yet), each cycle (game objects, materials, live memory, full GC ms), errors and warnings that first appeared after the first logout with their count per cycle, and static fields, component types and Unity objects that grew in every cycle (the signs of state a mod keeps across logouts)")]
        private static object RelogResult(int count = 15)
        {
            var p = P;
            var t = RelogTest.Running;
            if (p == null || t == null) return new { success = false, error = "no relog test this session (perf/relog_test)" };
            int n = Math.Max(1, count);
            var statics = t.GrowingEveryCycle(unity: false);
            var unity = t.GrowingEveryCycle(unity: true);
            return new
            {
                success = true, busy = t.Busy, status = t.Status, logouts = t.Logouts,
                cycles = t.Cycles.Select(c => new
                {
                    c.Number, sessionSeconds = Math.Round(c.SessionSeconds, 0), objectsLoaded = c.ObjectsLoaded, gameObjects = c.GameObjects, materials = c.Materials,
                    liveMbAfterGc = Round(c.MonoUsedMb), fullGcMs = Round(c.GcMs), censusObjects = c.CensusObjects < 0 ? (long?)null : c.CensusObjects, recorded = c.Done,
                }).ToList(),
                newErrorsAfterRelog = t.NewErrors(p.Registry).Take(n).Select(x => new
                {
                    x.mod, logName = x.entry.Source, x.entry.Level, message = x.entry.Message, total = x.entry.Count, perCycle = x.perCycle, x.whenFirst,
                    stack = x.entry.Stack.Length == 0 ? null : x.entry.Stack.Split('\n').Take(5).ToList(),
                }).ToList(),
                staticsGrowingEveryCycle = statics.Take(n).Select(x => { var (o, r) = MemoryCensus.SplitKey(x.name); return new { owner = o, root = r, objectsPerCycle = x.counts }; }).ToList(),
                unityGrowingEveryCycle = unity.Take(n).Select(x => new { what = x.name, perCycle = x.counts }).ToList(),
                summary = RelogSummary(t, statics),
                note = "perCycle: how many times each message was logged from that cycle's login to the next (the last one up to now). whenFirst 'while playing' is the interesting kind; 'during logout or loading' is often normal. Growth needs cycle 0 and at least two relogs with a census.",
            };
        }

        private static System.Collections.Generic.List<string> RelogSummary(RelogTest t, System.Collections.Generic.List<(string name, System.Collections.Generic.List<long> counts)> statics)
        {
            var l = new System.Collections.Generic.List<string>();
            var done = t.Cycles.Where(c => !double.IsNaN(c.MonoUsedMb)).ToList();
            if (done.Count >= 2)
            {
                double per = (done[done.Count - 1].MonoUsedMb - done[0].MonoUsedMb) / (done[done.Count - 1].Number - done[0].Number);
                l.Add($"Live memory after a full clean-up: {string.Join(" -> ", done.Select(c => c.MonoUsedMb.ToString("0", Csv.Inv)).ToArray())} MB ({(per >= 0 ? "+" : "")}{per.ToString("0", Csv.Inv)} MB per relog); full garbage collection {string.Join(" -> ", done.Select(c => c.GcMs.ToString("0", Csv.Inv)).ToArray())} ms.");
            }
            foreach (var s in statics.Take(5))
            {
                var (o, r) = MemoryCensus.SplitKey(s.name);
                l.Add($"{o}: {r} grew every relog ({string.Join(" -> ", s.counts.Select(x => x.ToString("N0", Csv.Inv)).ToArray())} objects).");
            }
            return l;
        }

        [BridgeTool("perf/first_opens", "First-open stutter: for the inventory, the map, the build menu, crafting stations and containers, the frame time of the first open this session against later opens (median), and the time of the opening code itself (game plus mod patches). Also opens that were slow again as the first after a relog")]
        private static object FirstOpensTool()
        {
            if (!FirstOpens.Active) return new { success = false, error = "first-open timing is not active (see the log)" };
            return new
            {
                success = true,
                windows = FirstOpens.Rows().Select(r => new
                {
                    window = r.Kind, opens = r.Count,
                    firstFrameMs = Math.Round(r.First.FrameMs, 1), firstCallMs = Math.Round(r.First.CallMs, 1),
                    laterMedianFrameMs = Round(r.LaterMedianFrameMs), laterMedianCallMs = Round(r.LaterMedianCallMs),
                    firstAtSessionSeconds = Math.Round(r.First.SessionSeconds, 0),
                }).ToList(),
                firstAfterRelog = FirstOpens.FirstAfterRelog().Select(o => new { window = o.Kind, frameMs = Math.Round(o.FrameMs, 1), callMs = Math.Round(o.CallMs, 1), sessionSeconds = Math.Round(o.SessionSeconds, 0) }).ToList(),
                note = "frameMs = the slowest of the frame the window opened in and the two after; callMs = the opening method itself, including mod patches on it. A first open far slower than later ones is a one-off load (icons, lists) that could be done earlier. Every open: first_opens.csv in the session folder.",
            };
        }

        [BridgeTool("perf/alarms", "Live watch state: pathfinding tiles and links against Unity's limit, the latest memory sample (game objects, materials, loaded objects), log floods, and recent events (teleports, logins, season changes, ValAgentBridge commands) that slow frames are labelled with")]
        private static object AlarmsTool(int events = 30)
        {
            var p = P;
            if (p == null || p.Alarms == null) return new { success = false, error = "monitor not started" };
            var a = p.Alarms;
            var m = p.Memory.Samples.LastOrDefault();
            return new
            {
                success = true,
                pathfinding = a.Tiles < 0 ? null : new
                {
                    tiles = a.Tiles, links = a.Links, linksWaitingRemoval = a.LinksQueued, tilesWaitingRemoval = a.TilesQueued,
                    pctOfLimit = Math.Round(100.0 * (a.Links + a.LinksQueued) / Alarms.LinkLimit, 1), limit = Alarms.LinkLimit,
                },
                memory = m == null ? null : new
                {
                    secondsAgo = Math.Round(p.SessionSeconds - m.SessionSeconds, 0), m.InWorld, objectsLoaded = m.Instances, gameObjects = m.GameObjects,
                    materials = m.Materials, textures = m.Textures, meshes = m.Meshes, monoUsedMb = Round(m.MonoUsedMb),
                },
                logWriteMsLastSecond = Math.Round(a.LastSecondLogMs, 1),
                floods = LogWatch.Instance?.Floods().Take(10).Select(f => new { mod = LogWatch.FloodOwner(f, p.Registry), f.Message, f.PeakPerSecond, lines = f.Total }).ToList(),
                events = Events.Last(Math.Max(1, events)).Select(e => new { e.Kind, e.Detail, secondsAgo = Math.Round(UnityEngine.Time.realtimeSinceStartup - e.At, 1) }).ToList(),
                files = new[] { "events.csv", "pathfinding.csv", "memory.csv" }.Select(f => System.IO.Path.Combine(p.SessionFolder, f)).ToList(),
            };
        }

        [BridgeTool("perf/session_logs", "Past game sessions from their saved logs (newest first): when each ended, whether it closed normally, did not (froze, Task Manager) or crashed, and its errors and warnings with the log names that had the most errors. search = text to find in those logs (matching lines). log = a file name (or 'latest') to read its last lines. Also the report of the last session that did not close normally")]
        private static object SessionLogsTool(int count = 10, string search = "", string log = "", int lines = 40)
        {
            if (P != null) Busy(P, refuse: false); // reading saved logs can take a while
            var files = SessionCheck.SavedLogs();
            var listed = files.Take(Math.Max(1, Math.Min(50, count))).ToList();
            object matches = null;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var found = new System.Collections.Generic.List<object>();
                int total = 0;
                foreach (var f in listed)
                {
                    int n = 0;
                    foreach (var line in SessionCheck.ReadLines(f.FullName))
                    {
                        n++;
                        if (line.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        total++;
                        if (found.Count < 60) found.Add(new { file = f.Name, line = n, text = line.Length > 300 ? line.Substring(0, 300) : line });
                    }
                }
                matches = new { total, shown = found.Count, lines = found };
            }
            object tail = null;
            if (!string.IsNullOrWhiteSpace(log))
            {
                var f = log.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase) ? files.FirstOrDefault()
                      : files.FirstOrDefault(x => x.Name.Equals(log.Trim(), StringComparison.OrdinalIgnoreCase) || x.Name.StartsWith(log.Trim(), StringComparison.OrdinalIgnoreCase));
                if (f == null) return new { success = false, error = $"no saved log '{log}'" };
                var all = SessionCheck.ReadLines(f.FullName).ToList();
                int take = Math.Max(1, Math.Min(300, lines));
                tail = new { file = f.Name, totalLines = all.Count, lastLines = all.Skip(Math.Max(0, all.Count - take)).ToList() };
            }
            string crash = null;
            try { if (System.IO.File.Exists(SessionCheck.CrashReportPath)) crash = System.IO.File.ReadAllText(SessionCheck.CrashReportPath); } catch { }
            return new
            {
                success = true, folder = SessionCheck.LogFolder, savedLogs = files.Count,
                sessions = listed.Select(f => SessionCheck.Summarize(f)).Select(s => new
                {
                    s.File, ended = s.Ended.ToString("yyyy-MM-dd HH:mm:ss"), s.Status, s.SizeKb, s.Errors, s.Warnings,
                    mostErrors = s.ErrorSources.Select(e => $"{e.name} ({e.errors})").ToList(),
                }).ToList(),
                search = matches, log = tail,
                lastCrashThisLaunch = SessionCheck.LastCrash != null, likelyMod = SessionCheck.LikelyMod,
                lastCrashReport = crash,
                note = "Errors here are per log name: game and Unity errors show as 'Unity Log'; perf/log_errors splits the current session by mod. Logs saved before ValPerfMonitor 1.1.0 are not checked for crashes.",
            };
        }

        [BridgeTool("perf/memory_census", "Count what keeps the managed heap full: every object reachable from static fields and from all components/ScriptableObjects, grouped by mod, by root (one static field or one component type) and by object type, plus Harmony's copies of patched methods split by patching mod, then time a full garbage collection. Each census is compared with the previous one this session (what grew: leak finder). Static fields are read without running any setup code. The game stutters for a few seconds (up to budgetMs per frame); stand still. Read with perf/memory_census_result")]
        private static object MemoryCensusTool(int budgetMs = 100)
        {
            var p = P;
            if (p == null) return new { success = false, error = "monitor not started" };
            var mc = p.StartMemoryCensus(budgetMs);
            if (mc.Error != null) return new { success = false, error = mc.Error };
            return new { success = true, budgetMs = mc.BudgetMs, note = "read the result with perf/memory_census_result" };
        }

        [BridgeTool("perf/memory_census_result", "Result of the running or last perf/memory_census. All sizes are MB (numbers), all counts are object counts. summary: plain-English lines. gc: full garbage collection ms. byOwner / byRoot / byType: what keeps the most live objects. harmonyByMod: Harmony's copies of patched methods per patching mod. growth: what grew since the previous census this session (null on the first)")]
        private static object MemoryCensusResult(int count = 15)
        {
            var mc = MemoryCensus.Running ?? MemoryCensus.Last;
            if (mc == null) return new { success = false, error = "no memory census this session" };
            if (!mc.Done) return new { success = true, done = false, status = mc.Status, frames = mc.Frames };
            if (mc.Error != null) return new { success = false, done = true, error = mc.Error };
            int n = Math.Max(1, count);
            return new
            {
                success = true, done = true,
                summary = mc.Plain(),
                file = mc.File,
                gc = new { medianMs = Math.Round(mc.GcMedianMs, 1), runsMs = mc.GcMs.Select(x => Math.Round(x, 1)).ToList() },
                memory = new
                {
                    monoUsedMbBefore = MemoryCensus.MbNum(mc.MonoUsedBefore), monoUsedMbAfterGc = MemoryCensus.MbNum(mc.MonoUsedAfterGc),
                    monoHeapMbBefore = MemoryCensus.MbNum(mc.MonoHeapBefore), monoHeapMbAfter = MemoryCensus.MbNum(mc.MonoHeapAfter),
                    objectsReached = mc.Objects, objectsMb = MemoryCensus.MbNum(mc.CountedBytes),
                },
                byOwner = mc.ByOwner.Take(n).Select(r => new { owner = r.Name, objects = r.Count, mb = MemoryCensus.MbNum(r.Bytes) }).ToList(),
                monitorItself = mc.Self == null ? null : new { objects = mc.Self.Count, mb = MemoryCensus.MbNum(mc.Self.Bytes) },
                byRoot = mc.ByRoot.Take(n).Select(r => new
                {
                    owner = r.Owner, root = r.Name, objects = r.Count, mb = MemoryCensus.MbNum(r.Bytes),
                    topTypes = r.Types.Select(t => new { t.type, objects = t.count, mb = MemoryCensus.MbNum(t.bytes) }).ToList(),
                }).ToList(),
                byType = mc.ByType.Take(n).Select(t => new { t.type, objects = t.count, mb = MemoryCensus.MbNum(t.bytes) }).ToList(),
                harmonyByMod = mc.PatchedMethods == 0 ? null : new
                {
                    patchedMethods = mc.PatchedMethods, objects = mc.PatchObjects, mb = MemoryCensus.MbNum(mc.PatchBytes),
                    mods = mc.HarmonyByMod.Take(n).Select(x => new { mod = x.mod, methods = x.methods, objects = x.count, mb = MemoryCensus.MbNum(x.bytes) }).ToList(),
                    note = "A method patched by several mods is shared out evenly between them. '" + MemoryCensus.SelfPatches + "' is the monitor's own cost and goes away without it.",
                },
                growth = !mc.HasPrevious ? null : new
                {
                    minutesSincePrevious = Math.Round(mc.PrevMinutesAgo, 1),
                    objectsChange = mc.Objects - mc.PrevObjects,
                    liveMbChange = MemoryCensus.MbNum(mc.MonoUsedAfterGc - mc.PrevUsedAfterGc),
                    gcMsBefore = Math.Round(mc.PrevGcMs, 1), gcMsNow = Math.Round(mc.GcMedianMs, 1),
                    byOwner = mc.OwnerGrowth.Take(n).Select(g => new { owner = g.name, objectsAdded = g.count, mbAdded = MemoryCensus.MbNum(g.bytes) }).ToList(),
                    byRoot = mc.RootGrowth.Take(n).Select(g => { var (o, r) = MemoryCensus.SplitKey(g.name); return new { owner = o, root = r, objectsAdded = g.count, mbAdded = MemoryCensus.MbNum(g.bytes) }; }).ToList(),
                    note = "Only what grew. A root never shows more than its owner grew (objects can move between roots of one owner between censuses). Growth in every census while standing in one place suggests a leak; one-off growth is usually objects loading as the player moves.",
                },
                unity = mc.Unity == null ? null : new
                {
                    gameObjects = mc.Unity.GameObjects, switchedOff = mc.Unity.Inactive, hidden = mc.Unity.Hidden,
                    materials = mc.Unity.Materials, materialCopies = mc.Unity.MaterialCopies,
                    byScene = mc.Unity.ByScene.Take(n).Select(x => new { scene = x.name, gameObjects = x.count }).ToList(),
                    byRoot = mc.Unity.ByRoot.Take(n).Select(x => new { root = x.name, gameObjects = x.count }).ToList(),
                    byMaterial = mc.Unity.ByMaterial.Take(n).Select(x => new { material = x.name, count = x.count }).ToList(),
                    growth = !mc.Unity.HasPrevious ? null : new
                    {
                        byScene = mc.Unity.SceneGrowth.Take(n).Select(x => new { scene = x.name, added = x.count }).ToList(),
                        byRoot = mc.Unity.RootGrowth.Take(n).Select(x => new { root = x.name, added = x.count }).ToList(),
                        byMaterial = mc.Unity.MaterialGrowth.Take(n).Select(x => new { material = x.name, added = x.count }).ToList(),
                    },
                    note = "Unity's own objects (not in the managed counts above). Copies are grouped with their original ((Clone)/(Instance) left out). Growth while the world stays the same is a leak.",
                },
                bigObjects = mc.Big.Take(n).Select(b => new { totalMb = MemoryCensus.MbNum(b.bytes), what = b.what }).ToList(), // objects of 4 MB or more; "N x" = N of one type under one root
                census = new
                {
                    staticsRead = mc.StaticsRead, staticsNote = mc.StaticsNote, staticFields = mc.StaticRoots, typesNotSetUp = mc.TypesNotSetUp,
                    components = mc.ComponentObjects, readErrors = mc.Errors, errorSamples = mc.ErrorSamples, unreadable = mc.Unreadable, unreadableSamples = mc.UnreadableSamples,
                    workSeconds = Math.Round(mc.SliceMsTotal / 1000, 1), frames = mc.Frames, longestFrameMs = Math.Round(mc.MaxSliceMs, 0), longestFrameAt = mc.MaxSliceAt, gcsDuringWalk = mc.GcsDuringWalk,
                },
                note = "Sizes are approximate. Each object counts once, under the first root that reaches it (game and engine first, then libraries, then mods). Lists are sorted by object count, which is what makes a full garbage collection slow.",
            };
        }

        private static double? Round(double v) => double.IsNaN(v) ? (double?)null : Math.Round(v, 2);
    }
}
