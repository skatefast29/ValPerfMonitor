using System;
using System.Collections.Generic;
using System.Linq;

namespace ValPerfMonitor
{
    /// <summary>
    /// How far to trust a garbage (allocation) number. On this runtime the counter is the size of the whole
    /// managed heap, which other threads also grow. Whatever method is running on the main thread when another
    /// thread allocates gets charged for it, so a method picks up false garbage in proportion to how long it
    /// runs. The check: false garbage for a method is at most (its own time per frame, from the latest Timing
    /// capture) x (the whole game's allocation rate per ms, measured during this capture). A method charged
    /// well above that allocates for real; one charged near it may be noise.
    /// </summary>
    internal static class GarbageConfidence
    {
        public const string Exact = "exact", Real = "likely real", Noise = "could be noise", Unchecked = "unchecked (no Timing capture yet)", Measured = "measured";
        private const double RealRatio = 3.0;

        private static double[] _msPerFrame = Array.Empty<double>();
        public static int TimingCapture { get; private set; }

        private static long _prevHeap = -1;
        private static double _bytes, _ms;

        /// <summary>The whole game's allocation rate during the current Allocations capture, KB per ms.</summary>
        public static double KbPerMs => _ms > 0 ? _bytes / 1024.0 / _ms : double.NaN;

        /// <summary>End of a Timing or Trace capture: remember each method's own time per frame.</summary>
        public static void TakeTiming(Capture c)
        {
            if (c.Mode != CaptureMode.Timing && c.Mode != CaptureMode.Trace) return;
            long frames = c.Stats.TotFrames;
            if (frames == 0) return;
            var a = new double[Probe.SourceCount];
            for (int i = 0; i < a.Length; i++)
                a[i] = Math.Max(0, Probe.SourceSelf[i]) * c.Stats.ValuePerRaw / frames;
            _msPerFrame = a;
            TimingCapture = c.Number;
        }

        public static void ResetRate()
        {
            _prevHeap = -1;
            _bytes = _ms = 0;
        }

        /// <summary>Every frame of an Allocations capture. Frames with a GC or not counted only move the reference point.</summary>
        public static void Frame(bool use, float rawMs)
        {
            long h;
            try { h = GC.GetTotalMemory(false); } catch { return; }
            if (use && _prevHeap >= 0 && h >= _prevHeap)
            {
                _bytes += h - _prevHeap;
                _ms += rawMs;
            }
            _prevHeap = h;
        }

        /// <summary>Most garbage a method could have been charged by mistake, KB per frame (NaN if unknown).</summary>
        public static double NoiseKb(int sourceId)
        {
            if (sourceId < 0 || sourceId >= _msPerFrame.Length || double.IsNaN(KbPerMs)) return double.NaN;
            return _msPerFrame[sourceId] * KbPerMs;
        }

        /// <summary>Verdict for a number in a capture of this mode: charged KB per frame vs. possible false garbage.</summary>
        public static string Verdict(CaptureMode mode, double chargedKb, double noiseKb)
        {
            if (mode != CaptureMode.Allocations) return Measured;
            if (AllocCounter.Exact) return Exact;
            if (double.IsNaN(noiseKb)) return Unchecked;
            return chargedKb >= RealRatio * noiseKb ? Real : Noise;
        }

        public static string ForSource(CaptureMode mode, int id, double chargedKb) => Verdict(mode, chargedKb, NoiseKb(id));

        /// <summary>A mod's verdict: all its measured methods together.</summary>
        public static string ForMod(CaptureMode mode, IEnumerable<SourceInfo> sources, double chargedKb)
        {
            if (mode != CaptureMode.Allocations || AllocCounter.Exact) return Verdict(mode, chargedKb, 0);
            double noise = 0;
            bool any = false;
            foreach (var s in sources)
            {
                double n = NoiseKb(s.Id);
                if (double.IsNaN(n)) continue;
                noise += n;
                any = true;
            }
            return Verdict(mode, chargedKb, any ? noise : double.NaN);
        }

        public static string Explain =>
            AllocCounter.Exact ? "Garbage numbers are exact on this runtime." :
            $"Garbage numbers are estimates: the counter also sees other threads' garbage. 'likely real' = at least {RealRatio:0}x more than it could have picked up by " +
            "chance while running (its own time per frame from the last Timing capture x the game's total garbage rate); 'could be noise' = not clearly above that; " +
            "confirm a game method with perf/alloc_check before fixing it.";
    }
}
