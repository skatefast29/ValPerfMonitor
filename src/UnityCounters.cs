using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Unity.Profiling;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Engine-side numbers the mod probes can't see: render load (draw calls, triangles), memory, and
    /// CPU-vs-GPU frame timing. Each counter is only used if this release build actually provides it;
    /// unavailable ones are reported as blank, never as zero.
    /// </summary>
    internal sealed class UnityCounters : IDisposable
    {
        internal sealed class Counter
        {
            public string Column;
            public ProfilerRecorder Recorder;
            public double Scale;          // raw value -> reported unit
            public double IvSum; public long IvSamples;
            public double TotSum; public long TotSamples;
            public bool Seen;             // ever returned a non-zero value
        }

        public readonly List<Counter> Counters = new List<Counter>();

        // FrameTimingManager (CPU main/render thread and GPU time). Needs the feature enabled in the build.
        private readonly FrameTiming[] _timings = new FrameTiming[1];
        private bool _frameTimingOk = true;
        public bool FrameTimingSeen;
        public double IvCpuMain, IvCpuRender, IvGpu, IvPresentWait; public long IvTimingSamples;
        public double TotCpuMain, TotCpuRender, TotGpu, TotPresentWait; public long TotTimingSamples;

        public UnityCounters(ManualLogSource log)
        {
            Add("draw_calls", ProfilerCategory.Render, "Draw Calls Count", 1);
            Add("batches", ProfilerCategory.Render, "Batches Count", 1);
            Add("setpass_calls", ProfilerCategory.Render, "SetPass Calls Count", 1);
            Add("triangles_k", ProfilerCategory.Render, "Triangles Count", 0.001);
            Add("vertices_k", ProfilerCategory.Render, "Vertices Count", 0.001);
            Add("shadow_casters", ProfilerCategory.Render, "Shadow Casters Count", 1);
            Add("gc_used_mb", ProfilerCategory.Memory, "GC Used Memory", 1.0 / 1048576);
            Add("gc_reserved_mb", ProfilerCategory.Memory, "GC Reserved Memory", 1.0 / 1048576);
            Add("total_used_mb", ProfilerCategory.Memory, "Total Used Memory", 1.0 / 1048576);
            Add("gfx_used_mb", ProfilerCategory.Memory, "Gfx Used Memory", 1.0 / 1048576);
            Add("gc_alloc_kb_per_frame", ProfilerCategory.Memory, "GC Allocated In Frame", 1.0 / 1024);
            int valid = Counters.FindAll(c => c.Recorder.Valid).Count;
            log.LogInfo($"Unity counters available: {valid}/{Counters.Count}");
        }

        private void Add(string column, ProfilerCategory category, string stat, double scale)
        {
            var c = new Counter { Column = column, Scale = scale };
            try { c.Recorder = ProfilerRecorder.StartNew(category, stat, 1); }
            catch { }
            Counters.Add(c);
        }

        /// <summary>Called once per counted frame.</summary>
        public void Sample()
        {
            foreach (var c in Counters)
            {
                if (!c.Recorder.Valid) continue;
                long v;
                try { v = c.Recorder.LastValue; } catch { continue; }
                if (v != 0) c.Seen = true;
                double x = v * c.Scale;
                c.IvSum += x; c.IvSamples++;
                c.TotSum += x; c.TotSamples++;
            }

            if (!_frameTimingOk) return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timings) == 0) return;
                var t = _timings[0];
                if (t.cpuFrameTime <= 0 && t.gpuFrameTime <= 0) return;
                FrameTimingSeen = true;
                IvCpuMain += t.cpuMainThreadFrameTime; IvCpuRender += t.cpuRenderThreadFrameTime;
                IvGpu += t.gpuFrameTime; IvPresentWait += t.cpuMainThreadPresentWaitTime; IvTimingSamples++;
                TotCpuMain += t.cpuMainThreadFrameTime; TotCpuRender += t.cpuRenderThreadFrameTime;
                TotGpu += t.gpuFrameTime; TotPresentWait += t.cpuMainThreadPresentWaitTime; TotTimingSamples++;
            }
            catch { _frameTimingOk = false; }
        }

        public void ResetInterval()
        {
            foreach (var c in Counters) { c.IvSum = 0; c.IvSamples = 0; }
            IvCpuMain = IvCpuRender = IvGpu = IvPresentWait = 0; IvTimingSamples = 0;
        }

        public void ResetAll()
        {
            ResetInterval();
            foreach (var c in Counters) { c.TotSum = 0; c.TotSamples = 0; }
            TotCpuMain = TotCpuRender = TotGpu = TotPresentWait = 0; TotTimingSamples = 0;
        }

        /// <summary>Latest value of one counter in its reported unit, or NaN if this build doesn't provide it.</summary>
        public double Last(string column)
        {
            var c = Counters.Find(x => x.Column == column);
            if (c == null || !c.Recorder.Valid) return double.NaN;
            try { return c.Recorder.LastValue * c.Scale; }
            catch { return double.NaN; }
        }

        public static string Avg(Counter c, bool interval)
        {
            long n = interval ? c.IvSamples : c.TotSamples;
            if (!c.Recorder.Valid || !c.Seen || n == 0) return "";
            return ((interval ? c.IvSum : c.TotSum) / n).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        public string Timing(bool interval, int which)
        {
            long n = interval ? IvTimingSamples : TotTimingSamples;
            if (!FrameTimingSeen || n == 0) return "";
            double v = which switch
            {
                0 => interval ? IvCpuMain : TotCpuMain,
                1 => interval ? IvCpuRender : TotCpuRender,
                2 => interval ? IvGpu : TotGpu,
                _ => interval ? IvPresentWait : TotPresentWait,
            };
            return (v / n).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            foreach (var c in Counters)
                try { c.Recorder.Dispose(); } catch { }
        }
    }
}
