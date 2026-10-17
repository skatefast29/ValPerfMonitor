using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Relog test: what goes wrong only after logging out and back in. Some mods keep data in static fields across a
    /// logout (ids of objects that no longer exist, lists that are never cleared), which shows up as errors that start
    /// after the first relog, or as memory that grows with every relog.
    ///
    /// perf/relog_test arms it in a world; that moment is cycle 0. The logging out and in is done by the caller
    /// (a ValAgentBridge test script, see examples/relog-test.json, or by hand). After every login, once the world has
    /// settled, a cycle is recorded: error counts, game objects and materials, and (unless census = false) a memory
    /// census, whose static fields and Unity objects are compared across cycles. perf/relog_result reports errors whose
    /// first appearance came after the first logout, and what grew in every cycle.
    /// </summary>
    internal sealed class RelogTest
    {
        public sealed class Cycle
        {
            public int Number;
            public double SessionSeconds, LogNow;
            public int GameObjects = -1, Materials = -1, ObjectsLoaded = -1;
            public double MonoUsedMb = double.NaN, GcMs = double.NaN;
            public long CensusObjects = -1;
            public Dictionary<string, int> LogCounts;
            public Dictionary<string, long> Roots;      // census: objects per static field / component type
            public Dictionary<string, int> UnityRoots;   // census: game objects per scene root, materials per name
            public bool Done;
        }

        public static RelogTest Running;
        public readonly bool Census;
        public readonly int SettleSeconds;
        public readonly List<Cycle> Cycles = new List<Cycle>();
        public double FirstLogoutAt = double.NaN; // LogWatch.Now
        // Each logout and the login after it (LogWatch.Now): messages first seen in between came with leaving or loading.
        private readonly List<(double logout, double login)> _away = new List<(double, double)>();
        public int Logouts;
        private Cycle _waiting;
        private float _inWorldSince = -1;
        private bool _wasInWorld = true;
        public string Status = "";

        public RelogTest(bool census, int settleSeconds)
        {
            Census = census;
            SettleSeconds = Math.Max(3, settleSeconds);
            _waiting = new Cycle { Number = 0 };
            _inWorldSince = Time.realtimeSinceStartup - SettleSeconds; // already settled: record cycle 0 now
        }

        /// <summary>True while a cycle is waiting to be recorded or its census is running: don't log out yet.</summary>
        public bool Busy => _waiting != null && !_waiting.Done;

        /// <summary>Every frame (Plugin.Tick).</summary>
        public void Tick(Plugin p, bool inWorld)
        {
            float now = Time.realtimeSinceStartup;
            if (inWorld != _wasInWorld)
            {
                _wasInWorld = inWorld;
                if (!inWorld)
                {
                    Logouts++;
                    if (double.IsNaN(FirstLogoutAt)) FirstLogoutAt = LogWatch.Now;
                    _away.Add((LogWatch.Now, double.PositiveInfinity));
                    if (_waiting != null && !_waiting.Done) Status = $"logged out before cycle {_waiting.Number} was recorded";
                    _waiting = new Cycle { Number = Cycles.Count };
                    _inWorldSince = -1;
                }
                else
                {
                    _inWorldSince = now;
                    if (_away.Count > 0) _away[_away.Count - 1] = (_away[_away.Count - 1].logout, LogWatch.Now);
                }
            }
            if (!inWorld || _waiting == null || _waiting.Done || _inWorldSince < 0) return;
            if (_waiting.SessionSeconds > 0) return; // recorded; waiting for its census
            if (now - _inWorldSince < SettleSeconds || !p.WorldReady || p.WarmingUp) { Status = $"cycle {_waiting.Number}: waiting for the world to settle"; return; }
            Record(p, _waiting);
        }

        private void Record(Plugin p, Cycle c)
        {
            p.MarkHeavy();
            c.SessionSeconds = p.SessionSeconds;
            c.LogNow = LogWatch.Now;
            c.LogCounts = LogWatch.Instance?.Snapshot() ?? new Dictionary<string, int>();
            c.ObjectsLoaded = GameContext.LoadedObjects();
            try
            {
                c.GameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                c.Materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            }
            catch { }
            Cycles.Add(c);
            if (!Census) { c.Done = true; Status = $"cycle {c.Number} recorded"; return; }
            var mc = p.StartMemoryCensus(100);
            if (mc.Error != null) { c.Done = true; Status = $"cycle {c.Number} recorded without a census: {mc.Error}"; return; }
            Status = $"cycle {c.Number}: memory census running";
        }

        /// <summary>From Plugin when a memory census finishes.</summary>
        public void CensusFinished(MemoryCensus mc)
        {
            var c = _waiting;
            if (c == null || c.Done || c.SessionSeconds <= 0) return;
            c.Done = true;
            if (mc.Error != null) { Status = $"cycle {c.Number}: census failed ({mc.Error})"; return; }
            c.MonoUsedMb = mc.MonoUsedAfterGc / 1048576.0;
            c.GcMs = mc.GcMedianMs;
            c.CensusObjects = mc.Objects;
            c.Roots = mc.RootTotals?.ToDictionary(kv => kv.Key, kv => kv.Value.count);
            if (mc.Unity != null)
            {
                c.UnityRoots = new Dictionary<string, int>();
                foreach (var kv in mc.Unity.RootCounts) c.UnityRoots["game objects under " + kv.Key] = kv.Value;
                foreach (var kv in mc.Unity.MaterialCounts) c.UnityRoots["material " + kv.Key] = kv.Value;
            }
            Status = $"cycle {c.Number} recorded";
        }

        // ---- Results -----------------------------------------------------------------------------------

        /// <summary>Errors and warnings first seen after the first logout, with how many in each cycle after it.</summary>
        /// <summary>whenFirst: "while playing", or "during logout or loading" (first seen between a logout and the next login).</summary>
        public List<(LogWatch.Entry entry, string mod, List<int> perCycle, string whenFirst)> NewErrors(ModRegistry registry)
        {
            var l = new List<(LogWatch.Entry, string, List<int>, string)>();
            var w = LogWatch.Instance;
            if (w == null || double.IsNaN(FirstLogoutAt)) return l;
            var now = w.Snapshot();
            foreach (var e in w.Entries().Where(e => e.FirstSeen >= FirstLogoutAt))
            {
                string key = LogWatch.Key(e);
                var per = new List<int>();
                for (int i = 1; i <= Cycles.Count; i++)
                {
                    var before = Cycles[i - 1].LogCounts;
                    var after = i < Cycles.Count ? Cycles[i].LogCounts : now;
                    int b = before != null && before.TryGetValue(key, out int x) ? x : 0;
                    int a = after != null && after.TryGetValue(key, out int y) ? y : 0;
                    per.Add(a - b);
                }
                bool away = _away.Any(a => e.FirstSeen >= a.logout && e.FirstSeen <= a.login + SettleSeconds);
                l.Add((e, w.Owner(e, registry), per, away ? "during logout or loading" : "while playing"));
            }
            return l.OrderByDescending(x => x.Item4 == "while playing").ThenByDescending(x => x.Item1.Level == "error").ThenByDescending(x => x.Item1.Count).ToList();
        }

        /// <summary>Census roots (static fields, component types, Unity objects) that grew in every cycle after the first.</summary>
        public List<(string name, List<long> counts)> GrowingEveryCycle(bool unity)
        {
            var withData = Cycles.Where(c => unity ? c.UnityRoots != null : c.Roots != null).ToList();
            var l = new List<(string, List<long>)>();
            if (withData.Count < 3) return l; // cycle 0 and at least two relogs
            IEnumerable<string> keys = unity ? withData.Last().UnityRoots.Keys : withData.Last().Roots.Keys;
            foreach (var k in keys)
            {
                var counts = withData.Select(c => unity ? (c.UnityRoots.TryGetValue(k, out int v) ? v : 0L) : (c.Roots.TryGetValue(k, out long v2) ? v2 : 0L)).ToList();
                bool grew = true;
                for (int i = 1; i < counts.Count && grew; i++) grew = counts[i] > counts[i - 1];
                if (grew && counts[counts.Count - 1] - counts[0] >= (unity ? 20 : 100)) l.Add((k, counts));
            }
            return l.OrderByDescending(x => x.Item2[x.Item2.Count - 1] - x.Item2[0]).Take(25).ToList();
        }
    }
}
