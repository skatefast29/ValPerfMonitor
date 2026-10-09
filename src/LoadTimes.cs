using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;

namespace ValPerfMonitor
{
    /// <summary>
    /// How long the game takes to start, to load a world and to log out, and how long each mod adds to
    /// startup. Per-mod startup times come from the companion preloader patcher (patchers folder), which
    /// timestamps BepInEx's "Loading [Mod]" lines before any mod loads. Everything else is watched once
    /// per frame with a few static field reads; nothing is hooked.
    /// </summary>
    internal sealed class LoadTimes
    {
        private const float HitchMs = 100f;        // a frame this slow counts as a loading hitch
        private const double SmoothSeconds = 3;    // this long without hitches = loading is over
        private const double GiveUpSeconds = 120;  // stop waiting for smoothness after this

        public sealed class ModLoad { public string Name, Guid; public double Seconds; }

        public sealed class WorldLoad
        {
            public string When;
            public double SceneSeconds = double.NaN, SpawnSeconds = double.NaN, ScreenSeconds = double.NaN, SmoothSeconds = double.NaN;
            public int Hitches;
            public bool GaveUp;
            public List<(string label, int calls, double seconds)> Steps = new List<(string, int, double)>();
            public List<(string step, string mod, int calls, double seconds)> StepMods = new List<(string, string, int, double)>();
        }

        // Startup.
        public bool HavePreloader;
        public double LaunchToBepInEx = double.NaN, BepInExToMods = double.NaN, ModLoading = double.NaN, ModsToMenu = double.NaN, MenuSettle = double.NaN, TotalToMenu = double.NaN;
        public readonly List<ModLoad> Mods = new List<ModLoad>();
        public List<(string label, int calls, double seconds)> StartupSteps = new List<(string, int, double)>();
        public List<(string step, string mod, int calls, double seconds)> StartupStepMods = new List<(string, string, int, double)>();
        public bool StartupDone;

        public readonly List<WorldLoad> WorldLoads = new List<WorldLoad>();
        public readonly List<(string when, double seconds)> Logouts = new List<(string, double)>();
        public List<(string label, int calls, double seconds)> LastLogoutSteps = new List<(string, int, double)>();
        public List<(string step, string mod, int calls, double seconds)> LastLogoutStepMods = new List<(string, string, int, double)>();

        private readonly ManualLogSource _log;
        private readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;
        private long _modsDoneTicks, _menuTicks;
        private DateTime _processStart = DateTime.MinValue;

        // World load in progress.
        private WorldLoad _load;
        private long _loadStart, _lastHitch, _settleFrom, _menuLastHitch;
        private bool _loadScreenSeen;
        private long _logoutStart;

        public LoadTimes(ManualLogSource log, long pluginAwakeTicks)
        {
            _log = log;
            _modsDoneTicks = pluginAwakeTicks; // replaced by the real "all mods loaded" time when the preloader is present
            try { _processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime(); } catch { }
            ReadPreloader();
        }

        /// <summary>True while a world load is being timed (the plugin waits with hooking until it settles).</summary>
        public bool WorldLoading => _load != null;

        private void ReadPreloader()
        {
            if (!(AppDomain.CurrentDomain.GetData("ValPerfMonitor.LoadEvents") is List<string> raw))
            {
                _log.LogInfo("Startup times per mod need ValPerfMonitor.Preloader.dll in BepInEx\\patchers (not found); only total startup and world load times are recorded.");
                return;
            }
            var events = new List<(long ticks, long utc, string text)>();
            lock (raw)
                foreach (var line in raw)
                {
                    var p = line.Split(new[] { '\t' }, 3);
                    if (p.Length == 3 && long.TryParse(p[0], out long t) && long.TryParse(p[1], out long u))
                        events.Add((t, u, p[2]));
                }
            if (events.Count == 0) return;
            HavePreloader = true;

            var first = events[0];
            if (_processStart != DateTime.MinValue)
                LaunchToBepInEx = (new DateTime(first.utc, DateTimeKind.Utc) - _processStart).TotalSeconds;

            long chainStart = 0, complete = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.text.StartsWith("Chainloader started")) chainStart = e.ticks;
                else if (e.text.StartsWith("Chainloader startup complete")) complete = e.ticks;
                else if (e.text.StartsWith("Loading ["))
                {
                    long end = i + 1 < events.Count ? events[i + 1].ticks : e.ticks;
                    Mods.Add(new ModLoad { Name = ModName(e.text), Guid = ModGuid(e.text), Seconds = (end - e.ticks) * _msPerTick / 1000 });
                }
            }
            if (chainStart != 0) BepInExToMods = (chainStart - first.ticks) * _msPerTick / 1000;
            long modsStart = events.FirstOrDefault(e => e.text.StartsWith("Loading [")).ticks;
            if (complete != 0 && modsStart != 0)
            {
                ModLoading = (complete - modsStart) * _msPerTick / 1000;
                _modsDoneTicks = complete;
            }
        }

        // "Loading [Mod Name 1.2.3] (guid)" -> "Mod Name 1.2.3" / "guid"
        private static string ModName(string t)
        {
            int a = t.IndexOf('['), b = t.LastIndexOf(']');
            return a >= 0 && b > a ? t.Substring(a + 1, b - a - 1) : t;
        }

        private static string ModGuid(string t)
        {
            int a = t.LastIndexOf('('), b = t.LastIndexOf(')');
            return a >= 0 && b > a ? t.Substring(a + 1, b - a - 1) : "";
        }

        public void OnSceneChanged(string scene, long lastFrameBoundary)
        {
            if (scene == "main" && _load == null)
            {
                _load = new WorldLoad { When = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) };
                _loadStart = lastFrameBoundary != 0 ? lastFrameBoundary : Stopwatch.GetTimestamp();
                _lastHitch = 0;
                _settleFrom = 0;
                _loadScreenSeen = false;
            }
        }

        /// <summary>
        /// Once per frame, at the end of the frame. monitorBusy = the monitor itself hitched this frame
        /// (hooking, warm-up compiling, report writing); such hitches are not blamed on loading.
        /// </summary>
        public void Tick(long now, float frameMs, bool monitorBusy, Action<LoadTimes> startupFinished, Action<WorldLoad> worldLoadFinished, Action<double> logoutFinished)
        {
            bool hitch = frameMs >= HitchMs && !monitorBusy;

            // Startup: main menu first shown, then settled.
            if (!StartupDone)
            {
                if (_menuTicks == 0 && FejdStartup.instance != null)
                {
                    _menuTicks = now;
                    ModsToMenu = (now - _modsDoneTicks) * _msPerTick / 1000;
                    _menuLastHitch = now;
                }
                else if (_menuTicks != 0)
                {
                    // A world load started before the menu settled (e.g. loaded straight away): startup ends
                    // where the load began, and the load's own steps and hitches are not counted here.
                    bool loadStarted = _load != null;
                    long end = loadStarted ? _loadStart : now;
                    if (hitch && !loadStarted) _menuLastHitch = now;
                    double quiet = (end - _menuLastHitch) * _msPerTick / 1000;
                    double since = (end - _menuTicks) * _msPerTick / 1000;
                    if (quiet >= SmoothSeconds || since >= GiveUpSeconds || loadStarted)
                    {
                        MenuSettle = (_menuLastHitch - _menuTicks) * _msPerTick / 1000;
                        if (_processStart != DateTime.MinValue)
                            TotalToMenu = (DateTime.UtcNow - _processStart).TotalSeconds - (now - _menuLastHitch) * _msPerTick / 1000;
                        StartupDone = true;
                        StartupSteps = LoadSteps.Summarize(0, end);
                        StartupStepMods = LoadPatches.Summarize(0, end);
                        startupFinished(this);
                    }
                }
            }

            // World load: scene switch -> player spawned -> loading screen gone -> no more hitches.
            if (_load != null)
            {
                double t = (now - _loadStart) * _msPerTick / 1000;
                if (double.IsNaN(_load.SceneSeconds)) _load.SceneSeconds = t;
                if (hitch) { _load.Hitches++; _lastHitch = now; }
                var player = Player.m_localPlayer;
                if (double.IsNaN(_load.SpawnSeconds) && player != null)
                    _load.SpawnSeconds = t;
                if (!double.IsNaN(_load.SpawnSeconds) && double.IsNaN(_load.ScreenSeconds))
                {
                    var hud = Hud.instance;
                    bool screenUp = hud != null && hud.m_loadingScreen != null && hud.m_loadingScreen.gameObject.activeSelf;
                    _loadScreenSeen |= screenUp;
                    if (!screenUp || hud == null)
                    {
                        _load.ScreenSeconds = t;
                        _settleFrom = now;
                        if (_lastHitch < _settleFrom) _lastHitch = _settleFrom;
                    }
                }
                if (!double.IsNaN(_load.ScreenSeconds))
                {
                    double quiet = (now - _lastHitch) * _msPerTick / 1000;
                    if (quiet >= SmoothSeconds || t >= GiveUpSeconds)
                    {
                        _load.GaveUp = quiet < SmoothSeconds;
                        _load.SmoothSeconds = (_lastHitch - _loadStart) * _msPerTick / 1000;
                        _load.Steps = LoadSteps.Summarize(_loadStart, now);
                        _load.StepMods = LoadPatches.Summarize(_loadStart, now);
                        WorldLoads.Add(_load);
                        var done = _load;
                        _load = null;
                        worldLoadFinished(done);
                    }
                }
                else if (t >= GiveUpSeconds * 3)
                {
                    _load = null; // never got in (connection failed, quit during load)
                }
            }

            // Logout: shutting down -> back at the main menu.
            var game = Game.instance;
            if (_logoutStart == 0 && game != null && game.IsShuttingDown())
            {
                _logoutStart = now;
                _load = null;
            }
            else if (_logoutStart != 0 && game == null && FejdStartup.instance != null)
            {
                double s = (now - _logoutStart) * _msPerTick / 1000;
                LastLogoutSteps = LoadSteps.Summarize(_logoutStart, now);
                LastLogoutStepMods = LoadPatches.Summarize(_logoutStart, now);
                _logoutStart = 0;
                Logouts.Add((DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), s));
                logoutFinished(s);
            }
        }

        // ---- Output -----------------------------------------------------------------------------

        public const string StartupHeader = "session,date,launch_to_bepinex_s,bepinex_setup_s,mod_loading_s,mods_to_main_menu_s,main_menu_settle_s,total_to_menu_s,mods,slowest_mods\n";
        public const string WorldHeader = "session,time,world,scene_load_s,player_spawned_s,loading_screen_gone_s,hitches_stopped_s,hitches_over_100ms,still_hitching_after_120s,plugins\n";
        public const string LogoutHeader = "session,time,logout_to_main_menu_s\n";

        public string StartupLine(string session)
        {
            string slowest = string.Join("; ", Mods.OrderByDescending(m => m.Seconds).Take(5).Select(m => $"{m.Name} {m.Seconds:0.00}s"));
            return string.Join(",", session, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                       Csv.F(LaunchToBepInEx, "0.0"), Csv.F(BepInExToMods, "0.0"), Csv.F(ModLoading, "0.0"), Csv.F(ModsToMenu, "0.0"),
                       Csv.F(MenuSettle, "0.0"), Csv.F(TotalToMenu, "0.0"), Mods.Count.ToString(CultureInfo.InvariantCulture), Csv.Q(slowest)) + "\n";
        }

        public string ModsCsv()
        {
            double total = Mods.Sum(m => m.Seconds);
            var sb = new StringBuilder("rank,mod,guid,load_s,pct_of_mod_loading\n");
            int rank = 0;
            foreach (var m in Mods.OrderByDescending(m => m.Seconds))
                sb.Append(++rank).Append(',').Append(Csv.Q(m.Name)).Append(',').Append(Csv.Q(m.Guid)).Append(',')
                  .Append(Csv.F(m.Seconds, "0.000")).Append(',').Append(Csv.F(total > 0 ? 100 * m.Seconds / total : 0, "0.0")).Append('\n');
            return sb.ToString();
        }

        public static string WorldLine(string session, WorldLoad w, string world, int plugins) =>
            string.Join(",", session, w.When, Csv.Q(world), Csv.F(w.SceneSeconds, "0.0"), Csv.F(w.SpawnSeconds, "0.0"), Csv.F(w.ScreenSeconds, "0.0"),
                Csv.F(w.SmoothSeconds, "0.0"), w.Hitches.ToString(CultureInfo.InvariantCulture), w.GaveUp ? "1" : "0", plugins.ToString(CultureInfo.InvariantCulture)) + "\n";

        public const string StepsHeader = "phase,time,step,calls,seconds\n";

        public static string StepsCsv(string phase, string when, List<(string label, int calls, double seconds)> steps, double phaseSeconds)
        {
            var sb = new StringBuilder();
            double sum = 0;
            foreach (var (label, calls, seconds) in steps)
            {
                sum += seconds;
                sb.Append(phase).Append(',').Append(when).Append(',').Append(Csv.Q(label)).Append(',').Append(calls).Append(',').Append(Csv.F(seconds, "0.000")).Append('\n');
            }
            if (!double.IsNaN(phaseSeconds) && phaseSeconds > sum)
                sb.Append(phase).Append(',').Append(when).Append(",\"Everything else (Unity loading, work spread over frames)\",,").Append(Csv.F(phaseSeconds - sum, "0.000")).Append('\n');
            return sb.ToString();
        }

        public static void AppendCsv(BackgroundWriter writer, string path, string header, string line)
        {
            bool exists = File.Exists(path);
            writer.Append(path, (exists ? "" : header) + line);
        }
    }
}
