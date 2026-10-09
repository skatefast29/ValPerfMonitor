using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Times every object the game spawns into the world (ZNetScene.CreateObject: the object, its components'
    /// startup code and every mod patch on that startup), per prefab. Arriving somewhere creates hundreds of
    /// objects, and a few kinds can take tens of milliseconds each; this names them. The hook stays attached
    /// in every capture mode (its own Harmony instance) and costs a timestamp and a dictionary update per
    /// object, only when objects are created.
    /// </summary>
    internal static class ObjectLoads
    {
        public sealed class Stat
        {
            public int Count, Over5Ms;
            public double TotalMs, MaxMs;
            public string Name = "", ModHint = "";
            // Split into parts (SpawnParts): only spawns after the object's scripts were hooked.
            public bool SplitQueued, SplitReady;
            public int SplitSpawns;
            public double SplitMs;
            public Dictionary<int, double> Parts; // part id -> own ms, summed over split spawns
        }

        private static readonly Dictionary<int, Stat> Stats = new Dictionary<int, Stat>();
        // Locations (ruins, camps, dungeon entrances) spawned by their LocationProxy, per location: a
        // LocationProxy's own time includes these when the location spawns right away.
        private static readonly Dictionary<int, Stat> LocationStats = new Dictionary<int, Stat>();
        private static System.Reflection.MethodInfo _getLocation;
        private static System.Reflection.FieldInfo _locationName;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static ModRegistry _registry;

        public static int Objects { get; private set; }

        public static void Attach(string harmonyId, ModRegistry registry, ManualLogSource log)
        {
            _registry = registry;
            var create = AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
            if (create == null || create.ReturnType != typeof(GameObject)) { log.LogWarning("Object load timing: ZNetScene.CreateObject has changed; not timed."); return; }
            try
            {
                new Harmony(harmonyId).Patch(create,
                    prefix: new HarmonyMethod(typeof(ObjectLoads), nameof(Prefix)) { priority = Priority.First + 100 },
                    finalizer: new HarmonyMethod(typeof(ObjectLoads), nameof(Finalizer)) { priority = Priority.Last - 100 });
            }
            catch (Exception e) { log.LogWarning($"Object load timing not attached: {e.Message}"); }

            var spawnLocation = AccessTools.Method(typeof(ZoneSystem), "SpawnProxyLocation", new[] { typeof(int), typeof(int), typeof(Vector3), typeof(Quaternion) });
            _getLocation = AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) });
            _locationName = AccessTools.Field(typeof(ZoneSystem.ZoneLocation), "m_prefabName");
            if (spawnLocation == null || _getLocation == null || _locationName == null) { log.LogWarning("Object load timing: ZoneSystem.SpawnProxyLocation has changed; locations not timed by name."); return; }
            try
            {
                new Harmony(harmonyId).Patch(spawnLocation,
                    prefix: new HarmonyMethod(typeof(ObjectLoads), nameof(LocationPrefix)) { priority = Priority.First + 100 },
                    postfix: new HarmonyMethod(typeof(ObjectLoads), nameof(LocationPostfix)) { priority = Priority.Last - 100 });
            }
            catch (Exception e) { log.LogWarning($"Location timing not attached: {e.Message}"); }
        }

        private static void LocationPostfix(int hash, long __state)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * MsPerTick;
            if (!LocationStats.TryGetValue(hash, out var s))
            {
                LocationStats[hash] = s = new Stat();
                try
                {
                    var loc = _getLocation.Invoke(ZoneSystem.instance, new object[] { hash });
                    s.Name = "Location: " + (loc == null ? hash.ToString() : _locationName.GetValue(loc) as string);
                }
                catch { s.Name = "Location: " + hash; }
            }
            s.Count++;
            s.TotalMs += ms;
            if (ms > s.MaxMs) s.MaxMs = ms;
            if (ms >= 5) s.Over5Ms++;
        }

        private static void LocationPrefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Prefix(out long __state)
        {
            SpawnParts.SpawnDepth++;
            __state = Stopwatch.GetTimestamp();
        }

        // A finalizer (not a postfix) so the spawn depth stays right when the spawn throws.
        private static void Finalizer(ZDO zdo, GameObject __result, long __state)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * MsPerTick;
            bool outer = --SpawnParts.SpawnDepth <= 0;
            if (outer) SpawnParts.SpawnDepth = 0;
            if (zdo == null) { if (outer) SpawnParts.EndSpawn(null, ms, null); return; }
            int hash = zdo.GetPrefab();
            if (!Stats.TryGetValue(hash, out var s))
            {
                // First of its kind: remember its name and mods now (after timing it), while it exists.
                Stats[hash] = s = new Stat();
                if (__result != null)
                {
                    s.Name = __result.name.EndsWith("(Clone)") ? __result.name.Substring(0, __result.name.Length - 7) : __result.name;
                    s.ModHint = ModHint(__result);
                }
            }
            s.Count++;
            s.TotalMs += ms;
            if (ms > s.MaxMs) s.MaxMs = ms;
            if (ms >= 5) s.Over5Ms++;
            Objects++;
            if (outer) SpawnParts.EndSpawn(s, ms, __result);
        }

        public static void Reset() { Stats.Clear(); LocationStats.Clear(); Objects = 0; }

        public sealed class Row
        {
            public string Prefab = "", ModHint = "";
            public int Count, Over5Ms;
            public double TotalMs, AvgMs, MaxMs;
            public int SplitSpawns;
            public double OtherAvgMs;   // per split spawn: time not in any timed part (Unity copying the object, unsplit game code)
            public List<(string name, double avgMs)> Parts = new List<(string, double)>();
        }

        /// <summary>The slowest prefabs, by total time spent creating them.</summary>
        public static List<Row> Top(int n, bool byMax = false)
        {
            var all = Stats.Concat(LocationStats.Select(kv => new KeyValuePair<int, Stat>(kv.Key, kv.Value)));
            var q = byMax ? all.OrderByDescending(kv => kv.Value.MaxMs) : all.OrderByDescending(kv => kv.Value.TotalMs);
            return q.Take(n).Select(kv =>
            {
                return new Row
                {
                    Prefab = kv.Value.Name.Length > 0 ? kv.Value.Name : kv.Key.ToString(),
                    ModHint = kv.Value.ModHint,
                    Count = kv.Value.Count, Over5Ms = kv.Value.Over5Ms,
                    TotalMs = kv.Value.TotalMs, AvgMs = kv.Value.TotalMs / kv.Value.Count, MaxMs = kv.Value.MaxMs,
                    SplitSpawns = kv.Value.SplitSpawns,
                    Parts = kv.Value.Parts == null || kv.Value.SplitSpawns == 0 ? new List<(string, double)>() :
                        kv.Value.Parts.OrderByDescending(p => p.Value).Select(p => (SpawnParts.PartNames[p.Key], p.Value / kv.Value.SplitSpawns)).ToList(),
                    OtherAvgMs = kv.Value.SplitSpawns == 0 ? double.NaN :
                        (kv.Value.SplitMs - (kv.Value.Parts?.Values.Sum() ?? 0)) / kv.Value.SplitSpawns,
                };
            }).ToList();
        }

        /// <summary>"Valheim: TreeBase.Awake 0.120; Seasonality: RandomColors.Awake 0.002" (average ms per split spawn).</summary>
        public static string PartsText(Row r, int n) =>
            string.Join("; ", r.Parts.Where(p => p.avgMs >= 0.0005).Take(n).Select(p => $"{p.name} {p.avgMs:0.000}"));

        private static AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> _sceneInstances;

        /// <summary>
        /// Objects loaded right now, grouped by prefab, most common first (null if not in a world). stacked: how many
        /// sit exactly on another object of the same prefab (same spot to the centimetre): wasted copies, e.g. from
        /// vegetation placed in groups with no group radius.
        /// </summary>
        public static List<(string prefab, string mods, int count, int stacked)> Loaded(int n, out int total, out int stackedTotal)
        {
            total = stackedTotal = 0;
            var scene = ZNetScene.instance;
            if (scene == null) return null;
            try { if (_sceneInstances == null) _sceneInstances = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances"); }
            catch { return null; }
            var counts = new Dictionary<int, int>();
            var stacked = new Dictionary<int, int>();
            var spots = new HashSet<(int, int, int, int)>();
            foreach (var zdo in _sceneInstances(scene).Keys)
            {
                if (zdo == null) continue;
                int hash = zdo.GetPrefab();
                counts.TryGetValue(hash, out int c);
                counts[hash] = c + 1;
                total++;
                var p = zdo.GetPosition();
                if (!spots.Add((hash, Mathf.RoundToInt(p.x * 100f), Mathf.RoundToInt(p.y * 100f), Mathf.RoundToInt(p.z * 100f))))
                {
                    stacked.TryGetValue(hash, out int st);
                    stacked[hash] = st + 1;
                    stackedTotal++;
                }
            }
            return counts.OrderByDescending(kv => kv.Value).Take(n).Select(kv =>
            {
                var prefab = scene.GetPrefab(kv.Key);
                stacked.TryGetValue(kv.Key, out int st);
                return (prefab == null ? kv.Key.ToString() : prefab.name, ModHint(prefab), kv.Value, st);
            }).ToList();
        }

        /// <summary>Mods whose own component types are on the object (a hint: a mod's object may use only vanilla components).</summary>
        private static string ModHint(GameObject prefab)
        {
            if (prefab == null || _registry == null) return "";
            var names = new HashSet<string>();
            try
            {
                foreach (var c in prefab.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    var m = _registry.Resolve(c.GetType().Assembly, null);
                    if (m != null && m.IsPlugin) names.Add(m.Name);
                }
            }
            catch { }
            return string.Join("; ", names.OrderBy(x => x));
        }

        public const string Header = "prefab,mod_components,count,total_ms,avg_ms,max_ms,count_over_5ms,split_spawns,split_avg_ms,unity_and_other_avg_ms,parts_avg_ms\n";

        public static string Csv(int n)
        {
            var sb = new StringBuilder(Header);
            foreach (var r in Top(n))
                sb.Append(ValPerfMonitor.Csv.Q(r.Prefab)).Append(',').Append(ValPerfMonitor.Csv.Q(r.ModHint)).Append(',').Append(r.Count).Append(',')
                  .Append(ValPerfMonitor.Csv.F(r.TotalMs, "0.0")).Append(',').Append(ValPerfMonitor.Csv.F(r.AvgMs, "0.000")).Append(',')
                  .Append(ValPerfMonitor.Csv.F(r.MaxMs, "0.00")).Append(',').Append(r.Over5Ms).Append(',').Append(r.SplitSpawns).Append(',')
                  .Append(r.SplitSpawns == 0 ? "" : ValPerfMonitor.Csv.F(r.OtherAvgMs + r.Parts.Sum(x => x.avgMs), "0.000")).Append(',')
                  .Append(r.SplitSpawns == 0 ? "" : ValPerfMonitor.Csv.F(r.OtherAvgMs, "0.000")).Append(',')
                  .Append(ValPerfMonitor.Csv.Q(PartsText(r, 8))).Append('\n');
            return sb.ToString();
        }
    }
}
