using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        public int GameObjects, Inactive, Hidden, Materials, MaterialCopies, UiObjects;
        public List<(string name, int count)> ByScene = new List<(string, int)>(), ByRoot = new List<(string, int)>(), ByMaterial = new List<(string, int)>(), ByScreen = new List<(string, int)>();
        public List<(string name, int count)> RootGrowth, MaterialGrowth, SceneGrowth, ScreenGrowth;
        public bool HasPrevious;
        private Dictionary<string, int> _roots, _materials, _scenes, _screens;
        /// <summary>All game objects per "scene: root" and all materials per name (kept for the relog test).</summary>
        public Dictionary<string, int> RootCounts = new Dictionary<string, int>(), MaterialCounts = new Dictionary<string, int>();
        private static UnityCensus _previous;

        /// <summary>
        /// Runs in slices: yields whenever <paramref name="outOfTime"/> says the frame's budget is used.
        /// <paramref name="ownerOf"/> names the mod (or "Valheim (game)", "Unity engine") an assembly belongs to.
        /// </summary>
        public IEnumerable<string> Run(Func<bool> outOfTime, Func<Assembly, string> ownerOf)
        {
            var scenes = new Dictionary<string, int>();
            var roots = new Dictionary<string, int>();
            var screens = new Dictionary<string, int>();
            var rootNames = new Dictionary<Transform, string>();
            _modOf = new Dictionary<Transform, string>();
            _ownerOf = ownerOf;
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
                var tr = go.transform;
                var root = tr.root;
                if (!rootNames.TryGetValue(root, out string rn)) rootNames[root] = rn = Clean(root.name, "(Clone)");
                Add(roots, (scene.IsValid() ? sceneName : "no scene") + ": " + rn);
                // Menus: the census of managed memory only says "Unity engine" for their texts and images (2026-10-26:
                // +174,000 of them between two censuses, with no hint which menu). Named by the mod or screen they belong to.
                if (tr is RectTransform && ownerOf != null)
                {
                    UiObjects++;
                    Add(screens, (scene.IsValid() ? "" : "no scene: ") + ScreenOf(tr));
                }
                if ((i & 255) == 0 && outOfTime()) yield return $"game objects {i:N0}/{gos.Length:N0}";
            }
            gos = null; rootNames = null; _modOf = null; _ownerOf = null;
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
            ByScreen = Top(screens, 30);
            _scenes = scenes; _roots = RootCounts = roots; _materials = MaterialCounts = byName; _screens = screens;
            if (_previous != null)
            {
                HasPrevious = true;
                SceneGrowth = Growth(_scenes, _previous._scenes);
                RootGrowth = Growth(_roots, _previous._roots);
                MaterialGrowth = Growth(_materials, _previous._materials);
                ScreenGrowth = Growth(_screens, _previous._screens);
                _previous._scenes = _previous._roots = _previous._materials = _previous._screens = null;
            }
            _previous = this;
        }

        private static void Add(Dictionary<string, int> d, string key) { d.TryGetValue(key, out int n); d[key] = n + 1; }

        private Dictionary<Transform, string> _modOf;
        private Func<Assembly, string> _ownerOf;
        private readonly List<MonoBehaviour> _behaviours = new List<MonoBehaviour>();

        /// <summary>
        /// The menu a UI object belongs to: the nearest object above it (or itself) with a mod's script on it, as
        /// "VanityPanel (VanityPanelController, AzuExtendedPlayerInventory)"; for the game's own menus, the first five
        /// names of its path ("_GameMain/LoadingGUI/PixelFix/IngameGui/Inventory_screen").
        /// </summary>
        private string ScreenOf(Transform t) => ModOf(t) ?? PathOf(t, 5);

        private string ModOf(Transform t)
        {
            if (t == null) return null;
            if (_modOf.TryGetValue(t, out var s)) return s;
            s = OwnMod(t) ?? ModOf(t.parent);
            _modOf[t] = s;
            return s;
        }

        private string OwnMod(Transform t)
        {
            _behaviours.Clear();
            try { t.GetComponents(_behaviours); } catch { return null; }
            foreach (var b in _behaviours)
            {
                if (b == null) continue;
                var type = b.GetType();
                string owner = _ownerOf(type.Assembly);
                if (owner == null || owner == "Unity engine" || owner == "Valheim (game)" || owner == ".NET runtime" || owner == MemoryCensus.SelfName) continue;
                return $"{Clean(t.name, "(Clone)")} ({type.Name}, {owner})";
            }
            return null;
        }

        private static string PathOf(Transform t, int depth)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Add(Clean(t.name, "(Clone)"));
            names.Reverse();
            return string.Join("/", names.Take(depth).ToArray());
        }

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
                var ui = (ScreenGrowth ?? new List<(string, int)>()).Where(x => x.count >= 500).Take(3).Select(x => $"{x.name} +{x.count:N0}").ToArray();
                if (ui.Length > 0) l.Add("Menu (UI) objects that grew, by the mod or screen they belong to: " + string.Join("; ", ui) + ". A menu that grows every time it opens is a leak.");
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
                foreach (var x in ScreenGrowth ?? new List<(string, int)>()) Line($"{"+" + x.count.ToString("N0"),12}  menu (UI) objects in {x.name}");
            }
            Line();
            Line($"== Menu (UI) objects by the mod or screen they belong to ({UiObjects:N0} in all) ==");
            Line("Named by the nearest object above them with a mod's script on it; the game's own menus by the first five names of their path.");
            foreach (var x in ByScreen) Line($"{x.count,12:N0}  {x.name}");
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
