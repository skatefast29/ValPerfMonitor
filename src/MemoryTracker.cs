using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Watches for memory leaks over a whole session: once a minute (never during timed tests) it counts
    /// Unity objects (game objects, textures, meshes, materials, sounds), live components per mod, and
    /// memory. A leak shows up as a count that keeps rising while the world itself (loaded objects) does not.
    /// Counting walks every Unity object, which takes a few tens of ms: that frame is never counted in results.
    /// Writes memory.csv and memory_by_mod.csv in the session folder.
    /// </summary>
    internal sealed class MemoryTracker
    {
        public sealed class Sample
        {
            public double SessionSeconds;
            public string Time, Where;
            public bool InWorld;
            public int Instances, Zdos, GameObjects, Components, Textures, Meshes, Materials, Audio;
            public int LocationObjects = -1;          // game objects inside locations and dungeon rooms: loaded prefabs, Jotunn's copies, placed ones (-1 = unknown)
            public double HeapMb, MonoUsedMb, TotalUsedMb, GfxUsedMb;
            public double LiveAfterGcMb = double.NaN; // managed memory right after the latest garbage collection
            public float WorldSeconds = -1;           // seconds since entering the world (-1 = not in one)
            public float X = float.NaN, Z = float.NaN; // player position (NaN = no player)
            public Dictionary<string, int> ByMod;
        }

        public readonly List<Sample> Samples = new List<Sample>();
        private readonly ModRegistry _registry;
        private readonly Assembly _game = typeof(ZNet).Assembly;
        private readonly Dictionary<Assembly, string> _ownerByAssembly = new Dictionary<Assembly, string>();
        private bool _headerWritten;

        public MemoryTracker(ModRegistry registry) => _registry = registry;

        /// <summary>behaviours = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour)), shared with the live-class scan.</summary>
        public Sample Take(UnityEngine.Object[] behaviours, double sessionSeconds, GameContext ctx, UnityCounters counters)
        {
            var s = new Sample
            {
                SessionSeconds = sessionSeconds,
                Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                InWorld = ctx.InWorld, Where = ctx.InWorld ? ctx.Biome : (ctx.Scene.Length > 0 ? ctx.Scene : "loading"),
                Instances = ctx.Instances, Zdos = ctx.Zdos,
                GameObjects = Count(typeof(GameObject)), Textures = Count(typeof(Texture)), Meshes = Count(typeof(Mesh)),
                Materials = Count(typeof(Material)), Audio = Count(typeof(AudioClip)),
                Components = behaviours?.Length ?? 0,
                HeapMb = GC.GetTotalMemory(false) / 1048576.0,
                MonoUsedMb = counters.Last("gc_used_mb"), TotalUsedMb = counters.Last("total_used_mb"), GfxUsedMb = counters.Last("gfx_used_mb"),
                ByMod = new Dictionary<string, int>(),
                LiveAfterGcMb = Plugin.Instance?.Alarms?.LiveAfterGcMb ?? double.NaN,
                WorldSeconds = Plugin.Instance?.WorldSeconds ?? -1,
                LocationObjects = CountLocationObjects(behaviours),
            };
            var player = Player.m_localPlayer;
            if (player != null) { var pos = player.transform.position; s.X = pos.x; s.Z = pos.z; }
            if (behaviours != null)
                foreach (var o in behaviours)
                {
                    if (o == null) continue;
                    string owner = OwnerOf(o.GetType().Assembly);
                    if (owner == null) continue;
                    s.ByMod.TryGetValue(owner, out int n);
                    s.ByMod[owner] = n + 1;
                }
            Samples.Add(s);
            return s;
        }

        private static int Count(Type t)
        {
            try { return Resources.FindObjectsOfTypeAll(t).Length; }
            catch { return -1; }
        }

        private readonly Dictionary<int, int> _objectsInLocation = new Dictionary<int, int>();
        private readonly HashSet<int> _seenLocations = new HashSet<int>();
        private bool _locationsBroken;

        /// <summary>
        /// Game objects inside locations and dungeon rooms (every Location or Room component, outermost only). Exploring
        /// raises them three ways without anything leaking: the game loads a location's prefab when you first come near
        /// one (it sits outside any scene; MWL_SwampComplex1 alone has 35,000), Jotunn keeps a working copy of each loaded
        /// modded location under _JotunnRoot, and the locations placed around you have many parts the loaded-object count
        /// doesn't see. Counted per object once (a prefab never changes), then only the components are walked.
        /// </summary>
        private int CountLocationObjects(UnityEngine.Object[] behaviours)
        {
            if (_locationsBroken || behaviours == null) return -1;
            try
            {
                _seenLocations.Clear();
                int total = 0;
                foreach (var o in behaviours)
                {
                    if (!(o is Location) && !(o is Room)) continue;
                    var c = (Component)o;
                    if (c == null) continue;
                    var t = c.transform;
                    if (InsideLocation(t.parent)) continue; // a dungeon's rooms sit inside its location
                    int id = t.GetInstanceID();
                    if (!_seenLocations.Add(id)) continue;
                    if (!_objectsInLocation.TryGetValue(id, out int n))
                        _objectsInLocation[id] = n = t.GetComponentsInChildren<Transform>(true).Length;
                    total += n;
                }
                if (_objectsInLocation.Count > 2 * _seenLocations.Count + 1000)
                    foreach (var id in _objectsInLocation.Keys.Where(k => !_seenLocations.Contains(k)).ToList())
                        _objectsInLocation.Remove(id);
                return total;
            }
            catch (Exception e)
            {
                _locationsBroken = true;
                Plugin.Instance?.Log.LogInfo($"Game objects in locations are not counted (the leak check counts them as possible leaks): {e.Message}");
                return -1;
            }
        }

        private static bool InsideLocation(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.GetComponent<Location>() != null || t.GetComponent<Room>() != null) return true;
            return false;
        }

        private string OwnerOf(Assembly asm)
        {
            if (_ownerByAssembly.TryGetValue(asm, out var name)) return name;
            if (asm == _game) name = "Valheim";
            else
            {
                var mod = _registry.ForPluginAssembly(asm);
                name = mod?.Name; // Unity's own and other library components are left out
            }
            _ownerByAssembly[asm] = name;
            return name;
        }

        public void Write(BackgroundWriter writer, string sessionFolder, Sample s)
        {
            var sb = new StringBuilder();
            if (!_headerWritten)
                sb.Append("time,session_s,in_world,where,objects_loaded,zdos,game_objects,components,textures,meshes,materials,audio_clips,managed_heap_mb,mono_used_mb,unity_total_used_mb,gfx_used_mb,live_after_gc_mb,location_objects\n");
            sb.Append(s.Time).Append(',').Append(Csv.F(s.SessionSeconds, "0")).Append(',').Append(s.InWorld ? "1" : "0").Append(',').Append(Csv.Q(s.Where)).Append(',')
              .Append(N(s.Instances)).Append(',').Append(N(s.Zdos)).Append(',').Append(N(s.GameObjects)).Append(',').Append(N(s.Components)).Append(',')
              .Append(N(s.Textures)).Append(',').Append(N(s.Meshes)).Append(',').Append(N(s.Materials)).Append(',').Append(N(s.Audio)).Append(',')
              .Append(Csv.F(s.HeapMb, "0")).Append(',').Append(Csv.F(s.MonoUsedMb, "0")).Append(',').Append(Csv.F(s.TotalUsedMb, "0")).Append(',').Append(Csv.F(s.GfxUsedMb, "0")).Append(',').Append(Csv.F(s.LiveAfterGcMb, "0")).Append(',').Append(N(s.LocationObjects)).Append('\n');
            writer.Append(Path.Combine(sessionFolder, "memory.csv"), sb.ToString());

            sb.Length = 0;
            if (!_headerWritten)
                sb.Append("time,session_s,owner,components\n");
            foreach (var kv in s.ByMod.OrderByDescending(kv => kv.Value))
                sb.Append(s.Time).Append(',').Append(Csv.F(s.SessionSeconds, "0")).Append(',').Append(Csv.Q(kv.Key)).Append(',').Append(kv.Value).Append('\n');
            writer.Append(Path.Combine(sessionFolder, "memory_by_mod.csv"), sb.ToString());
            _headerWritten = true;
        }

        private static string N(int v) => v < 0 ? "" : v.ToString(CultureInfo.InvariantCulture);

        // ---- Leak view for the panel ------------------------------------------------------------

        /// <summary>First and latest in-world samples, if there are at least two taken 5+ minutes apart.</summary>
        public bool TrendWindow(out Sample first, out Sample last)
        {
            var w = Samples.Where(x => x.InWorld).ToList();
            first = w.FirstOrDefault();
            last = w.LastOrDefault();
            return first != null && last != null && last.SessionSeconds - first.SessionSeconds >= 300;
        }

        /// <summary>Mods whose live component count grew the most between the first and latest in-world sample.</summary>
        public List<(string mod, int from, int to)> GrowingMods(int n)
        {
            if (!TrendWindow(out var a, out var b)) return new List<(string, int, int)>();
            return b.ByMod.Select(kv => (mod: kv.Key, from: a.ByMod.TryGetValue(kv.Key, out int f) ? f : 0, to: kv.Value))
                .Where(x => x.to > x.from).OrderByDescending(x => x.to - x.from).Take(n).ToList();
        }
    }
}
