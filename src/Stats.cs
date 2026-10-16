using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ValPerfMonitor
{
    internal struct SpikeRecord
    {
        public double SessionSeconds;
        public float RawMs, CorrectedMs, ModsValue, GameValue;
        public bool Gc;
        public string Context;
        public string Cause;       // best guess at why the frame was slow, e.g. "garbage collection + loading objects (+40)"
        public string Owner1, Owner2, Owner3;
        public float Value1, Value2, Value3;
        public string TraceFile;
    }

    /// <summary>What happened in one frame, as seen by the plugin.</summary>
    internal struct FrameSample
    {
        public float RawMs;        // wall-clock time since the previous frame boundary
        public float MonitorMs;    // time this monitor itself spent on the main thread in that frame
        public bool Counted;       // false = excluded (warm-up, hooking, heavy monitor work)
        public bool Gc;            // a garbage collection completed during the frame
        public bool Saving;        // the world was being saved
        public bool Teleporting;   // the player was teleporting (new area loading)
        public int ObjectsChanged; // objects loaded or unloaded around the player this frame
        public double SessionSeconds;
        public string Context;
    }

    /// <summary>
    /// Turns the probe's per-frame counters into three views: a rolling window for the overlay,
    /// per-interval numbers for the timeline, and capture totals for the summaries.
    ///
    /// Accuracy rules:
    ///  - frames in which the monitor hooked methods, wrote reports or was warming up are excluded;
    ///  - corrected frame time = raw frame time - the monitor's own main-thread time - probe overhead
    ///    (calls x calibrated cost), i.e. an estimate of the frame without the monitor present;
    ///  - per-owner values are self time with child probe overhead already removed by the probe.
    /// </summary>
    internal sealed class Stats
    {
        private readonly ModRegistry _registry;
        public readonly CaptureMode Mode;
        public readonly string Unit;            // "ms" or "KB"
        private readonly double _valuePerRaw;   // ticks -> ms or bytes -> KB
        private readonly double _callOverheadMs;

        public readonly int RingSize;
        public int RingPos, RingFilled;
        public readonly float[] FrameRing, ModsRing, GameRing;

        // Window (overlay), refreshed on demand.
        public float WindowFrameAvg, WindowFrameMax, WindowModsAvg, WindowGameAvg, WindowCallsPerFrame;

        // Current log interval.
        public int IvFrames, IvExcluded, IvGcFrames;
        public double IvRawSum, IvCorrSum, IvMonitorSum, IvOverheadSum, IvModsSum, IvGameSum; public long IvCalls;
        public readonly List<float> IvFrameTimes = new List<float>();

        // Capture totals.
        public long TotFrames, TotExcluded, TotGcFrames;
        public double TotRawSum, TotCorrSum, TotMonitorSum, TotOverheadSum, TotModsSum, TotGameSum; public long TotCalls;
        public readonly List<float> FrameTimes = new List<float>();

        public readonly List<SpikeRecord> PendingSpikes = new List<SpikeRecord>();
        public int SpikeCount;
        /// <summary>Slow frames per main cause (first part of the label), for the report.</summary>
        public readonly Dictionary<string, int> CauseCounts = new Dictionary<string, int>();

        public Stats(ModRegistry registry, CaptureMode mode, int ringSize, double callOverheadTicks)
        {
            _registry = registry;
            Mode = mode;
            RingSize = ringSize;
            FrameRing = new float[ringSize];
            ModsRing = new float[ringSize];
            GameRing = new float[ringSize];
            bool alloc = mode == CaptureMode.Allocations;
            Unit = alloc ? "KB" : "ms";
            _valuePerRaw = alloc ? 1.0 / 1024 : 1000.0 / Stopwatch.Frequency;
            _callOverheadMs = mode == CaptureMode.Baseline ? 0 : callOverheadTicks * 1000.0 / Stopwatch.Frequency;
            foreach (var m in registry.Mods) m.ResetValues();
            Probe.ResetSources();
        }

        public double ValuePerRaw => _valuePerRaw;

        /// <summary>Allocations mode on a counter that drops when the GC runs: GC frames carry no per-owner values.</summary>
        public bool DropsGcFrames => Mode == CaptureMode.Allocations && !AllocCounter.Exact;

        /// <summary>Roll up one frame. Returns the spike record if this frame was a spike.</summary>
        public SpikeRecord? EndFrame(in FrameSample f, float spikeMs, float ownerSpikeValue)
        {
            if (!f.Counted)
            {
                IvExcluded++; TotExcluded++;
                return null;
            }

            var mods = _registry.Mods;
            var raw = Probe.ModFrame;
            // With a heap-size allocation counter, a GC inside a measured call shows up as a huge negative
            // allocation for that call. Such a frame still counts for frame time and GC rate, but its
            // per-owner values are dropped (about 1 frame in 1000).
            bool dropValues = DropsGcFrames && f.Gc;
            var calls = Probe.ModFrameCalls;

            long frameCalls = 0;
            for (int i = 1; i < mods.Count; i++) frameCalls += calls[i];
            float overheadMs = (float)(frameCalls * _callOverheadMs);
            float corrected = Math.Max(0.01f, f.RawMs - f.MonitorMs - overheadMs);

            float modsV = 0, gameV = 0;
            int pos = RingPos;
            string o1 = null, o2 = null, o3 = null; float v1 = 0, v2 = 0, v3 = 0;
            for (int i = 1; i < mods.Count; i++)
            {
                var m = mods[i];
                int c = dropValues ? 0 : calls[i];
                float v = c == 0 ? 0 : (float)(raw[i] * _valuePerRaw);
                if (v < 0) v = 0;
                m.Ring[pos] = v;
                m.CallRing[pos] = c;
                if (c == 0)
                    continue;
                if (m.IsGame) gameV += v; else modsV += v;
                m.IvSum += v; m.IvCalls += c; if (v > m.IvMax) m.IvMax = v;
                if (v >= ownerSpikeValue) m.IvSpikeFrames++;
                m.TotSum += v; m.TotCalls += c; if (v > m.TotMax) m.TotMax = v;
                if (v > v1) { o3 = o2; v3 = v2; o2 = o1; v2 = v1; o1 = m.Name; v1 = v; }
                else if (v > v2) { o3 = o2; v3 = v2; o2 = m.Name; v2 = v; }
                else if (v > v3) { o3 = m.Name; v3 = v; }
            }

            FrameRing[pos] = corrected;
            ModsRing[pos] = modsV;
            GameRing[pos] = gameV;
            RingPos = (pos + 1) % RingSize;
            if (RingFilled < RingSize) RingFilled++;

            IvFrames++; IvRawSum += f.RawMs; IvCorrSum += corrected; IvMonitorSum += f.MonitorMs; IvOverheadSum += overheadMs;
            IvModsSum += modsV; IvGameSum += gameV; IvCalls += frameCalls; IvFrameTimes.Add(corrected);
            TotFrames++; TotRawSum += f.RawMs; TotCorrSum += corrected; TotMonitorSum += f.MonitorMs; TotOverheadSum += overheadMs;
            TotModsSum += modsV; TotGameSum += gameV; TotCalls += frameCalls; FrameTimes.Add(corrected);
            if (f.Gc) { IvGcFrames++; TotGcFrames++; }

            if (corrected < spikeMs)
                return null;
            SpikeCount++;
            string cause = Cause(f, corrected, modsV + gameV, o1, v1, out string mainCause);
            CauseCounts.TryGetValue(mainCause, out int cc);
            CauseCounts[mainCause] = cc + 1;
            var r = new SpikeRecord
            {
                SessionSeconds = f.SessionSeconds, RawMs = f.RawMs, CorrectedMs = corrected, ModsValue = modsV, GameValue = gameV,
                Gc = f.Gc, Context = f.Context, Cause = cause,
                Owner1 = o1 ?? "", Owner2 = o2 ?? "", Owner3 = o3 ?? "", Value1 = v1, Value2 = v2, Value3 = v3,
            };
            PendingSpikes.Add(r);
            return r;
        }

        /// <summary>
        /// Labels a slow frame. Several causes can apply; the first one listed is the main one.
        /// Per-mod blame needs Timing or Trace (Allocations values are KB, Baseline has none).
        /// </summary>
        private string Cause(in FrameSample f, float frameMs, float measured, string top, float topValue, out string main)
        {
            var parts = new List<string>(3);
            var keys = new List<string>(3);
            if (f.Gc) { parts.Add("garbage collection"); keys.Add("garbage collection"); }
            if (f.Saving) { parts.Add("world save"); keys.Add("world save"); }
            if (f.Teleporting) { parts.Add("teleport (new area loading)"); keys.Add("teleport"); }
            // A game or test-tool event in the last 3 s (looked up only for slow frames: no garbage on normal ones).
            string ev = Events.Recent(UnityEngine.Time.realtimeSinceStartup, 3f, out string evKind);
            if (ev != null && evKind != "teleport") { parts.Add(ev); keys.Add(evKind == "bridge" ? "after a ValAgentBridge command" : "after " + evKind); }
            if (f.ObjectsChanged >= 10) { parts.Add($"objects loaded/unloaded ({f.ObjectsChanged})"); keys.Add("objects loading/unloading"); }
            bool timed = Mode == CaptureMode.Timing || Mode == CaptureMode.Trace;
            if (timed && top != null && topValue >= 0.4f * frameMs)
            {
                parts.Add($"{top} ({topValue:0.0} ms)");
                keys.Add(top);
            }
            else if (timed && parts.Count == 0 && measured < 0.3f * frameMs)
            {
                parts.Add("engine / rendering (outside any measured code)");
                keys.Add("engine / rendering");
            }
            if (parts.Count == 0)
            {
                string unclear = timed ? "spread over many mods and systems" : Mode == CaptureMode.Baseline ? "not identified (Baseline has no per-mod data)" : "not identified (see Timing)";
                parts.Add(unclear);
                keys.Add(timed ? "spread out" : "not identified");
            }
            main = keys[0];
            return string.Join(" + ", parts);
        }

        public void RefreshWindow()
        {
            int n = RingFilled;
            if (n == 0) return;
            double f = 0, mm = 0, gg = 0; float fmax = 0;
            for (int i = 0; i < n; i++)
            {
                f += FrameRing[i]; mm += ModsRing[i]; gg += GameRing[i];
                if (FrameRing[i] > fmax) fmax = FrameRing[i];
            }
            WindowFrameAvg = (float)(f / n);
            WindowFrameMax = fmax;
            WindowModsAvg = (float)(mm / n);
            WindowGameAvg = (float)(gg / n);

            long allCalls = 0;
            var mods = _registry.Mods;
            for (int k = 1; k < mods.Count; k++)
            {
                var m = mods[k];
                double sum = 0; float max = 0; long c = 0;
                for (int i = 0; i < n; i++) { float v = m.Ring[i]; sum += v; if (v > max) max = v; c += m.CallRing[i]; }
                m.WindowAvg = (float)(sum / n);
                m.WindowMax = max;
                m.WindowCalls = c;
                m.WindowCallsPerFrame = (float)c / n;
                allCalls += c;
            }
            WindowCallsPerFrame = (float)allCalls / n;
        }

        public void ResetInterval()
        {
            IvFrames = 0; IvExcluded = 0; IvGcFrames = 0;
            IvRawSum = IvCorrSum = IvMonitorSum = IvOverheadSum = IvModsSum = IvGameSum = 0; IvCalls = 0;
            IvFrameTimes.Clear();
            foreach (var m in _registry.Mods) { m.IvSum = 0; m.IvMax = 0; m.IvCalls = 0; m.IvSpikeFrames = 0; }
        }

        /// <summary>Frame time at a percentile (0..100), e.g. 99 = the slowest 1% of frames start here.</summary>
        public static float Percentile(List<float> values, double pct)
        {
            if (values.Count == 0) return float.NaN;
            var arr = values.ToArray();
            Array.Sort(arr);
            int idx = (int)Math.Ceiling(pct / 100.0 * arr.Length) - 1;
            return arr[Math.Max(0, Math.Min(arr.Length - 1, idx))];
        }

        /// <summary>Average of the slowest pct% of frames (the usual "1% low", as a frame time).</summary>
        public static float WorstAverage(List<float> values, double pct)
        {
            if (values.Count == 0) return float.NaN;
            var arr = values.ToArray();
            Array.Sort(arr);
            int n = Math.Max(1, (int)Math.Round(arr.Length * pct / 100.0));
            double s = 0;
            for (int i = arr.Length - n; i < arr.Length; i++) s += arr[i];
            return (float)(s / n);
        }
    }
}
