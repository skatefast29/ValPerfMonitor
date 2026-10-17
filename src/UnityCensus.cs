using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// The Unity side of the memory census: every GameObject grouped by scene and by its root object, and every
    /// Material grouped by name. The managed census can't see these (they live in Unity's own memory), but a leak of
    /// hidden game objects or of material copies (a "(Instance)" made each time a script touches .material) shows up
    /// here by name. Compared with the previous census this session.
    /// </summary>
    internal sealed class UnityCensus
    {
        public const string NoScene = "(not in a scene: prefabs, assets and hidden objects)";

        public int GameObjects, Inactive, Hidden, Materials, MaterialCopies;
        public List<(string name, int count)> ByScene = new List<(string, int)>(), ByRoot = new List<(string, int)>(), ByMaterial = new List<(string, int)>();
        public List<(string name, int count)> RootGrowth, MaterialGrowth, SceneGrowth;
        public bool HasPrevious;
        private Dictionary<string, int> _roots, _materials, _scenes;
        /// <summary>All game objects per "scene: root" and all materials per name (kept for the relog test).</summary>
        public Dictionary<string, int> RootCounts = new Dictionary<string, int>(), MaterialCounts = new Dictionary<string, int>();
        private static UnityCensus _previous;

        /// <summary>Runs in slices: yields whenever <paramref name="outOfTime"/> says the frame's budget is used.</summary>
        public IEnumerable<string> Run(Func<bool> outOfTime)
        {
            var scenes = new Dictionary<string, int>();
            var roots = new Dictionary<string, int>();
            var rootNames = new Dictionary<Transform, string>();
            var gos = Resources.FindObjectsOfTypeAll<GameObject>();
            GameObjects = gos.Length;
            yield return $"game objects 0/{gos.Length:N0}";
            for (int i = 0; i < gos.Length; i++)
            {
                var go = gos[i];
                if (go == null) continue;
                var scene = go.scene;
                string sceneName = scene.IsValid() ? scene.name : NoScene;
                Add(scenes, sceneName);
                if (!go.activeInHierarchy) Inactive++;
                if (go.hideFlags != HideFlags.None) Hidden++;
                var root = go.transform.root;
                if (!rootNames.TryGetValue(root, out string rn)) rootNames[root] = rn = Clean(root.name, "(Clone)");
                Add(roots, (scene.IsValid() ? sceneName : "no scene") + ": " + rn);
                if ((i & 255) == 0 && outOfTime()) yield return $"game objects {i:N0}/{gos.Length:N0}";
            }
            gos = null; rootNames = null;
            yield return "materials";
            var mats = Resources.FindObjectsOfTypeAll<Material>();
            Materials = mats.Length;
            var byName = new Dictionary<string, int>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                string n = m.name ?? "";
                if (n.IndexOf("(Instance)", StringComparison.Ordinal) >= 0) MaterialCopies++;
                Add(byName, Clean(n, "(Instance)"));
                if ((i & 255) == 0 && outOfTime()) yield return $"materials {i:N0}/{mats.Length:N0}";
            }
            ByScene = Top(scenes, 20);
            ByRoot = Top(roots, 40);
            ByMaterial = Top(byName, 40);
            _scenes = scenes; _roots = RootCounts = roots; _materials = MaterialCounts = byName;
            if (_previous != null)
            {
                HasPrevious = true;
                SceneGrowth = Growth(_scenes, _previous._scenes);
                RootGrowth = Growth(_roots, _previous._roots);
                MaterialGrowth = Growth(_materials, _previous._materials);
                _previous._scenes = _previous._roots = _previous._materials = null;
            }
            _previous = this;
        }

        private static void Add(Dictionary<string, int> d, string key) { d.TryGetValue(key, out int n); d[key] = n + 1; }

        // "Torch(Clone)(Clone)" -> "Torch", "Wood (Instance) (Instance)" -> "Wood": copies are grouped with their original.
        private static string Clean(string name, string suffix)
        {
            if (string.IsNullOrEmpty(name)) return "(no name)";
            string s = name.Replace(suffix, "").Trim();
            return s.Length == 0 ? "(no name)" : s;
        }

        private static List<(string, int)> Top(Dictionary<string, int> d, int n) =>
            d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value)).ToList();

        private static List<(string, int)> Growth(Dictionary<string, int> now, Dictionary<string, int> before)
        {
            if (now == null || before == null) return new List<(string, int)>();
            return now.Select(kv => (kv.Key, kv.Value - (before.TryGetValue(kv.Key, out int b) ? b : 0)))
                      .Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(25).ToList();
        }

        public List<string> Plain()
        {
            var l = new List<string>();
            l.Add($"Unity objects: {GameObjects:N0} game objects ({Inactive:N0} switched off, {Hidden:N0} hidden from the scene), {Materials:N0} materials ({MaterialCopies:N0} of them runtime copies).");
            var top = ByRoot.Take(3).Select(r => $"{r.name} ({r.count:N0})").ToArray();
            if (top.Length > 0) l.Add("Most game objects under: " + string.Join("; ", top) + ".");
            var mats = ByMaterial.Take(3).Select(r => $"{r.name} ({r.count:N0})").ToArray();
            if (mats.Length > 0) l.Add("Most common materials: " + string.Join("; ", mats) + ".");
            if (HasPrevious)
            {
                var g = RootGrowth.Take(3).Select(x => $"{x.name} +{x.count:N0}").Concat(MaterialGrowth.Take(2).Select(x => $"material {x.name} +{x.count:N0}")).ToArray();
                if (g.Length > 0) l.Add("Unity objects that grew since the last census: " + string.Join("; ", g) + ".");
            }
            return l;
        }

        public void Text(StringBuilder sb)
        {
            void Line(string s = "") => sb.AppendLine(s);
            Line();
            Line($"== Unity objects: {GameObjects:N0} game objects ({Inactive:N0} switched off, {Hidden:N0} hidden), {Materials:N0} materials ({MaterialCopies:N0} runtime copies) ==");
            Line("Copies are grouped with their original ((Clone) and (Instance) left out of names). A count that grows in every");
            Line("census while the world around you stays the same is a leak: something creates them and never destroys them.");
            if (HasPrevious)
            {
                Line();
                Line("== Unity objects that grew since the previous census ==");
                foreach (var x in SceneGrowth) Line($"{"+" + x.count.ToString("N0"),12}  game objects in scene {x.name}");
                foreach (var x in RootGrowth) Line($"{"+" + x.count.ToString("N0"),12}  game objects under {x.name}");
                foreach (var x in MaterialGrowth) Line($"{"+" + x.count.ToString("N0"),12}  material {x.name}");
            }
            Line();
            Line("== Game objects by scene ==");
            foreach (var x in ByScene) Line($"{x.count,12:N0}  {x.name}");
            Line();
            Line("== Game objects by root object (scene: root) ==");
            foreach (var x in ByRoot) Line($"{x.count,12:N0}  {x.name}");
            Line();
            Line("== Materials by name ==");
            foreach (var x in ByMaterial) Line($"{x.count,12:N0}  {x.name}");
        }
    }
}
