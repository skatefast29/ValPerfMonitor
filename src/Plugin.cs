using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheimmods.ValPerfMonitor";
        public const string Name = "ValPerfMonitor";
        public const string Version = "1.3.0";

        internal static Plugin Instance;
        internal ModRegistry Registry;
        internal Instrumenter Instrumenter;
        internal UnityCounters Counters;
        internal GameContext Context = new GameContext();
        internal BackgroundWriter Writer;
        internal Capture Current;
        internal Suite Suite;
        internal AbTest Ab, LastAb;
        internal string SessionFolder, RootFolder, SessionName;
        internal LoadTimes Loads;
        internal NetworkStats Net;
        internal MemoryTracker Memory;
        private Overlay _overlay;
        private Harmony _harmony;
        private InputBlock _input;
        private SafetyGuard _guard;
        internal readonly List<CaptureResult> Results = new List<CaptureResult>();
        internal Alarms Alarms;
        private bool _bridgeWatched;
        internal string Toast = "";
        internal float ToastUntil;

        internal ConfigEntry<CaptureMode> CfgStartMode;
        internal ConfigEntry<string> CfgLabel, CfgExclude;
        internal ConfigEntry<OverlayMode> CfgOverlay;
        internal ConfigEntry<bool> CfgCoroutines, CfgNetwork, CfgSplitSpawns;
        internal ConfigEntry<float> CfgSplitSpawnMs;
        internal ConfigEntry<string> CfgExtraMethods, CfgSplitStartupMods;
        internal ConfigEntry<int> CfgSplitStartupSlowest;
        internal ConfigEntry<bool> CfgUnityCallbacks, CfgGameSystems, CfgRecordOnLaunch, CfgSuiteAlloc, CfgSuiteTrace, CfgSuiteHideOverlay;
        internal ConfigEntry<KeyboardShortcut> CfgToggleKey, CfgRestartKey, CfgSuiteKey, CfgOverlayKey;
        internal ConfigEntry<float> CfgInterval, CfgSummaryInterval, CfgFrameSpikeMs, CfgOwnerSpikeMs, CfgOwnerSpikeKb, CfgPatchBudgetMs, CfgTestPatchBudgetMs,
                                    CfgWarmup, CfgSwitchWarmup, CfgWorldSettle, CfgMonitorExcludeMs, CfgSuiteSeconds, CfgBenchmarkSeconds, CfgScale;
        internal ConfigEntry<float> CfgMemorySeconds, CfgLeakSeconds;
        internal ConfigEntry<int> CfgFloodLines;
        internal ConfigEntry<int> CfgWindowFrames, CfgRows, CfgFontSize, CfgPosX, CfgPosY, CfgMaxTraces;

        // Frame bookkeeping.
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();
        private long _lastBoundary, _monitorTicks;
        private bool _heavy, _lastFrameCounted;
        private int _lastGcCount;
        private float _sessionStart, _nextInterval, _nextSummary, _nextRescanCheck, _warmupUntil, _nextLiveScan = float.MaxValue;
        private bool _wasPending;
        private string[] _sourceNames = Array.Empty<string>();
        private readonly List<string> _runLines = new List<string>();
        internal long MonitorBytes;
        private int _captureNumber;
        private long _awakeTicks;
        private int _lastObjects = -1;
        private float _nextMemory;
        private bool _limiterLogged;

        /// <summary>This plugin's log (BaseUnityPlugin.Logger is protected).</summary>
        internal ManualLogSource Log => Logger;

        internal bool WarmingUp => Time.realtimeSinceStartup < _warmupUntil || (Instrumenter != null && Instrumenter.Pending > 0);

        // Hooking waits until a world has been loaded and stable for a few seconds (see Instrumenter.Hooked).
        private float _worldSince = -1f;
        private bool _autoStartPending;
        // Also waits (up to 30 s) until the world load stops hitching, so hooking doesn't blur the load timing.
        internal bool WorldReady => _worldSince >= 0 && Time.realtimeSinceStartup - _worldSince >= WorldSettleSeconds &&
                                    (Loads == null || !Loads.WorldLoading || Time.realtimeSinceStartup - _worldSince >= 30f);
        internal float WorldReadyIn => _worldSince < 0 ? -1 : Mathf.Max(0, WorldSettleSeconds - (Time.realtimeSinceStartup - _worldSince));
        private float WorldSettleSeconds => Mathf.Max(0f, CfgWorldSettle?.Value ?? 5f);
        internal float WarmupLeft => Mathf.Max(0, _warmupUntil - Time.realtimeSinceStartup);
        internal double SessionSeconds => Time.realtimeSinceStartup - _sessionStart;

        private void Awake()
        {
            _awakeTicks = Stopwatch.GetTimestamp();
            Instance = this;
            Probe.MainThreadId = Thread.CurrentThread.ManagedThreadId;

            CfgRecordOnLaunch = Config.Bind("1 - General", "Record on launch", true, "Start a continuous recording as soon as the game loads. Off = wait until a test is started from the panel.");
            CfgStartMode = Config.Bind("1 - General", "Capture mode on launch", CaptureMode.Timing,
                "Mode for the recording on launch and for Ctrl+F11. Baseline = no hooks, true game speed. Timing = CPU time per mod and game system. Allocations = memory allocated per mod (GC pressure). Trace = timing plus flame graphs of slow frames.");
            CfgLabel = Config.Bind("1 - General", "Run label", "", "Text stored with every capture, e.g. 'all mods' or 'without Seasons'. Shows up in the comparison page.");
            CfgOverlay = Config.Bind("1 - General", "Live overlay", OverlayMode.Off, "Live readout shown while you play (no mouse needed): Compact = top mods, Full = graph and full table. Costs a little frame time; it is measured and removed.");
            CfgOverlayKey = Config.Bind("1 - General", "Live overlay key", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftAlt), "Cycles the live overlay: Off -> Compact -> Full.");
            CfgToggleKey = Config.Bind("1 - General", "Test panel key", new KeyboardShortcut(KeyCode.F11), "Opens / closes the test panel. F11 is unused by Valheim and by the mods in this profile.");
            CfgRestartKey = Config.Bind("1 - General", "Restart capture key", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl), "Ends the current capture (saving its report) and starts a fresh one in the same mode.");
            CfgSuiteKey = Config.Bind("1 - General", "Run full suite key", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftShift), "Runs Baseline, Timing, Allocations and Trace one after another at the current spot.");
            CfgExclude = Config.Bind("1 - General", "Excluded mod GUIDs", "", "Comma-separated plugin GUIDs to leave unmeasured.");

            CfgUnityCallbacks = Config.Bind("2 - Measuring", "Measure mod Update/LateUpdate/FixedUpdate/OnGUI", true, "Also time each mod's own Unity callbacks, not only its Harmony patches.");
            CfgCoroutines = Config.Bind("2 - Measuring", "Measure mod coroutines", true, "Also time mods' coroutines (code that runs a step at a time across frames). Restart required.");
            CfgExtraMethods = Config.Bind("2 - Measuring", "Extra methods to measure", "", "Comma-separated Namespace.Type:Method entries (game or any mod) to time as well, so a slow mod method can be split into its parts, e.g. HearthBelow.VoxelMagic.VoxelZone:BuildChunkMesh. Read when probes are attached on entering a world.");
            CfgSplitSpawns = Config.Bind("2 - Measuring", "Split slow spawns into parts", true, "When an object takes long to spawn, time the startup code (Awake, OnEnable) of every script on it and every mod patch on that code from then on, so the slow part is named instead of guessed. Shown in slow_objects.csv and perf/slow_objects.");
            CfgSplitSpawnMs = Config.Bind("2 - Measuring", "Split spawns slower than ms", 2f, "Objects whose spawn took at least this long get split into parts.");
            CfgNetwork = Config.Bind("2 - Measuring", "Track network messages", true, "Count network messages (RPCs) per name and per mod, with their size. Not in Baseline.");
            CfgGameSystems = Config.Bind("2 - Measuring", "Measure game systems", true, "Also measure Valheim's own update loops (about 200 methods) so mods can be compared against the base game. Restart required.");
            CfgPatchBudgetMs = Config.Bind("2 - Measuring", "Hooking budget ms per frame", 8f, "Time per frame spent attaching probes for small top-ups while playing (mods that patch late, newly seen kinds of objects). Those frames are never counted.");
            CfgTestPatchBudgetMs = Config.Bind("2 - Measuring", "Fast hooking budget ms per frame", 40f, "Used when all probes go on at once (a few seconds after entering a world, and when a test puts them back after Baseline) and during tests. Higher = done sooner; the game stutters until hooking is done (usually under 10 s). Those frames are never counted.");
            CfgWarmup = Config.Bind("2 - Measuring", "Warm-up after hooking (seconds)", 5f, "After probes are attached, frames are not counted for this long: the game compiles each rebuilt method the first time it runs, which shows up as short hitches for a few seconds.");
            CfgSwitchWarmup = Config.Bind("2 - Measuring", "Warm-up after mode switch (seconds)", 2f, "Switching between modes that keep the same probes needs no recompiling; this just lets the switch frame and the first report write settle.");
            CfgWorldSettle = Config.Bind("2 - Measuring", "World settle (seconds)", 5f, "After spawning in a world, wait this long before attaching probes so the world finishes loading first.");
            CfgMonitorExcludeMs = Config.Bind("2 - Measuring", "Exclude frames where monitor used more than ms", 1f, "Frames where this monitor itself used more main-thread time than this are left out instead of corrected.");
            // Read by the early-start plugin on the next launch (see StartupDetail); bound here too so config managers show them.
            CfgSplitStartupMods = Config.Bind(StartupDetail.Section, StartupDetail.ModsKey, "", StartupDetail.ModsHelp);
            CfgSplitStartupSlowest = Config.Bind(StartupDetail.Section, StartupDetail.SlowestKey, 0, new ConfigDescription(StartupDetail.SlowestHelp, new AcceptableValueRange<int>(0, 20)));
            CfgWindowFrames = Config.Bind("2 - Measuring", "Overlay window frames", 300, new ConfigDescription("Recent frames the overlay averages over. Restart required.", new AcceptableValueRange<int>(60, 3600)));

            CfgInterval = Config.Bind("3 - Logging", "Interval seconds", 10f, "How often frames.csv and timeline.csv get a new row.");
            CfgSummaryInterval = Config.Bind("3 - Logging", "Summary rewrite seconds", 60f, "How often the summary CSVs are rewritten during a capture (always written when it ends).");
            CfgFrameSpikeMs = Config.Bind("3 - Logging", "Frame spike threshold ms", 50f, "Frames slower than this are logged in spikes.csv (and traced in Trace mode).");
            CfgOwnerSpikeMs = Config.Bind("3 - Logging", "Owner spike threshold ms", 2f, "A mod or game system over this in one frame counts as a spike for it (Timing/Trace).");
            CfgOwnerSpikeKb = Config.Bind("3 - Logging", "Owner spike threshold KB", 64f, "Same, for Allocations mode.");
            CfgMemorySeconds = Config.Bind("3 - Logging", "Memory check every (seconds)", 60f, "How often object counts and memory are recorded for leak tracking (memory.csv). Never during timed tests. 0 = off.");
            CfgLeakSeconds = Config.Bind("3 - Logging", "Leak check every (seconds) when not recording", 300f, "While nothing is recording, object counts and memory are still checked this often in a world (a short stutter each time), so a leak during normal play is noticed: a warning appears when game objects or materials keep growing while the world around you does not. 0 = only while recording.");
            CfgFloodLines = Config.Bind("3 - Logging", "Log flood: lines per second", 20, new ConfigDescription("Warn (log and on screen) when one message is written more often than this in one second. A mod repeating an error every frame costs frame time and fills the log.", new AcceptableValueRange<int>(5, 1000)));
            LogWatch.FloodThreshold = CfgFloodLines.Value;
            CfgFloodLines.SettingChanged += (_, __) => LogWatch.FloodThreshold = CfgFloodLines.Value;
            CfgMaxTraces = Config.Bind("3 - Logging", "Max flame graphs per capture", 40, "Trace mode saves at most this many slow frames.");
            // Read by the preloader on the next launch (it saves the log before BepInEx empties it); bound here so the switches are in this file.
            Config.Bind("3 - Logging", "Keep past session logs", true, "On the next launch, save a copy of the previous session's BepInEx log (LogOutput.log) in BepInEx\\ValPerfMonitor\\session_logs, named by when that session ended. Includes sessions that crashed. Off = BepInEx's usual single log. Needs ValPerfMonitor.Preloader.dll.");
            Config.Bind("3 - Logging", "Days to keep past session logs", 14, new ConfigDescription("Saved session logs older than this are deleted. 0 = keep them all.", new AcceptableValueRange<int>(0, 365)));

            CfgSuiteSeconds = Config.Bind("4 - Suite", "Seconds per mode", 60f, "Measured time per mode in the full suite (warm-up not included).");
            CfgSuiteAlloc = Config.Bind("4 - Suite", "Include Allocations", true, "");
            CfgSuiteTrace = Config.Bind("4 - Suite", "Include Trace", true, "");
            CfgSuiteHideOverlay = Config.Bind("4 - Suite", "Hide live overlay during timed tests", true, "Only the one-line status shows while a timed test runs. The overlay itself costs frame time and allocates memory; hiding it keeps it out of the measurement.");
            CfgBenchmarkSeconds = Config.Bind("4 - Suite", "Single test seconds", 60f, "Length of a single-mode timed test.");

            CfgRows = Config.Bind("5 - Overlay", "Rows", 15, "");
            CfgFontSize = Config.Bind("5 - Overlay", "Font size", 13, "");
            CfgScale = Config.Bind("5 - Overlay", "Scale", 1f, "Overall overlay scale (for 1440p/4K).");
            CfgPosX = Config.Bind("5 - Overlay", "Position X", 20, "");
            CfgPosY = Config.Bind("5 - Overlay", "Position Y", 80, "");

            LogWatch.Start(); // normally already started by the early-start plugin
            SessionCheck.Running();
            Application.quitting += SessionCheck.Closed;
            _harmony = new Harmony(Guid);
            LoadSteps.Attach(Guid + ".loadsteps", Logger); // before the menu loads, so its steps are timed too
            Registry = new ModRegistry(CfgWindowFrames.Value);
            _overlay = new Overlay(this);
        }

        // Start runs after every plugin's Awake, which is where most mods apply their Harmony patches.
        private void Start()
        {
            long setupStart = Stopwatch.GetTimestamp();
            RootFolder = Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor");
            SessionName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Csv.Inv);
            SessionFolder = Path.Combine(RootFolder, SessionName);
            Writer = new BackgroundWriter(Logger);
            _input = new InputBlock(Logger);
            LoadRunHistory();
            Loads = new LoadTimes(Logger, _awakeTicks);

            Registry.LoadPlugins(typeof(Plugin).Assembly);
            try { SessionCheck.Report(Registry, Logger); }
            catch (Exception e) { Logger.LogWarning($"Could not report on the last session: {e.Message}"); }
            AllocCounter.Init(Logger);
            Counters = new UnityCounters(Logger);
            var excluded = CfgExclude.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
            var guard = _guard = new SafetyGuard(RootFolder, Logger);
            guard.Prime();
            Instrumenter = new Instrumenter(_harmony, Registry, Logger, typeof(Plugin).Assembly, excluded, guard) { IncludeCoroutines = CfgCoroutines.Value, ExtraMethods = CfgExtraMethods.Value };
            CfgExtraMethods.SettingChanged += (_, __) => Instrumenter.ExtraMethods = CfgExtraMethods.Value;
            ObjectLoads.Attach(Guid + ".objectloads", Registry, Logger);
            SpawnParts.Init(Guid + ".spawnparts", Registry, guard, Logger);
            LoadPatches.Init(Guid + ".loadpatches", Guid, Registry, guard, Logger);
            LoadPatches.Arm(atMainMenu: false); // before the main menu loads, so startup can be split by mod too
            SpawnParts.Enabled = CfgSplitSpawns.Value;
            SpawnParts.ThresholdMs = CfgSplitSpawnMs.Value;
            CfgSplitSpawns.SettingChanged += (_, __) => SpawnParts.Enabled = CfgSplitSpawns.Value;
            CfgSplitSpawnMs.SettingChanged += (_, __) => SpawnParts.ThresholdMs = CfgSplitSpawnMs.Value;
            AbTest.CheckLeftover(RootFolder, Logger);
            CpuClock.Init(Logger);
            Net = new NetworkStats(Registry, Logger);
            Memory = new MemoryTracker(Registry);
            Alarms = new Alarms(this);
            FirstOpens.Attach(Guid + ".firstopens", Logger);
            Instrumenter.Calibrate();
            double probeBytes = Instrumenter.ChildOverhead[(int)CaptureMode.Allocations];
            if (AllocCounter.Exact && probeBytes > 8)
                Logger.LogWarning($"Each probe call allocates about {probeBytes:0} bytes on this runtime. Per-mod numbers are corrected for it, " +
                                  "but it adds garbage collection: use Baseline captures for GC rates.");
            int queued = Instrumenter.Scan(CfgUnityCallbacks.Value, CfgGameSystems.Value);
            Logger.LogInfo($"Found {Registry.Visible.Count(m => m.IsPlugin)} plugins, {Instrumenter.PatchedTargets} game methods patched by mods; " +
                           $"{queued} methods to measure ({Instrumenter.GameMethods} of them game systems). Logs: {SessionFolder}");

            LoadSteps.Add("ValPerfMonitor setup (this tool, not in normal play)", setupStart, Stopwatch.GetTimestamp());
            _sessionStart = Time.realtimeSinceStartup;
            _nextInterval = _sessionStart + CfgInterval.Value;
            _nextSummary = _sessionStart + CfgSummaryInterval.Value;
            _nextRescanCheck = _sessionStart + 15f;
            _nextLiveScan = _sessionStart + 60f;
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (from, to) =>
            {
                _nextLiveScan = Mathf.Min(_nextLiveScan, Time.realtimeSinceStartup + 10f); // new scene: new kinds of objects
                Loads.OnSceneChanged(to.name, _lastBoundary);
            };
            _nextMemory = _sessionStart + 30f;
            _lastGcCount = GC.CollectionCount(0);
            Probe.Recording = false; // on while a capture runs (StartCapture), off when idle (GoIdle)
            _autoStartPending = CfgRecordOnLaunch.Value;
            StartCoroutine(FrameLoop());
        }

        // ---- Per frame --------------------------------------------------------------------------

        private IEnumerator FrameLoop()
        {
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            while (true)
            {
                yield return _endOfFrame;
                long t0 = Stopwatch.GetTimestamp();
                long a0 = AllocCounter.Exact ? Probe.ReadAllocated() : 0;
                long frameStart = _lastBoundary;
                _lastBoundary = t0;
                int gcNow = GC.CollectionCount(0);
                bool gc = gcNow != _lastGcCount;
                _lastGcCount = gcNow;

                _lastFrameCounted = false;
                float rawMs = frameStart != 0 ? (float)((t0 - frameStart) * msPerTick) : 0f;
                float monitorMs = (float)(_monitorTicks * msPerTick);
                bool monitorBusy = _heavy || WarmingUp || monitorMs > CfgMonitorExcludeMs.Value;
                // Frames in which ValAgentBridge ran a command are test tooling, not play: left out, like the monitor's own work.
                bool bridge = Events.TakeBridgeFrame(out string bridgeName);
                if (Ab != null && Ab.Script != null) bridge = false; // the A/B test's own action script: those frames are what it measures
                if (bridge && Current != null && Probe.Recording && frameStart != 0) Current.LeftOutForBridge(bridgeName, rawMs);
                int objects = GameContext.LoadedObjects();
                int objectsChanged = objects >= 0 && _lastObjects >= 0 ? Math.Abs(objects - _lastObjects) : 0;
                _lastObjects = objects;
                if (frameStart != 0 && Current != null && Probe.Recording)
                {
                    // A GC while this monitor was writing or doing heavy work may have been caused by it: don't count it.
                    // Frames while the test panel is open are never counted (nobody is playing, and the UI is heavy).
                    bool counted = !WarmingUp && !_heavy && !bridge && !_overlay.PanelOpen && monitorMs <= CfgMonitorExcludeMs.Value && !(gc && (_heavy || Writer.Busy));
                    var sample = new FrameSample
                    {
                        RawMs = rawMs, MonitorMs = monitorMs, Counted = counted, Gc = gc,
                        SessionSeconds = SessionSeconds, Context = Context.Short, ObjectsChanged = objectsChanged,
                        Saving = ZNet.instance != null && ZNet.instance.IsSaving(),
                        Teleporting = Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting(),
                    };
                    _lastFrameCounted = counted;
                    if (counted)
                        Counters.Sample();
                    var mode = Current.Mode;
                    var spike = Current.Stats.EndFrame(sample, CfgFrameSpikeMs.Value,
                        mode == CaptureMode.Allocations ? CfgOwnerSpikeKb.Value : CfgOwnerSpikeMs.Value);
                    if (spike.HasValue && mode == CaptureMode.Trace && Current.TracesWritten < CfgMaxTraces.Value)
                    {
                        RefreshSourceNames();
                        string file = Current.QueueTrace(sample.SessionSeconds, frameStart, t0, spike.Value.CorrectedMs, _sourceNames);
                        var list = Current.Stats.PendingSpikes;
                        var last = list[list.Count - 1];
                        last.TraceFile = file;
                        list[list.Count - 1] = last;
                    }
                }
                FirstOpens.Frame(rawMs);
                if (Current != null && Current.Mode == CaptureMode.Allocations)
                    GarbageConfidence.Frame(_lastFrameCounted && !gc, rawMs);
                Ab?.Frame(_lastFrameCounted && !gc);
                Probe.CommitSources(_lastFrameCounted && !(gc && Current != null && Current.Stats.DropsGcFrames));
                Probe.ClearFrame();
                _heavy = false;
                if (frameStart != 0)
                {
                    try { Loads.Tick(t0, rawMs, monitorBusy, StartupTimed, WorldLoadTimed, LogoutTimed); }
                    catch (Exception e) { Logger.LogDebug($"Load timing: {e.Message}"); }
                }
                // This bookkeeping happened after the boundary, so it belongs to the next frame's monitor time.
                _monitorTicks = Stopwatch.GetTimestamp() - t0;
                if (AllocCounter.Exact) MonitorBytes += Probe.ReadAllocated() - a0;
            }
        }

        private void Update()
        {
            if (Instrumenter == null)
                return;
            long t0 = Stopwatch.GetTimestamp();
            long a0 = AllocCounter.Exact ? Probe.ReadAllocated() : 0;
            try { Tick(); }
            catch (Exception e) { Logger.LogError(e); }
            _monitorTicks += Stopwatch.GetTimestamp() - t0;
            if (AllocCounter.Exact) MonitorBytes += Probe.ReadAllocated() - a0;
        }

        private void Tick()
        {
            if (CfgSuiteKey.Value.IsDown())
            {
                if (Suite != null) StopTest();
                else StartTimed(fullSuite: true, CaptureMode.Timing);
            }
            else if (CfgRestartKey.Value.IsDown())
                RestartCapture();
            else if (CfgOverlayKey.Value.IsDown())
            {
                SetOverlay((OverlayMode)(((int)CfgOverlay.Value + 1) % 3));
            }
            else if (CfgToggleKey.Value.IsDown())
                SetPanel(!_overlay.PanelOpen);

            bool inWorld = Player.m_localPlayer != null && ZNetScene.instance != null;
            if (!inWorld)
            {
                // A test can't go on without the world it measures (logout, death): stop it and keep what it recorded.
                if (_worldSince >= 0 && TestRunning != null)
                {
                    Logger.LogInfo($"Left the world (logout or death) during {TestRunning}: stopped.");
                    StopTest();
                    ShowToast("Left the world: the test was stopped; what was recorded is saved.");
                }
                _worldSince = -1f;
            }
            else if (_worldSince < 0) _worldSince = Time.realtimeSinceStartup;
            if (_autoStartPending && WorldReady)
            {
                _autoStartPending = false;
                StartCapture(CfgStartMode.Value, CfgLabel.Value);
                ShowToast($"ValPerfMonitor: recording {Current?.Mode}. F11 opens the test panel.");
            }

            // At the main menu, before any world loads: also time mod patches added since startup.
            if (!LoadPatches.Armed && FejdStartup.instance != null && ZNet.instance == null && LoadPatches.Arm(atMainMenu: true))
                _heavy = true;

            float budget = Instrumenter.FullRound || Suite != null || Ab != null ? Mathf.Max(CfgPatchBudgetMs.Value, CfgTestPatchBudgetMs.Value) : CfgPatchBudgetMs.Value;
            if (Instrumenter.Drain(budget))
            {
                _heavy = true;
                _wasPending = true;
            }
            else if (_wasPending)
            {
                // Hooking just finished: the rebuilt methods still need to be JIT-compiled on first use.
                _wasPending = false;
                BeginWarmup();
            }
            // Splitting slow spawns into parts: hooks a few startup methods at a time, after the main probes.
            // Not during tests or Baseline: new hooks mid-test would make its turns or steps run different code.
            if (Instrumenter.Pending == 0 && TestRunning == null && (Current == null || Current.Mode != CaptureMode.Baseline) && SpawnParts.Drain(budget))
                _heavy = true;

            float now = Time.realtimeSinceStartup;
            Alarms.Tick();
            RelogTest.Running?.Tick(this, inWorld);
            if (now >= _nextRescanCheck)
            {
                if (!_bridgeWatched) _bridgeWatched = Events.WatchBridge(new Harmony(Guid + ".events"), Logger);
                // Some mods patch late (on world load, on first use). Pick those up too.
                _nextRescanCheck = now + 15f;
                if (Instrumenter.Hooked && Instrumenter.Pending == 0 && Instrumenter.PatchSetChanged())
                {
                    _heavy = true;
                    int queued = Instrumenter.Scan(false, false);
                    if (queued > 0) Logger.LogInfo($"Rescan found {queued} new mod methods to measure.");
                }
            }

            // Every minute while recording: pick up classes that only now have live objects (new scene, biome,
            // creatures), and record object counts for leak tracking. Both walk every live object, so they share one
            // list. Not during timed tests (new hooks would restart the warm-up, and the walk is a hitch), and not
            // while idle: nobody reads the result then, and the walk is a stutter the player feels.
            bool scanDue = now >= _nextLiveScan && Instrumenter.Pending == 0 && Instrumenter.Hooked;
            // While idle, the memory check still runs every few minutes in a world (setting "Leak check every"), for the leak alarm.
            bool recording = Current != null;
            bool memoryDue = now >= _nextMemory && (recording ? CfgMemorySeconds.Value > 0
                                                              : CfgLeakSeconds.Value > 0 && inWorld && !Loads.WorldLoading);
            if (((scanDue && recording) || memoryDue) && TestRunning == null)
            {
                _heavy = true;
                UnityEngine.Object[] behaviours = null;
                try { behaviours = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)); }
                catch (Exception e) { Logger.LogDebug($"Could not list live objects: {e.Message}"); }
                if (scanDue)
                {
                    _nextLiveScan = now + 60f;
                    int queued = Instrumenter.ScanLiveBehaviours(behaviours);
                    if (queued > 0) Logger.LogInfo($"Found {queued} more methods to measure on newly loaded objects.");
                }
                if (memoryDue)
                {
                    _nextMemory = now + Mathf.Max(10f, recording ? CfgMemorySeconds.Value : CfgLeakSeconds.Value);
                    Context.Sample(full: false);
                    var sample = Memory.Take(behaviours, SessionSeconds, Context, Counters);
                    Memory.Write(Writer, SessionFolder, sample);
                    Alarms.CheckLeak(sample, Memory.Samples);
                }
            }

            Suite?.Tick(this);
            Ab?.Tick(this);
            var oc = ObjectCosts.Running;
            if (oc != null)
            {
                if (oc.Tick(Mathf.Max(CfgPatchBudgetMs.Value, CfgTestPatchBudgetMs.Value))) _heavy = true;
                if (oc.Done) ObjectCostsFinished(oc);
            }
            var mc = MemoryCensus.Running;
            if (mc != null)
            {
                mc.Tick();
                _heavy = true; // its frames (and the garbage collections it forces) never count in a capture
                if (mc.Done) MemoryCensusFinished(mc);
            }

            if (now >= _nextInterval)
            {
                _nextInterval = now + Mathf.Max(1f, CfgInterval.Value);
                if (Current != null) // nothing reads it while idle
                {
                    _heavy = true;
                    Context.Sample(full: true);
                    Current.WriteInterval(SessionSeconds, Context);
                }
            }
            if (now >= _nextSummary)
            {
                _nextSummary = now + Mathf.Max(5f, CfgSummaryInterval.Value);
                _heavy = true;
                Current?.WriteSummaries();
            }
        }

        private void OnGUI()
        {
            if (_overlay == null) return;
            long t0 = Stopwatch.GetTimestamp();
            long a0 = AllocCounter.Exact ? Probe.ReadAllocated() : 0;
            _overlay.OnGUI(Suite != null && CfgSuiteHideOverlay.Value);
            _monitorTicks += Stopwatch.GetTimestamp() - t0;
            if (AllocCounter.Exact) MonitorBytes += Probe.ReadAllocated() - a0;
        }

        // ---- Captures ---------------------------------------------------------------------------

        /// <summary>Full warm-up: probes were just attached and their methods still need compiling.</summary>
        internal void BeginWarmup() => _warmupUntil = Mathf.Max(_warmupUntil, Time.realtimeSinceStartup + Mathf.Max(0, CfgWarmup.Value));

        /// <summary>Short settle: nothing new to compile (mode switch, resume, Baseline).</summary>
        internal void BeginShortWarmup() => _warmupUntil = Mathf.Max(_warmupUntil, Time.realtimeSinceStartup + Mathf.Max(0, CfgSwitchWarmup.Value));

        internal bool CanUse(CaptureMode mode) => mode != CaptureMode.Allocations || AllocCounter.Available;

        internal void StartCapture(CaptureMode mode, string label)
        {
            if (!CanUse(mode))
            {
                Logger.LogWarning("Allocations mode is not available on this runtime; staying in Timing.");
                mode = CaptureMode.Timing;
            }
            if (mode != CaptureMode.Baseline && !WorldReady)
            {
                Suite = null;
                ShowToast(_worldSince < 0 ? "Load into a world first: tests that measure mods start once the world is loaded."
                                          : $"The world is still settling; try again in {WorldReadyIn:0} s.");
                return;
            }
            FinishCapture();
            _heavy = true;

            if (mode == CaptureMode.Baseline)
            {
                Instrumenter.Unhook(); // removes the network hooks too (same Harmony instance)
                Net.Attached = false;
            }
            else if (!Instrumenter.Hooked)
            {
                Instrumenter.Rehook();
                Instrumenter.ScanLiveBehaviours(); // the world's objects exist now
                _nextLiveScan = Time.realtimeSinceStartup + 60f;
            }
            else if (Instrumenter.Pending == 0)
            {
                // Objects that loaded while idle (no scans then): their probes go on now, with the usual warm-up after.
                Instrumenter.ScanLiveBehaviours();
                _nextLiveScan = Time.realtimeSinceStartup + 60f;
            }
            if (mode != CaptureMode.Baseline && CfgNetwork.Value)
                Net.Attach(_harmony);

            if (!_limiterLogged) { _limiterLogged = true; LogFrameLimiter(); } // in a world: the game's own settings are applied by now
            Probe.Mode = mode;
            Probe.ChildOverheadFx = mode == CaptureMode.Baseline ? 0 : (long)(Instrumenter.ChildOverhead[(int)mode] * 1024);
            Probe.ClearFrame();
            GarbageConfidence.ResetRate();
            Current = new Capture(++_captureNumber, mode, label, SessionFolder, Registry, Instrumenter, Counters, Writer, Net, CfgWindowFrames.Value);
            Current.LogBefore = LogWatch.Instance?.Snapshot();
            Probe.Recording = true;
            MonitorBytes = 0;
            Context.Sample(full: true);
            _warmupUntil = 0;
            BeginShortWarmup(); // hooking (if any) queues the full warm-up when it finishes
            _nextInterval = Time.realtimeSinceStartup + Mathf.Max(1f, CfgInterval.Value);
            Logger.LogInfo($"Capture {Current.Number} started: {mode}{(Current.Label.Length > 0 ? $" ({Current.Label})" : "")}.");
        }

        /// <summary>Switches the live overlay. It needs a recording to show anything, so turning it on
        /// while idle starts a continuous Timing recording.</summary>
        internal void SetOverlay(OverlayMode mode)
        {
            bool wasOff = CfgOverlay.Value == OverlayMode.Off;
            CfgOverlay.Value = mode;
            if (mode != OverlayMode.Off && wasOff && Current == null && Suite == null && WorldReady)
            {
                StartContinuous(CaptureMode.Timing);
                if (Current != null) ShowToast($"Live overlay: {mode}. Started a Timing recording for it (F11 → Stop ends it).", 4f);
                return;
            }
            ShowToast($"Live overlay: {mode}", 2f);
        }

        internal void RestartCapture()
        {
            AbortAb();
            Suite = null;
            StartCapture(Current?.Mode ?? CfgStartMode.Value, CfgLabel.Value);
        }

        /// <summary>Record in one mode until stopped.</summary>
        internal void StartContinuous(CaptureMode mode)
        {
            AbortAb();
            Suite = null;
            StartCapture(mode, CfgLabel.Value);
            if (Current != null) ShowToast($"Recording {Current.Mode} until you stop it (F11 → Stop).");
        }

        /// <summary>A timed test: the full suite, or one mode for a fixed time. Ends idle with results saved.</summary>
        /// <param name="label">null = the "Run label" setting.</param>
        /// <param name="seconds">0 = the setting for this kind of test.</param>
        internal void StartTimed(bool fullSuite, CaptureMode mode, string label = null, float seconds = 0)
        {
            if (!WorldReady)
            {
                ShowToast(_worldSince < 0 ? "Load into a world first: tests start once the world is loaded."
                                          : $"The world is still settling; try again in {WorldReadyIn:0} s.");
                return;
            }
            AbortAb();
            var steps = new List<CaptureMode>();
            if (!fullSuite) steps.Add(CanUse(mode) ? mode : CaptureMode.Timing);
            else
            {
                steps.Add(CaptureMode.Baseline);
                steps.Add(CaptureMode.Timing);
                if (CfgSuiteAlloc.Value && AllocCounter.Available) steps.Add(CaptureMode.Allocations);
                if (CfgSuiteTrace.Value) steps.Add(CaptureMode.Trace);
            }
            if (seconds <= 0) seconds = fullSuite ? CfgSuiteSeconds.Value : CfgBenchmarkSeconds.Value;
            Suite = new Suite(steps, seconds, label ?? CfgLabel.Value, !fullSuite);
            Logger.LogInfo($"{(fullSuite ? "Suite" : "Test")} started: {string.Join(" -> ", steps)}, {Suite.Seconds:0} s each.");
            Suite.Begin(this);
        }

        /// <summary>Stop whatever is running, save what was recorded, and go idle.</summary>
        internal void StopTest()
        {
            bool wasSuite = Suite != null || Ab != null || ObjectCosts.Running != null || MemoryCensus.Running != null;
            AbortAb();
            Suite = null;
            FinishCapture();
            GoIdle();
            ShowToast(wasSuite ? "Test stopped early; what was recorded is saved." : "Recording stopped and saved.");
        }

        internal void SetPanel(bool open)
        {
            if (open == _overlay.PanelOpen) return;
            _overlay.PanelOpen = open;
            _input.Set(open);
            _heavy = true;
            if (open) _overlay.Refresh();
        }

        /// <summary>
        /// A frame-rate cap or vsync hides CPU savings: frames finish early and then wait. When the game is
        /// running at its cap, compare CPU ms per frame between runs rather than FPS.
        /// </summary>
        internal static string FrameLimiter()
        {
            try
            {
                int vsync = QualitySettings.vSyncCount;
                int target = Application.targetFrameRate;
                if (vsync > 0) return $"vsync on ({vsync})";
                return target > 0 ? $"{target} FPS cap" : "uncapped";
            }
            catch { return "unknown"; }
        }

        private void LogFrameLimiter()
        {
            string l = FrameLimiter();
            if (l != "uncapped")
                Logger.LogInfo($"Frame limiter: {l}. While the game runs at its cap, FPS can't show CPU savings; compare CPU ms per frame instead (Valheim graphics settings: set FPS limit to unlimited and vsync off for the clearest results).");
        }

        internal void ShowToast(string text, float seconds = 8f)
        {
            Toast = text;
            ToastUntil = Time.realtimeSinceStartup + seconds;
        }

        internal static void OpenPath(string path)
        {
            try { Application.OpenURL("file:///" + path.Replace('\\', '/')); }
            catch { }
        }

        internal void FinishCapture()
        {
            var c = Current;
            if (c == null || c.Finished)
                return;
            c.Finished = true;
            _heavy = true;
            Context.Sample(full: true);
            c.WriteInterval(SessionSeconds, Context);
            c.WriteSpikes();
            c.WriteSummaries();
            if (c.Stats.TotFrames == 0)
            {
                Logger.LogInfo($"Capture {c.Number} ended with no counted frames; no report written.");
                return;
            }
            GarbageConfidence.TakeTiming(c);

            string json = Report.CaptureJson(c, SessionName, Registry, Instrumenter, Counters, Context);
            json = json.Insert(json.Length - 1, ",\"monitorKbPerMin\":" + Csv.JNum(MonitorBytes / 1024.0 / Math.Max(1, c.MeasuredSeconds) * 60, "0.0"));
            string title = $"{c.Mode} {c.Number}{(c.Label.Length > 0 ? " " + c.Label : "")}";
            Writer.Write(Path.Combine(c.Folder, "report.html"), Report.CaptureHtml(json, title));

            if (c.AbTurn)
            {
                // One turn of an A/B test: its result goes to ab_tests.csv, not the run history.
                Logger.LogInfo($"Capture {c.Number} (A/B turn) saved: {Path.Combine(c.Folder, "report.html")}");
                return;
            }
            string rel = Path.Combine(SessionName, Path.GetFileName(c.Folder)).Replace('\\', '/');
            string runsCsv = Path.Combine(RootFolder, "runs.csv");
            RunStamp.UpgradeHeader(runsCsv, Report.RunCsvHeader);
            bool newFile = _runLines.Count == 0 && !File.Exists(runsCsv);
            Writer.Append(runsCsv, (newFile ? Report.RunCsvHeader : "") + Report.RunCsvLine(c, SessionName, Registry, Context, rel));
            string runJson = Report.RunJson(c, SessionName, Registry, Context, rel);
            _runLines.Add(runJson);
            Writer.Append(Path.Combine(RootFolder, "runs.jsonl"), runJson + "\n");
            Writer.Write(Path.Combine(RootFolder, "compare.html"), Report.CompareHtml(_runLines));

            var s = Report.Summarize(c.Stats);
            var top = c.Mode == CaptureMode.Baseline ? null : c.OwnerRows().FirstOrDefault(r => !r.Mod.IsGame);
            Results.Add(new CaptureResult
            {
                Number = c.Number, Mode = c.Mode, Label = c.Label, Seconds = c.MeasuredSeconds, Fps = s.Fps, Fps1Low = s.Fps1Low,
                ModsPerFrame = c.Mode == CaptureMode.Baseline ? double.NaN : s.Mods, Unit = c.Stats.Unit,
                Top = top == null ? "" : $"{top.Mod.Name} ({top.Avg:0.000} {c.Stats.Unit})", Report = Path.Combine(c.Folder, "report.html"),
            });
            Logger.LogInfo($"Capture {c.Number} ({c.Mode}) saved: {s.Fps:0.0} FPS, 1% low {s.Fps1Low:0.0}, " +
                           $"{c.Stats.TotFrames} frames, report: {Path.Combine(c.Folder, "report.html")}");
            if (c.Mode != CaptureMode.Baseline)
            {
                bool alloc = c.Mode == CaptureMode.Allocations;
                foreach (var r in c.OwnerRows().Where(r => !r.Mod.IsGame).Take(5))
                    Logger.LogInfo($"  {r.Avg,8:0.000} {c.Stats.Unit}/frame  {r.Mod.Name}" +
                                   (alloc ? $"  [{GarbageConfidence.ForMod(c.Mode, Registry.Sources.Where(x => x != null && x.Mod == r.Mod), r.Avg)}]" : ""));
                if (alloc) Logger.LogInfo("  " + GarbageConfidence.Explain);
            }
            var log = LogWatch.ForCapture(c, Registry);
            if (log.errors > 0)
                Logger.LogInfo($"  Errors in the log during this capture: {log.errors} ({log.perMin:0.0} per minute), most from {log.top}. Details: perf/log_errors or {Path.Combine(SessionFolder, "log_errors.csv")}");
            WriteLogErrors();
            long logLines = (LogWatch.Instance?.Lines ?? 0) - c.LogLinesAtStart;
            double minutesC = Math.Max(1.0 / 60, (DateTime.Now - c.Started).TotalMinutes);
            if (LogWatch.Timed && logLines > 0)
                Logger.LogInfo($"  Log: {logLines / minutesC:0} lines per minute; writing them took {LogWatch.TicksToMs(LogWatch.MainThreadTicks - c.LogTicksAtStart) / minutesC:0.0} ms of main-thread time per minute (Unity's own cost for building errors comes on top).");
            if (c.BridgeLeftOut.Count > 0)
                Logger.LogInfo($"  Frames left out because ValAgentBridge ran a command in them: {string.Join(", ", c.BridgeLeftOut.OrderByDescending(k => k.Value.worstMs).Take(6).Select(k => $"{k.Key} {k.Value.frames}x (slowest {k.Value.worstMs:0} ms)"))}");
            if (c.Stats.SpikeCount > 0)
                Logger.LogInfo($"  Slow frames: {c.Stats.SpikeCount} ({string.Join(", ", c.Stats.CauseCounts.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}"))})");
            var net = c.Net.Top(3).ToList();
            if (net.Count > 0)
                Logger.LogInfo($"  Busiest network messages: {string.Join(", ", net.Select(e => $"{e.Name} ({(e.SentCalls + e.RecvCalls) / Math.Max(1, c.MeasuredSeconds):0.0}/s{(e.Owner.Length > 0 ? ", " + e.Owner : "")})"))}");
        }

        // ---- Load times -------------------------------------------------------------------------

        private void StartupTimed(LoadTimes l)
        {
            _heavy = true;
            LoadTimes.AppendCsv(Writer, Path.Combine(RootFolder, "startup_times.csv"), LoadTimes.StartupHeader, l.StartupLine(SessionName));
            if (l.Mods.Count > 0)
                Writer.Write(Path.Combine(SessionFolder, "startup_mods.csv"), l.ModsCsv());
            Logger.LogInfo($"Startup: {Secs(l.TotalToMenu)} from launch to a settled main menu (mods loading {Secs(l.ModLoading)}, then {Secs(l.ModsToMenu)} to the menu, menu settled after {Secs(l.MenuSettle)}).");
            foreach (var m in l.Mods.OrderByDescending(m => m.Seconds).Take(5))
                Logger.LogInfo($"  {m.Seconds,7:0.00} s  {m.Name}");
            LogSteps("Startup steps after the mods loaded", l.StartupSteps, l.StartupStepMods);
            if (StartupDetail.Active)
            {
                StartupDetail.Finish();
                Writer.Write(Path.Combine(SessionFolder, "startup_methods.csv"), StartupDetail.Csv());
                foreach (var mod in StartupDetail.Mods)
                {
                    var load = l.Mods.FirstOrDefault(m => string.Equals(m.Guid, mod.Guid, StringComparison.OrdinalIgnoreCase));
                    Logger.LogInfo($"  Startup of {mod.Name}{(load != null ? $" ({load.Seconds:0.00} s)" : "")}, slowest of its own methods (total includes what they call; own = without the timed methods they call):");
                    foreach (var s in StartupDetail.Results.Where(r => r.Mod == mod.Name && StartupDetail.Seconds(r.Total) >= 0.02).Take(10))
                        Logger.LogInfo($"     {StartupDetail.Seconds(s.Total),6:0.00} s  {s.Name}  (own {StartupDetail.Seconds(s.Self):0.00} s, {s.Calls}x)");
                }
                Logger.LogInfo($"  Full list: {Path.Combine(SessionFolder, "startup_methods.csv")}. Timers removed.");
            }
            if (SessionCheck.LastCrash != null)
                ShowToast("Last session didn't close normally" + (SessionCheck.LikelyMod != null ? $" (likely involved: {SessionCheck.LikelyMod})" : "") +
                          ". Details: BepInEx\\ValPerfMonitor\\last_crash.txt", 25f);
            WriteSteps("startup", l.StartupSteps, l.ModsToMenu + (double.IsNaN(l.MenuSettle) ? 0 : l.MenuSettle), l.StartupStepMods);
        }

        private void WorldLoadTimed(LoadTimes.WorldLoad w)
        {
            _heavy = true;
            string world = "";
            try { world = ZNet.World?.m_name ?? ""; } catch { }
            LoadTimes.AppendCsv(Writer, Path.Combine(RootFolder, "world_load_times.csv"), LoadTimes.WorldHeader,
                LoadTimes.WorldLine(SessionName, w, world, Registry.Visible.Count(m => m.IsPlugin)));
            Logger.LogInfo($"World load: player in the world after {Secs(w.SpawnSeconds)}, loading screen gone after {Secs(w.ScreenSeconds)}, " +
                           (w.GaveUp ? $"still hitching after {Secs(w.SmoothSeconds)}" : $"hitches stopped after {Secs(w.SmoothSeconds)}") + $" ({w.Hitches} frames over 100 ms).");
            LogSteps("World load steps", w.Steps, w.StepMods);
            WriteSteps("world load", w.Steps, w.SmoothSeconds, w.StepMods);
        }

        private void LogoutTimed(double seconds)
        {
            _heavy = true;
            LoadTimes.AppendCsv(Writer, Path.Combine(RootFolder, "logout_times.csv"), LoadTimes.LogoutHeader,
                $"{SessionName},{DateTime.Now.ToString("HH:mm:ss", Csv.Inv)},{Csv.F(seconds, "0.0")}\n");
            Logger.LogInfo($"Logout: back at the main menu after {Secs(seconds)}.");
            LogSteps("Logout steps", Loads.LastLogoutSteps, Loads.LastLogoutStepMods);
            WriteSteps("logout", Loads.LastLogoutSteps, seconds, Loads.LastLogoutStepMods);
            WriteSlowObjects();
        }

        /// <summary>slow_objects.csv for this session, and the slowest single objects in the log.</summary>
        internal void WriteSlowObjects()
        {
            if (ObjectLoads.Objects == 0) return;
            Writer.Write(Path.Combine(SessionFolder, "slow_objects.csv"), ObjectLoads.Csv(100));
            var top = ObjectLoads.Top(5, byMax: true).Where(r => r.MaxMs >= 10).ToList();
            if (top.Count == 0) return;
            Logger.LogInfo($"  Slowest objects to spawn ({ObjectLoads.Objects} spawned this session; worst single one, average):");
            foreach (var r in top)
            {
                Logger.LogInfo($"  {r.MaxMs,7:0.0} ms  {r.Prefab} (avg {r.AvgMs:0.00} ms x{r.Count}){(r.ModHint.Length > 0 ? " [scripts from: " + r.ModHint + "]" : "")}");
                if (r.SplitSpawns > 0)
                    Logger.LogInfo($"           split over {r.SplitSpawns} spawns: Unity creating the object and unsplit game code {r.OtherAvgMs:0.000} ms; {ObjectLoads.PartsText(r, 4)}");
            }
        }

        /// <summary>log_errors.csv for this session: every distinct error and warning, with its mod and count.</summary>
        internal void WriteLogErrors()
        {
            var w = LogWatch.Instance;
            if (w == null || Writer == null || w.Errors + w.Warnings == 0) return;
            Writer.Write(Path.Combine(SessionFolder, "log_errors.csv"), w.Csv(Registry));
        }

        private void LogSteps(string title, List<(string label, int calls, double seconds)> steps,
                              List<(string step, string mod, int calls, double seconds)> mods = null)
        {
            var top = steps.Where(s => s.seconds >= 0.05).Take(6).ToList();
            if (top.Count == 0) return;
            Logger.LogInfo($"  {title} (slowest; each includes the mod patches on it):");
            foreach (var (label, calls, seconds) in top)
            {
                Logger.LogInfo($"  {seconds,7:0.00} s  {label}{(calls > 1 ? $" ({calls} times)" : "")}");
                if (mods == null || seconds < 0.5) continue;
                var inStep = mods.Where(m => m.step == label).ToList();
                if (inStep.Count == 0) continue;
                double modSum = inStep.Sum(m => m.seconds);
                string list = string.Join(", ", inStep.Where(m => m.seconds >= 0.01).Take(6).Select(m => $"{m.mod} {m.seconds:0.00} s"));
                Logger.LogInfo($"             mods' patches {modSum:0.00} s{(list.Length > 0 ? " (" + list + (inStep.Count(m => m.seconds >= 0.01) > 6 ? ", ..." : "") + ")" : "")}; " +
                               $"the game's own code {Math.Max(0, seconds - modSum):0.00} s");
            }
        }

        private bool _stepsHeader, _stepModsHeader;

        private void WriteSteps(string phase, List<(string label, int calls, double seconds)> steps, double phaseSeconds,
                                List<(string step, string mod, int calls, double seconds)> mods = null)
        {
            string when = DateTime.Now.ToString("HH:mm:ss", Csv.Inv);
            string path = Path.Combine(SessionFolder, "load_steps.csv");
            string rows = LoadTimes.StepsCsv(phase, when, steps, phaseSeconds);
            Writer.Append(path, (_stepsHeader ? "" : LoadTimes.StepsHeader) + rows);
            _stepsHeader = true;
            if (mods == null || mods.Count == 0) return;
            Writer.Append(Path.Combine(SessionFolder, "load_step_mods.csv"), (_stepModsHeader ? "" : LoadPatches.CsvHeader) + LoadPatches.Csv(phase, when, mods));
            _stepModsHeader = true;
        }

        private static string Secs(double s) => double.IsNaN(s) ? "?" : $"{s:0.0} s";

        private void LoadRunHistory()
        {
            try
            {
                string path = Path.Combine(RootFolder, "runs.jsonl");
                if (File.Exists(path))
                    _runLines.AddRange(File.ReadAllLines(path).Where(l => l.StartsWith("{")));
            }
            catch (Exception e) { Logger.LogWarning($"Could not read run history: {e.Message}"); }
        }

        // ---- A/B test ---------------------------------------------------------------------------

        /// <summary>Start an A/B test (see AbTest). Returns it; check Error.</summary>
        internal AbTest StartAb(string mod, string patchesOn, string skip, float seconds, int rounds, string label, int objectId = 0, string objectPath = "", string prefab = "", string script = "")
        {
            var t = new AbTest(mod, patchesOn, skip, objectId, objectPath, prefab, seconds, rounds, label, Registry, Guid + ".abtest", RootFolder, Logger);
            if (t.Error != null) return t;
            if (!string.IsNullOrWhiteSpace(script))
            {
                string why = BridgeScript.Check();
                if (why != null) { t.Error = "can't run the action script: " + why; return t; }
                t.Script = new BridgeScript(script);
            }
            if (!WorldReady) { t.Error = "not in a world yet (or the world is still loading)"; return t; }
            AbortAb();
            Suite = null;
            Ab = LastAb = t;
            Logger.LogInfo($"A/B test started: {t.What}, {t.Plan.Count} turns of {t.Seconds:0} s ({t.PatchCount} patches, {t.SkipCount} skipped methods, {t.ObjectCount} objects).");
            foreach (var d in t.Describe().Take(15)) Logger.LogInfo("  " + d);
            t.Begin(this);
            if (Current == null) { t.Abort(); Ab = null; t.Error = "could not start a capture"; }
            return t;
        }

        /// <summary>Start the "most expensive objects" recording (see ObjectCosts). Returns it; check Error.</summary>
        internal ObjectCosts StartObjectCosts(float seconds)
        {
            if (!WorldReady) return new ObjectCosts(seconds, Instrumenter, Registry, _guard, Guid + ".objectcosts", Logger) { Error = "not in a world yet (or the world is still loading)" };
            AbortAb();
            Suite = null;
            var oc = new ObjectCosts(seconds, Instrumenter, Registry, _guard, Guid + ".objectcosts", Logger);
            if (oc.Error != null) return oc;
            StartCapture(CaptureMode.Baseline, ObjectCosts.CaptureLabel); // the normal probes come off; the object timers go on instead
            ObjectCosts.Running = ObjectCosts.Last = oc;
            Logger.LogInfo($"Object costs started: timing each object's per-frame code for {oc.Seconds:0} s.");
            return oc;
        }

        private void ObjectCostsFinished(ObjectCosts oc)
        {
            ObjectCosts.Running = null;
            DropObjectCostsCapture();
            Writer.Write(Path.Combine(SessionFolder, "object_costs.csv"), oc.Csv());
            Logger.LogInfo($"Object costs: {oc.Objects.Count} objects, {oc.TotalMs:0.00} ms per frame in {oc.MethodsTimed} timed per-frame methods over {oc.Frames} frames. Full list: {Path.Combine(SessionFolder, "object_costs.csv")}");
            Logger.LogInfo("  Most expensive prefabs (all loaded copies together):");
            foreach (var p in oc.Prefabs.Take(8))
                Logger.LogInfo($"  {p.Ms,8:0.000} ms/frame  {p.Prefab} x{p.Objects}{(p.Mods.Count > 0 ? "  [mod code: " + string.Join(", ", p.Mods.ToArray()) + "]" : "")}");
            Logger.LogInfo("  Most expensive single objects:");
            foreach (var o in oc.Objects.Take(8))
                Logger.LogInfo($"  {o.Ms,8:0.000} ms/frame  {o.Prefab} ({(double.IsNaN(o.DistanceM) ? "" : $"{o.DistanceM:0} m away, ")}instanceId {o.InstanceId}): {string.Join(", ", o.Parts.Take(3).Select(x => $"{x.part} {x.ms:0.000}").ToArray())}");
            var top = oc.Prefabs.FirstOrDefault();
            ShowToast(top == null ? "Object costs finished: nothing measured." : $"Object costs finished. Most expensive: {top.Prefab} x{top.Objects} ({top.Ms:0.00} ms/frame). Details in the log.", 20f);
        }

        /// <summary>Start a memory census (see MemoryCensus). Returns it; check Error.</summary>
        internal MemoryCensus StartMemoryCensus(int budgetMs)
        {
            var mc = new MemoryCensus(budgetMs, Registry);
            if (MemoryCensus.Running != null) { mc.Error = "a memory census is already running"; return mc; }
            if (Suite != null || Ab != null || ObjectCosts.Running != null) { mc.Error = "a test is running; stop it first (perf/stop)"; return mc; }
            if (Current != null)
            {
                // Its forced garbage collections would change the recording's GC numbers from here on.
                Logger.LogInfo($"Memory census: ending capture {Current.Number} ({Current.Mode}) first; it is saved.");
                FinishCapture();
                GoIdle();
            }
            MemoryCensus.Running = MemoryCensus.Last = mc;
            Logger.LogInfo($"Memory census started: counting live objects in slices of up to {mc.BudgetMs} ms per frame. Stand still; the game stutters for a few seconds.");
            return mc;
        }

        private void MemoryCensusFinished(MemoryCensus mc)
        {
            MemoryCensus.Running = null;
            RelogTest.Running?.CensusFinished(mc);
            if (mc.Error != null) { Logger.LogWarning("Memory census: " + mc.Error); return; }
            string file = Path.Combine(SessionFolder, "memory_census_" + DateTime.Now.ToString("HH-mm-ss", Csv.Inv) + ".txt");
            mc.File = file;
            Writer.Write(file, mc.Text());
            Logger.LogInfo("Memory census finished:");
            foreach (var line in mc.Plain()) Logger.LogInfo("  " + line);
            if (mc.Errors > 0) Logger.LogInfo($"  {mc.Errors} objects could not be read (skipped).");
            Logger.LogInfo("  Full list: " + file);
            string growth = !mc.HasPrevious ? "" : mc.OwnerGrowth.Count > 0
                ? $" Grew most since the last census: {mc.OwnerGrowth[0].name} ({MemoryCensus.Signed(mc.OwnerGrowth[0].count)} objects)."
                : " Nothing grew since the last census.";
            ShowToast($"Memory census finished: full garbage collection {mc.GcMedianMs.ToString("0", Csv.Inv)} ms, {MemoryCensus.Millions(mc.Objects)} objects.{growth} Details in the log.", 15f);
        }

        /// <summary>The Baseline capture under an object costs recording had its timers on: not a clean run, so not saved.</summary>
        private void DropObjectCostsCapture()
        {
            if (Current != null && Current.Mode == CaptureMode.Baseline && Current.Label == ObjectCosts.CaptureLabel)
            {
                Current.Finished = true;
                GoIdle();
            }
        }

        private void AbortAb()
        {
            if (ObjectCosts.Running != null)
            {
                ObjectCosts.Running.Abort();
                ObjectCosts.Running = null;
                DropObjectCostsCapture();
                Logger.LogInfo("Object costs stopped early; timers removed.");
            }
            if (MemoryCensus.Running != null)
            {
                MemoryCensus.Running.Abort();
                MemoryCensus.Running = null;
                Logger.LogInfo("Memory census stopped early.");
            }
            if (Ab == null) return;
            Ab.Abort();
            Logger.LogInfo($"A/B test stopped early; everything put back.{(Ab.Phases.Count > 0 ? " So far: " + Ab.Verdict : "")}");
            Ab = null;
        }

        internal void AbFinished(AbTest t)
        {
            Ab = null;
            FinishCapture();
            GoIdle();
            Logger.LogInfo($"A/B test finished: {t.What}{(t.Script != null ? $" (with the action script {(t.Script.Spec.StartsWith("{") ? "given inline" : t.Script.Spec)}, {string.Join("/", t.Phases.Select(ph => ph.ScriptRuns.ToString(Csv.Inv)).ToArray())} runs per turn)" : "")}");
            foreach (var ph in t.Phases)
                Logger.LogInfo($"  turn {(ph.On ? "normal" : "off   ")}: main-thread CPU {ph.CpuMainMs:0.000} ms, frame {ph.FrameMs:0.00} ms, garbage {ph.GarbageKbPerFrame:0.0} KB/frame, 1% low {ph.Fps1Low:0.0} FPS, {ph.GcPerMin:0.0} GC/min, {ph.SlowPerMin:0.0} slow frames/min; weather {ph.WeatherText}, moved {ph.MovedM:0} m, {ph.ObjectsMin}-{ph.ObjectsMax} objects loaded");
            foreach (var line in t.VerdictLines) Logger.LogInfo("  " + line);
            string path = Path.Combine(RootFolder, "ab_tests.csv");
            RunStamp.UpgradeHeader(path, AbTest.CsvHeader);
            Writer.Append(path, (File.Exists(path) ? "" : AbTest.CsvHeader) + t.CsvLine(SessionName, Registry));
            ShowToast("A/B test finished: " + t.Verdict, 20f);
        }

        internal void SuiteFinished(Suite suite)
        {
            Suite = null;
            FinishCapture();
            GoIdle();
            Logger.LogInfo($"{(suite.Single ? "Test" : "Suite")} finished. Compare runs: {Path.Combine(RootFolder, "compare.html")}");
            ShowToast($"{(suite.Single ? "Test" : "Suite")} finished. Press F11 and open Results.", 20f);
        }

        /// <summary>
        /// Nothing is recording. The probes stay attached (taking them off and on again costs seconds of hooking each
        /// way) but stop doing any work, and so does the network counting.
        /// </summary>
        private void GoIdle()
        {
            Current = null;
            Probe.Recording = false;
            if (Net != null) Net.Recording = false;
        }

        /// <summary>The on-demand test running now (one that must not be disturbed), or null.</summary>
        internal string TestRunning =>
            Suite != null ? (Suite.Single ? "a timed test" : "a suite") : Ab != null ? "an A/B test" :
            ObjectCosts.Running != null ? "object costs" : MemoryCensus.Running != null ? "a memory census" : null;

        /// <summary>This frame did work for a bridge command: never count it in a capture.</summary>
        internal void MarkHeavy() => _heavy = true;

        private void RefreshSourceNames()
        {
            if (_sourceNames.Length == Probe.SourceCount) return;
            var names = new string[Probe.SourceCount];
            foreach (var s in Registry.Sources)
                if (s != null && s.Id >= 0 && s.Id < names.Length)
                    names[s.Id] = $"{s.Mod.Name}: {s.Kind} {s.Name}{(s.Targets.Count > 0 && s.Kind != "Game" ? " → " + s.Targets[0] : "")}";
            _sourceNames = names;
        }

        private void OnDestroy()
        {
            Probe.Recording = false;
            Ab?.PutBack();
            ObjectCosts.Running?.Abort();
            MemoryCensus.Running?.Abort();
            SessionCheck.Closed();
            _guard?.AllDone(); // a normal quit while hooking is not a crash
            if (Writer == null)
                return;
            try { FinishCapture(); } catch (Exception e) { Logger.LogError(e); }
            try { WriteLogErrors(); } catch (Exception e) { Logger.LogError(e); }
            Writer.Dispose();
            Counters?.Dispose();
        }
    }

    internal enum OverlayMode { Off, Compact, Full }

    internal sealed class CaptureResult
    {
        public int Number; public CaptureMode Mode; public string Label, Unit, Top, Report;
        public double Seconds, Fps, Fps1Low, ModsPerFrame;
    }

    /// <summary>Runs a list of capture modes back to back, each for a fixed amount of counted time.</summary>
    internal sealed class Suite
    {
        public readonly List<CaptureMode> Steps;
        public readonly float Seconds;
        public readonly string Label;
        public readonly bool Single;
        public int Step;

        public Suite(List<CaptureMode> steps, float seconds, string label, bool single)
        {
            Steps = steps; Seconds = seconds; Single = single;
            string id = DateTime.Now.ToString("HHmm", Csv.Inv);
            Label = (string.IsNullOrWhiteSpace(label) ? "" : label.Trim() + " ") + (single ? "test " : "suite ") + id;
        }

        public string Status(Plugin p)
        {
            var c = p.Current;
            if (c == null) return "";
            string phase = p.WarmingUp ? $"warming up {p.WarmupLeft:0}s" : $"{c.MeasuredSeconds:0}/{Seconds:0}s";
            return Single ? $"Test: {Steps[Step]} – {phase}" : $"Suite step {Step + 1}/{Steps.Count}: {Steps[Step]} – {phase}";
        }

        public void Begin(Plugin p) => p.StartCapture(Steps[0], Label);

        public void Tick(Plugin p)
        {
            var c = p.Current;
            if (c == null || c.MeasuredSeconds < Seconds)
                return;
            Step++;
            if (Step >= Steps.Count) p.SuiteFinished(this);
            else p.StartCapture(Steps[Step], Label);
        }
    }
}
