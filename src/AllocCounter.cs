using System;
using System.Reflection;
using BepInEx.Logging;
using Unity.Profiling;

namespace ValPerfMonitor
{
    /// <summary>
    /// Picks the best way this runtime offers to read "bytes allocated so far" on the main thread,
    /// and proves it works by allocating a known amount before trusting it.
    /// Unity's Mono uses the Boehm GC, where the precise per-thread counter may not be implemented,
    /// so fallbacks are tried in order and the chosen one is written into every allocation report.
    /// </summary>
    internal static class AllocCounter
    {
        public static string Method { get; private set; } = "unavailable";
        public static bool Exact { get; private set; }
        public static bool Available { get; private set; }

        private static ProfilerRecorder _recorder;

        public static void Init(ManualLogSource log)
        {
            // 1. GC.GetAllocatedBytesForCurrentThread: exact, per thread. Not in the net472 reference
            //    assemblies we compile against, so bound by reflection.
            try
            {
                var mi = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                {
                    var f = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), mi);
                    if (Verify(f, requireSmall: true))
                    {
                        Use(f, "GC.GetAllocatedBytesForCurrentThread (exact)", true, log);
                        return;
                    }
                }
            }
            catch (Exception e) { log.LogDebug($"Per-thread allocation counter unusable: {e.Message}"); }

            // 2. Unity's "GC Allocated In Frame" counter, if it is live in this (release) build.
            try
            {
                _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
                if (_recorder.Valid)
                {
                    Func<long> f = () => _recorder.CurrentValue;
                    if (Verify(f, requireSmall: true))
                    {
                        Use(f, "Unity 'GC Allocated In Frame' counter (exact, main thread + jobs)", true, log);
                        return;
                    }
                }
                _recorder.Dispose();
            }
            catch (Exception e) { log.LogDebug($"Unity allocation counter unusable: {e.Message}"); }

            // 3. Managed heap size. Moves in heap-block steps and drops when the GC runs, so single calls
            //    are coarse, but totals over thousands of calls still rank mods by allocation volume.
            try
            {
                Func<long> f = () => GC.GetTotalMemory(false);
                if (Verify(f, requireSmall: false))
                {
                    Use(f, "GC.GetTotalMemory (approximate: heap-block granularity)", false, log);
                    return;
                }
            }
            catch { }

            log.LogWarning("No working allocation counter found; Allocations mode is unavailable on this runtime.");
        }

        private static void Use(Func<long> f, string method, bool exact, ManualLogSource log)
        {
            Probe.ReadAllocated = f;
            Method = method;
            Exact = exact;
            Available = true;
            log.LogInfo($"Allocation counter: {method}");
        }

        private static object _sink;

        private static bool Verify(Func<long> read, bool requireSmall)
        {
            // Large allocation must be seen.
            long a = read();
            var big = new byte[256 * 1024];
            long b = read();
            _sink = big;
            if (b - a < 200 * 1024)
                return false;
            if (!requireSmall)
                return true;

            // Many small allocations must be seen too (this is what mod code mostly does).
            long c = read();
            var arr = new object[2000];
            for (int i = 0; i < arr.Length; i++) arr[i] = new object();
            long d = read();
            _sink = arr;
            _sink = null;
            return d - c >= 2000 * 8;
        }
    }
}
