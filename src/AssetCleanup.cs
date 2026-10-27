using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValPerfMonitor
{
    /// <summary>
    /// The game's clean-up of unused assets (Game.CollectResources -> Resources.UnloadUnusedAssets): after loading a
    /// world, every 20 min after a respawn, sleep or a quiet moment, hourly, and on leaving a world. Unity does the work
    /// on the main thread in the frames after the call, and with a big world it freezes the game or server: 0.5 s every
    /// hour on the owner's server, 1 s right after it loads the world (2026-10-27). It also frees materials
    /// nothing uses any more, which the leak check must know.
    ///
    /// A transpiler hands the call's AsyncOperation to Track (the game drops it), so the clean-up is timed until Unity
    /// says it is done. If the method is not as analysed, only the prefix runs and the next 5 s are watched instead.
    /// Changes nothing in what the game does.
    /// </summary>
    internal static class AssetCleanup
    {
        public sealed class Run
        {
            public string Reason;
            public int AskFrame, DoneFrame = int.MaxValue;
            public long AskTick, DoneTick = long.MaxValue; // Stopwatch ticks, for callers that cut frames by the clock
            public float AskedAt, DoneAt = -1;
            public double DoneSession = -1;
            public float AskFrameMs, LongestMs, AfterMs;
            public int Frames;
            public bool Timed; // Unity said when it was done (else the next 5 s were watched)
            public long HeapBefore, HeapAfter, UnityBefore, UnityAfter;
            public int MaterialsBefore = -1, MaterialsAfter = -1, TexturesBefore = -1, TexturesAfter = -1, MeshesBefore = -1, MeshesAfter = -1;
            public AsyncOperation Op;
        }

        public static Run Current, Last;
        /// <summary>Session seconds at which each clean-up this session was done.</summary>
        public static readonly List<double> DoneAtSession = new List<double>();
        /// <summary>Count materials, textures and meshes before and after (the game client; a few ms each time).</summary>
        public static bool CountUnityObjects;
        public static Action<Run> Finished;

        private const float WatchSeconds = 5f, GiveUpSeconds = 60f;
        private static ManualLogSource _log;
        private static bool _transpiled;

        public static void Attach(string harmonyId, ManualLogSource log)
        {
            _log = log;
            var m = AccessTools.Method(typeof(Game), "CollectResources", new[] { typeof(bool) });
            if (m == null) { log.LogInfo("Game.CollectResources is not there: the game's asset clean-up is not timed."); return; }
            try
            {
                new Harmony(harmonyId).Patch(m, prefix: new HarmonyMethod(typeof(AssetCleanup), nameof(Prefix)),
                                                transpiler: new HarmonyMethod(typeof(AssetCleanup), nameof(Transpiler)));
                if (!_transpiled)
                    log.LogInfo("Game.CollectResources is not as expected: the asset clean-up is timed by the slowest frame in the 5 s after it instead of until Unity is done.");
            }
            catch (Exception e) { log.LogInfo($"The game's asset clean-up is not timed: {e.Message}"); }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var unload = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets));
            var track = AccessTools.Method(typeof(AssetCleanup), nameof(Track));
            var list = instructions.ToList();
            int found = 0;
            for (int i = 0; i + 1 < list.Count; i++)
                if (list[i].Calls(unload) && list[i + 1].opcode == OpCodes.Pop) found++;
            if (found != 1 || unload == null) return list; // not as analysed: leave the method alone
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (!list[i].Calls(unload) || list[i + 1].opcode != OpCodes.Pop) continue;
                // The pop that drops the AsyncOperation becomes a call that takes it (keeping the pop's labels and blocks).
                list[i + 1].opcode = OpCodes.Call;
                list[i + 1].operand = track;
                break;
            }
            _transpiled = true;
            return list;
        }

        private static void Prefix()
        {
            try
            {
                var r = new Run
                {
                    Reason = Reason(), AskFrame = Time.frameCount, AskTick = Stopwatch.GetTimestamp(), AskedAt = Time.realtimeSinceStartup,
                    HeapBefore = GC.GetTotalMemory(false), UnityBefore = Profiler.GetTotalAllocatedMemoryLong(),
                };
                if (CountUnityObjects)
                {
                    r.MaterialsBefore = Count(typeof(Material)); r.TexturesBefore = Count(typeof(Texture)); r.MeshesBefore = Count(typeof(Mesh));
                    Plugin.Instance?.MarkHeavy(); // the counting is the monitor's own work in this frame
                }
                if (Current != null) Finish(Current); // asked again before the last one was done
                Current = r;
                Events.Mark("asset cleanup", r.Reason);
            }
            catch (Exception e) { _log?.LogDebug($"Asset clean-up: {e.Message}"); }
        }

        public static void Track(AsyncOperation op)
        {
            if (Current != null && Current.AskFrame == Time.frameCount) Current.Op = op;
        }

        /// <summary>Who asked for it, from the calling method (rare: a stack trace is fine).</summary>
        private static string Reason()
        {
            try
            {
                var frames = new StackTrace(2, false).GetFrames() ?? new StackFrame[0];
                foreach (var f in frames.Take(8))
                {
                    var m = f.GetMethod();
                    string n = m?.Name ?? "", t = m?.DeclaringType?.Name ?? "";
                    if (n.Contains("CollectResourcesCheckPeriodic")) return "hourly";
                    if (n.Contains("CollectResourcesCheck")) return "20 min check (respawn, sleep or a quiet moment)";
                    if (n.Contains("LoadWorld") || n.Contains("LoadOldWorld")) return "after loading the world";
                    if (t == "ZDOMan") return "leaving the world";
                    if (t == "Terminal") return "console command";
                    if (t.Contains("HourlyAssetUnload")) return "hourly, held for an empty server (ValheimCommunityPatch)";
                }
            }
            catch { }
            return "asked by the game";
        }

        private static int Count(Type t)
        {
            try { return Resources.FindObjectsOfTypeAll(t).Length; } catch { return -1; }
        }

        /// <summary>
        /// Once per frame with the length of a frame that ended (<paramref name="frame"/> = its Time.frameCount).
        /// Returns true when it did heavy work of its own (counting objects after the clean-up).
        /// With <paramref name="fromTick"/>/<paramref name="toTick"/> (the server, which cuts frames at its own Update),
        /// the frame is the stretch between two Stopwatch ticks: the game asks in a scene's Awake, before that Update.
        /// </summary>
        public static bool Frame(float ms, int frame, long fromTick = 0, long toTick = 0)
        {
            var r = Current;
            if (r == null) return false;
            bool byTick = toTick > 0;
            if (byTick ? toTick < r.AskTick : frame < r.AskFrame) return false;
            if (byTick ? r.AskTick > fromTick : frame == r.AskFrame) { r.AskFrameMs = ms; return false; }
            r.Frames++;
            r.AfterMs += ms;
            if (ms > r.LongestMs) r.LongestMs = ms;
            float now = Time.realtimeSinceStartup;
            bool done;
            try { done = r.Op != null ? r.Op.isDone : now - r.AskedAt >= WatchSeconds; }
            catch { done = true; }
            if (!done && now - r.AskedAt < GiveUpSeconds) return false;
            r.Timed = r.Op != null && done;
            r.DoneFrame = frame;
            if (byTick) r.DoneTick = toTick;
            Finish(r);
            return CountUnityObjects;
        }

        private static void Finish(Run r)
        {
            if (Current == r) Current = null;
            r.Op = null;
            r.DoneAt = Time.realtimeSinceStartup;
            if (r.DoneFrame == int.MaxValue) r.DoneFrame = Time.frameCount;
            if (r.DoneTick == long.MaxValue) r.DoneTick = Stopwatch.GetTimestamp();
            r.DoneSession = Plugin.Instance != null ? Plugin.Instance.SessionSeconds : r.DoneAt;
            try
            {
                r.HeapAfter = GC.GetTotalMemory(false);
                r.UnityAfter = Profiler.GetTotalAllocatedMemoryLong();
                if (CountUnityObjects) { r.MaterialsAfter = Count(typeof(Material)); r.TexturesAfter = Count(typeof(Texture)); r.MeshesAfter = Count(typeof(Mesh)); }
            }
            catch { }
            Last = r;
            DoneAtSession.Add(r.DoneSession);
            _log?.LogInfo(Describe(r));
            try { Finished?.Invoke(r); } catch (Exception e) { _log?.LogDebug($"Asset clean-up: {e.Message}"); }
        }

        /// <summary>"yes" for a frame the clean-up ran in, "asked" for the frame the game asked for it in, else "".</summary>
        public static string Label(int frame)
        {
            foreach (var r in new[] { Current, Last })
            {
                if (r == null) continue;
                if (frame == r.AskFrame) return "asked";
                if (frame > r.AskFrame && frame <= r.DoneFrame) return "yes";
            }
            return "";
        }

        /// <summary>Same, for a frame that is the stretch between two Stopwatch ticks (see Frame).</summary>
        public static string Label(long fromTick, long toTick)
        {
            foreach (var r in new[] { Current, Last })
            {
                if (r == null) continue;
                if (r.AskTick > fromTick && r.AskTick <= toTick) return "asked";
                if (r.AskTick <= fromTick && fromTick < r.DoneTick) return "yes";
            }
            return "";
        }

        public static string Describe(Run r)
        {
            string time = r.Timed
                ? $"Unity was done after {r.Frames} frame{(r.Frames == 1 ? "" : "s")} ({r.AfterMs / 1000:0.0} s); the slowest froze the game for {r.LongestMs:0} ms"
                : $"slowest frame in the {WatchSeconds:0} s after it {r.LongestMs:0} ms";
            string freed = $"managed memory {Mb(r.HeapBefore)} -> {Mb(r.HeapAfter)} MB" +
                           (r.UnityBefore > 0 && r.UnityAfter > 0 ? $", Unity's memory {Mb(r.UnityBefore)} -> {Mb(r.UnityAfter)} MB" : "");
            if (r.MaterialsBefore >= 0 && r.MaterialsAfter >= 0)
                freed += $"; materials {r.MaterialsBefore:N0} -> {r.MaterialsAfter:N0}, textures {r.TexturesBefore:N0} -> {r.TexturesAfter:N0}, meshes {r.MeshesBefore:N0} -> {r.MeshesAfter:N0}";
            return $"Asset clean-up ({r.Reason}; the game's Resources.UnloadUnusedAssets): {time}; the frame that asked for it took {r.AskFrameMs:0} ms. {freed}.";
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0", Csv.Inv);
    }
}
