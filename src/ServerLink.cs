using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// The connection to a dedicated server, seen from this client, whether or not a capture is recording.
    /// No hooks: once a second it reads what the game and Steam already keep, and once a frame two timers.
    ///  - Network ping: Steam's round trip, answered by Steam's own network thread on the server.
    ///  - Game ping: the game's own ping message, answered by the server's main loop. Game ping minus network
    ///    ping is how long the server took to get to it (its frame time shows up here).
    ///  - Server hitches: no answer for over a second. If the network ping and the connection quality stayed
    ///    good meanwhile, the server itself froze (a world save, a mod's work); otherwise the connection had trouble.
    ///  - Upload backlog: data waiting to be sent. The game stops sending world updates once 10 KB are waiting.
    ///  - Who runs the area: objects and creatures nearby whose owner (the player that simulates them) is you,
    ///    another player, or nobody.
    /// Files (session folder): connection.csv (one row per second), server_hitches.csv; one line per server
    /// visit in server_visits.csv (BepInEx\ValPerfMonitor).
    /// </summary>
    internal sealed class ServerLink
    {
        public const string RowHeader = "time,session_s,players,network_ping_ms,game_ping_ms,server_delay_ms,quality_local,quality_remote," +
                                        "upload_kb_s,download_kb_s,upload_backlog_kb,world_objects_sent_s,world_objects_received_s," +
                                        "objects_nearby,objects_mine,objects_other_players,creatures_nearby,creatures_mine,fps,worst_frame_ms,context\n";
        public const string HitchHeader = "time,session_s,seconds_without_answer,likely_cause,network_ping_ms,worst_quality_local,worst_quality_remote,download_kb_s,players,context\n";
        public const string VisitHeader = "session,joined,server,minutes,players_avg,network_ping_avg_ms,network_ping_p95_ms,game_ping_avg_ms,game_ping_p95_ms," +
                                          "server_delay_avg_ms,server_delay_p95_ms,hitches,hitches_server,hitches_connection,longest_hitch_s,quality_local_avg,quality_remote_avg," +
                                          "upload_kb_s_avg,download_kb_s_avg,upload_backed_up_pct,world_objects_received_s_avg,objects_mine_pct,creatures_mine_pct,join_s\n";

        /// <summary>The game stops adding world updates to a connection once this much is waiting to be sent.</summary>
        private const int VanillaBacklogLimit = 10240;

        /// <summary>The limit in use: the game's 10 KB, or the Network mod's "Batch Size" when that mod raises it.</summary>
        public int BacklogLimit { get; private set; } = VanillaBacklogLimit;

        private static int ReadBacklogLimit()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("org.bepinex.plugins.network", out var info) || info.Instance == null) return VanillaBacklogLimit;
                var cfg = info.Instance.Config;
                string Get(string section, string key)
                {
                    var def = new BepInEx.Configuration.ConfigDefinition(section, key);
                    return cfg.ContainsKey(def) ? cfg[def].BoxedValue?.ToString() : null;
                }
                if (Get("1 - General", "Enable Networking Improvements") != "On" || Get("2 - ZDO Sending", "Increase Batch Size") != "On") return VanillaBacklogLimit;
                return int.TryParse(Get("2 - ZDO Sending", "Batch Size"), out int size) && size > 0 ? size : VanillaBacklogLimit;
            }
            catch { return VanillaBacklogLimit; }
        }

        private static readonly AccessTools.FieldRef<ZRpc, float> PingTimer = Safe(() => AccessTools.FieldRefAccess<ZRpc, float>("m_pingTimer"));
        private static readonly AccessTools.FieldRef<ZRpc, float> SinceAnswer = Safe(() => AccessTools.FieldRefAccess<ZRpc, float>("m_timeSinceLastPing"));
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances =
            Safe(() => AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances"));

        private static AccessTools.FieldRef<TO, TF> Safe<TO, TF>(Func<AccessTools.FieldRef<TO, TF>> f)
        {
            try { return f(); } catch { return null; }
        }

        public static ServerLink Instance;
        public UploadWatch Upload, LastUpload;
        private float _nextUploadCsv, _nextUploadCount;

        private readonly ManualLogSource _log;
        private readonly StringBuilder _rows = new StringBuilder();
        private bool _rowHeader, _hitchHeader;
        private float _nextFlush;

        // The connection being watched.
        public bool Connected;
        public string Server = "";
        public DateTime Joined;
        private ZRpc _rpc;
        private ISocket _socket;
        private float _lastTimer, _lastSince;
        private float _pingSentAt = -1;

        // This second.
        private float _secondStart;
        private int _frames;
        private float _worstFrame, _frameSum;
        private float _gamePing = float.NaN;

        // The latest readings (the panel and perf/connection show these).
        public float NetPing = float.NaN, GamePing = float.NaN, ServerDelay = float.NaN, QualityLocal = float.NaN, QualityRemote = float.NaN;
        public float UpKbs, DownKbs, BacklogKb;
        public int Players, ZdosSent, ZdosRecv;
        public int Nearby = -1, Mine, Others, Creatures = -1, CreaturesMine;
        private float _nextOwners;

        // A hitch in progress: waiting for an answer for over a second.
        private bool _inHitch;
        private float _hitchWorstLocal = float.NaN, _hitchWorstRemote = float.NaN, _hitchNetPing;

        // This visit, for the summary.
        private readonly List<float> _netPings = new List<float>(), _gamePings = new List<float>(), _delays = new List<float>();
        private double _sumUp, _sumDown, _sumPlayers, _sumQl, _sumQr, _sumZdoRecv, _sumMinePct, _sumCreaturesMinePct;
        private int _seconds, _qualitySeconds, _backedUp, _ownerSamples, _creatureSamples;
        public int Hitches, HitchesServer, HitchesConnection;
        public float LongestHitch;
        public readonly List<string> RecentHitches = new List<string>();

        public ServerLink(ManualLogSource log)
        {
            _log = log;
            Instance = this;
        }

        /// <summary>Seconds the server has not answered the game's ping (0 when not waiting).</summary>
        public float Waiting => _pingSentAt >= 0 ? Time.realtimeSinceStartup - _pingSentAt : 0;

        /// <summary>Main thread, every frame.</summary>
        public void Frame(Plugin p)
        {
            var znet = ZNet.instance;
            ZNetPeer peer = null;
            try
            {
                if (znet != null && !znet.IsServer() && ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected)
                    peer = znet.GetServerPeer();
            }
            catch { }
            if (peer == null || peer.m_rpc == null)
            {
                if (Connected) EndVisit(p, "disconnected");
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (!Connected || peer.m_rpc != _rpc) BeginVisit(p, znet, peer, now);

            _frames++;
            float dt = Time.unscaledDeltaTime * 1000f;
            _frameSum += dt;
            if (dt > _worstFrame) _worstFrame = dt;

            if (PingTimer != null && SinceAnswer != null)
            {
                float timer = PingTimer(_rpc), since = SinceAnswer(_rpc);
                // The game sends its ping once a second (the timer starts over) and the answer resets the other timer.
                if (timer < _lastTimer && _pingSentAt < 0) _pingSentAt = now;
                if (since < _lastSince && _pingSentAt >= 0)
                {
                    float rtt = (now - _pingSentAt) * 1000f;
                    _gamePing = float.IsNaN(_gamePing) ? rtt : Math.Max(_gamePing, rtt);
                    if (_inHitch) EndHitch(p, rtt / 1000f);
                    _pingSentAt = -1;
                }
                _lastTimer = timer; _lastSince = since;
                if (!_inHitch && Waiting > 1f) _inHitch = true;
            }

            if (now - _secondStart >= 1f) Second(p, znet, now);
            if (now >= _nextFlush) Flush(p);
            if (Upload != null && now >= _nextUploadCount)
            {
                _nextUploadCount = now + 30f;
                Upload.CountLoaded();
            }
            if (Upload != null && now >= _nextUploadCsv)
            {
                _nextUploadCsv = now + 60f;
                p.Writer.Write(Path.Combine(p.SessionFolder, "upload.csv"), Upload.Csv());
            }
        }

        private void BeginVisit(Plugin p, ZNet znet, ZNetPeer peer, float now)
        {
            if (Connected) EndVisit(p, "server changed");
            Connected = true;
            _rpc = peer.m_rpc;
            _socket = peer.m_socket;
            _pingSentAt = -1;
            _lastTimer = PingTimer != null ? PingTimer(_rpc) : 0;
            _lastSince = SinceAnswer != null ? SinceAnswer(_rpc) : 0;
            _secondStart = now;
            BacklogLimit = ReadBacklogLimit();
            if (p.CfgNetwork.Value) Upload = LastUpload = UploadWatch.Start(Plugin.Guid + ".upload", p.Registry, _log);
            _nextUploadCsv = now + 60f;
            _nextUploadCount = now + 20f;
            _nextOwners = now + 5f;
            _nextFlush = now + 10f;
            Joined = DateTime.Now;
            Server = JoinWatch.ServerName(peer);
            _netPings.Clear(); _gamePings.Clear(); _delays.Clear(); RecentHitches.Clear();
            _sumUp = _sumDown = _sumPlayers = _sumQl = _sumQr = _sumZdoRecv = _sumMinePct = _sumCreaturesMinePct = 0;
            _seconds = _qualitySeconds = _backedUp = _ownerSamples = _creatureSamples = 0;
            Hitches = HitchesServer = HitchesConnection = 0;
            LongestHitch = 0;
            _inHitch = false;
            _log.LogInfo($"Connected to {(Server.Length > 0 ? Server : "a server")}: watching the connection (connection.csv in the session folder).");
        }

        private void Second(Plugin p, ZNet znet, float now)
        {
            _secondStart = now;
            float ql = float.NaN, qr = float.NaN; int ping = 0; float outB = 0, inB = 0;
            try { _socket.GetConnectionQuality(out ql, out qr, out ping, out outB, out inB); } catch { }
            NetPing = ping > 0 ? ping : float.NaN;
            QualityLocal = ql > 0 ? ql : float.NaN; // below 0 = not known yet; 0 = no report (non-Steam connection)
            QualityRemote = qr > 0 ? qr : float.NaN;
            UpKbs = outB / 1024f; DownKbs = inB / 1024f;
            int backlog = 0;
            try { backlog = _socket.GetSendQueueSize(); } catch { }
            BacklogKb = backlog / 1024f;
            try { Players = znet.GetNrOfPlayers(); } catch { }
            var zdo = ZDOMan.instance;
            if (zdo != null) { ZdosSent = zdo.GetSentZDOs(); ZdosRecv = zdo.GetRecvZDOs(); }
            if (!float.IsNaN(_gamePing)) GamePing = _gamePing;
            ServerDelay = !float.IsNaN(_gamePing) && !float.IsNaN(NetPing) ? Math.Max(0, _gamePing - NetPing) : float.NaN;
            if (now >= _nextOwners) { _nextOwners = now + 5f; CountOwners(); }
            else Nearby = Creatures = -1;

            if (_inHitch)
            {
                if (!float.IsNaN(QualityLocal)) _hitchWorstLocal = float.IsNaN(_hitchWorstLocal) ? QualityLocal : Math.Min(_hitchWorstLocal, QualityLocal);
                if (!float.IsNaN(QualityRemote)) _hitchWorstRemote = float.IsNaN(_hitchWorstRemote) ? QualityRemote : Math.Min(_hitchWorstRemote, QualityRemote);
                if (!float.IsNaN(NetPing)) _hitchNetPing = Math.Max(_hitchNetPing, NetPing);
            }

            _seconds++;
            if (!float.IsNaN(NetPing)) _netPings.Add(NetPing);
            if (!float.IsNaN(_gamePing)) _gamePings.Add(_gamePing);
            if (!float.IsNaN(ServerDelay)) _delays.Add(ServerDelay);
            _sumUp += UpKbs; _sumDown += DownKbs; _sumPlayers += Players; _sumZdoRecv += ZdosRecv;
            if (!float.IsNaN(QualityLocal) && !float.IsNaN(QualityRemote)) { _qualitySeconds++; _sumQl += QualityLocal; _sumQr += QualityRemote; }
            if (backlog >= BacklogLimit * 0.9) _backedUp++;

            if (!_rowHeader) { _rows.Append(RowHeader); _rowHeader = true; }
            _rows.Append(DateTime.Now.ToString("HH:mm:ss", Csv.Inv)).Append(',').Append(Csv.F(p.SessionSeconds, "0.0")).Append(',')
                 .Append(Players).Append(',').Append(Csv.F(NetPing, "0")).Append(',').Append(Csv.F(_gamePing, "0")).Append(',').Append(Csv.F(ServerDelay, "0")).Append(',')
                 .Append(Csv.F(QualityLocal, "0.00")).Append(',').Append(Csv.F(QualityRemote, "0.00")).Append(',')
                 .Append(Csv.F(UpKbs, "0.0")).Append(',').Append(Csv.F(DownKbs, "0.0")).Append(',').Append(Csv.F(BacklogKb, "0.0")).Append(',')
                 .Append(ZdosSent).Append(',').Append(ZdosRecv).Append(',')
                 .Append(Nearby >= 0 ? Nearby.ToString(Csv.Inv) : "").Append(',').Append(Nearby >= 0 ? Mine.ToString(Csv.Inv) : "").Append(',')
                 .Append(Nearby >= 0 ? Others.ToString(Csv.Inv) : "").Append(',')
                 .Append(Creatures >= 0 ? Creatures.ToString(Csv.Inv) : "").Append(',').Append(Creatures >= 0 ? CreaturesMine.ToString(Csv.Inv) : "").Append(',')
                 .Append(Csv.F(_frames > 0 ? 1000.0 * _frames / Math.Max(1, _frameSum) : double.NaN, "0")).Append(',').Append(Csv.F(_worstFrame, "0")).Append(',')
                 .Append(Csv.Q(p.Context.Short)).Append('\n');
            _frames = 0; _frameSum = 0; _worstFrame = 0; _gamePing = float.NaN;
        }

        /// <summary>Loaded objects and creatures by who simulates them. Every 5 s; walks the loaded objects once (no allocations).</summary>
        private void CountOwners()
        {
            Nearby = Creatures = -1;
            try
            {
                var scene = ZNetScene.instance;
                if (scene != null && Instances != null)
                {
                    int all = 0, mine = 0, others = 0;
                    foreach (var kv in Instances(scene))
                    {
                        var z = kv.Key;
                        if (z == null) continue;
                        all++;
                        if (z.IsOwner()) mine++;
                        else if (z.HasOwner()) others++;
                    }
                    Nearby = all; Mine = mine; Others = others;
                    if (all > 0) { _ownerSamples++; _sumMinePct += 100.0 * mine / all; }
                }
                int c = 0, cm = 0;
                foreach (var ch in Character.GetAllCharacters())
                {
                    if (ch == null || ch.IsPlayer()) continue;
                    c++;
                    if (ch.IsOwner()) cm++;
                }
                Creatures = c; CreaturesMine = cm;
                if (c > 0) { _creatureSamples++; _sumCreaturesMinePct += 100.0 * cm / c; }
            }
            catch { }
        }

        private void EndHitch(Plugin p, float seconds)
        {
            _inHitch = false;
            // Steam keeps answering the network ping from its own thread while the server's main loop is stuck.
            bool netGood = (float.IsNaN(_hitchWorstLocal) || _hitchWorstLocal >= 0.9f) && (float.IsNaN(_hitchWorstRemote) || _hitchWorstRemote >= 0.9f)
                           && (_hitchNetPing <= 0 || _netPings.Count == 0 || _hitchNetPing < Median(_netPings) * 2 + 100);
            bool known = !float.IsNaN(_hitchWorstLocal) || _hitchNetPing > 0;
            string cause = !known ? "unknown (no network readings)" : netGood ? "server busy (connection fine)" : "connection trouble";
            Hitches++;
            if (!known) { }
            else if (netGood) HitchesServer++;
            else HitchesConnection++;
            LongestHitch = Math.Max(LongestHitch, seconds);
            string when = DateTime.Now.ToString("HH:mm:ss", Csv.Inv);
            RecentHitches.Add($"{when} {seconds:0.0} s, {cause}");
            if (RecentHitches.Count > 20) RecentHitches.RemoveAt(0);
            string path = Path.Combine(p.SessionFolder, "server_hitches.csv");
            p.Writer.Append(path, (_hitchHeader ? "" : HitchHeader) +
                $"{when},{Csv.F(p.SessionSeconds, "0.0")},{Csv.F(seconds, "0.00")},{Csv.Q(cause)},{Csv.F(_hitchNetPing > 0 ? _hitchNetPing : double.NaN, "0")}," +
                $"{Csv.F(_hitchWorstLocal, "0.00")},{Csv.F(_hitchWorstRemote, "0.00")},{Csv.F(DownKbs, "0.0")},{Players},{Csv.Q(p.Context.Short)}\n");
            _hitchHeader = true;
            Events.Mark("server hitch", $"{seconds:0.0} s, {cause}");
            if (seconds >= 2f) _log.LogInfo($"Server did not answer for {seconds:0.0} s: {cause}.");
            _hitchWorstLocal = _hitchWorstRemote = float.NaN;
            _hitchNetPing = 0;
        }

        private void Flush(Plugin p)
        {
            _nextFlush = Time.realtimeSinceStartup + 10f;
            if (_rows.Length == 0 || p.Writer == null) return;
            p.Writer.Append(Path.Combine(p.SessionFolder, "connection.csv"), _rows.ToString());
            _rows.Length = 0;
        }

        /// <summary>Left the server (or the game is closing): the visit's summary to the log and server_visits.csv.</summary>
        public void EndVisit(Plugin p, string why)
        {
            if (!Connected) return;
            Connected = false;
            if (_inHitch) EndHitch(p, Waiting);
            _pingSentAt = -1;
            _rpc = null; _socket = null;
            Flush(p);
            var upload = Upload;
            Upload = null;
            if (upload != null)
            {
                upload.Stop();
                p.Writer.Write(Path.Combine(p.SessionFolder, "upload.csv"), upload.Csv());
            }
            double minutes = (DateTime.Now - Joined).TotalMinutes;
            if (_seconds < 10) return;
            double s = _seconds;
            double join = JoinWatch.Last != null && JoinWatch.Last.Server == Server ? JoinWatch.Last.TotalSeconds : double.NaN;
            string line = string.Join(",", new[]
            {
                p.SessionName, Joined.ToString("HH:mm:ss", Csv.Inv), Csv.Q(Server), Csv.F(minutes, "0.0"), Csv.F(_sumPlayers / s, "0.0"),
                Csv.F(Avg(_netPings), "0"), Csv.F(Pct(_netPings, 95), "0"), Csv.F(Avg(_gamePings), "0"), Csv.F(Pct(_gamePings, 95), "0"),
                Csv.F(Avg(_delays), "0"), Csv.F(Pct(_delays, 95), "0"), Hitches.ToString(Csv.Inv), HitchesServer.ToString(Csv.Inv), HitchesConnection.ToString(Csv.Inv),
                Csv.F(LongestHitch, "0.0"), Csv.F(_qualitySeconds > 0 ? _sumQl / _qualitySeconds : double.NaN, "0.00"), Csv.F(_qualitySeconds > 0 ? _sumQr / _qualitySeconds : double.NaN, "0.00"),
                Csv.F(_sumUp / s, "0.0"), Csv.F(_sumDown / s, "0.0"), Csv.F(100.0 * _backedUp / s, "0.0"), Csv.F(_sumZdoRecv / s, "0"),
                Csv.F(_ownerSamples > 0 ? _sumMinePct / _ownerSamples : double.NaN, "0"), Csv.F(_creatureSamples > 0 ? _sumCreaturesMinePct / _creatureSamples : double.NaN, "0"),
                Csv.F(join, "0.0"),
            }) + "\n";
            string path = Path.Combine(p.RootFolder, "server_visits.csv");
            RunStamp.UpgradeHeader(path, VisitHeader);
            p.Writer.Append(path, (File.Exists(path) ? "" : VisitHeader) + line);
            foreach (var l in Summary(minutes)) _log.LogInfo(l);
            if (upload != null)
                foreach (var l in UploadLines(upload, 8)) _log.LogInfo(l);
        }

        /// <summary>The biggest parts of the upload, in plain words.</summary>
        public static List<string> UploadLines(UploadWatch u, int n)
        {
            double s = u.Seconds;
            var l = new List<string>();
            var top = u.Top(n).Where(r => r.Sent + r.Changes > 0).ToList();
            if (top.Count == 0) return l;
            double kb = u.Rows.Sum(r => r.SentBytes) / s / 1024;
            l.Add($"  What filled the upload ({kb:0.0} KB/s of world objects; per type of object: sent per second, KB/s, changes per second, and the code that changed them):");
            foreach (var r in top)
                l.Add($"  {r.SentBytes / s / 1024,7:0.0} KB/s  {r.Name}{(r.Loaded > 0 ? $" (up to {r.Loaded} loaded, {r.LoadedMine} run by you)" : "")}: {r.Sent / s:0} sent/s, {r.Changes / s:0} changes/s" +
                      (r.Samples > 0 ? $"; changed by {UploadWatch.ChangedByText(r, 3)}" : ""));
            l.Add($"  Full list: upload.csv in the session folder. (Finding the code took {u.SampleTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, s / 60):0.0} ms per minute; it is left out of the results.)");
            return l;
        }

        /// <summary>The visit so far in plain words (log, perf/connection).</summary>
        public List<string> Summary(double minutes)
        {
            var l = new List<string>();
            double s = Math.Max(1, _seconds);
            l.Add($"Server {(Server.Length > 0 ? Server : "")}: {minutes:0} min, {_sumPlayers / s:0.0} players on average.");
            l.Add($"  Ping: network {Avg(_netPings):0} ms (95% under {Pct(_netPings, 95):0}), through the game {Avg(_gamePings):0} ms (95% under {Pct(_gamePings, 95):0}); " +
                  $"the server took {Avg(_delays):0} ms on average to answer (95% under {Pct(_delays, 95):0}).");
            l.Add(Hitches == 0 ? "  Server hitches (no answer for over a second): none."
                               : $"  Server hitches (no answer for over a second): {Hitches} ({HitchesServer} server busy, {HitchesConnection} connection trouble), longest {LongestHitch:0.0} s. Times: server_hitches.csv.");
            if (_qualitySeconds > 0)
                l.Add($"  Connection quality (share of packets that arrive): yours {_sumQl / _qualitySeconds:P0}, the server's {_sumQr / _qualitySeconds:P0}.");
            l.Add($"  Traffic: upload {_sumUp / s:0.0} KB/s, download {_sumDown / s:0.0} KB/s; upload backed up (at least 90% of the {BacklogLimit / 1024} KB limit, world updates held back) {100.0 * _backedUp / s:0}% of the time; " +
                  $"{_sumZdoRecv / s:0} world objects received per second.");
            if (_ownerSamples > 0)
                l.Add($"  You ran {_sumMinePct / _ownerSamples:0}% of the objects loaded around you" +
                      (_creatureSamples > 0 ? $" and {_sumCreaturesMinePct / _creatureSamples:0}% of the creatures" : "") + " (the rest ran on other players' PCs or nobody's).");
            return l;
        }

        public double MinutesConnected => Connected ? (DateTime.Now - Joined).TotalMinutes : 0;

        private static float Avg(List<float> v) => v.Count == 0 ? float.NaN : v.Average();
        private static float Median(List<float> v) => Pct(v, 50);

        private static float Pct(List<float> v, double pct)
        {
            if (v.Count == 0) return float.NaN;
            var sorted = v.ToArray();
            Array.Sort(sorted);
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(pct / 100 * sorted.Length) - 1)];
        }
    }
}
