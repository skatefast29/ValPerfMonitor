using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// Times the game's own big loading steps (main menu setup, item database, world file, locations,
    /// player spawn...) so startup and world load times can be broken down. Each step's time includes every
    /// mod patch on that method; steps that run inside another step are subtracted from it (self time), so
    /// nothing is counted twice. These methods only run while loading, so the hooks cost nothing during
    /// play, and they stay attached in every capture mode (a separate Harmony instance from the probes).
    /// Only vanilla methods are hooked here: hooking mods' own startup patches is what crashed the game.
    /// </summary>
    internal static class LoadSteps
    {
        private static readonly (string type, string method, string label)[] Targets =
        {
            ("FejdStartup", "Awake", "Main menu setup (FejdStartup.Awake)"),
            ("FejdStartup", "Start", "Main menu start (FejdStartup.Start)"),
            ("FejdStartup", "SetupObjectDB", "Main menu item database (FejdStartup.SetupObjectDB)"),
            ("ObjectDB", "Awake", "Item database (ObjectDB.Awake)"),
            ("ObjectDB", "CopyOtherDB", "Item database copy (ObjectDB.CopyOtherDB)"),
            ("ZNetScene", "Awake", "Prefab registry (ZNetScene.Awake)"),
            ("ZNet", "Awake", "Network and world setup (ZNet.Awake)"),
            ("ZNet", "Start", "Network start (ZNet.Start)"),
            ("ZNet", "LoadWorld", "World file loading (ZNet.LoadWorld)"),
            // Inside ZNet.Awake / ZNet.Start: world generation setup (world-gen mods replace parts of it).
            ("WorldGenerator", "Initialize", "World generator setup (WorldGenerator.Initialize)"),
            ("WorldGenerator", "Pregenerate", "World generator precalculation (WorldGenerator.Pregenerate)"),
            ("AltBiomeWorldData", "VerifyBiomeData", "Alternative biome check (AltBiomeWorldData.VerifyBiomeData)"),
            ("ZDOMan", "Load", "World objects loading (ZDOMan.Load)"),
            ("ZoneSystem", "Awake", "Zone system setup (ZoneSystem.Awake)"),
            ("ZoneSystem", "Start", "Zone system start (ZoneSystem.Start)"),
            ("ZoneSystem", "SetupLocations", "Location list setup (ZoneSystem.SetupLocations)"),
            ("ZoneSystem", "GenerateLocationsIfNeeded", "Location generation (ZoneSystem.GenerateLocationsIfNeeded)"),
            ("DungeonDB", "Awake", "Dungeon database (DungeonDB.Awake)"),
            ("DungeonDB", "Start", "Dungeon database start (DungeonDB.Start)"),
            ("ClutterSystem", "Awake", "Grass and clutter setup (ClutterSystem.Awake)"),
            ("EnvMan", "Awake", "Weather setup (EnvMan.Awake)"),
            ("Game", "Awake", "Game setup (Game.Awake)"),
            ("Game", "Start", "Game start (Game.Start)"),
            ("Minimap", "Start", "Map setup (Minimap.Start)"),
            ("Minimap", "LoadMapData", "Map data loading (Minimap.LoadMapData)"),
            ("Game", "SpawnPlayer", "Player spawn (Game.SpawnPlayer)"),
            ("Player", "OnSpawned", "Player spawned (Player.OnSpawned)"),
            ("ZNet", "SaveWorld", "World save (ZNet.SaveWorld)"),
            ("ZNet", "Shutdown", "Network shutdown (ZNet.Shutdown)"),
            ("ZNetScene", "Shutdown", "Objects shutdown (ZNetScene.Shutdown)"),
            // Called by many mods (every bundled copy of ItemManager / PieceManager ...): count and cost of the repeats.
            ("ObjectDB", "UpdateRegisters", "Item index rebuild (ObjectDB.UpdateRegisters)"),
            ("Localization", "SetupLanguage", "Translations load (Localization.SetupLanguage)"),
        };

        public struct Event { public string Label; public long Start, End; public double SelfMs; }

        private static readonly Dictionary<MethodBase, string> Labels = new Dictionary<MethodBase, string>();
        private static readonly List<Event> Events = new List<Event>();
        private static readonly long[] StackStart = new long[64];
        private static readonly long[] StackChild = new long[64];
        private static readonly MethodBase[] StackMethod = new MethodBase[64];
        // Inclusive time of the steps that finished at each depth, so LoadPatches can take nested steps out
        // of a mod patch's time.
        internal static readonly long[] DoneAtDepth = new long[65];
        private static int _depth;
        private static int _mainThread;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        public static void Attach(string harmonyId, ManualLogSource log)
        {
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            var harmony = new Harmony(harmonyId);
            int ok = 0;
            foreach (var (typeName, name, label) in Targets)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) { log.LogDebug($"Load step type not found: {typeName}"); continue; }
                var m = AccessTools.GetDeclaredMethods(type).FirstOrDefault(x => x.Name == name && !x.IsGenericMethod && !x.IsAbstract);
                if (m == null) { log.LogDebug($"Load step not found: {type.Name}.{name}"); continue; }
                try
                {
                    Labels[m] = label;
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(LoadSteps), nameof(Prefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(LoadSteps), nameof(Finalizer)) { priority = Priority.Last });
                    ok++;
                }
                catch (Exception e) { log.LogDebug($"Load step {type.Name}.{name} not timed: {e.Message}"); }
            }
            if (ok < Targets.Length) log.LogInfo($"Timing {ok} of {Targets.Length} loading steps (the others have changed in this game version).");
        }

        /// <summary>The hooked steps and their labels (LoadPatches times the mod patches on them).</summary>
        internal static IEnumerable<KeyValuePair<MethodBase, string>> Steps => Labels;

        internal static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThread;
        internal static int Depth => _depth;

        /// <summary>Label of the innermost step running now, or null outside any step.</summary>
        internal static string CurrentLabel
        {
            get
            {
                if (_depth <= 0) return null;
                var m = StackMethod[_depth - 1];
                return m != null && Labels.TryGetValue(m, out var l) ? l : m?.Name;
            }
        }

        private static void Prefix(MethodBase __originalMethod, out bool __state)
        {
            __state = Thread.CurrentThread.ManagedThreadId == _mainThread && _depth < StackStart.Length;
            if (!__state) return;
            StackChild[_depth] = 0;
            StackMethod[_depth] = __originalMethod;
            StackStart[_depth++] = Stopwatch.GetTimestamp();
        }

        private static void Finalizer(MethodBase __originalMethod, bool __state)
        {
            if (!__state) return;
            long end = Stopwatch.GetTimestamp();
            int d = --_depth;
            long incl = end - StackStart[d];
            if (d > 0) StackChild[d - 1] += incl;
            DoneAtDepth[d] += incl;
            StackMethod[d] = null;
            Labels.TryGetValue(__originalMethod, out var label);
            if (Events.Count < 10000)
                Events.Add(new Event { Label = label ?? __originalMethod.Name, Start = StackStart[d], End = end, SelfMs = (incl - StackChild[d]) * MsPerTick });
        }

        /// <summary>Record time spent outside these methods but worth showing (e.g. this monitor's own setup).</summary>
        public static void Add(string label, long start, long end)
        {
            Events.Add(new Event { Label = label, Start = start, End = end, SelfMs = (end - start) * MsPerTick });
        }

        /// <summary>Self time per step for steps that started in [from, to), slowest first.</summary>
        public static List<(string label, int calls, double seconds)> Summarize(long from, long to) =>
            Events.Where(e => e.Start >= from && e.Start < to)
                  .GroupBy(e => e.Label)
                  .Select(g => (label: g.Key, calls: g.Count(), seconds: g.Sum(e => e.SelfMs) / 1000))
                  .OrderByDescending(x => x.seconds).ToList();
    }
}
