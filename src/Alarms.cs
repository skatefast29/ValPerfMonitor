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
        private static string _lastBridge = "";

        /// <summary>
        /// Notices ValAgentBridge commands without referencing it: the tool name from Lib.GAB's ToolRegistry.CallToolAsync
        /// (network thread), and the frame the work runs in from MainThreadDispatcher.Update finding its queue non-empty.
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
            bool b = _bridgeFrame;
            _bridgeFrame = false;
            return b;
        }

        // ---- Game events (polled, main thread) -------------------------------------------------------

        private static bool _inWorld, _teleporting;
        private static float _nextSeasonPoll;
        private static ConfigEntryBase _season;
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
                        _season = info.Instance.Config.FirstOrDefault(kv => kv.Key.Section == "1 - Settings" && kv.Key.Key == "Season").Value;
                }
                catch { }
            }
            if (_season == null) return;
            string v;
            try { v = _season.BoxedValue?.ToString(); } catch { _season = null; return; }
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

        private float _lastGoWarn = -1e9f, _lastMatWarn = -1e9f;
        private int _goWarnedAt, _matWarnedAt;

        /// <summary>
        /// After each memory sample: compares with the earliest in-world sample this session that had about the same
        /// number of loaded objects (within 15%). Same world size but many more game objects or materials = something
        /// creates them and never destroys them.
        /// </summary>
        public void CheckLeak(MemoryTracker.Sample s, List<MemoryTracker.Sample> all)
        {
            if (!s.InWorld || s.Instances <= 0) return;
            var refSample = all.FirstOrDefault(x => x != s && x.InWorld && x.Instances > 0 && Math.Abs(x.Instances - s.Instances) <= 0.15 * s.Instances && x.GameObjects > 0);
            if (refSample == null) return;
            float now = Time.realtimeSinceStartup;
            string mins = ((s.SessionSeconds - refSample.SessionSeconds) / 60).ToString("0", Csv.Inv);
            int go = s.GameObjects - refSample.GameObjects;
            if (go > 20000 && go > 0.25 * refSample.GameObjects && (now - _lastGoWarn > 1800 || s.GameObjects > _goWarnedAt * 1.25))
            {
                _lastGoWarn = now; _goWarnedAt = s.GameObjects;
                Warn($"Game objects: {refSample.GameObjects:N0} -> {s.GameObjects:N0} in {mins} min, while loaded world objects stayed about the same ({refSample.Instances:N0} -> {s.Instances:N0}).",
                     $"Hidden game objects piling up: {refSample.GameObjects:N0} -> {s.GameObjects:N0}.");
            }
            int mat = s.Materials - refSample.Materials;
            if (mat > 1000 && mat > 0.3 * refSample.Materials && (now - _lastMatWarn > 1800 || s.Materials > _matWarnedAt * 1.25))
            {
                _lastMatWarn = now; _matWarnedAt = s.Materials;
                Warn($"Materials: {refSample.Materials:N0} -> {s.Materials:N0} in {mins} min, while loaded world objects stayed about the same ({refSample.Instances:N0} -> {s.Instances:N0}).",
                     $"Materials piling up: {refSample.Materials:N0} -> {s.Materials:N0}.");
            }
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
