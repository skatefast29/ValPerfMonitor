using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// Network messages by name and by the mod that handles them.
    ///  - Routed RPCs (ZRoutedRpc): what mods and the game use to talk between players, including RPCs on
    ///    world objects (ZNetView). Counted when sent and when handled; handled includes calls a player
    ///    makes to itself, which in single player is all of them (no network, but still CPU).
    ///  - Peer messages (ZRpc): what actually goes over the connection, e.g. "ZDOData" (world object sync)
    ///    and "RoutedRPC" (the routed RPCs above, wrapped). Only exists with another player or a server.
    /// The owner is the assembly of the registered handler, looked up once per message name.
    /// Totals from the game's own connection stats (KB/s in and out, ping) are sampled per interval.
    /// Hooks exist only while the monitor's probes are attached (never in Baseline).
    /// </summary>
    internal sealed class NetworkStats
    {
        internal sealed class Entry
        {
            public string Kind, Name, Owner;
            public int Hash;
            public bool OwnerTried;
            public long SentCalls, SentBytes, RecvCalls, RecvBytes;          // capture totals
            public long IvSentCalls, IvSentBytes, IvRecvCalls, IvRecvBytes;  // current interval
        }

        private static NetworkStats _instance;

        private readonly ModRegistry _registry;
        private readonly ManualLogSource _log;
        private readonly Dictionary<int, Entry> _routed = new Dictionary<int, Entry>();
        private readonly Dictionary<string, Entry> _routedByName = new Dictionary<string, Entry>();
        private readonly Dictionary<int, Entry> _peer = new Dictionary<int, Entry>();
        private readonly Dictionary<string, Entry> _peerByName = new Dictionary<string, Entry>();
        public bool Attached;
        public bool Recording;

        // Connection totals, summed per interval and per capture.
        public double IvOutBytesSec, IvInBytesSec, IvPing; public int IvSamples;
        public double TotOutBytesSec, TotInBytesSec, TotPing; public int TotSamples;
        public int Peers;

        private static readonly AccessTools.FieldRef<ZRpc, ZPackage> RpcPkg = Safe(() => AccessTools.FieldRefAccess<ZRpc, ZPackage>("m_pkg"));
        // The handler tables hold internal/private types, so they are read as plain IDictionary (once per message name).
        private static readonly FieldInfo RpcFunctions = AccessTools.Field(typeof(ZRpc), "m_functions");
        private static readonly FieldInfo RoutedFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
        private static readonly FieldInfo ViewFunctions = AccessTools.Field(typeof(ZNetView), "m_functions");
        private static readonly AccessTools.FieldRef<ZRoutedRpc, List<ZNetPeer>> RoutedPeers =
            Safe(() => AccessTools.FieldRefAccess<ZRoutedRpc, List<ZNetPeer>>("m_peers"));

        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        public NetworkStats(ModRegistry registry, ManualLogSource log)
        {
            _registry = registry;
            _log = log;
            _instance = this;
        }

        // ---- Hooks ------------------------------------------------------------------------------

        public void Attach(Harmony harmony)
        {
            if (Attached) return;
            Attached = true;
            int ok = 0;
            ok += Patch(harmony, AccessTools.Method(typeof(ZRoutedRpc), nameof(ZRoutedRpc.InvokeRoutedRPC), new[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) }), nameof(RoutedSendPrefix), null);
            ok += Patch(harmony, AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC"), nameof(RouteRpcPrefix), null);
            ok += Patch(harmony, AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC"), nameof(RoutedHandlePrefix), null);
            ok += Patch(harmony, AccessTools.Method(typeof(ZRpc), nameof(ZRpc.Invoke), new[] { typeof(string), typeof(object[]) }), nameof(PeerSendPrefix), nameof(PeerSendPostfix));
            ok += Patch(harmony, AccessTools.Method(typeof(ZRpc), "HandlePackage"), nameof(PeerHandlePrefix), null);
            if (ok < 5) _log.LogWarning($"Network tracking: only {ok} of 5 hooks attached (the game's network code changed); some messages are not counted.");
        }

        private int Patch(Harmony harmony, MethodBase target, string prefix, string postfix)
        {
            if (target == null) return 0;
            try
            {
                // Already attached (a probe removal for Baseline failed on this method): don't add a second copy.
                var info = Harmony.GetPatchInfo(target);
                if (info != null && info.Prefixes.Concat(info.Postfixes).Any(x => x.owner == harmony.Id && x.PatchMethod?.DeclaringType == typeof(NetworkStats)))
                    return 1;
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(NetworkStats), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(NetworkStats), postfix));
                return 1;
            }
            catch (Exception e)
            {
                _log.LogDebug($"Network hook on {target.Name} failed: {e.Message}");
                return 0;
            }
        }

        private static bool Active => _instance != null && _instance.Recording && Thread.CurrentThread.ManagedThreadId == Probe.MainThreadId;

        private static void RoutedSendPrefix(string methodName)
        {
            if (!Active || methodName == null) return;
            var e = _instance.RoutedByName(methodName);
            e.SentCalls++; e.IvSentCalls++;
        }

        private static void RouteRpcPrefix(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!Active || rpcData == null) return;
            var peers = RoutedPeers != null ? RoutedPeers(__instance) : null;
            int n = peers == null ? 0 : peers.Count;
            if (n == 0) return; // nobody to send to: nothing leaves this machine
            if (rpcData.m_targetPeerID != 0) n = 1;
            long bytes = (long)(rpcData.m_parameters?.Size() ?? 0) * n;
            var e = _instance.Routed(rpcData.m_methodHash);
            e.SentBytes += bytes; e.IvSentBytes += bytes;
        }

        private static void RoutedHandlePrefix(ZRoutedRpc __instance, ZRoutedRpc.RoutedRPCData data)
        {
            if (!Active || data == null) return;
            var e = _instance.Routed(data.m_methodHash);
            long bytes = data.m_parameters?.Size() ?? 0;
            e.RecvCalls++; e.IvRecvCalls++;
            e.RecvBytes += bytes; e.IvRecvBytes += bytes;
            if (!e.OwnerTried) _instance.ResolveRouted(e, __instance, data);
        }

        private static void PeerSendPrefix(ZRpc __instance, out bool __state)
        {
            __state = Active && __instance.IsConnected();
        }

        private static void PeerSendPostfix(ZRpc __instance, string method, bool __state)
        {
            if (!__state || method == null || RpcPkg == null) return;
            var e = _instance.PeerByName(method);
            long bytes = RpcPkg(__instance)?.Size() ?? 0;
            e.SentCalls++; e.IvSentCalls++;
            e.SentBytes += bytes; e.IvSentBytes += bytes;
            if (!e.OwnerTried) _instance.ResolvePeer(e, __instance);
        }

        private static void PeerHandlePrefix(ZRpc __instance, ZPackage package)
        {
            if (!Active || package == null) return;
            int hash;
            int pos = package.GetPos();
            try { hash = package.ReadInt(); }
            catch { return; }
            finally { package.SetPos(pos); }
            var e = _instance.Peer(hash);
            long bytes = package.Size();
            e.RecvCalls++; e.IvRecvCalls++;
            e.RecvBytes += bytes; e.IvRecvBytes += bytes;
            if (!e.OwnerTried) _instance.ResolvePeer(e, __instance);
        }

        // ---- Lookups ----------------------------------------------------------------------------

        private Entry RoutedByName(string name)
        {
            if (_routedByName.TryGetValue(name, out var e)) return e;
            e = Routed(name.GetStableHashCode());
            if (e.Name.StartsWith("#")) e.Name = name;
            _routedByName[name] = e;
            return e;
        }

        private Entry Routed(int hash)
        {
            if (!_routed.TryGetValue(hash, out var e))
                _routed[hash] = e = new Entry { Kind = "routed", Hash = hash, Name = "#" + hash, Owner = "" };
            return e;
        }

        private Entry PeerByName(string name)
        {
            if (_peerByName.TryGetValue(name, out var e)) return e;
            e = Peer(name.GetStableHashCode());
            if (e.Name.StartsWith("#")) e.Name = name;
            _peerByName[name] = e;
            return e;
        }

        private Entry Peer(int hash)
        {
            if (!_peer.TryGetValue(hash, out var e))
                _peer[hash] = e = new Entry { Kind = "peer", Hash = hash, Name = hash == 0 ? "(ping)" : "#" + hash, Owner = hash == 0 ? "Valheim" : "" };
            return e;
        }

        private void ResolveRouted(Entry e, ZRoutedRpc rpc, ZRoutedRpc.RoutedRPCData data)
        {
            e.OwnerTried = true;
            try
            {
                object handler = null;
                if (data.m_targetZDO.IsNone())
                {
                    handler = Handler(RoutedFunctions, rpc, e.Hash);
                }
                else
                {
                    // An RPC on a world object: the handler is registered on that object's ZNetView.
                    var zdo = ZDOMan.instance?.GetZDO(data.m_targetZDO);
                    var view = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                    if (view != null) handler = Handler(ViewFunctions, view, e.Hash);
                    else e.OwnerTried = false; // object not here this time; try again on the next one
                }
                if (handler != null) e.Owner = OwnerOf(handler);
            }
            catch { }
        }

        private void ResolvePeer(Entry e, ZRpc rpc)
        {
            e.OwnerTried = true;
            try
            {
                var h = Handler(RpcFunctions, rpc, e.Hash);
                if (h != null) e.Owner = OwnerOf(h);
            }
            catch { }
        }

        private static object Handler(FieldInfo table, object owner, int hash)
        {
            if (table == null || owner == null) return null;
            return table.GetValue(owner) is System.Collections.IDictionary d && d.Contains(hash) ? d[hash] : null;
        }

        /// <summary>Every RPC holder class keeps its delegate in a private "m_action" field.</summary>
        private string OwnerOf(object holder)
        {
            var field = holder.GetType().GetField("m_action", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (!(field?.GetValue(holder) is Delegate d) || d.Method == null) return "";
            var asm = d.Method.DeclaringType?.Assembly;
            if (asm == null) return "";
            if (asm == typeof(ZNet).Assembly) return "Valheim";
            return _registry.Resolve(asm, null).Name;
        }

        // ---- Capture bookkeeping ----------------------------------------------------------------

        public IEnumerable<Entry> Entries => _routed.Values.Concat(_peer.Values);

        public void ResetCapture()
        {
            foreach (var e in Entries)
                e.SentCalls = e.SentBytes = e.RecvCalls = e.RecvBytes = e.IvSentCalls = e.IvSentBytes = e.IvRecvCalls = e.IvRecvBytes = 0;
            IvOutBytesSec = IvInBytesSec = IvPing = 0; IvSamples = 0;
            TotOutBytesSec = TotInBytesSec = TotPing = 0; TotSamples = 0;
        }

        /// <summary>Connection totals from the game (works in every mode, no hooks needed). Once per interval.</summary>
        public void SampleConnection()
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return;
                znet.GetNetStats(out _, out _, out int ping, out float outB, out float inB);
                Peers = znet.GetPeerConnections();
                IvOutBytesSec += outB; IvInBytesSec += inB; IvPing += ping; IvSamples++;
                TotOutBytesSec += outB; TotInBytesSec += inB; TotPing += ping; TotSamples++;
            }
            catch { }
        }

        public const string IntervalHeader = "time,session_s,kind,name,owner,sent_per_s,sent_kb_per_s,handled_per_s,received_kb_per_s\n";

        /// <summary>Rows for network.csv for one interval, then clears the interval counters.</summary>
        public string IntervalRows(string prefix, double seconds)
        {
            var sb = new StringBuilder();
            double s = Math.Max(0.001, seconds);
            if (IvSamples > 0)
                sb.Append(prefix).Append("connection,(all traffic),,,").Append(Csv.F(IvOutBytesSec / IvSamples / 1024, "0.00")).Append(",,")
                  .Append(Csv.F(IvInBytesSec / IvSamples / 1024, "0.00")).Append('\n');
            foreach (var e in Entries.Where(x => x.IvSentCalls + x.IvRecvCalls > 0).OrderByDescending(x => x.IvSentCalls + x.IvRecvCalls))
                sb.Append(prefix).Append(e.Kind).Append(',').Append(Csv.Q(e.Name)).Append(',').Append(Csv.Q(e.Owner)).Append(',')
                  .Append(Csv.F(e.IvSentCalls / s, "0.00")).Append(',').Append(Csv.F(e.IvSentBytes / s / 1024, "0.000")).Append(',')
                  .Append(Csv.F(e.IvRecvCalls / s, "0.00")).Append(',').Append(Csv.F(e.IvRecvBytes / s / 1024, "0.000")).Append('\n');
            foreach (var e in Entries) e.IvSentCalls = e.IvSentBytes = e.IvRecvCalls = e.IvRecvBytes = 0;
            IvOutBytesSec = IvInBytesSec = IvPing = 0; IvSamples = 0;
            return sb.ToString();
        }

        public string SummaryCsv(double seconds)
        {
            double s = Math.Max(0.001, seconds);
            var sb = new StringBuilder("kind,name,owner,sent,sent_per_s,sent_kb,handled,handled_per_s,received_kb\n");
            if (TotSamples > 0)
                sb.Append("connection,(all traffic; average KB/s),,,,").Append(Csv.F(TotOutBytesSec / TotSamples / 1024, "0.00")).Append(",,,")
                  .Append(Csv.F(TotInBytesSec / TotSamples / 1024, "0.00")).Append('\n');
            foreach (var e in Top(int.MaxValue))
                sb.Append(e.Kind).Append(',').Append(Csv.Q(e.Name)).Append(',').Append(Csv.Q(e.Owner)).Append(',')
                  .Append(e.SentCalls).Append(',').Append(Csv.F(e.SentCalls / s, "0.00")).Append(',').Append(Csv.F(e.SentBytes / 1024.0, "0.0")).Append(',')
                  .Append(e.RecvCalls).Append(',').Append(Csv.F(e.RecvCalls / s, "0.00")).Append(',').Append(Csv.F(e.RecvBytes / 1024.0, "0.0")).Append('\n');
            return sb.ToString();
        }

        public IEnumerable<Entry> Top(int n) =>
            Entries.Where(x => x.SentCalls + x.RecvCalls > 0).OrderByDescending(x => x.SentCalls + x.RecvCalls).ThenBy(x => x.Name).Take(n);
    }
}
