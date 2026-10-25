using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// What fills the upload to a dedicated server: every world object this PC sends (ZDOMan.SendZDOs, one
    /// ZDO.Serialize each) is counted per prefab with its size, and every change to an object (its data
    /// revision going up: a value set, a move, a turn) too. A few times a second the code that made the
    /// change is noted from the call stack (the first game method, and the first mod on the way), so each
    /// prefab's changes can be put down to a mod. Hooks only while connected to a server (ServerLink).
    /// The stack samples cost about 0.1 ms each and are added to the monitor's own time.
    /// </summary>
    internal sealed class UploadWatch
    {
        public sealed class Row
        {
            public int Prefab;
            public string Name;
            public long Sent, SentBytes, Changes, OwnerChanges;
            public readonly Dictionary<string, double> ChangedBy = new Dictionary<string, double>();
            public double Samples;
            public long ChangesSinceSample;
            public double NextSample;
            /// <summary>Most copies loaded at once, and how many of them this PC ran then (counted every 30 s).</summary>
            public int Loaded, LoadedMine;
        }

        public const string CsvHeader = "prefab,loaded_most,run_by_you,sent_per_s,sent_kb_per_s,changes_per_s,owner_changes_per_s,changed_by\n";

        /// <summary>Bytes around each object in a ZDOData message besides its own data (id, revisions, owner, position, length).</summary>
        private const int PerObjectOverhead = 42;
        /// <summary>Each kind of object is sampled at most this often, and all of them together at most MaxSamplesPerSecond.</summary>
        private const float SampleEvery = 2f;
        private const int MaxSamplesPerSecond = 8;

        private static UploadWatch _on;
        private readonly Harmony _harmony;
        private readonly ModRegistry _registry;
        private readonly Assembly _game = typeof(ZNet).Assembly, _self = typeof(UploadWatch).Assembly;
        private readonly Dictionary<int, Row> _rows = new Dictionary<int, Row>();
        private readonly Dictionary<MethodBase, string> _who = new Dictionary<MethodBase, string>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _sending;
        private double _budget = MaxSamplesPerSecond, _budgetAt;
        public long SampleTicks;
        public readonly DateTime Since = DateTime.Now;

        public double Seconds => Math.Max(1, (DateTime.Now - Since).TotalSeconds);
        public IEnumerable<Row> Rows => _rows.Values;

        public static UploadWatch Start(string harmonyId, ModRegistry registry, ManualLogSource log)
        {
            var w = new UploadWatch(harmonyId, registry);
            int ok = 0;
            ok += w.Hook(typeof(ZDOMan), "SendZDOs", nameof(SendPrefix), nameof(SendPostfix), log);
            ok += w.Hook(typeof(ZDO), nameof(ZDO.Serialize), null, nameof(SerializePostfix), log);
            ok += w.Hook(typeof(ZDO), "IncreaseDataRevision", nameof(ChangedPrefix), null, log);
            ok += w.Hook(typeof(ZDO), "IncreaseOwnerRevision", nameof(OwnerChangedPrefix), null, log);
            if (ok < 4) log.LogInfo($"Upload breakdown: only {ok} of 4 hooks attached (the game's code changed); some of it is not counted.");
            _on = w;
            return w;
        }

        private UploadWatch(string harmonyId, ModRegistry registry)
        {
            _harmony = new Harmony(harmonyId);
            _registry = registry;
        }

        private int Hook(Type type, string method, string prefix, string postfix, ManualLogSource log)
        {
            try
            {
                var m = AccessTools.Method(type, method);
                if (m == null) return 0;
                _harmony.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(UploadWatch), prefix),
                                  postfix: postfix == null ? null : new HarmonyMethod(typeof(UploadWatch), postfix));
                return 1;
            }
            catch (Exception e) { log.LogDebug($"Upload breakdown hook on {method}: {e.Message}"); return 0; }
        }

        public void Stop()
        {
            if (_on == this) _on = null;
            try { _harmony.UnpatchSelf(); } catch { }
        }

        private static bool OnMain => System.Threading.Thread.CurrentThread.ManagedThreadId == Probe.MainThreadId;

        private static void SendPrefix() { if (_on != null && OnMain) _on._sending = true; }
        private static void SendPostfix() { if (_on != null && OnMain) _on._sending = false; }

        private static void SerializePostfix(ZDO __instance, ZPackage pkg)
        {
            var w = _on;
            if (w == null || !w._sending || __instance == null || pkg == null || !OnMain) return;
            var r = w.Get(__instance.GetPrefab());
            r.Sent++;
            r.SentBytes += pkg.Size() + PerObjectOverhead;
        }

        private static void OwnerChangedPrefix(ZDO __instance)
        {
            var w = _on;
            if (w == null || __instance == null || !OnMain) return;
            w.Get(__instance.GetPrefab()).OwnerChanges++;
        }

        private static void ChangedPrefix(ZDO __instance)
        {
            var w = _on;
            if (w == null || __instance == null || !OnMain) return;
            var r = w.Get(__instance.GetPrefab());
            r.Changes++;
            r.ChangesSinceSample++;
            double now = w._clock.Elapsed.TotalSeconds;
            if (now < r.NextSample) return;
            // Per kind of object, so one that updates early in every frame can't take all the samples; within a
            // global budget per second.
            w._budget = Math.Min(MaxSamplesPerSecond, w._budget + (now - w._budgetAt) * MaxSamplesPerSecond);
            w._budgetAt = now;
            if (w._budget < 1) return;
            w._budget--;
            r.NextSample = now + SampleEvery;
            long t0 = Stopwatch.GetTimestamp();
            // This sample stands for this kind's changes since its last sample.
            string who = w.Who();
            r.ChangedBy.TryGetValue(who, out double v);
            r.ChangedBy[who] = v + r.ChangesSinceSample;
            r.Samples += r.ChangesSinceSample;
            r.ChangesSinceSample = 0;
            long spent = Stopwatch.GetTimestamp() - t0;
            w.SampleTicks += spent;
            Plugin.Instance?.AddMonitorTicks(spent);
        }

        /// <summary>"game method <- its caller [mod]": the first two callers outside ZDO, and the first mod on the stack (up to 14 frames).</summary>
        private string Who()
        {
            string game = null, caller = null, mod = null;
            try
            {
                var trace = new StackTrace(2, false);
                int n = Math.Min(trace.FrameCount, 14);
                for (int i = 0; i < n && mod == null; i++)
                {
                    var m = trace.GetFrame(i)?.GetMethod();
                    if (m == null) continue;
                    var type = m.DeclaringType;
                    if (type == typeof(ZDO) || type == typeof(ZDOExtraData) || type == typeof(UploadWatch)) continue;
                    string name = Name(m);
                    if (name.StartsWith("ZDO.", StringComparison.Ordinal) || name.StartsWith("ZDOExtraData.", StringComparison.Ordinal)) continue; // patched copies of ZDO's own methods
                    if (game == null) game = name;
                    else if (caller == null) caller = name;
                    var asm = type?.Assembly;
                    if (asm == null || asm == _game || asm == _self) continue;
                    if (!_who.TryGetValue(m, out mod))
                        _who[m] = mod = _registry.ForPluginAssembly(asm)?.Name;
                }
            }
            catch { }
            if (game == null) return "(unknown)";
            string where = caller == null ? game : game + " <- " + caller;
            return mod == null ? where : $"{where} [{mod}]";
        }

        /// <summary>"Type.Method". A method patched with Harmony runs as a copy named "DMD&lt;Type::Method&gt;": shown as the original.</summary>
        private static string Name(MethodBase m)
        {
            string full = m.DeclaringType != null ? m.DeclaringType.Name + "." + m.Name : m.Name;
            int a = full.IndexOf("DMD<", StringComparison.Ordinal);
            if (a < 0) return full;
            int b = full.IndexOf('>', a);
            return b > a ? full.Substring(a + 4, b - a - 4).Replace("::", ".") : full;
        }

        private Row Get(int prefab)
        {
            if (!_rows.TryGetValue(prefab, out var r))
            {
                string name = null;
                try { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null; if (go != null) name = go.name; } catch { }
                _rows[prefab] = r = new Row { Prefab = prefab, Name = name ?? "#" + prefab };
            }
            return r;
        }

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances = InstancesRef();

        private static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> InstancesRef()
        {
            try { return AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances"); } catch { return null; }
        }

        private readonly Dictionary<int, (int all, int mine)> _count = new Dictionary<int, (int, int)>();

        /// <summary>Loaded copies per prefab right now, keeping each prefab's highest count. Walks the loaded objects once.</summary>
        public void CountLoaded()
        {
            var scene = ZNetScene.instance;
            if (scene == null || Instances == null) return;
            _count.Clear();
            try
            {
                foreach (var kv in Instances(scene))
                {
                    var z = kv.Key;
                    if (z == null) continue;
                    int prefab = z.GetPrefab();
                    _count.TryGetValue(prefab, out var c);
                    _count[prefab] = (c.all + 1, c.mine + (z.IsOwner() ? 1 : 0));
                }
            }
            catch { return; }
            foreach (var kv in _count)
            {
                if (!_rows.TryGetValue(kv.Key, out var r)) continue; // only kinds that were sent or changed
                if (kv.Value.all > r.Loaded) { r.Loaded = kv.Value.all; r.LoadedMine = kv.Value.mine; }
            }
        }

        public List<Row> Top(int n) => _rows.Values.OrderByDescending(r => r.SentBytes).ThenByDescending(r => r.Changes).Take(n).ToList();

        /// <summary>The top changers of one prefab, as "who 62%, who 30%".</summary>
        public static string ChangedByText(Row r, int n) =>
            r.Samples <= 0 ? "" : string.Join(", ", r.ChangedBy.OrderByDescending(k => k.Value).Take(n).Select(k => $"{k.Key} {100 * k.Value / r.Samples:0}%").ToArray());

        public string Csv()
        {
            double s = Seconds;
            var sb = new StringBuilder(CsvHeader);
            foreach (var r in _rows.Values.Where(r => r.Sent + r.Changes > 0).OrderByDescending(r => r.SentBytes).ThenByDescending(r => r.Changes))
                sb.Append(ValPerfMonitor.Csv.Q(r.Name)).Append(',').Append(r.Loaded).Append(',').Append(r.LoadedMine).Append(',').Append(ValPerfMonitor.Csv.F(r.Sent / s, "0.0")).Append(',')
                  .Append(ValPerfMonitor.Csv.F(r.SentBytes / s / 1024, "0.00")).Append(',').Append(ValPerfMonitor.Csv.F(r.Changes / s, "0.0")).Append(',')
                  .Append(ValPerfMonitor.Csv.F(r.OwnerChanges / s, "0.00")).Append(',').Append(ValPerfMonitor.Csv.Q(ChangedByText(r, 4))).Append('\n');
            return sb.ToString();
        }
    }
}
