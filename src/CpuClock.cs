using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace ValPerfMonitor
{
    /// <summary>
    /// CPU time actually used by the main thread, from Windows' per-thread cycle counter. Unlike frame time it
    /// leaves out waiting (for the graphics card, the frame limiter or vsync), so a CPU saving shows even when
    /// the game is held back by the GPU. Cycles are turned into milliseconds with a short calibration at startup.
    /// </summary>
    internal static class CpuClock
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        public static bool Available { get; private set; }
        private static double _cyclesPerMs;

        /// <summary>Main thread only.</summary>
        public static void Init(ManualLogSource log)
        {
            try
            {
                double best = 0;
                for (int i = 0; i < 3; i++)
                {
                    ulong c0 = Read();
                    long t0 = Stopwatch.GetTimestamp(), end = t0 + Stopwatch.Frequency / 100; // 10 ms of spinning
                    long t;
                    do t = Stopwatch.GetTimestamp(); while (t < end);
                    ulong c1 = Read();
                    double ms = (t - t0) * 1000.0 / Stopwatch.Frequency;
                    // Being switched out only lowers the ratio, so the highest of three is the truest.
                    best = Math.Max(best, (c1 - c0) / ms);
                }
                _cyclesPerMs = best;
                Available = best > 1000;
                log.LogInfo(Available ? $"Main-thread CPU clock: {best / 1000:0} MHz cycle counter." : "Main-thread CPU clock unavailable.");
            }
            catch (Exception e) { log.LogInfo($"Main-thread CPU clock unavailable: {e.Message}"); }
        }

        public static ulong Read()
        {
            QueryThreadCycleTime(GetCurrentThread(), out ulong c);
            return c;
        }

        public static double ToMs(ulong cycles) => Available ? cycles / _cyclesPerMs : double.NaN;
    }
}
