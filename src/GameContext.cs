using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValPerfMonitor
{
    /// <summary>
    /// What was going on in the world when numbers were recorded, so a cost can be tied to a situation
    /// ("mod X is cheap in the wild but expensive at my base"). Sampled on log intervals, not per frame.
    /// </summary>
    internal sealed class GameContext
    {
        public string Scene = "", Biome = "", Weather = "";
        public bool InWorld, IsServer;
        public float X, Z, DayFraction;
        public int Players, Zdos = -1, Instances = -1, Characters = -1, PiecesNearby = -1, WearNTear = -1;

        public const string CsvHeader = "scene,in_world,pos_x,pos_z,biome,weather,day_fraction,players,is_server,zdos,instances,characters,pieces_within_50m,building_pieces_loaded";

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> Instances_ =
            Safe(() => AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances"));
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> Objects_ =
            Safe(() => AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID"));
        private static readonly AccessTools.FieldRef<List<WearNTear>> AllWnt_ =
            Safe(() => AccessTools.StaticFieldRefAccess<List<WearNTear>>(AccessTools.Field(typeof(WearNTear), "s_allInstances")));

        private readonly List<Piece> _pieces = new List<Piece>();

        /// <summary>Objects currently loaded around the player (ZNetScene instances); cheap enough for every frame. -1 if unknown.</summary>
        public static int LoadedObjects()
        {
            var scene = ZNetScene.instance;
            if (scene == null || Instances_ == null) return -1;
            try { return Instances_(scene).Count; } catch { return -1; }
        }

        /// <summary>The loaded copies of one prefab (by its name's hash), as game objects.</summary>
        public static List<GameObject> LoadedOfPrefab(int hash)
        {
            var list = new List<GameObject>();
            var scene = ZNetScene.instance;
            if (scene == null || Instances_ == null) return list;
            foreach (var kv in Instances_(scene))
                if (kv.Key != null && kv.Value != null && kv.Key.GetPrefab() == hash)
                    list.Add(kv.Value.gameObject);
            return list;
        }

        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        /// <summary>Full sample: includes a piece query around the player (a few hundred µs at big bases).</summary>
        public void Sample(bool full)
        {
            try { Scene = SceneManager.GetActiveScene().name; } catch { Scene = ""; }
            var player = Player.m_localPlayer;
            InWorld = player != null;
            try
            {
                var znet = ZNet.instance;
                IsServer = znet != null && znet.IsServer();
                Players = znet != null ? znet.GetNrOfPlayers() : 0;
            }
            catch { }

            if (player != null)
            {
                var p = player.transform.position;
                X = p.x; Z = p.z;
                try { Biome = player.GetCurrentBiome().ToString(); } catch { Biome = ""; }
            }
            else { Biome = ""; X = Z = 0; }

            try
            {
                var env = EnvMan.instance;
                Weather = env != null ? env.GetCurrentEnvironment()?.m_name ?? "" : "";
                DayFraction = env != null ? env.GetDayFraction() : 0;
            }
            catch { }

            try { Instances = ZNetScene.instance != null && Instances_ != null ? Instances_(ZNetScene.instance).Count : -1; } catch { Instances = -1; }
            try { Zdos = ZDOMan.instance != null && Objects_ != null ? Objects_(ZDOMan.instance).Count : -1; } catch { Zdos = -1; }
            try { Characters = Character.GetAllCharacters().Count; } catch { Characters = -1; }
            try { WearNTear = AllWnt_ != null ? AllWnt_().Count : -1; } catch { WearNTear = -1; }

            if (full && player != null)
            {
                try
                {
                    _pieces.Clear();
                    Piece.GetAllPiecesInRadius(player.transform.position, 50f, _pieces);
                    PiecesNearby = _pieces.Count;
                    _pieces.Clear();
                }
                catch { PiecesNearby = -1; }
            }
            else if (player == null) PiecesNearby = -1;

            Short = InWorld
                ? $"{Biome}, {Instances} objects loaded{(PiecesNearby >= 0 ? $", {PiecesNearby} pieces within 50 m" : "")}{(Players > 1 ? $", {Players} players" : "")}"
                : (Scene.Length > 0 ? Scene : "loading");
        }

        public string ToCsv()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join(",",
                Csv.Q(Scene), InWorld ? "1" : "0",
                InWorld ? X.ToString("0", inv) : "", InWorld ? Z.ToString("0", inv) : "",
                Csv.Q(Biome), Csv.Q(Weather), InWorld ? DayFraction.ToString("0.00", inv) : "",
                Players.ToString(inv), IsServer ? "1" : "0",
                N(Zdos), N(Instances), N(Characters), N(PiecesNearby), N(WearNTear));
        }

        /// <summary>One-line description, rebuilt on each sample so spike records don't allocate.</summary>
        public string Short { get; private set; } = "";

        private static string N(int v) => v < 0 ? "" : v.ToString(CultureInfo.InvariantCulture);
    }
}
