using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Things that happened in the game which explain slow frames: teleports and portals, entering and leaving a
    /// world, a season change (Seasonality) and commands from ValAgentBridge (test tooling). Kept for the session,
    /// written to events.csv, named in frames.csv and on slow frames shortly after them.
    ///
    /// Frames in which ValAgentBridge ran a command on the main thread are left out of captures, like the monitor's
    /// own work: they are test tooling, not play. How many, and how slow, is reported per command.
    /// </summary>
    internal static class Events
    {
        public struct Ev { public double SessionSeconds; public float At; public string Kind, Detail; }

        private static readonly List<Ev> _all = new List<Ev>();
        private const int Max = 5000;
        public static int Count => _all.Count;

        /// <summary>Main thread.</summary>
        public static void Mark(string kind, string detail)
        {
            var p = Plugin.Instance;
            var e = new Ev { SessionSeconds = p != null ? p.SessionSeconds : 0, At = Time.realtimeSinceStartup, Kind = kind, Detail = detail ?? "" };
            if (_all.Count >= Max) _all.RemoveRange(0, 1000);
            _all.Add(e);
            if (p?.Writer != null && p.SessionFolder != null)
            {
                string path = Path.Combine(p.SessionFolder, "events.csv");
                p.Writer.Append(path, (_headerWritten ? "" : "time,session_s,kind,detail,recording\n") +
                    $"{DateTime.Now.ToString("HH:mm:ss", Csv.Inv)},{Csv.F(e.SessionSeconds, "0.0")},{kind},{Csv.Q(e.Detail)},{(p.Current != null ? p.Current.Number.ToString(Csv.Inv) : "")}\n");
                _headerWritten = true;
            }
        }
        private static bool _headerWritten;

        public static string Label(Ev e) => e.Detail.Length > 0 ? e.Kind + " " + e.Detail : e.Kind;

        /// <summary>Events from index <paramref name="from"/> on, joined with "; "; moves the index to the end.</summary>
        public static string Since(ref int from)
        {
            if (from > _all.Count) from = 0; // the list was trimmed
            if (from == _all.Count) return "";
            var s = string.Join("; ", _all.Skip(from).Take(10).Select(Label).ToArray()) + (_all.Count - from > 10 ? $"; +{_all.Count - from - 10} more" : "");
            from = _all.Count;
            return s;
        }

        /// <summary>The latest event in the last <paramref name="seconds"/>, as "after season change to Winter (0.4 s)"; null if none.</summary>
        public static string Recent(float now, float seconds, out string kind)
        {
            kind = null;
            if (_all.Count == 0) return null;
            var e = _all[_all.Count - 1];
            if (now - e.At > seconds) return null;
            kind = e.Kind;
            return $"after {Label(e)} ({(now - e.At).ToString("0.0", Csv.Inv)} s)";
        }

        public static List<Ev> Last(int n) => _all.Skip(Math.Max(0, _all.Count - n)).ToList();

        // ---- ValAgentBridge commands ----------------------------------------------------------------

        private static readonly ConcurrentQueue<string> _bridgeNames = new ConcurrentQueue<string>();
        [ThreadStatic] private static int _callDepth;
        private static Func<bool> _queueEmpty;
        private static bool _bridgeFrame;
        // ValAgentBridge 1.8.0+: MainThreadDispatcher.LastWorkFrame, the last Time.frameCount in which it ran tool work.
        private static AccessTools.FieldRef<int> _lastWorkFrame;
        private static string _lastBridge = "";

        /// <summary>
        /// Notices ValAgentBridge commands without referencing it: the tool name from Lib.GAB's ToolRegistry.CallToolAsync
        /// (network thread), and the frame the work runs in: from the bridge's own record (MainThreadDispatcher.LastWorkFrame,
        /// 1.8.0 and later), or for older bridges from MainThreadDispatcher.Update finding its queue non-empty.
        /// Stands down with a log line if either is not as expected.
        /// </summary>
        /// <returns>false while the bridge's assemblies are not loaded yet (call again later).</returns>
        public static bool WatchBridge(Harmony harmony, ManualLogSource log)
        {
            var registry = AccessTools.TypeByName("Lib.GAB.Tools.ToolRegistry");
            var dispatcher = AccessTools.TypeByName("ValAgentBridge.MainThreadDispatcher");
            if (registry == null || dispatcher == null) return false; // no bridge (yet)
            try
            {
                var call = AccessTools.Method(registry, "CallToolAsync", new[] { typeof(string), typeof(object) });
                var workFrame = AccessTools.Field(dispatcher, "LastWorkFrame");
                if (call != null && workFrame != null && workFrame.IsStatic && workFrame.FieldType == typeof(int))
                {
                    _lastWorkFrame = AccessTools.StaticFieldRefAccess<int>(workFrame);
                    harmony.Patch(call, prefix: new HarmonyMethod(typeof(Events), nameof(CallPrefix)), finalizer: new HarmonyMethod(typeof(Events), nameof(CallFinalizer)));
                    return true;
                }
                var update = AccessTools.Method(dispatcher, "Update");
                var queueField = AccessTools.Field(dispatcher, "ExecutionQueue");
                var queue = queueField != null && queueField.IsStatic ? queueField.GetValue(null) as ConcurrentQueue<Action> : null;
                if (call == null || update == null || queue == null)
                {
                    log.LogInfo("ValAgentBridge is not as expected: its commands are not labelled or left out of captures.");
                    return true;
                }
                _queueEmpty = () => queue.IsEmpty;
                harmony.Patch(call, prefix: new HarmonyMethod(typeof(Events), nameof(CallPrefix)), finalizer: new HarmonyMethod(typeof(Events), nameof(CallFinalizer)));
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(Events), nameof(DispatchPrefix)));
            }
            catch (Exception e) { log.LogInfo($"ValAgentBridge commands are not labelled: {e.Message}"); }
            return true;
        }

        // ToolGuard calls an inner registry from inside the outer call: only the outermost call is a command.
        private static void CallPrefix(string name)
        {
            if (_callDepth++ == 0 && name != null) _bridgeNames.Enqueue(name);
        }

        private static void CallFinalizer() { if (_callDepth > 0) _callDepth--; }

        private static void DispatchPrefix()
        {
            try { if (_queueEmpty != null && !_queueEmpty()) _bridgeFrame = true; }
            catch { _queueEmpty = null; }
        }

        /// <summary>Main thread, once per frame: new command names become events. True if bridge work ran this frame.</summary>
        public static bool TakeBridgeFrame(out string name)
        {
            while (_bridgeNames.TryDequeue(out var n))
            {
                _lastBridge = n;
                Mark("bridge", n);
            }
            name = _lastBridge;
            if (_lastWorkFrame != null)
            {
                try { return _lastWorkFrame() == Time.frameCount; }
                catch { _lastWorkFrame = null; return false; }
            }
            bool b = _bridgeFrame;
            _bridgeFrame = false;
            return b;
        }

        // ---- Game events (polled, main thread) -------------------------------------------------------

        private static bool _inWorld, _teleporting;
        private static float _nextSeasonPoll;
        private static ConfigEntryBase _season;
        private static FieldInfo _seasonNow;
        private static bool _seasonLooked;
        private static string _seasonValue;

        public static void Poll()
        {
            var player = Player.m_localPlayer;
            bool inWorld = player != null && ZNetScene.instance != null;
            if (inWorld != _inWorld)
            {
                _inWorld = inWorld;
                Mark(inWorld ? "login" : "logout", inWorld ? "entered the world" : "left the world (logout or death)");
                if (inWorld) FirstOpens.OnLogin();
            }
            bool tp = false;
            try { tp = inWorld && player.IsTeleporting(); } catch { }
            if (tp && !_teleporting) Mark("teleport", "teleport or portal");
            _teleporting = tp;

            float now = Time.realtimeSinceStartup;
            if (now < _nextSeasonPoll) return;
            _nextSeasonPoll = now + 1f;
            if (!_seasonLooked && inWorld)
            {
                _seasonLooked = true;
                try
                {
                    if (Chainloader.PluginInfos.TryGetValue("RustyMods.Seasonality", out var info) && info.Instance != null)
                    {
                        // 3.8.5+: the season in play (its 'season set' command and a client's server sync skip the setting).
                        _seasonNow = info.Instance.GetType().Assembly.GetType("Seasonality.SeasonTime", false)
                            ?.GetField("m_currentSeason", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        _season = info.Instance.Config.FirstOrDefault(kv => kv.Key.Section == "1 - Settings" && kv.Key.Key == "Season").Value;
                    }
                }
                catch { }
            }
            if (_season == null && _seasonNow == null) return;
            string v;
            try { v = (_seasonNow != null ? _seasonNow.GetValue(null) : _season.BoxedValue)?.ToString(); }
            catch { _season = null; _seasonNow = null; return; }
            if (_seasonValue != null && v != _seasonValue) Mark("season", $"change to {v} (Seasonality)");
            _seasonValue = v;
        }
    }

    /// <summary>
    /// Live warnings while playing (log line + on-screen message):
    ///  - log floods: one message repeated more than "Log flood: lines per second" times a second;
    ///  - hidden objects piling up: game objects or materials growing while the world around you (loaded objects) does not;
    ///  - pathfinding links nearing Unity's limit (about 65,535), after which the game logs "Failed to allocate NavMeshLink".
    /// </summary>
    internal sealed class Alarms
    {
        private readonly Plugin _p;
        private float _nextSecond;
        private long _lastLogTicks;
        public double LastSecondLogMs;

        public Alarms(Plugin p) { _p = p; _lastLogTicks = LogWatch.MainThreadTicks; }

        public void Tick()
        {
            Events.Poll();
            float now = Time.realtimeSinceStartup;
            if (now < _nextSecond) return;
            _nextSecond = now + 1f;
            long t = LogWatch.MainThreadTicks;
            LastSecondLogMs = LogWatch.TicksToMs(t - _lastLogTicks);
            _lastLogTicks = t;
            CheckFloods();
            if (now >= _nextPath) SamplePathfinding(now);
        }

        // ---- Log floods ---------------------------------------------------------------------------------

        private void CheckFloods()
        {
            var w = LogWatch.Instance;
            if (w == null) return;
            foreach (var f in w.TakeNewFloods())
            {
                double now = LogWatch.Now;
                if (now - f.LastWarned < 300) continue; // once per message every 5 minutes
                f.LastWarned = now;
                string mod = LogWatch.FloodOwner(f, _p.Registry);
                string again = f.FloodSeconds > 1 ? $" (flooding again: {f.Total:N0} lines so far)" : "";
                string cost = LogWatch.Timed ? $" Writing the log took {LastSecondLogMs.ToString("0", Csv.Inv)} ms of game time in the last second." : "";
                _p.Log.LogWarning($"Log flood: {mod} wrote the same {f.Level} {f.PeakPerSecond} times in one second{again}: \"{Short(f.Message, 140)}\".{cost} " +
                                     $"A message repeated this often costs frame time and fills the log. Details: perf/log_errors.");
                if (f.Message.IndexOf("NavMeshLink", StringComparison.Ordinal) >= 0) _nextPath = 0; // check the links right away
                // On screen only in a world: floods while loading (Unity's shader warnings at every launch) are logged, not shown.
                if (Player.m_localPlayer != null)
                    _p.ShowToast($"Log flood: {mod} repeats \"{Short(f.Message, 60)}\" {f.PeakPerSecond}x per second.", 12f);
            }
        }

        internal static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

        // ---- Pathfinding ----------------------------------------------------------------------------------

        public const int LinkLimit = 65535;
        public int Tiles = -1, Links = -1, LinksQueued = -1, TilesQueued = -1;
        private float _nextPath;
        private bool _pathBroken, _pathHeader;
        private float _lastPathWarn = -1e9f;
        private static FieldInfo _tilesF, _linkQueueF, _tileQueueF, _links1F, _links2F;

        /// <summary>
        /// Every 10 s in a world: navigation-mesh tiles, the links that stitch them together (each one a Unity
        /// NavMeshLink) and links waiting to be removed (still allocated). Written to pathfinding.csv.
        /// </summary>
        private void SamplePathfinding(float now)
        {
            _nextPath = now + 10f;
            var pf = Pathfinding.instance;
            if (_pathBroken || pf == null || Player.m_localPlayer == null) return;
            try
            {
                if (_tilesF == null)
                {
                    _tilesF = AccessTools.Field(typeof(Pathfinding), "m_tiles");
                    _linkQueueF = AccessTools.Field(typeof(Pathfinding), "m_linkRemoveQueue");
                    _tileQueueF = AccessTools.Field(typeof(Pathfinding), "m_tileRemoveQueue");
                    var tileType = AccessTools.Inner(typeof(Pathfinding), "NavMeshTile");
                    _links1F = tileType == null ? null : AccessTools.Field(tileType, "m_links1");
                    _links2F = tileType == null ? null : AccessTools.Field(tileType, "m_links2");
                    if (_tilesF == null || _linkQueueF == null || _links1F == null || _links2F == null)
                    {
                        _pathBroken = true;
                        _p.Log.LogInfo("The game's pathfinding code is not as expected: pathfinding links are not counted.");
                        return;
                    }
                }
                var tiles = (IDictionary)_tilesF.GetValue(pf);
                int links = 0;
                foreach (var tile in tiles.Values)
                {
                    if (tile == null) continue;
                    links += ((ICollection)_links1F.GetValue(tile))?.Count ?? 0;
                    links += ((ICollection)_links2F.GetValue(tile))?.Count ?? 0;
                }
                Tiles = tiles.Count;
                Links = links;
                LinksQueued = ((ICollection)_linkQueueF.GetValue(pf))?.Count ?? 0;
                TilesQueued = _tileQueueF == null ? -1 : ((ICollection)_tileQueueF.GetValue(pf))?.Count ?? 0;
            }
            catch (Exception e)
            {
                _pathBroken = true;
                _p.Log.LogInfo($"Pathfinding links are not counted: {e.Message}");
                return;
            }
            _p.MarkHeavy();
            if (_p.Writer != null)
            {
                _p.Writer.Append(Path.Combine(_p.SessionFolder, "pathfinding.csv"), (_pathHeader ? "" : "time,session_s,tiles,links,links_waiting_removal,tiles_waiting_removal,links_in_use_pct_of_limit,where\n") +
                    $"{DateTime.Now.ToString("HH:mm:ss", Csv.Inv)},{Csv.F(_p.SessionSeconds, "0")},{Tiles},{Links},{LinksQueued},{TilesQueued},{Csv.F(100.0 * (Links + LinksQueued) / LinkLimit, "0.0")},{Csv.Q(_p.Context.Short)}\n");
                _pathHeader = true;
            }
            int used = Links + LinksQueued;
            if (used >= LinkLimit * 0.75 && now - _lastPathWarn > 300)
            {
                _lastPathWarn = now;
                _p.Log.LogWarning($"Pathfinding links: {used:N0} in use ({Links:N0} in {Tiles:N0} tiles, {LinksQueued:N0} waiting to be removed), " +
                                     $"{100.0 * used / LinkLimit:0}% of Unity's limit of about {LinkLimit:N0}. At the limit the game logs \"Failed to allocate NavMeshLink\" " +
                                     "every frame and creatures stop finding paths. Usually caused by many navigation tiles staying loaded (sailing, big areas built up).");
                _p.ShowToast($"Pathfinding links at {100.0 * used / LinkLimit:0}% of Unity's limit ({used:N0}).", 12f);
            }
        }

        // ---- Hidden objects piling up ----------------------------------------------------------------

        private float _lastGoWarn = -1e9f, _lastMatWarn = -1e9f, _lastLiveWarn = -1e9f, _lastLocNote = -1e9f;
        private double _liveWarnedAt;

        /// <summary>
        /// Managed memory at its lowest point after the latest garbage collection (MB). The game doesn't clear everything in
        /// one go, so this still holds some garbage (a few hundred MB more than a forced full clean-up shows), but it moves
        /// with what is really kept. NaN before the first collection.
        /// </summary>
        public double LiveAfterGcMb = double.NaN;

        private int _gcWindow;
        private long _minUsed;

        /// <summary>
        /// Every frame from the frame loop (gc = a garbage collection finished in it). The game frees memory over several
        /// frames after a collection, so the lowest reading over the next 60 frames is taken.
        /// One cheap native call per frame, only in those 60 frames.
        /// </summary>
        public void FrameMemory(bool gc)
        {
            if (gc) { _gcWindow = 60; _minUsed = long.MaxValue; }
            if (_gcWindow <= 0) return;
            try
            {
                long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                if (used > 0 && used < _minUsed) _minUsed = used;
            }
            catch { _gcWindow = 0; return; }
            if (--_gcWindow == 0 && _minUsed != long.MaxValue) LiveAfterGcMb = _minUsed / 1048576.0;
        }
        private int _goWarnedAt, _matWarnedAt;

        /// <summary>
        /// After each memory sample: compares with the earliest in-world sample this session taken at the same spot (within
        /// 100 m) with about the same number of loaded objects (within 15%). Same place, same world size, but many more game
        /// objects, materials or managed memory (measured just after a garbage collection, so garbage doesn't count) =
        /// something creates them and never lets them go. A relog test showed mods keeping ~300 MB more after every logout
        /// and login. Different places are never compared: a Dvergr town has many more parts per loaded object than a forest.
        /// </summary>
        public void CheckLeak(MemoryTracker.Sample s, List<MemoryTracker.Sample> all)
        {
            if (!s.InWorld || s.Instances <= 0) return;
            var refSample = all.FirstOrDefault(x => x != s && x.InWorld && x.Instances > 0 && Math.Abs(x.Instances - s.Instances) <= 0.15 * s.Instances && x.GameObjects > 0 && SameSpot(x, s));
            if (refSample == null) return;
            float now = Time.realtimeSinceStartup;
            string mins = ((s.SessionSeconds - refSample.SessionSeconds) / 60).ToString("0", Csv.Inv);
            // Game objects in locations and dungeon rooms are left out: exploring loads them, it is not a leak.
            bool locKnown = s.LocationObjects >= 0 && refSample.LocationObjects >= 0;
            int locGrew = locKnown ? s.LocationObjects - refSample.LocationObjects : 0;
            int goRef = refSample.GameObjects - (locKnown ? refSample.LocationObjects : 0);
            int goNow = s.GameObjects - (locKnown ? s.LocationObjects : 0);
            int go = goNow - goRef;
            if (go > 20000 && go > 0.25 * goRef && (now - _lastGoWarn > 1800 || goNow > _goWarnedAt * 1.25))
            {
                _lastGoWarn = now; _goWarnedAt = goNow;
                string loc = locKnown ? $" Not counting game objects in locations ({refSample.LocationObjects:N0} -> {s.LocationObjects:N0})." : "";
                Warn($"Game objects: {goRef:N0} -> {goNow:N0} in {mins} min, while loaded world objects stayed about the same ({refSample.Instances:N0} -> {s.Instances:N0}).{loc}",
                     $"Hidden game objects piling up: {goRef:N0} -> {goNow:N0}.");
            }
            else if (locKnown && s.GameObjects - refSample.GameObjects > 20000 && s.GameObjects - refSample.GameObjects > 0.25 * refSample.GameObjects
                     && now - _lastLocNote > 1800)
            {
                _lastLocNote = now;
                _p.Log.LogInfo($"Game objects: {refSample.GameObjects:N0} -> {s.GameObjects:N0} in {mins} min. Not a leak: {locGrew:N0} of them are in locations " +
                               "loaded while exploring (the game loads a location when you first come near one, and Jotunn keeps a copy of modded ones).");
            }
            // Locations bring their own materials, so while exploring (many more location objects) this is a note, not a warning.
            int mat = s.Materials - refSample.Materials;
            bool exploring = locGrew > 20000;
            if (mat > 1000 && mat > 0.3 * refSample.Materials && exploring && now - _lastMatWarn > 1800)
            {
                _lastMatWarn = now;
                _p.Log.LogInfo($"Materials: {refSample.Materials:N0} -> {s.Materials:N0} in {mins} min while exploring ({locGrew:N0} more game objects in locations): " +
                               "new locations bring their own materials. Not counted as a leak.");
            }
            else if (mat > 1000 && mat > 0.3 * refSample.Materials && (now - _lastMatWarn > 1800 || s.Materials > _matWarnedAt * 1.25))
            {
                _lastMatWarn = now; _matWarnedAt = s.Materials;
                Warn($"Materials: {refSample.Materials:N0} -> {s.Materials:N0} in {mins} min, while loaded world objects stayed about the same ({refSample.Instances:N0} -> {s.Instances:N0}).",
                     $"Materials piling up: {refSample.Materials:N0} -> {s.Materials:N0}.");
            }
            // Only moments at least 30 s after entering the world: right after loading in, the world is still filling memory.
            var liveRef = all.FirstOrDefault(x => x != s && x.InWorld && x.Instances > 0 && Math.Abs(x.Instances - s.Instances) <= 0.15 * s.Instances
                                                  && !double.IsNaN(x.LiveAfterGcMb) && x.WorldSeconds >= 30 && SameSpot(x, s));
            if (liveRef != null && !double.IsNaN(s.LiveAfterGcMb) && s.WorldSeconds >= 30)
            {
                double grew = s.LiveAfterGcMb - liveRef.LiveAfterGcMb;
                // A high bar: the reading still includes some garbage, which comes and goes.
                if (grew > 500 && grew > 0.4 * liveRef.LiveAfterGcMb && (now - _lastLiveWarn > 1800 || s.LiveAfterGcMb > _liveWarnedAt * 1.2))
                {
                    _lastLiveWarn = now; _liveWarnedAt = s.LiveAfterGcMb;
                    string lmins = ((s.SessionSeconds - liveRef.SessionSeconds) / 60).ToString("0", Csv.Inv);
                    Warn($"Mod and game memory (lowest point after garbage collection): {liveRef.LiveAfterGcMb:0} -> {s.LiveAfterGcMb:0} MB in {lmins} min, while loaded world objects stayed about the same ({liveRef.Instances:N0} -> {s.Instances:N0}). More live memory also makes every garbage-collection stutter longer.",
                         $"Memory piling up: {liveRef.LiveAfterGcMb:0} -> {s.LiveAfterGcMb:0} MB.");
                }
            }
        }

        private static bool SameSpot(MemoryTracker.Sample a, MemoryTracker.Sample b)
        {
            if (float.IsNaN(a.X) || float.IsNaN(b.X)) return float.IsNaN(a.X) && float.IsNaN(b.X);
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz <= 100f * 100f;
        }

        private void Warn(string what, string toast)
        {
            var recent = Events.Last(200).Where(e => e.Kind != "bridge").GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}").ToArray();
            _p.Log.LogWarning($"Possible leak: {what} Something keeps creating them without removing them" +
                                 (recent.Length > 0 ? $" (events so far this session: {string.Join(", ", recent)})" : "") +
                                 ". F11 -> Memory census (or perf/memory_census) lists them by name. Trend: memory.csv.");
            _p.ShowToast("Possible leak: " + toast + " Run a memory census (F11) to see what they are.", 15f);
        }
    }
}
