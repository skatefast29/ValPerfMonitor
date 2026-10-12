using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheimmods.ValPerfMonitor";
        public const string Name = "ValPerfMonitor";
        public const string Version = "1.0.2";

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
        internal string Toast = "";
        internal float ToastUntil;

        internal ConfigEntry<CaptureMode> CfgStartMode;
        internal ConfigEntry<string> CfgLabel, CfgExclude;
        internal ConfigEntry<OverlayMode> CfgOverlay;
        internal ConfigEntry<bool> CfgCoroutines, CfgNetwork, CfgSplitSpawns;
        internal ConfigEntry<float> CfgSplitSpawnMs;
        internal ConfigEntry<string> CfgExtraMethods, CfgSplitStartupMods;
        internal ConfigEntry<int> CfgSplitStartupSlowest;
        internal ConfigEntry<bool> CfgUnityCallbacks, CfgGameSystems, CfgCompact, CfgRecordOnLaunch, CfgSuiteAlloc, CfgSuiteTrace, CfgSuiteHideOverlay;
        internal ConfigEntry<KeyboardShortcut> CfgToggleKey, CfgRestartKey, CfgSuiteKey, CfgOverlayKey;
        internal ConfigEntry<float> CfgInterval, CfgSummaryInterval, CfgFrameSpikeMs, CfgOwnerSpikeMs, CfgOwnerSpikeKb, CfgPatchBudgetMs, CfgTestPatchBudgetMs,
                                    CfgWarmup, CfgSwitchWarmup, CfgWorldSettle, CfgMonitorExcludeMs, CfgSuiteSeconds, CfgBenchmarkSeconds, CfgScale;
        internal ConfigEntry<float> CfgMemorySeconds;
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
            CfgPatchBudgetMs = Config.Bind("2 - Measuring", "Hooking budget ms per frame", 8f, "Time per frame spent attaching probes. Those frames are never counted.");
            CfgTestPatchBudgetMs = Config.Bind("2 - Measuring", "Hooking budget ms per frame during tests", 40f, "Same, while a test suite or A/B test runs (you stand still then, so short stutters don't matter). Higher = probes attached sooner; the game runs at a low frame rate until hooking is done.");
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
            CfgMaxTraces = Config.Bind("3 - Logging", "Max flame graphs per capture", 40, "Trace mode saves at most this many slow frames.");

            CfgSuiteSeconds = Config.Bind("4 - Suite", "Seconds per mode", 60f, "Measured time per mode in the full suite (warm-up not included).");
            CfgSuiteAlloc = Config.Bind("4 - Suite", "Include Allocations", true, "");
            CfgSuiteTrace = Config.Bind("4 - Suite", "Include Trace", true, "");
            CfgSuiteHideOverlay = Config.Bind("4 - Suite", "Hide live overlay during timed tests", true, "Only the one-line status shows while a timed test runs. The overlay itself costs frame time and allocates memory; hiding it keeps it out of the measurement.");
            CfgBenchmarkSeconds = Config.Bind("4 - Suite", "Single test seconds", 60f, "Length of a single-mode timed test.");

            CfgRows = Config.Bind("5 - Overlay", "Rows", 15, "");
            CfgFontSize = Config.Bind("5 - Overlay", "Font size", 13, "");
            CfgScale = Config.Bind("5 - Overlay", "Scale", 1f, "Overall overlay scale (for 1440p/4K).");
            CfgCompact = Config.Bind("5 - Overlay", "Compact", false, "Hide the frame-time graph.");
            CfgPosX = Config.Bind("5 - Overlay", "Position X", 20, "");
            CfgPosY = Config.Bind("5 - Overlay", "Position Y", 80, "");

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
            Probe.Recording = true;
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
                int objects = GameContext.LoadedObjects();
                int objectsChanged = objects >= 0 && _lastObjects >= 0 ? Math.Abs(objects - _lastObjects) : 0;
                _lastObjects = objects;
                if (frameStart != 0 && Current != null && Probe.Recording)
                {
                    // A GC while this monitor was writing or doing heavy work may have been caused by it: don't count it.
                    // Frames while the test panel is open are never counted (nobody is playing, and the UI is heavy).
                    bool counted = !WarmingUp && !_heavy && !_overlay.PanelOpen && monitorMs <= CfgMonitorExcludeMs.Value && !(gc && (_heavy || Writer.Busy));
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
            if (!inWorld) _worldSince = -1f;
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

            float budget = Suite != null || Ab != null ? Mathf.Max(CfgPatchBudgetMs.Value, CfgTestPatchBudgetMs.Value) : CfgPatchBudgetMs.Value;
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
            if (Instrumenter.Pending == 0 && SpawnParts.Drain(budget))
                _heavy = true;

            float now = Time.realtimeSinceStartup;
            if (now >= _nextRescanCheck)
            {
                // Some mods patch late (on world load, on first use). Pick those up too.
                _nextRescanCheck = now + 15f;
                if (Instrumenter.Hooked && Instrumenter.Pending == 0 && Instrumenter.PatchSetChanged())
                {
                    _heavy = true;
                    int queued = Instrumenter.Scan(false, false);
                    if (queued > 0) Logger.LogInfo($"Rescan found {queued} new mod methods to measure.");
                }
            }

            // Every minute: pick up classes that only now have live objects (new scene, biome, creatures), and
            // record object counts for leak tracking. Both walk every live object, so they share one list.
            // Not during timed tests: new hooks would restart the warm-up, and the walk is a hitch.
            bool scanDue = now >= _nextLiveScan && Instrumenter.Pending == 0 && Instrumenter.Hooked;
            bool memoryDue = CfgMemorySeconds.Value > 0 && now >= _nextMemory;
            if ((scanDue || memoryDue) && Suite == null && Ab == null)
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
                    _nextMemory = now + Mathf.Max(10f, CfgMemorySeconds.Value);
                    Context.Sample(full: false);
                    Memory.Write(Writer, SessionFolder, Memory.Take(behaviours, SessionSeconds, Context, Counters));
                }
            }

            Suite?.Tick(this);
            Ab?.Tick(this);

            if (now >= _nextInterval)
            {
                _nextInterval = now + Mathf.Max(1f, CfgInterval.Value);
                _heavy = true;
                Context.Sample(full: true);
                Current?.WriteInterval(SessionSeconds, Context);
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
            if (mode != CaptureMode.Baseline && CfgNetwork.Value)
                Net.Attach(_harmony);

            if (!_limiterLogged) { _limiterLogged = true; LogFrameLimiter(); } // in a world: the game's own settings are applied by now
            Probe.Mode = mode;
            Probe.ChildOverheadFx = mode == CaptureMode.Baseline ? 0 : (long)(Instrumenter.ChildOverhead[(int)mode] * 1024);
            Probe.ClearFrame();
            GarbageConfidence.ResetRate();
            Current = new Capture(++_captureNumber, mode, label, SessionFolder, Registry, Instrumenter, Counters, Writer, Net, CfgWindowFrames.Value);
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
        internal void StartTimed(bool fullSuite, CaptureMode mode)
        {
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
            Suite = new Suite(steps, fullSuite ? CfgSuiteSeconds.Value : CfgBenchmarkSeconds.Value, CfgLabel.Value, !fullSuite);
            Logger.LogInfo($"{(fullSuite ? "Suite" : "Test")} started: {string.Join(" -> ", steps)}, {Suite.Seconds:0} s each.");
            Suite.Begin(this);
        }

        /// <summary>Stop whatever is running, save what was recorded, and go idle.</summary>
        internal void StopTest()
        {
            bool wasSuite = Suite != null || Ab != null;
            AbortAb();
            Suite = null;
            FinishCapture();
            Current = null;
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
        internal AbTest StartAb(string mod, string patchesOn, string skip, float seconds, int rounds, string label)
        {
            var t = new AbTest(mod, patchesOn, skip, seconds, rounds, label, Registry, Guid + ".abtest", RootFolder, Logger);
            if (t.Error != null) return t;
            if (!WorldReady) { t.Error = "not in a world yet (or the world is still loading)"; return t; }
            AbortAb();
            Suite = null;
            Ab = LastAb = t;
            Logger.LogInfo($"A/B test started: {t.What}, {t.Plan.Count} turns of {t.Seconds:0} s ({t.PatchCount} patches, {t.SkipCount} skipped methods).");
            foreach (var d in t.Describe().Take(15)) Logger.LogInfo("  " + d);
            t.Begin(this);
            if (Current == null) { t.Abort(); Ab = null; t.Error = "could not start a capture"; }
            return t;
        }

        private void AbortAb()
        {
            if (Ab == null) return;
            Ab.Abort();
            Logger.LogInfo($"A/B test stopped early; everything put back.{(Ab.Phases.Count > 0 ? " So far: " + Ab.Verdict : "")}");
            Ab = null;
        }

        internal void AbFinished(AbTest t)
        {
            Ab = null;
            FinishCapture();
            Current = null;
            Logger.LogInfo($"A/B test finished: {t.What}");
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
            Current = null;
            Logger.LogInfo($"{(suite.Single ? "Test" : "Suite")} finished. Compare runs: {Path.Combine(RootFolder, "compare.html")}");
            ShowToast($"{(suite.Single ? "Test" : "Suite")} finished. Press F11 and open Results.", 20f);
        }

        internal void SetRecording(bool on)
        {
            Probe.Recording = on;
            if (on) BeginShortWarmup();
        }

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
            _guard?.AllDone(); // a normal quit while hooking is not a crash
            if (Writer == null)
                return;
            try { FinishCapture(); } catch (Exception e) { Logger.LogError(e); }
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
