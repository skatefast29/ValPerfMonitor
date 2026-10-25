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
    ///  - server_players.csv (every 5 s): each player's ping, connection quality, traffic, upload backlog, position
    ///  - server_hitches.csv: every frame slower than the threshold, and whether a world save was running
    ///  - server_network.csv / server_network_summary.csv: network messages per name and mod (optional)
    /// A summary goes to the log every few minutes, with the time this monitor itself used.
    /// Files: BepInEx\ValPerfMonitor\server_&lt;date&gt;\.
    /// </summary>
    internal sealed class ServerMode
    {
        public const string RowHeader = "time,uptime_s,players,fps,frame_ms_avg,worst_frame_ms,slow_frames,world_objects,objects_sent_s,objects_received_s," +
                                        "upload_kb_s,download_kb_s,worst_ping_ms,worst_ping_player,worst_backlog_kb,gc,heap_mb,saving\n";
        public const string PlayerHeader = "time,player,game_ping_ms,network_ping_ms,quality_local,quality_remote,to_player_kb_s,from_player_kb_s,backlog_kb,seconds_since_answer,pos_x,pos_z\n";
        public const string HitchHeader = "time,uptime_s,frame_ms,players,world_save,world_objects\n";

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
            public float LastTimer, LastSince, SentAt = -1, Ping = float.NaN, CountedAt;
            public int LastSent, LastRecv;
            public float ToKbs, FromKbs;
            public bool Counted;
        }

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

        /// <summary>Said once per server run: the network ping and connection quality columns stay empty, the rest still works.</summary>
        private void SteamUnavailable(string why)
        {
            if (_steamReported) return;
            _steamReported = true;
            _log.LogInfo($"Steam's connection details (network ping, quality) are not available on this server ({why}); the game ping and traffic per player are recorded instead.");
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
        private readonly ConfigEntry<bool> _on, _network;
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
        private int _gcAt;

        // Since the last log summary.
        private int _sFrames, _sSlow, _sHitches, _sSaves, _sSeconds;
        private float _sFrameSum, _sWorst, _sWorstPing;
        private double _sPlayers;
        private string _sWorstPingPlayer = "";
        private long _selfTicks;

        // World saves: the main-thread freeze when one starts, then the background write.
        private bool _saving;
        private float _saveStartedAt = -100, _saveFreezeMs;

        public ServerMode(Plugin p, ConfigFile cfg, ManualLogSource log)
        {
            _p = p;
            _log = log;
            const string section = "7 - Dedicated server";
            _on = cfg.Bind(section, "Server logging", true, "On a dedicated server, write down frame rate, players' pings and traffic, slow frames and world saves once a second. Nothing else of this monitor runs on a server.");
            _network = cfg.Bind(section, "Count network messages per mod", true, "Also count every network message the server handles, per name and per mod (server_network.csv). Costs well under a millisecond per second.");
            _hitchMs = cfg.Bind(section, "Slow frame ms", 100f, "Server frames slower than this are listed in server_hitches.csv. Players feel a freeze when the server stops for a few hundred ms.");
            _summaryMinutes = cfg.Bind(section, "Log summary every (minutes)", 10f, "How often a short summary goes to the server log (LogOutput.log). 0 = only when the server shuts down.");
        }

        public void Start()
        {
            if (!_on.Value)
            {
                _log.LogInfo("Dedicated server: server logging is off (setting \"Server logging\").");
                return;
            }
            _folder = Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor", "server_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Csv.Inv));
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
            _gcAt = GC.CollectionCount(0);
            _writer.Write(Path.Combine(_folder, "server.csv"), RowHeader);
            _writer.Write(Path.Combine(_folder, "server_players.csv"), PlayerHeader);
            _log.LogInfo($"Dedicated server: logging frame rate, players' connections and slow frames once a second (no mod timing on a server). Files: {_folder}");
            _log.LogInfo($"  The server runs at most {(Application.targetFrameRate > 0 ? Application.targetFrameRate + " FPS" : "as fast as it can (no cap set yet; the game sets 30 FPS once loaded)")}: at that cap it is keeping up; slow frames are what players feel.");
        }

        private float SummarySeconds => _summaryMinutes.Value > 0 ? Mathf.Max(1, _summaryMinutes.Value) * 60f : float.MaxValue;

        /// <summary>Every frame (Plugin.Update).</summary>
        public void Tick()
        {
            if (_writer == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try { Frame(); }
            catch (Exception e) { _log.LogDebug($"Server logging: {e.Message}"); }
            _selfTicks += Stopwatch.GetTimestamp() - t0;
        }

        private void Frame()
        {
            float now = Time.realtimeSinceStartup;
            var znet = ZNet.instance;
            if (_net == null && _network.Value && znet != null && ZRoutedRpc.instance != null)
            {
                _net = new NetworkStats(_registry, _log) { Recording = true };
                _net.Attach(new Harmony(Plugin.Guid + ".servernet"));
            }

            if (znet != null) WatchPings(znet, now);
            float ms = Time.unscaledDeltaTime * 1000f;
            _frames++; _frameSum += ms;
            if (ms > _worst) _worst = ms;

            bool saving = znet != null && znet.IsSaving();
            if (saving && !_saving) { _saveStartedAt = now; _saveFreezeMs = ms; _sSaves++; }
            else if (!saving && _saving)
                _log.LogInfo($"World save: the server stood still for {_saveFreezeMs:0} ms, then wrote the file in the background for {now - _saveStartedAt:0.0} s.");
            _saving = saving;

            bool first = !_seenFrame;
            _seenFrame = true;
            if (ms >= _hitchMs.Value && !first)
            {
                _slow++; _sHitches++;
                bool save = saving || now - _saveStartedAt < 2f;
                int players = znet != null ? znet.GetNrOfPlayers() : 0;
                int objects = ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : -1;
                _writer.Append(Path.Combine(_folder, "server_hitches.csv"), (_hitchHeader ? "" : HitchHeader) +
                    $"{DateTime.Now.ToString("HH:mm:ss", Csv.Inv)},{Csv.F(now - _started, "0.0")},{Csv.F(ms, "0")},{players},{(save ? "yes" : "")},{objects}\n");
                _hitchHeader = true;
                if (ms >= 1000f) _log.LogInfo($"Server froze for {ms / 1000f:0.0} s{(save ? " (world save)" : "")} with {players} players on.");
            }

            if (now - _secondStart >= 1f) Second(znet, now);
            if (now >= _nextPlayers) { _nextPlayers = now + 5f; Players(znet); }
            if (now >= _nextFlush) Flush(now);
            if (_net != null && now >= _nextNetwork) Network(now, summary: false);
            if (now >= _nextSummary) Summary(now);
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
                    if (!float.IsNaN(ping) && ping > worstPing) { worstPing = ping; worstPlayer = peer.m_playerName ?? ""; }
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
                    bool known = Quality(peer.m_socket, out float ql, out float qr, out int ping, out _, out _);
                    float since = SinceAnswer != null && peer.m_rpc != null ? SinceAnswer(peer.m_rpc) : float.NaN;
                    var w = peer.m_rpc != null ? Watch(peer.m_rpc) : null;
                    var pos = peer.m_refPos;
                    _players.Append(when).Append(',').Append(Csv.Q(peer.m_playerName ?? "")).Append(',').Append(Csv.F(w != null ? w.Ping : double.NaN, "0")).Append(',')
                            .Append(known && ping > 0 ? ping.ToString(Csv.Inv) : "").Append(',')
                            .Append(Csv.F(ql > 0 ? ql : double.NaN, "0.00")).Append(',').Append(Csv.F(qr > 0 ? qr : double.NaN, "0.00")).Append(',')
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

        private void Network(float now, bool summary)
        {
            _nextNetwork = now + 60f;
            string rows = _net.IntervalRows(DateTime.Now.ToString("HH:mm:ss", Csv.Inv) + "," + Csv.F(now - _started, "0") + ",", Math.Max(1, now - _lastNetworkAt));
            _lastNetworkAt = now;
            if (rows.Length > 0)
            {
                _writer.Append(Path.Combine(_folder, "server_network.csv"), (_networkHeader ? "" : NetworkStats.IntervalHeader.Replace("session_s", "uptime_s")) + rows);
                _networkHeader = true;
            }
            if (summary) _writer.Write(Path.Combine(_folder, "server_network_summary.csv"), _net.SummaryCsv(now - _started));
        }

        private void Summary(float now)
        {
            _nextSummary = now + SummarySeconds;
            if (_sSeconds == 0) return;
            if (_net != null) Network(now, summary: true);
            double minutes = _sSeconds / 60.0;
            double selfMs = _selfTicks * 1000.0 / Stopwatch.Frequency;
            _log.LogInfo($"Server, last {minutes:0} min: {_sPlayers / _sSeconds:0.0} players, {(_sFrameSum > 0 ? 1000.0 * _sFrames / _sFrameSum : 0):0} FPS, " +
                         $"worst frame {_sWorst:0} ms, {_sHitches} frames over {_hitchMs.Value:0} ms, {_sSaves} world saves" +
                         (_sWorstPing > 0 ? $", worst ping {_sWorstPing:0} ms ({_sWorstPingPlayer})" : "") + ".");
            if (_net != null)
            {
                var top = _net.Top(3).ToList();
                if (top.Count > 0)
                    _log.LogInfo($"  Busiest network messages: {string.Join(", ", top.Select(e => $"{e.Name} ({(e.SentCalls + e.RecvCalls) / Math.Max(1, now - _started):0.0}/s{(e.Owner.Length > 0 ? ", " + e.Owner : "")})").ToArray())}");
            }
            _log.LogInfo($"  This monitor used {selfMs / minutes:0.0} ms per minute of the server's time ({selfMs / (_sSeconds * 10.0):0.000}% of it). Files: {_folder}");
            var w = LogWatch.Instance;
            if (w != null && w.Errors + w.Warnings > 0) _writer.Write(Path.Combine(_folder, "log_errors.csv"), w.Csv(_registry));
            _sFrames = _sSlow = _sHitches = _sSaves = _sSeconds = 0;
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
                Flush(now);
                Summary(now);
            }
            catch (Exception e) { _log.LogDebug($"Server logging: {e.Message}"); }
            _writer.Dispose();
        }
    }
}
