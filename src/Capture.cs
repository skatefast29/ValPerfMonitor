using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ValPerfMonitor
{
    /// <summary>One interval of the timeline, kept in memory for the HTML report charts.</summary>
    internal sealed class IntervalPoint
    {
        public double T, Fps, FrameAvg, P99, Mods, Game, Monitor, Overhead, HeapMb;
        public int Gc, Excluded, Frames;
        public string Draw, Tris, Gpu, CpuMain, Context;
    }

    /// <summary>
    /// One recording in one mode. Writes into its own folder:
    ///   frames.csv          per interval: frame time stats, monitor cost, Unity counters, world context
    ///   timeline.csv        per interval x owner (mod or game system), long format for pivots
    ///   spikes.csv          every slow frame, its top 3 owners, GC flag, context, trace file
    ///   summary_owners.csv  ranked mods and game systems for the whole capture
    ///   summary_methods.csv every measured method
    ///   traces/*.json       (Trace mode) flame graphs of slow frames, open at https://www.speedscope.app
    ///   report.html         charts and tables of all of the above
    /// </summary>
    internal sealed class Capture
    {
        public readonly int Number;
        public readonly CaptureMode Mode;
        public readonly string Label;
        public readonly string Folder;
        public readonly Stats Stats;
        public readonly DateTime Started = DateTime.Now;
        public readonly List<IntervalPoint> Points = new List<IntervalPoint>();
        public int TracesWritten;
        public bool Finished;

        private readonly ModRegistry _registry;
        private readonly Instrumenter _instr;
        private readonly UnityCounters _counters;
        private readonly BackgroundWriter _writer;
        public readonly NetworkStats Net;
        private readonly string _modeName;
        private double _lastIntervalAt = -1;
        private int _spikeRows;
        private const int MaxSpikeRows = 20000;

        public Capture(int number, CaptureMode mode, string label, string sessionFolder, ModRegistry registry,
                       Instrumenter instr, UnityCounters counters, BackgroundWriter writer, NetworkStats net, int ringSize)
        {
            Number = number;
            Mode = mode;
            Label = string.IsNullOrWhiteSpace(label) ? "" : label.Trim();
            _registry = registry;
            _instr = instr;
            _counters = counters;
            _writer = writer;
            Net = net;
            _modeName = mode.ToString();
            string safeLabel = new string(Label.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            // Long labels (A/B tests naming a patch) blow the 260-char Windows path limit: keep the start
            // and add a short hash of the full label so folders stay unique. The full label stays in the files.
            if (safeLabel.Length > MaxLabelInFolder)
                safeLabel = safeLabel.Substring(0, MaxLabelInFolder).TrimEnd('_') + "_" + ShortHash(Label);
            Folder = Path.Combine(sessionFolder, $"{number:00}_{_modeName}{(safeLabel.Length > 0 ? "_" + safeLabel : "")}");
            Stats = new Stats(registry, mode, ringSize, mode == CaptureMode.Baseline ? 0 : instr.CallOverheadTicks[(int)mode]);
            counters.ResetAll();
            net.ResetCapture();
            net.Recording = mode != CaptureMode.Baseline;

            string u = Stats.Unit.ToLowerInvariant();
            writer.Write(Path.Combine(Folder, "frames.csv"),
                "time,session_s,frames_counted,frames_excluded,fps,frame_ms_avg,frame_ms_p50,frame_ms_p95,frame_ms_p99,frame_ms_max,raw_frame_ms_avg," +
                $"monitor_ms_per_frame,probe_overhead_ms_per_frame,mods_{u}_per_frame,game_{u}_per_frame,probe_calls_per_frame,gc_frames,heap_mb," +
                string.Join(",", counters.Counters.Select(c => c.Column)) + ",cpu_main_ms,cpu_render_ms,gpu_ms,present_wait_ms," + GameContext.CsvHeader + "\n");
            if (mode != CaptureMode.Baseline)
            {
                writer.Write(Path.Combine(Folder, "timeline.csv"),
                    $"time,session_s,kind,owner,guid,avg_{u}_per_frame,max_{u}_in_a_frame,pct_of_frame,calls_per_frame,frames_over_owner_spike\n");
            }
            writer.Write(Path.Combine(Folder, "network.csv"), NetworkStats.IntervalHeader);
            writer.Write(Path.Combine(Folder, "spikes.csv"),
                $"session_s,frame_ms,raw_frame_ms,gc_in_frame,likely_cause,mods_{u},game_{u},top_owner,top_{u},second_owner,second_{u},third_owner,third_{u},context,trace_file\n");
        }

        private const int MaxLabelInFolder = 60;

        /// <summary>8 hex chars of FNV-1a over the label (stable across runs, unlike string.GetHashCode).</summary>
        private static string ShortHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }

        public double MeasuredSeconds => Stats.TotRawSum / 1000.0;

        // ---- Periodic output --------------------------------------------------------------------

        public void WriteInterval(double sessionSeconds, GameContext ctx)
        {
            var s = Stats;
            Net.SampleConnection();
            double ivSeconds = _lastIntervalAt < 0 ? s.IvRawSum / 1000.0 : sessionSeconds - _lastIntervalAt;
            _lastIntervalAt = sessionSeconds;
            string netRows = Net.IntervalRows(DateTime.Now.ToString("HH:mm:ss", Csv.Inv) + "," + Csv.F(sessionSeconds, "0.0") + ",", ivSeconds);
            if (netRows.Length > 0) _writer.Append(Path.Combine(Folder, "network.csv"), netRows);
            if (s.IvFrames == 0)
            {
                s.ResetInterval();
                _counters.ResetInterval();
                return;
            }

            int n = s.IvFrames;
            double avg = s.IvCorrSum / n;
            var p = new IntervalPoint
            {
                T = sessionSeconds, Frames = n, Excluded = s.IvExcluded, Fps = avg > 0 ? 1000 / avg : 0, FrameAvg = avg,
                P99 = Stats.Percentile(s.IvFrameTimes, 99), Mods = s.IvModsSum / n, Game = s.IvGameSum / n,
                Monitor = s.IvMonitorSum / n, Overhead = s.IvOverheadSum / n, Gc = s.IvGcFrames,
                HeapMb = GC.GetTotalMemory(false) / 1048576.0, Context = ctx.Short,
                Draw = Counter("draw_calls"), Tris = Counter("triangles_k"), Gpu = _counters.Timing(true, 2), CpuMain = _counters.Timing(true, 0),
            };
            Points.Add(p);

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss", Csv.Inv)).Append(',').Append(Csv.F(sessionSeconds, "0.0")).Append(',')
              .Append(n).Append(',').Append(s.IvExcluded).Append(',').Append(Csv.F(p.Fps, "0.0")).Append(',').Append(Csv.F(avg, "0.00")).Append(',')
              .Append(Csv.F(Stats.Percentile(s.IvFrameTimes, 50), "0.00")).Append(',').Append(Csv.F(Stats.Percentile(s.IvFrameTimes, 95), "0.00")).Append(',')
              .Append(Csv.F(p.P99, "0.00")).Append(',').Append(Csv.F(Stats.Percentile(s.IvFrameTimes, 100), "0.00")).Append(',')
              .Append(Csv.F(s.IvRawSum / n, "0.00")).Append(',').Append(Csv.F(p.Monitor, "0.000")).Append(',').Append(Csv.F(p.Overhead, "0.000")).Append(',')
              .Append(Mode == CaptureMode.Baseline ? "" : Csv.F(p.Mods, "0.000")).Append(',').Append(Mode == CaptureMode.Baseline ? "" : Csv.F(p.Game, "0.000")).Append(',')
              .Append(Csv.F((double)s.IvCalls / n, "0")).Append(',').Append(s.IvGcFrames).Append(',').Append(Csv.F(p.HeapMb, "0")).Append(',');
            foreach (var c in _counters.Counters) sb.Append(UnityCounters.Avg(c, true)).Append(',');
            sb.Append(_counters.Timing(true, 0)).Append(',').Append(_counters.Timing(true, 1)).Append(',')
              .Append(_counters.Timing(true, 2)).Append(',').Append(_counters.Timing(true, 3)).Append(',')
              .Append(ctx.ToCsv()).Append('\n');
            _writer.Append(Path.Combine(Folder, "frames.csv"), sb.ToString());

            if (Mode != CaptureMode.Baseline)
            {
                sb.Length = 0;
                string prefix = DateTime.Now.ToString("HH:mm:ss", Csv.Inv) + "," + Csv.F(sessionSeconds, "0.0") + ",";
                foreach (var m in _registry.Visible.Where(m => m.IvCalls > 0).OrderByDescending(m => m.IvSum))
                {
                    double v = m.IvSum / n;
                    sb.Append(prefix).Append(m.Kind).Append(',').Append(Csv.Q(m.Name)).Append(',').Append(Csv.Q(m.Guid)).Append(',')
                      .Append(Csv.F(v, "0.0000")).Append(',').Append(Csv.F(m.IvMax, "0.000")).Append(',')
                      .Append(Mode == CaptureMode.Allocations ? "" : Csv.F(avg > 0 ? 100 * v / avg : 0, "0.00")).Append(',')
                      .Append(Csv.F((double)m.IvCalls / n, "0.0")).Append(',').Append(m.IvSpikeFrames).Append('\n');
                }
                _writer.Append(Path.Combine(Folder, "timeline.csv"), sb.ToString());
            }

            WriteSpikes();
            s.ResetInterval();
            _counters.ResetInterval();
        }

        private string Counter(string column)
        {
            var c = _counters.Counters.FirstOrDefault(x => x.Column == column);
            return c == null ? "" : UnityCounters.Avg(c, true);
        }

        public void WriteSpikes()
        {
            var spikes = Stats.PendingSpikes;
            if (spikes.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var r in spikes)
            {
                if (_spikeRows++ >= MaxSpikeRows) break;
                sb.Append(Csv.F(r.SessionSeconds, "0.00")).Append(',').Append(Csv.F(r.CorrectedMs, "0.0")).Append(',').Append(Csv.F(r.RawMs, "0.0")).Append(',')
                  .Append(r.Gc ? "1" : "0").Append(',').Append(Csv.Q(r.Cause)).Append(',').Append(Csv.F(r.ModsValue, "0.00")).Append(',').Append(Csv.F(r.GameValue, "0.00")).Append(',')
                  .Append(Csv.Q(r.Owner1)).Append(',').Append(Csv.F(r.Value1, "0.00")).Append(',')
                  .Append(Csv.Q(r.Owner2)).Append(',').Append(Csv.F(r.Value2, "0.00")).Append(',')
                  .Append(Csv.Q(r.Owner3)).Append(',').Append(Csv.F(r.Value3, "0.00")).Append(',')
                  .Append(Csv.Q(r.Context)).Append(',').Append(Csv.Q(r.TraceFile)).Append('\n');
            }
            AllSpikes.AddRange(spikes.Take(Math.Max(0, 500 - AllSpikes.Count)));
            spikes.Clear();
            _writer.Append(Path.Combine(Folder, "spikes.csv"), sb.ToString());
        }

        public readonly List<SpikeRecord> AllSpikes = new List<SpikeRecord>();

        // ---- Summaries --------------------------------------------------------------------------

        public sealed class OwnerRow
        {
            public ModEntry Mod; public double Avg, Pct, Max, CallsPerFrame, Total; public SourceInfo Top;
        }

        public List<OwnerRow> OwnerRows()
        {
            long frames = Math.Max(1, Stats.TotFrames);
            double frameAvg = Stats.TotCorrSum / frames;
            var bySource = _registry.Sources.Where(x => x != null && !x.Mod.Hidden && Probe.SourceCalls[x.Id] > 0)
                .GroupBy(x => x.Mod).ToDictionary(g => g.Key, g => g.OrderByDescending(x => Probe.SourceSelf[x.Id]).First());
            return _registry.Visible
                .Where(m => m.IsPlugin || m.PatchCount > 0 || m.TotCalls > 0)
                .Where(m => !m.IsGame || m.TotCalls > 0)
                .Select(m =>
                {
                    bySource.TryGetValue(m, out var top);
                    double avg = m.TotSum / frames;
                    return new OwnerRow
                    {
                        Mod = m, Avg = avg, Pct = Mode == CaptureMode.Allocations || frameAvg <= 0 ? double.NaN : 100 * avg / frameAvg,
                        Max = m.TotMax, CallsPerFrame = (double)m.TotCalls / frames, Total = m.TotSum, Top = top,
                    };
                })
                .OrderByDescending(r => r.Avg).ThenBy(r => r.Mod.Name).ToList();
        }

        public void WriteSummaries()
        {
            if (Mode == CaptureMode.Baseline)
                return;
            _writer.Write(Path.Combine(Folder, "summary_network.csv"), Net.SummaryCsv(MeasuredSeconds));
            long frames = Math.Max(1, Stats.TotFrames);
            string u = Stats.Unit.ToLowerInvariant();
            var sb = new StringBuilder();
            sb.Append($"rank,kind,name,guid,version,avg_{u}_per_frame,pct_of_frame,max_{u}_in_a_frame,calls_per_frame,total_{u},")
              .Append("prefixes,postfixes,finalizers,transpilers,unity_callbacks,coroutines,measured_methods,unmeasurable,heaviest_method\n");
            int rank = 0;
            foreach (var r in OwnerRows())
            {
                var m = r.Mod;
                sb.Append(++rank).Append(',').Append(m.Kind).Append(',').Append(Csv.Q(m.Name)).Append(',').Append(Csv.Q(m.Guid)).Append(',').Append(Csv.Q(m.Version)).Append(',')
                  .Append(Csv.F(r.Avg, "0.0000")).Append(',').Append(Csv.F(r.Pct, "0.00")).Append(',').Append(Csv.F(r.Max, "0.000")).Append(',')
                  .Append(Csv.F(r.CallsPerFrame, "0.0")).Append(',').Append(Csv.F(r.Total, "0.0")).Append(',')
                  .Append(m.Prefixes).Append(',').Append(m.Postfixes).Append(',').Append(m.Finalizers).Append(',').Append(m.Transpilers).Append(',')
                  .Append(m.UnityCallbacks).Append(',').Append(m.Coroutines).Append(',').Append(m.Instrumented).Append(',').Append(m.Failed).Append(',')
                  .Append(Csv.Q(r.Top == null ? "" : $"{r.Top.Kind} {r.Top.Name}")).Append('\n');
            }
            _writer.Write(Path.Combine(Folder, "summary_owners.csv"), sb.ToString());

            sb.Length = 0;
            bool alloc = Mode == CaptureMode.Allocations;
            sb.Append($"owner,owner_kind,hook,method,attached_to,calls,calls_per_frame,total_{u},avg_{u}_per_frame,")
              .Append(alloc ? "avg_bytes_per_call,max_bytes_single_call,how_sure\n" : "avg_us_per_call,max_us_single_call\n");
            double perRaw = Stats.ValuePerRaw;
            double perCallScale = alloc ? 1 : 1e6 / Stopwatch.Frequency;
            foreach (var x in _registry.Sources.Where(x => x != null && !x.Mod.Hidden && Probe.SourceCalls[x.Id] > 0)
                                               .OrderByDescending(x => Probe.SourceSelf[x.Id]))
            {
                long calls = Probe.SourceCalls[x.Id];
                long self = Math.Max(0, Probe.SourceSelf[x.Id]);
                double total = self * perRaw;
                string targets = x.Targets.Count <= 3 ? string.Join(" | ", x.Targets) : string.Join(" | ", x.Targets.Take(3)) + $" | +{x.Targets.Count - 3} more";
                sb.Append(Csv.Q(x.Mod.Name)).Append(',').Append(x.Mod.Kind).Append(',').Append(x.Kind).Append(',').Append(Csv.Q(x.Name)).Append(',').Append(Csv.Q(targets)).Append(',')
                  .Append(calls).Append(',').Append(Csv.F((double)calls / frames, "0.00")).Append(',').Append(Csv.F(total, "0.000")).Append(',')
                  .Append(Csv.F(total / frames, "0.0000")).Append(',').Append(Csv.F(self * perCallScale / calls, "0.00")).Append(',')
                  .Append(Csv.F(Probe.SourceMax[x.Id] * perCallScale, "0.0"));
                if (alloc) sb.Append(',').Append(Csv.Q(GarbageConfidence.ForSource(Mode, x.Id, total / frames)));
                sb.Append('\n');
            }
            _writer.Write(Path.Combine(Folder, "summary_methods.csv"), sb.ToString());
        }

        // ---- Trace (flame graph) ----------------------------------------------------------------

        internal static readonly int[] TraceSourceCopy = new int[Probe.TraceCapacity];
        internal static readonly long[] TraceTicksCopy = new long[Probe.TraceCapacity];
        private static volatile bool _traceBusy;

        /// <summary>
        /// Copy this frame's call record and turn it into a speedscope file on the writer thread.
        /// Only one trace is processed at a time; spikes arriving meanwhile are logged without a trace.
        /// </summary>
        public string QueueTrace(double sessionSeconds, long frameStartTicks, long frameEndTicks, float frameMs, string[] sourceNames)
        {
            if (_traceBusy || Probe.TraceCount == 0)
                return "";
            int count = Probe.TraceCount;
            bool overflow = Probe.TraceOverflow;
            Array.Copy(Probe.TraceSource, TraceSourceCopy, count);
            Array.Copy(Probe.TraceTicks, TraceTicksCopy, count);
            _traceBusy = true;
            TracesWritten++;
            string file = $"frame_{sessionSeconds:000000.00}s_{frameMs:0}ms.speedscope.json".Replace(',', '.');
            string path = Path.Combine(Folder, "traces", file);
            string title = $"{_modeName} capture {Number}{(Label.Length > 0 ? " (" + Label + ")" : "")}: frame at {sessionSeconds:0.00}s, {frameMs:0.0} ms";
            _writer.Job(() =>
            {
                try { return (path, BuildSpeedscope(count, overflow, frameStartTicks, frameEndTicks, sourceNames, title)); }
                finally { _traceBusy = false; }
            });
            return "traces/" + file;
        }

        internal static string BuildSpeedscope(int count, bool overflow, long start, long end, string[] names, string title)
        {
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            var frameIndex = new Dictionary<int, int>();
            var frameNames = new List<string> { overflow ? "Frame (unmeasured engine/render/vanilla time; TRACE BUFFER FULL, later calls missing)" : "Frame (time not inside any measured method: engine, rendering, unhooked vanilla code)" };
            var ev = new StringBuilder(count * 32);
            ev.Append("{\"type\":\"O\",\"frame\":0,\"at\":0}");
            var stack = new Stack<int>();
            double endAt = (end - start) * msPerTick;
            for (int i = 0; i < count; i++)
            {
                int s = TraceSourceCopy[i];
                double at = Math.Min(endAt, Math.Max(0, (TraceTicksCopy[i] - start) * msPerTick));
                if (s >= 0)
                {
                    if (!frameIndex.TryGetValue(s, out int fi))
                    {
                        fi = frameNames.Count;
                        frameIndex[s] = fi;
                        frameNames.Add(s < names.Length && names[s] != null ? names[s] : "source " + s);
                    }
                    stack.Push(s);
                    ev.Append(",{\"type\":\"O\",\"frame\":").Append(fi).Append(",\"at\":").Append(Csv.JNum(at, "0.#####")).Append('}');
                }
                else
                {
                    int id = ~s;
                    if (stack.Count == 0 || stack.Peek() != id) continue; // open was lost to overflow
                    stack.Pop();
                    ev.Append(",{\"type\":\"C\",\"frame\":").Append(frameIndex[id]).Append(",\"at\":").Append(Csv.JNum(at, "0.#####")).Append('}');
                }
            }
            while (stack.Count > 0)
                ev.Append(",{\"type\":\"C\",\"frame\":").Append(frameIndex[stack.Pop()]).Append(",\"at\":").Append(Csv.JNum(endAt, "0.#####")).Append('}');
            ev.Append(",{\"type\":\"C\",\"frame\":0,\"at\":").Append(Csv.JNum(endAt, "0.#####")).Append('}');

            var sb = new StringBuilder(ev.Length + frameNames.Count * 64 + 512);
            sb.Append("{\"$schema\":\"https://www.speedscope.app/file-format-schema.json\",\"exporter\":\"ValPerfMonitor\",\"name\":").Append(Csv.Json(title))
              .Append(",\"activeProfileIndex\":0,\"shared\":{\"frames\":[");
            for (int i = 0; i < frameNames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Csv.Json(frameNames[i])).Append('}');
            }
            sb.Append("]},\"profiles\":[{\"type\":\"evented\",\"name\":").Append(Csv.Json(title))
              .Append(",\"unit\":\"milliseconds\",\"startValue\":0,\"endValue\":").Append(Csv.JNum(endAt, "0.#####"))
              .Append(",\"events\":[").Append(ev).Append("]}]}");
            return sb.ToString();
        }
    }
}
