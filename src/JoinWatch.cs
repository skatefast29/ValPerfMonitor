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
    /// Joining a dedicated server, step by step: from the game starting its network (ZNet.Awake) through
    /// the connection, the handshake, the password, the server accepting you, the first world data, to your
    /// player standing in the world. Also counts everything the server sends meanwhile, per message and
    /// mod (config sync from ServerSync and Jötunn mods shows up here), with its own hooks that come off
    /// once you are in the world.
    /// Files: join_steps.csv and join_traffic.csv in the session folder; one line per join in
    /// BepInEx\ValPerfMonitor\join_times.csv.
    /// </summary>
    internal sealed class JoinWatch
    {
        public const string StepsHeader = "time,step,seconds_from_start,seconds_for_step\n";
        public const string TimesHeader = "session,time,server,total_s,connect_s,handshake_s,password_s,accepted_s,first_world_data_s,in_world_s,received_kb,config_sync_kb,biggest_message,outcome\n";

        private static Harmony _start;
        private static string _harmonyId;
        private static ManualLogSource _log;
        private static ModRegistry _registry;

        public static JoinWatch Running, Last;

        public readonly List<(string step, double at)> Steps = new List<(string, double)>();
        public string Server = "";
        public double TotalSeconds = double.NaN;
        private readonly long _t0 = Stopwatch.GetTimestamp();
        private readonly DateTime _when = DateTime.Now;
        private readonly Harmony _harmony;
        private readonly NetworkStats _traffic;
        private bool _gotWorldData, _spawned;

        /// <summary>One small permanent hook on ZNet.Awake (once per world load); the rest only while joining.</summary>
        public static void Attach(string harmonyId, ModRegistry registry, ManualLogSource log)
        {
            _log = log;
            _registry = registry;
            _harmonyId = harmonyId;
            _start = new Harmony(harmonyId + ".start");
            try
            {
                _start.Patch(AccessTools.Method(typeof(ZNet), "Awake"), postfix: new HarmonyMethod(typeof(JoinWatch), nameof(ZNetAwakePostfix)));
            }
            catch (Exception e) { log.LogWarning($"Join timing off: {e.Message}"); }
        }

        private static void ZNetAwakePostfix(ZNet __instance)
        {
            try
            {
                if (__instance == null || __instance.IsServer()) return;
                Running?.Finish("left before it finished");
                Running = new JoinWatch();
            }
            catch (Exception e) { _log.LogDebug($"Join timing: {e.Message}"); }
        }

        private JoinWatch()
        {
            _harmony = new Harmony(_harmonyId + ".steps");
            Mark("network started (world scene loaded)");
            int ok = 0;
            ok += Hook(typeof(ZNet), "OnNewConnection", nameof(Connected));
            ok += Hook(typeof(ZNet), "RPC_ClientHandshake", nameof(Handshake));
            ok += Hook(typeof(ZNet), "SendPeerInfo", nameof(PeerInfoSent));
            ok += Hook(typeof(ZNet), "RPC_PeerInfo", nameof(Accepted));
            ok += Hook(typeof(ZDOMan), "RPC_ZDOData", nameof(WorldData));
            if (ok < 5) _log.LogDebug($"Join timing: {ok} of 5 step hooks attached.");
            _traffic = new NetworkStats(_registry, _log, join: true);
            _traffic.AttachJoin(_harmony);
        }

        private int Hook(Type type, string method, string prefix)
        {
            try
            {
                var m = AccessTools.Method(type, method);
                if (m == null) return 0;
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(JoinWatch), prefix));
                return 1;
            }
            catch (Exception e) { _log.LogDebug($"Join timing hook on {method}: {e.Message}"); return 0; }
        }

        private double Now => (Stopwatch.GetTimestamp() - _t0) / (double)Stopwatch.Frequency;

        private void Mark(string step)
        {
            if (Steps.Any(s => s.step == step)) return;
            Steps.Add((step, Now));
        }

        private static void Connected() => Running?.Mark("connected to the server");
        private static void Handshake() => Running?.Mark("server answered (handshake)");
        private static void PeerInfoSent() => Running?.Mark("sent your details (after the password, if one was asked)");
        private static void Accepted() => Running?.Mark("server accepted you");

        private static void WorldData()
        {
            var r = Running;
            if (r == null || r._gotWorldData) return;
            r._gotWorldData = true;
            r.Mark("first world data arrived");
        }

        /// <summary>Main thread, every frame while joining.</summary>
        public void Tick(Plugin p)
        {
            if (!_spawned && Player.m_localPlayer != null)
            {
                _spawned = true;
                Mark("your player is in the world");
            }
            if (Server.Length == 0)
                try { var peer = ZNet.instance?.GetServerPeer(); if (peer != null) Server = ServerName(peer); } catch { }
            if (_spawned && p.WorldReady) Finish("joined");
            else if (Now > 300) Finish("still not in the world after 5 minutes");
            else if (ZNet.instance == null) Finish("left before it finished");
        }

        internal static string ServerName(ZNetPeer peer)
        {
            string name = "";
            try { name = ZNet.instance.GetWorldName() ?? ""; } catch { }
            try { string host = peer.m_socket.GetHostName(); if (!string.IsNullOrEmpty(host)) name = (name.Length > 0 ? name + " @ " : "") + host; } catch { }
            return name;
        }

        public void Finish(string outcome)
        {
            if (Running != this) return;
            Running = null;
            Last = this;
            try { _harmony.UnpatchSelf(); } catch (Exception e) { _log.LogDebug($"Join timing: {e.Message}"); }
            var p = Plugin.Instance;
            bool joined = outcome == "joined";
            if (joined) Mark("world settled (loading done)");
            TotalSeconds = Steps.Count > 0 ? Steps[Steps.Count - 1].at : double.NaN;
            if (p == null || p.Writer == null || p.SessionFolder == null) return;

            string when = _when.ToString("HH:mm:ss", Csv.Inv);
            var sb = new StringBuilder(StepsHeader);
            double prev = 0;
            foreach (var (step, at) in Steps)
            {
                sb.Append(when).Append(',').Append(Csv.Q(step)).Append(',').Append(Csv.F(at, "0.00")).Append(',').Append(Csv.F(at - prev, "0.00")).Append('\n');
                prev = at;
            }
            string stamp = _when.ToString("HH-mm-ss", Csv.Inv);
            p.Writer.Write(Path.Combine(p.SessionFolder, $"join_steps_{stamp}.csv"), sb.ToString());
            p.Writer.Write(Path.Combine(p.SessionFolder, $"join_traffic_{stamp}.csv"), _traffic.SummaryCsv(Math.Max(1, Now)));

            var received = _traffic.Entries.Where(e => e.Kind == "peer" && e.RecvBytes > 0).OrderByDescending(e => e.RecvBytes).ToList();
            double kb = _traffic.PeerReceivedKb;
            var sync = _traffic.Entries.Where(e => e.RecvBytes > 0 && e.Name.IndexOf("ConfigSync", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            double syncKb = sync.Sum(e => e.RecvBytes) / 1024.0;
            var big = received.FirstOrDefault(e => e.Name != "ZDOData" && e.Name != "RoutedRPC") ?? received.FirstOrDefault();
            string line = string.Join(",", new[]
            {
                p.SessionName, when, Csv.Q(Server), Csv.F(TotalSeconds, "0.0"),
                Csv.F(At("connected to the server"), "0.0"), Csv.F(At("server answered (handshake)"), "0.0"),
                Csv.F(At("sent your details (after the password, if one was asked)") - At("server answered (handshake)"), "0.0"),
                Csv.F(At("server accepted you"), "0.0"), Csv.F(At("first world data arrived"), "0.0"), Csv.F(At("your player is in the world"), "0.0"),
                Csv.F(kb, "0"), Csv.F(syncKb, "0"), Csv.Q(big == null ? "" : $"{Label(big)} {big.RecvBytes / 1024.0:0} KB"), Csv.Q(outcome),
            }) + "\n";
            string path = Path.Combine(p.RootFolder, "join_times.csv");
            RunStamp.UpgradeHeader(path, TimesHeader);
            p.Writer.Append(path, (File.Exists(path) ? "" : TimesHeader) + line);

            _log.LogInfo($"Joining {(Server.Length > 0 ? Server : "the server")}: {outcome} after {TotalSeconds:0.0} s; {kb:0} KB received from the server meanwhile.");
            prev = 0;
            foreach (var (step, at) in Steps)
            {
                _log.LogInfo($"  {at - prev,6:0.00} s  {step}");
                prev = at;
            }
            foreach (var e in received.Take(5))
                _log.LogInfo($"  received {e.RecvBytes / 1024.0,7:0.0} KB  {Label(e)} ({e.RecvCalls}x)");
            if (sync.Count > 0)
                _log.LogInfo($"  Config sync from the server: {syncKb:0.0} KB over {sync.Count} mods (largest: {string.Join(", ", sync.OrderByDescending(e => e.RecvBytes).Take(3).Select(e => $"{Label(e)} {e.RecvBytes / 1024.0:0.0} KB").ToArray())}).");
        }

        private static string Label(NetworkStats.Entry e) => e.Owner.Length > 0 && e.Owner != "Valheim" && !e.Name.Contains(e.Owner) ? $"{e.Name} [{e.Owner}]" : e.Name;

        private double At(string step)
        {
            foreach (var s in Steps) if (s.step == step) return s.at;
            return double.NaN;
        }
    }
}
