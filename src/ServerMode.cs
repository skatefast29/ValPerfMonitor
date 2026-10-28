using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValPerfMonitor
{
    /// <summary>
    /// On a dedicated server the monitor does none of its usual work (no probes on mods, no panel, no tests).
    /// It only writes down what the game already knows, once a second, so it costs next to nothing:
    ///  - server.csv: frame rate, worst frame, players, world objects, traffic, the worst player's ping
    ///  - server_players.csv (every 5 s): each player's ping, whether they are still joining, traffic, upload backlog, position
    ///  - server_hitches.csv: every frame slower than the threshold, and whether a world save, a garbage collection or
    ///    the game's asset clean-up ran in it
    ///  - memory_census_*.txt: once per run with nobody on, what keeps the server's memory (optional)
    ///  - server_network.csv / server_network_summary.csv: network messages per name and mod (optional)
    ///  - server_startup_*.csv: the loading steps split by mod, and each slow startup frame split into what ran in it
    ///    (timers only until startup is done; startup is reported once and left out of the summaries)
    /// A summary goes to the log every few minutes, with the time this monitor itself used.
    /// Files: BepInEx\ValPerfMonitor\server_&lt;date&gt;\.
    /// </summary>
    internal sealed class ServerMode
    {
        public const string RowHeader = "time,uptime_s,players,fps,frame_ms_avg,worst_frame_ms,slow_frames,world_objects,objects_sent_s,objects_received_s," +
                                        "upload_kb_s,download_kb_s,worst_ping_ms,worst_ping_player,worst_backlog_kb,gc,heap_mb,saving\n";
        public const string PlayerHeader = "time,player,game_ping_ms,joining,to_player_kb_s,from_player_kb_s,backlog_kb,seconds_since_answer,pos_x,pos_z\n";
        public const string HitchHeader = "time,uptime_s,frame_ms,players,world_save,world_objects,garbage_collection,asset_cleanup,player_joined_left\n";

        /// <summary>No graphics device, or started with -batchmode: the dedicated server.</summary>
        public static bool Detect()
        {
            try { return Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null; }
            catch { return false; }
        }

        private static readonly AccessTools.FieldRef<ZRpc, float> SinceAnswer = Safe(() => AccessTools.FieldRefAccess<ZRpc, float>("m_timeSinceLastPing"));
        private static readonly AccessTools.FieldRef<ZRpc, float> PingTimer = Safe(() => AccessTools.FieldRefAccess<ZRpc, float>("m_pingTimer"));
        private static readonly AccessTools.FieldRef<ZRpc, int> SentData = Safe(() => AccessTools.FieldRefAccess<ZRpc, int>("m_sentData"));
        private static readonly AccessTools.FieldRef<ZRpc, int> RecvData = Safe(() => AccessTools.FieldRefAccess<ZRpc, int>("m_recvData"));

        /// <summary>
        /// One player's connection from the game's own bookkeeping, which works whatever Steam says: the game pings every
        /// player once a second (the round trip is the game ping), and counts the bytes it sends and receives.
        /// </summary>
        private sealed class PeerWatch
        {
            public float LastTimer, LastSince, SentAt = -1, Ping = float.NaN, CountedAt, FirstSeen = Time.realtimeSinceStartup;
            public int LastSent, LastRecv;
            public float ToKbs, FromKbs;
            public bool Counted;
        }

        private const float JoinGraceSeconds = 30f;
        private readonly Dictionary<ZRpc, PeerWatch> _watch = new Dictionary<ZRpc, PeerWatch>();
        private readonly List<ZRpc> _gone = new List<ZRpc>();
        private bool _steamReported;

        private PeerWatch Watch(ZRpc rpc)
        {
            if (!_watch.TryGetValue(rpc, out var w))
            {
                _watch[rpc] = w = new PeerWatch();
                if (PingTimer != null) w.LastTimer = PingTimer(rpc);
                if (SinceAnswer != null) w.LastSince = SinceAnswer(rpc);
            }
            return w;
        }

        /// <summary>Every frame: spot the game's ping going out (its timer starts over) and the answer coming back.</summary>
        private void WatchPings(ZNet znet, float now)
        {
            if (PingTimer == null || SinceAnswer == null) return;
            foreach (var peer in znet.GetPeers())
            {
                var rpc = peer?.m_rpc;
                if (rpc == null) continue;
                var w = Watch(rpc);
                float timer = PingTimer(rpc), since = SinceAnswer(rpc);
                if (timer < w.LastTimer && w.SentAt < 0) w.SentAt = now;
                if (since < w.LastSince && w.SentAt >= 0) { w.Ping = (now - w.SentAt) * 1000f; w.SentAt = -1; }
                w.LastTimer = timer; w.LastSince = since;
            }
        }

        /// <summary>Once a second: bytes to and from each player since the last count.</summary>
        private void CountTraffic(ZNet znet, float now)
        {
            if (SentData == null || RecvData == null) return;
            foreach (var peer in znet.GetPeers())
            {
                var rpc = peer?.m_rpc;
                if (rpc == null) continue;
                var w = Watch(rpc);
                int sent = SentData(rpc), recv = RecvData(rpc);
                float dt = now - w.CountedAt;
                if (w.Counted && dt > 0.1f)
                {
                    w.ToKbs = Math.Max(0, sent - w.LastSent) / 1024f / dt;
                    w.FromKbs = Math.Max(0, recv - w.LastRecv) / 1024f / dt;
                }
                w.LastSent = sent; w.LastRecv = recv; w.CountedAt = now; w.Counted = true;
            }
            _gone.Clear();
            foreach (var rpc in _watch.Keys)
            {
                bool found = false;
                foreach (var peer in znet.GetPeers()) if (peer?.m_rpc == rpc) { found = true; break; }
                if (!found) _gone.Add(rpc);
            }
            foreach (var rpc in _gone) _watch.Remove(rpc);
        }
        private static readonly System.Reflection.FieldInfo SteamCon = AccessTools.Field(typeof(ZSteamSocket), "m_con");

        /// <summary>
        /// A player's connection as Steam sees it. The game's own ZSteamSocket.GetConnectionQuality asks Steam's client
        /// interface, which isn't running on a dedicated server, so it always fails there; this asks the server interface.
        /// </summary>
        private bool Quality(ISocket socket, out float ql, out float qr, out int ping, out float outB, out float inB)
        {
            ql = qr = outB = inB = 0; ping = 0;
            try
            {
                if (socket is ZSteamSocket && SteamCon != null) return SteamQuality(socket, out ql, out qr, out ping, out outB, out inB);
                socket.GetConnectionQuality(out ql, out qr, out ping, out outB, out inB);
                return true;
            }
            catch (Exception e)
            {
                SteamUnavailable($"{e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>Said once per server run: Steam's ping isn't there to fall back on, the game ping and traffic still work.</summary>
        private void SteamUnavailable(string why)
        {
            if (_steamReported) return;
            _steamReported = true;
            _log.LogInfo($"Steam's connection details are not available on this server ({why}); the game's own ping and traffic per player are recorded instead.");
        }

        private bool SteamQuality(ISocket socket, out float ql, out float qr, out int ping, out float outB, out float inB)
        {
            ql = qr = outB = inB = 0; ping = 0;
            var con = (HSteamNetConnection)SteamCon.GetValue(socket);
            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t lanes = default;
            var result = SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(con, ref status, 0, ref lanes);
            if (result != EResult.k_EResultOK) { SteamUnavailable("Steam answered " + result); return false; }
            ql = status.m_flConnectionQualityLocal; qr = status.m_flConnectionQualityRemote; ping = status.m_nPing;
            outB = status.m_flOutBytesPerSec; inB = status.m_flInBytesPerSec;
            return true;
        }

        /// <summary>
        /// Still joining: the first 30 s, or no position yet (the player's character isn't in the world). A rejoin after a
        /// drop counts again, since the game makes a new connection for it.
        /// </summary>
        private static bool Joining(ZNetPeer peer, PeerWatch w, float now) =>
            now - w.FirstSeen < JoinGraceSeconds || (peer.m_refPos.x == 0 && peer.m_refPos.z == 0);

        private static int Backlog(ISocket socket)
        {
            try { return socket.GetSendQueueSize(); } catch { return 0; }
        }

        private static AccessTools.FieldRef<TO, TF> Safe<TO, TF>(Func<AccessTools.FieldRef<TO, TF>> f)
        {
            try { return f(); } catch { return null; }
        }

        private readonly Plugin _p;
        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _on, _network, _startupSplit, _census;
        private readonly ConfigEntry<float> _hitchMs, _summaryMinutes;
        private BackgroundWriter _writer;
        private ModRegistry _registry;
        private NetworkStats _net;
        private string _folder;
        private readonly StringBuilder _rows = new StringBuilder(), _players = new StringBuilder();
        private float _started;
        private bool _seenFrame;
        private float _secondStart, _nextPlayers, _nextFlush, _nextNetwork, _nextSummary, _lastNetworkAt;
        private bool _hitchHeader;
        private readonly List<ZNetPeer> _peers = new List<ZNetPeer>();

        // This second.
        private int _frames, _slow;
        private float _frameSum, _worst;
        private int _gcAt, _gcFrame;

        // Since the last log summary.
        private int _sFrames, _sSlow, _sHitches, _sSaves, _sSeconds, _sHitchSave, _sHitchGc, _sHitchCleanup, _sHitchJoin;

        // Players joining and leaving: the server does a burst of work then (sending a joining player the world around
        // them, handing a leaving player's objects to others), so a slow frame within a few seconds of it is labelled.
        private const float JoinLeaveSeconds = 3f;
        private int _peerCount = -1, _playerCount = -1;
        private float _joinLeaveAt = -100;
        private string _joinLeave = "";
        private float _sFrameSum, _sWorst, _sWorstPing;
        private double _sPlayers;
        private string _sWorstPingPlayer = "";
        private long _selfTicks;

        // Network messages per name at the last summary, so the summary shows only its own minutes.
        private readonly Dictionary<NetworkStats.Entry, long> _netAtSummary = new Dictionary<NetworkStats.Entry, long>();
        private float _lastSummaryAt;

        // World saves: the main-thread freeze when one starts, then the background write.
        private bool _saving;
        private float _saveStartedAt = -100, _saveFreezeMs;

        // Startup (launch until the world is loaded and frames are smooth, or the first player joins): reported
        // once on its own and kept out of the summaries. Its slow frames are split into the timed steps and mods.
        private const float StartupQuietSeconds = 10f, StartupMaxSeconds = 600f;
        private bool _inStartup = true, _splitting;
        private float _worldReadyAt = -1, _lastSlowAt;
        private long _lastTick;
        private readonly List<(float at, float ms, long from, long to, string cleanup)> _startupFrames = new List<(float, float, long, long, string)>();

        // Memory census: once per server run, after startup, while nobody is on (it stops if someone joins and tries again later).
        private const float CensusEmptySeconds = 120f;
        private const int CensusSliceMs = 30;
        private MemoryCensus _runningCensus;
        private bool _censusDone;
        private float _emptySince = -1;
        private int _censusEndedFrame = -1;

        public ServerMode(Plugin p, ConfigFile cfg, ManualLogSource log)
        {
            _p = p;
            _log = log;
            const string section = "7 - Dedicated server";
            _on = cfg.Bind(section, "Server logging", true, "On a dedicated server, write down frame rate, players' pings and traffic, slow frames and world saves once a second. Nothing else of this monitor runs on a server, apart from the startup timing below.");
            _network = cfg.Bind(section, "Count network messages per mod", true, "Also count every network message the server handles, per name and per mod (server_network.csv). Costs well under a millisecond per second.");
            _hitchMs = cfg.Bind(section, "Slow frame ms", 100f, "Server frames slower than this are listed in server_hitches.csv. Players feel a freeze when the server stops for a few hundred ms.");
            _summaryMinutes = cfg.Bind(section, "Log summary every (minutes)", 10f, "How often a short summary goes to the server log (LogOutput.log). 0 = only when the server shuts down.");
            _startupSplit = cfg.Bind(section, "Split startup by mod", true, "Time the server's loading steps and its first frames after the world loads, with every mod's patch on them, so a slow startup is explained (server_startup_*.csv and the log). The timers come off once startup is done.");
            _census = cfg.Bind(section, "Memory census when empty", false, "Once per server run, when nobody has been on for 2 minutes after startup, count what keeps the server's memory, per mod (memory_census_*.txt and the log). Off by default: it is the one heavy job here. It takes a minute or two in small slices, then times three full garbage collections (each a short freeze, with nobody on); it stops at once if a player joins and tries again later.");
            if (cfg.Bind(section, "Install the patcher if missing", true, "When BepInEx/patchers has no ValPerfMonitor.Preloader.dll, write the copy this plugin carries there (it works from the next restart). It keeps past session logs and times startup per mod.").Value)
                PreloaderInstall.EnsureOnServer(log);
            // Before the world scene loads, so its loading steps are timed (only the game's own methods here; mods' patches at Start).
            if (_on.Value && _startupSplit.Value) LoadSteps.Attach(Plugin.Guid + ".loadsteps", log);
        }

        public void Start()
        {
            if (!_on.Value)
            {
                _log.LogInfo("Dedicated server: server logging is off (setting \"Server logging\").");
                return;
            }
            _folder = Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor", "server_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Csv.Inv));
            SessionCheck.RememberFolder(_folder);
            _writer = new BackgroundWriter(_log);
            _registry = new ModRegistry(60);
            _registry.LoadPlugins(typeof(Plugin).Assembly);
            float now = Time.realtimeSinceStartup;
            _started = now;
            _secondStart = now;
            _nextPlayers = now + 5f;
            _nextFlush = now + 10f;
            _nextNetwork = _lastNetworkAt = now + 60f;
            _nextSummary = now + SummarySeconds;
            _lastSummaryAt = now;
            _gcAt = _gcFrame = GC.CollectionCount(0);
            AssetCleanup.Attach(Plugin.Guid + ".assetcleanup", _log);
            if (_startupSplit.Value)
            {
                try
                {
                    var guard = new SafetyGuard(Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor"), _log);
                    guard.Prime();
                    LoadSteps.AttachFrameSteps(Plugin.Guid + ".serverframes", _log); // before Arm, so mods' patches on the frame loops get timers too
                    LoadPatches.Init(Plugin.Guid + ".loadpatches", Plugin.Guid, _registry, guard, _log);
                    LoadPatches.Arm(atMainMenu: false);
                    _splitting = true;
                }
                catch (Exception e) { _log.LogWarning($"Startup timing could not be set up: {e.Message}"); }
            }
            _writer.Write(Path.Combine(_folder, "server.csv"), RowHeader);
            _writer.Write(Path.Combine(_folder, "server_players.csv"), PlayerHeader);
            _log.LogInfo($"Dedicated server: logging frame rate, players' connections and slow frames once a second (no mod timing on a server{(_splitting ? " after startup" : "")}). Files: {_folder}");
            _log.LogInfo($"  The server runs at most {(Application.targetFrameRate > 0 ? Application.targetFrameRate + " FPS" : "as fast as it can (no cap set yet; the game sets 30 FPS once loaded)")}: at that cap it is keeping up; slow frames are what players feel.");
        }

        private float SummarySeconds => _summaryMinutes.Value > 0 ? Mathf.Max(1, _summaryMinutes.Value) * 60f : float.MaxValue;

        /// <summary>Every frame (Plugin.Update).</summary>
        public void Tick()
        {
            if (_writer == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try { Frame(t0); }
            catch (Exception e) { _log.LogDebug($"Server logging: {e.Message}"); }
            _selfTicks += Stopwatch.GetTimestamp() - t0;
        }

        private void Frame(long tick)
        {
            // The frame that just ended ran from the last tick to this one (what a slow startup frame is split into).
            long frameFrom = _lastTick;
            _lastTick = tick;
            float now = Time.realtimeSinceStartup;
            var znet = ZNet.instance;
            if (_net == null && _network.Value && znet != null && ZRoutedRpc.instance != null)
            {
                _net = new NetworkStats(_registry, _log) { Recording = true };
                _net.Attach(new Harmony(Plugin.Guid + ".servernet"));
            }

            if (znet != null) WatchPings(znet, now);
            // Measured by the clock from the last Update to this one. Unity's own frame time is wrong around the world
            // load on the server: it gave 6.4 s for the frame that loaded the world (12 s of timed steps in it) and 14.1 s
            // for the 1 s frame after it, a "freeze" that never happened (2026-10-27 log).
            float ms = frameFrom > 0 ? (float)((tick - frameFrom) * 1000.0 / Stopwatch.Frequency) : Time.unscaledDeltaTime * 1000f;
            int frame = Time.frameCount - 1; // the frame that just ended, which ms is about
            int gcNow = GC.CollectionCount(0);
            bool gcFrame = gcNow != _gcFrame;
            _gcFrame = gcNow;
            // Frames the memory census worked in (and its timed full collections) are this monitor's work, not the
            // server's: left out of frame rate and slow frames. Everything else goes on as usual.
            bool own = _runningCensus != null || frame <= _censusEndedFrame;
            if (!own)
            {
                _frames++; _frameSum += ms;
                if (ms > _worst) _worst = ms;
            }

            bool saving = znet != null && znet.IsSaving();
            if (saving && !_saving) { _saveStartedAt = now; _saveFreezeMs = ms; _sSaves++; }
            else if (!saving && _saving)
                _log.LogInfo($"World save: the server stood still for {_saveFreezeMs:0} ms, then wrote the file in the background for {now - _saveStartedAt:0.0} s.");
            _saving = saving;

            // A connection opening or closing (peers), or a player's character arriving or going (players).
            int peerCount = znet != null ? znet.GetPeers().Count : 0, playerCount = znet != null ? znet.GetNrOfPlayers() : 0;
            if (_peerCount >= 0 && (peerCount != _peerCount || playerCount != _playerCount))
            {
                _joinLeaveAt = now;
                _joinLeave = peerCount > _peerCount || playerCount > _playerCount ? "joined" : "left";
            }
            _peerCount = peerCount; _playerCount = playerCount;

            bool first = !_seenFrame;
            _seenFrame = true;
            if (ms >= _hitchMs.Value && !first && !own)
            {
                _slow++; _sHitches++;
                bool save = saving || now - _saveStartedAt < 2f;
                string cleanup = frameFrom > 0 ? AssetCleanup.Label(frameFrom, tick) : AssetCleanup.Label(frame);
                string joinLeave = now - _joinLeaveAt <= JoinLeaveSeconds ? _joinLeave : "";
                if (save) _sHitchSave++;
                if (gcFrame) _sHitchGc++;
                if (cleanup.Length > 0) _sHitchCleanup++; // "asked" too: the clean-up's freeze can land on the frame that asked for it
                if (joinLeave.Length > 0) _sHitchJoin++;
                int players = playerCount;
                int objects = ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : -1;
                _writer.Append(Path.Combine(_folder, "server_hitches.csv"), (_hitchHeader ? "" : HitchHeader) +
                    $"{DateTime.Now.ToString("HH:mm:ss", Csv.Inv)},{Csv.F(now - _started, "0.0")},{Csv.F(ms, "0")},{players},{(save ? "yes" : "")},{objects},{(gcFrame ? "yes" : "")},{cleanup},{joinLeave}\n");
                _hitchHeader = true;
                if (ms >= 1000f && _inStartup)
                    _log.LogInfo($"Server froze for {ms / 1000f:0.0} s while starting up (split into its loading steps once startup is done).");
                else if (ms >= 1000f)
                {
                    var why = new List<string>(3);
                    if (save) why.Add("world save");
                    if (cleanup.Length > 0) why.Add("the game's asset clean-up");
                    if (gcFrame) why.Add("a garbage collection ran in it");
                    if (joinLeave.Length > 0) why.Add("a player " + joinLeave);
                    _log.LogInfo($"Server froze for {ms / 1000f:0.0} s{(why.Count > 0 ? " (" + string.Join(", ", why.ToArray()) + ")" : "")} with {players} players on.");
                }
                if (_inStartup)
                {
                    _lastSlowAt = now;
                    if (frameFrom > 0) _startupFrames.Add((now, ms, frameFrom, tick, cleanup));
                }
            }
            AssetCleanup.Frame(ms, frame, frameFrom, tick);
            if (_inStartup && now - _secondStart >= 1f) StartupCheck(znet, now);
            if (!_inStartup && now - _secondStart >= 1f) CensusCheck(znet, now);
            if (_runningCensus != null) CensusFrame();

            if (now - _secondStart >= 1f) Second(znet, now);
            if (now >= _nextPlayers) { _nextPlayers = now + 5f; Players(znet); }
            if (now >= _nextFlush) Flush(now);
            if (_net != null && now >= _nextNetwork) Network(now);
            if (now >= _nextSummary) Summary(now);
        }

        /// <summary>Once a second while starting: done when the world is loaded and frames have been smooth for a while, or a player joins.</summary>
        private void StartupCheck(ZNet znet, float now)
        {
            if (_worldReadyAt < 0 && znet != null && ZNet.World != null && ZDOMan.instance != null && ZoneSystem.instance != null) _worldReadyAt = now;
            string why = null;
            if (znet != null && znet.GetNrOfPlayers() > 0) why = "a player joined";
            else if (_worldReadyAt >= 0 && now - _worldReadyAt >= StartupQuietSeconds && now - _lastSlowAt >= StartupQuietSeconds) why = "world loaded and running smoothly";
            else if (now - _started >= StartupMaxSeconds) why = $"still not settled after {StartupMaxSeconds / 60:0} min";
            if (why != null) EndStartup(now, why);
        }

        /// <summary>Once a second after startup: start the memory census once nobody has been on for a while.</summary>
        private void CensusCheck(ZNet znet, float now)
        {
            if (!_census.Value || _censusDone || _runningCensus != null || znet == null) return;
            if (znet.GetPeers().Count > 0 || znet.IsSaving() || AssetCleanup.Current != null) { _emptySince = -1; return; }
            if (_emptySince < 0) { _emptySince = now; return; }
            if (now - _emptySince < CensusEmptySeconds) return;
            _runningCensus = new MemoryCensus(CensusSliceMs, _registry);
            _log.LogInfo($"Memory census started (nobody on): counting what keeps the server's memory, {CensusSliceMs} ms per frame; it stops if a player joins.");
        }

        /// <summary>One slice of the census per frame; stops at once when someone connects.</summary>
        private void CensusFrame()
        {
            var mc = _runningCensus;
            var znet = ZNet.instance;
            if (znet != null && znet.GetPeers().Count > 0)
            {
                mc.Abort();
                _runningCensus = null;
                _censusEndedFrame = Time.frameCount;
                _emptySince = -1;
                _log.LogInfo($"Memory census stopped: a player is connecting. It tries again once the server has been empty for {CensusEmptySeconds / 60:0} minutes.");
                return;
            }
            mc.Tick();
            if (!mc.Done) return;
            _runningCensus = null;
            _censusEndedFrame = Time.frameCount;
            _censusDone = true; // once per run, also when it failed
            if (mc.Error != null) { _log.LogInfo($"Memory census: {mc.Error}"); return; }
            string file = Path.Combine(_folder, "memory_census_" + DateTime.Now.ToString("HH-mm-ss", Csv.Inv) + ".txt");
            _writer.Write(file, mc.Text());
            _log.LogInfo("Server memory census (nobody on):");
            foreach (var line in mc.Plain().Take(6)) _log.LogInfo("  " + line);
            _log.LogInfo($"  Full list: {file}");
        }

        private void EndStartup(float now, string why)
        {
            _inStartup = false;
            long end = Stopwatch.GetTimestamp();
            int slow = _startupFrames.Count;
            float longest = slow > 0 ? _startupFrames.Max(f => f.ms) : 0;
            _log.LogInfo($"Server startup: ready {now:0} s after launch ({why}); {slow} frames over {_hitchMs.Value:0} ms" +
                         (slow > 0 ? $", longest {longest / 1000:0.0} s" : "") + ". Startup is left out of the summaries below.");
            if (_splitting)
            {
                try { StartupReport(end, now); }
                catch (Exception e) { _log.LogWarning($"Startup timing report failed: {e.Message}"); }
                // The frame loops run every frame from now on: their timers come off.
                var frameSteps = LoadSteps.FrameStepMethods;
                LoadPatches.Disarm(frameSteps);
                LoadSteps.DetachFrameSteps(_log);
                _splitting = false;
            }
            _startupFrames.Clear();
            ResetSummary(now);
            _nextSummary = now + SummarySeconds;
        }

        /// <summary>The startup steps split by mod (log and CSV), and every slow startup frame split into what ran in it.</summary>
        private void StartupReport(long end, float now)
        {
            string when = DateTime.Now.ToString("HH:mm:ss", Csv.Inv);
            var steps = LoadSteps.Summarize(0, end);
            var mods = LoadPatches.Summarize(0, end);
            Plugin.LogSteps(_log, "Startup steps", steps, mods);
            _writer.Write(Path.Combine(_folder, "server_startup_steps.csv"), LoadTimes.StepsHeader + LoadTimes.StepsCsv("server startup", when, steps, now));
            if (mods.Count > 0) _writer.Write(Path.Combine(_folder, "server_startup_step_mods.csv"), LoadPatches.CsvHeader + LoadPatches.Csv("server startup", when, mods));
            if (_startupFrames.Count == 0) return;

            var csv = new StringBuilder("uptime_s,frame_ms,step,mod,seconds\n");
            foreach (var (at, ms, from, to, cleanup) in _startupFrames)
            {
                var fs = LoadSteps.Summarize(from, to);
                var fm = LoadPatches.Summarize(from, to);
                double inSteps = fs.Sum(s => s.seconds), rest = Math.Max(0, ms / 1000.0 - inSteps);
                string up = Csv.F(at - _started, "0.0"), frame = Csv.F(ms, "0");
                foreach (var (label, _, seconds) in fs)
                    csv.Append(up).Append(',').Append(frame).Append(',').Append(Csv.Q(label)).Append(",,").Append(Csv.F(seconds, "0.000")).Append('\n');
                foreach (var (step, mod, _, seconds) in fm)
                    csv.Append(up).Append(',').Append(frame).Append(',').Append(Csv.Q(step)).Append(',').Append(Csv.Q(mod)).Append(',').Append(Csv.F(seconds, "0.000")).Append('\n');
                string outside = cleanup == "yes" ? "Outside the timed steps (the game's asset clean-up ran in this frame)" : "Outside the timed steps (Unity, mods' own Update and coroutines)";
                csv.Append(up).Append(',').Append(frame).Append(',').Append(Csv.Q(outside)).Append(",,").Append(Csv.F(rest, "0.000")).Append('\n');
                if (ms < 1000f) continue;

                string parts = string.Join(", ", fs.Where(s => s.seconds >= 0.05).Take(4).Select(s => $"{s.label} {s.seconds:0.0} s"));
                string where = cleanup == "yes" ? "the game's asset clean-up ran in this frame (Resources.UnloadUnusedAssets)" : "Unity, mods' own Update and coroutines";
                _log.LogInfo($"  Frame of {ms / 1000:0.0} s, {at:0} s after launch: {(parts.Length > 0 ? parts + "; " : "")}outside the timed steps {rest:0.0} s ({where}).");
                var top = fm.Where(m => m.seconds >= 0.05).Take(5).ToList();
                if (top.Count > 0)
                    _log.LogInfo($"     mods' patches in it: {string.Join(", ", top.Select(m => $"{m.mod} {m.seconds:0.00} s ({m.step})"))}");
            }
            _writer.Write(Path.Combine(_folder, "server_startup_frames.csv"), csv.ToString());
            _log.LogInfo($"  Slow startup frames split up: {Path.Combine(_folder, "server_startup_frames.csv")}");
        }

        private void Second(ZNet znet, float now)
        {
            _secondStart = now;
            int players = 0, objects = -1, sent = 0, recv = 0;
            float up = 0, down = 0, worstPing = 0, worstBacklog = 0;
            string worstPlayer = "";
            if (znet != null)
            {
                players = znet.GetNrOfPlayers();
                CountTraffic(znet, now);
                _peers.Clear();
                _peers.AddRange(znet.GetPeers());
                foreach (var peer in _peers)
                {
                    if (peer == null || !peer.IsReady() || peer.m_socket == null || peer.m_rpc == null) continue;
                    var w = Watch(peer.m_rpc);
                    up += w.ToKbs * 1024; down += w.FromKbs * 1024;
                    // The game ping (through the server's and the player's main loops); Steam's network ping when that is all there is.
                    float ping = w.Ping;
                    if (float.IsNaN(ping) && Quality(peer.m_socket, out _, out _, out int steamPing, out _, out _) && steamPing > 0) ping = steamPing;
                    // Worst ping leaves out players still joining: their answers wait behind the world data (4-14 s, 2026-10-27 log).
                    if (!float.IsNaN(ping) && ping > worstPing && !Joining(peer, w, now)) { worstPing = ping; worstPlayer = peer.m_playerName ?? ""; }
                    worstBacklog = Math.Max(worstBacklog, Backlog(peer.m_socket));
                }
            }
            var zdo = ZDOMan.instance;
            if (zdo != null) { objects = zdo.NrOfObjects(); sent = zdo.GetSentZDOs(); recv = zdo.GetRecvZDOs(); }
            int gc = GC.CollectionCount(0);
            _rows.Append(DateTime.Now.ToString("HH:mm:ss", Csv.Inv)).Append(',').Append(Csv.F(now - _started, "0")).Append(',').Append(players).Append(',')
                 .Append(Csv.F(_frameSum > 0 ? 1000.0 * _frames / _frameSum : double.NaN, "0.0")).Append(',').Append(Csv.F(_frames > 0 ? _frameSum / _frames : double.NaN, "0.0")).Append(',')
                 .Append(Csv.F(_worst, "0")).Append(',').Append(_slow).Append(',').Append(objects).Append(',').Append(sent).Append(',').Append(recv).Append(',')
                 .Append(Csv.F(up / 1024, "0.0")).Append(',').Append(Csv.F(down / 1024, "0.0")).Append(',').Append(Csv.F(worstPing, "0")).Append(',').Append(Csv.Q(worstPlayer)).Append(',')
                 .Append(Csv.F(worstBacklog / 1024, "0.0")).Append(',').Append(gc - _gcAt).Append(',').Append(Csv.F(GC.GetTotalMemory(false) / 1048576.0, "0")).Append(',')
                 .Append(_saving ? "yes" : "").Append('\n');
            _gcAt = gc;
            _sSeconds++; _sFrames += _frames; _sFrameSum += _frameSum; _sSlow += _slow; _sPlayers += players;
            if (_worst > _sWorst) _sWorst = _worst;
            if (worstPing > _sWorstPing) { _sWorstPing = worstPing; _sWorstPingPlayer = worstPlayer; }
            _frames = 0; _frameSum = 0; _worst = 0; _slow = 0;
        }

        private void Players(ZNet znet)
        {
            if (znet == null) return;
            string when = DateTime.Now.ToString("HH:mm:ss", Csv.Inv);
            foreach (var peer in znet.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_socket == null) continue;
                try
                {
                    float since = SinceAnswer != null && peer.m_rpc != null ? SinceAnswer(peer.m_rpc) : float.NaN;
                    var w = peer.m_rpc != null ? Watch(peer.m_rpc) : null;
                    var pos = peer.m_refPos;
                    _players.Append(when).Append(',').Append(Csv.Q(peer.m_playerName ?? "")).Append(',').Append(Csv.F(w != null ? w.Ping : double.NaN, "0")).Append(',')
                            .Append(w != null && Joining(peer, w, Time.realtimeSinceStartup) ? "yes" : "").Append(',')
                            .Append(Csv.F(w != null && w.Counted ? w.ToKbs : double.NaN, "0.0")).Append(',').Append(Csv.F(w != null && w.Counted ? w.FromKbs : double.NaN, "0.0")).Append(',')
                            .Append(Csv.F(Backlog(peer.m_socket) / 1024.0, "0.0")).Append(',').Append(Csv.F(since, "0.0")).Append(',')
                            .Append(Csv.F(pos.x, "0")).Append(',').Append(Csv.F(pos.z, "0")).Append('\n');
                }
                catch { }
            }
        }

        private void Flush(float now)
        {
            _nextFlush = now + 10f;
            if (_rows.Length > 0) { _writer.Append(Path.Combine(_folder, "server.csv"), _rows.ToString()); _rows.Length = 0; }
            if (_players.Length > 0) { _writer.Append(Path.Combine(_folder, "server_players.csv"), _players.ToString()); _players.Length = 0; }
        }

        private bool _networkHeader;

        private void Network(float now)
        {
            _nextNetwork = now + 60f;
            _net.NameFromCode(now); // names found by the last scan go into this minute's rows; starts the scan the first minute one is unnamed
            string rows = _net.IntervalRows(DateTime.Now.ToString("HH:mm:ss", Csv.Inv) + "," + Csv.F(now - _started, "0") + ",", Math.Max(1, now - _lastNetworkAt));
            _lastNetworkAt = now;
            if (rows.Length > 0)
            {
                _writer.Append(Path.Combine(_folder, "server_network.csv"), (_networkHeader ? "" : NetworkStats.IntervalHeader.Replace("session_s", "uptime_s")) + rows);
                _networkHeader = true;
            }
            // Every minute, like the rows: a copy downloaded mid-run is at most a minute old.
            _writer.Write(Path.Combine(_folder, "server_network_summary.csv"), _net.SummaryCsv(now - _started));
        }

        private void Summary(float now)
        {
            _nextSummary = now + SummarySeconds;
            if (_sSeconds == 0) return;
            if (_net != null) Network(now);
            double minutes = _sSeconds / 60.0;
            double selfMs = _selfTicks * 1000.0 / Stopwatch.Frequency;
            string span = _sSeconds < 60 ? $"{_sSeconds} s" : $"{minutes:0} min"; // the last stretch before a shutdown can be short
            _log.LogInfo($"Server, last {span}: {_sPlayers / _sSeconds:0.0} players, {(_sFrameSum > 0 ? 1000.0 * _sFrames / _sFrameSum : 0):0} FPS, " +
                         $"worst frame {_sWorst:0} ms, {_sHitches} frames over {_hitchMs.Value:0} ms{HitchCauses()}, {_sSaves} world saves" +
                         (_sWorstPing > 0 ? $", worst ping {_sWorstPing:0} ms ({_sWorstPingPlayer})" : "") + ".");
            if (_net != null)
            {
                // Only the messages since the last summary (the whole session is in server_network_summary.csv).
                double seconds = Math.Max(1, now - _lastSummaryAt);
                var top = _net.Entries.Select(e => (e, n: e.SentCalls + e.RecvCalls - (_netAtSummary.TryGetValue(e, out long at) ? at : 0)))
                              .Where(x => x.n > 0).OrderByDescending(x => x.n).ThenBy(x => x.e.Name).Take(3).ToList();
                if (top.Count > 0)
                    _log.LogInfo($"  Busiest network messages: {string.Join(", ", top.Select(x => $"{x.e.Name} ({x.n / seconds:0.0}/s{(x.e.Owner.Length > 0 ? ", " + x.e.Owner : "")})").ToArray())}");
            }
            _log.LogInfo($"  This monitor used {selfMs / minutes:0.0} ms per minute of the server's time ({selfMs / (_sSeconds * 10.0):0.000}% of it). Files: {_folder}");
            var w = LogWatch.Instance;
            if (w != null && w.Errors + w.Warnings > 0) _writer.Write(Path.Combine(_folder, "log_errors.csv"), w.Csv(_registry));
            ResetSummary(now);
        }

        /// <summary>" (3 in world saves, 1 in a garbage collection)" for the slow frames with a known cause; "" when none.</summary>
        private string HitchCauses()
        {
            if (_sHitches == 0) return "";
            var c = new List<string>(3);
            if (_sHitchSave > 0) c.Add($"{_sHitchSave} in world saves");
            if (_sHitchCleanup > 0) c.Add($"{_sHitchCleanup} in the game's asset clean-up");
            if (_sHitchGc > 0) c.Add($"{_sHitchGc} with a garbage collection");
            if (_sHitchJoin > 0) c.Add($"{_sHitchJoin} as a player joined or left");
            return c.Count > 0 ? " (" + string.Join(", ", c.ToArray()) + ")" : "";
        }

        /// <summary>Start a new summary window (after each summary, and once startup is done).</summary>
        private void ResetSummary(float now)
        {
            _lastSummaryAt = now;
            if (_net != null)
                foreach (var e in _net.Entries) _netAtSummary[e] = e.SentCalls + e.RecvCalls;
            _sFrames = _sSlow = _sHitches = _sSaves = _sSeconds = 0;
            _sHitchSave = _sHitchGc = _sHitchCleanup = _sHitchJoin = 0;
            _sFrameSum = _sWorst = _sWorstPing = 0;
            _sPlayers = 0;
            _sWorstPingPlayer = "";
            _selfTicks = 0;
        }

        public void Stop()
        {
            if (_writer == null) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_runningCensus != null) { _runningCensus.Abort(); _runningCensus = null; }
                if (_inStartup) EndStartup(now, "the server shut down before startup was done");
                Flush(now);
                Summary(now);
            }
            catch (Exception e) { _log.LogDebug($"Server logging: {e.Message}"); }
            _writer.Dispose();
        }
    }
}
