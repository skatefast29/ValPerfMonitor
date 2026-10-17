using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// First-open stutter: the first time the inventory, the map, the build menu or a crafting station is opened, the
    /// game (and mods' patches) often builds its UI, loads icons or fills lists, and that frame hitches; later opens
    /// are cheap. Every open is timed: the opening call itself (game code plus mod patches on it) and the slowest of
    /// the frame it happened in and the two after (the UI often finishes building in the next frame).
    /// Always on: a postfix on four methods that only run when a window opens. Written to first_opens.csv.
    /// </summary>
    internal static class FirstOpens
    {
        public sealed class Open
        {
            public string Kind;
            public double SessionSeconds;
            public double CallMs;
            public float FrameMs;           // slowest of the open frame and the two after
            public bool FirstThisSession, FirstSinceLogin;
            public int FramesSeen;
        }

        public sealed class Row
        {
            public string Kind;
            public Open First;
            public List<Open> Later = new List<Open>();
            public int Count => 1 + Later.Count;
            public double LaterMedianFrameMs => Median(Later.Select(o => (double)o.FrameMs));
            public double LaterMedianCallMs => Median(Later.Select(o => o.CallMs));
        }

        private static readonly List<Open> _all = new List<Open>();
        private static readonly List<Open> _pending = new List<Open>();
        private static readonly HashSet<string> _seen = new HashSet<string>(), _seenSinceLogin = new HashSet<string>();
        private static long _callStart;
        private static int _callDepth;
        private static ManualLogSource _log;
        private static bool _header;
        public static bool Active;

        public static void Attach(string harmonyId, ManualLogSource log)
        {
            _log = log;
            var h = new Harmony(harmonyId);
            int ok = 0;
            ok += Hook(h, AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Show), new[] { typeof(Container), typeof(int) }), nameof(InventoryPost));
            ok += Hook(h, AccessTools.Method(typeof(Minimap), nameof(Minimap.SetMapMode)), nameof(MapPost), nameof(MapPre));
            ok += Hook(h, AccessTools.Method(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu)), nameof(BuildPost));
            Active = ok > 0;
            if (ok < 3) log.LogInfo($"First-open timing: {3 - ok} of the game's window methods are not as expected; those windows are not timed.");
        }

        private static int Hook(Harmony h, System.Reflection.MethodInfo m, string post, string pre = nameof(Pre))
        {
            if (m == null) return 0;
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(FirstOpens), pre) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(FirstOpens), post) { priority = Priority.Last });
                return 1;
            }
            catch (Exception e) { _log.LogInfo($"First-open timing: could not hook {m.Name}: {e.Message}"); return 0; }
        }

        private static void Pre()
        {
            if (_callDepth++ == 0) _callStart = Stopwatch.GetTimestamp();
        }

        private static double EndCall()
        {
            if (_callDepth > 0) _callDepth--;
            return (Stopwatch.GetTimestamp() - _callStart) * 1000.0 / Stopwatch.Frequency;
        }

        private static void InventoryPost(Container container)
        {
            double ms = EndCall();
            string kind = "inventory";
            try
            {
                var station = Player.m_localPlayer != null ? Player.m_localPlayer.GetCurrentCraftingStation() : null;
                if (station != null) kind = "crafting station " + Clean(station.gameObject.name);
                else if (container != null) kind = "container " + Clean(container.gameObject.name);
            }
            catch { }
            Add(kind, ms);
        }

        private static bool _mapOpening;
        private static void MapPre(Minimap __instance, Minimap.MapMode mode)
        {
            Pre();
            _mapOpening = __instance != null && mode == Minimap.MapMode.Large && __instance.m_mode != Minimap.MapMode.Large && !Game.m_noMap;
        }

        private static void MapPost()
        {
            double ms = EndCall();
            if (_mapOpening) Add("map", ms);
            _mapOpening = false;
        }

        private static void BuildPost() => Add("build menu", EndCall());

        private static string Clean(string name) => (name ?? "").Replace("(Clone)", "").Trim();

        private static void Add(string kind, double callMs)
        {
            if (Player.m_localPlayer == null) return;
            var p = Plugin.Instance;
            var o = new Open
            {
                Kind = kind, CallMs = callMs, SessionSeconds = p != null ? p.SessionSeconds : 0,
                FirstThisSession = _seen.Add(kind), FirstSinceLogin = _seenSinceLogin.Add(kind),
            };
            _pending.Add(o);
        }

        public static void OnLogin() => _seenSinceLogin.Clear();

        /// <summary>Every frame from the plugin's frame loop, with that frame's wall-clock time.</summary>
        public static void Frame(float rawMs)
        {
            if (_pending.Count == 0) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var o = _pending[i];
                if (rawMs > o.FrameMs) o.FrameMs = rawMs;
                if (++o.FramesSeen < 3) continue;
                _pending.RemoveAt(i);
                Done(o);
            }
        }

        private static void Done(Open o)
        {
            _all.Add(o);
            var p = Plugin.Instance;
            if (p?.Writer == null) return;
            p.Writer.Append(Path.Combine(p.SessionFolder, "first_opens.csv"), (_header ? "" : "time,session_s,window,frame_ms,call_ms,first_this_session,first_since_login\n") +
                $"{DateTime.Now.ToString("HH:mm:ss", Csv.Inv)},{Csv.F(o.SessionSeconds, "0.0")},{Csv.Q(o.Kind)},{Csv.F(o.FrameMs, "0.0")},{Csv.F(o.CallMs, "0.0")},{(o.FirstThisSession ? 1 : 0)},{(o.FirstSinceLogin ? 1 : 0)}\n");
            _header = true;
            if (o.FirstThisSession && o.FrameMs >= 50)
                p.Log.LogInfo($"First open of the {o.Kind}: {o.FrameMs:0} ms frame (the opening code itself {o.CallMs:0.0} ms). Later opens are compared in perf/first_opens.");
        }

        /// <summary>Per window: the first open this session and the later ones.</summary>
        public static List<Row> Rows()
        {
            var rows = new List<Row>();
            foreach (var g in _all.GroupBy(o => o.Kind))
            {
                var list = g.ToList();
                var first = list.FirstOrDefault(o => o.FirstThisSession) ?? list[0];
                rows.Add(new Row { Kind = g.Key, First = first, Later = list.Where(o => o != first).ToList() });
            }
            return rows.OrderByDescending(r => r.First.FrameMs - (double.IsNaN(r.LaterMedianFrameMs) ? 0 : r.LaterMedianFrameMs)).ToList();
        }

        /// <summary>Opens that were the first since a login, other than the very first of the session (stutter that comes back after each relog).</summary>
        public static List<Open> FirstAfterRelog() => _all.Where(o => o.FirstSinceLogin && !o.FirstThisSession).ToList();

        internal static double Median(IEnumerable<double> values)
        {
            var a = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
            if (a.Length == 0) return double.NaN;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }
    }
}
