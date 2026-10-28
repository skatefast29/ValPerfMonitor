using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// Full play recording: while the owner plays normally, rotates Timing, Allocations and Trace in blocks of a few
    /// minutes (one capture each), runs a memory census between blocks now and then, and keeps one combined summary
    /// of the whole session (play_summary.txt, play_blocks.csv), rewritten after every block so a crash loses little.
    /// Heavier than the usual recording (more stutter, lower FPS in the Allocations and Trace blocks); off by default.
    /// It pauses while a test runs and while out of a world, and picks up again after.
    /// </summary>
    internal sealed class PlayRecording
    {
        public readonly List<(CaptureMode mode, float minutes)> Cycle;
        public readonly string Label;
        public readonly DateTime Started = DateTime.Now;
        public readonly float CensusMinutes;
        public int Blocks;
        /// <summary>The memory census running now was started by this recording (between blocks).</summary>
        public bool OwnsCensus;

        private int _next;
        private Capture _block;
        private float _blockStart, _blockMinutes, _nextCensus;
        private string _summaryPath, _blocksPath;

        // Combined over all blocks.
        private sealed class OwnerSum { public string Name; public bool Game; public double Total, Max; }
        private sealed class PlaceSum { public double Frames, FrameMs, Mods, Seconds; public float WorstP99; }
        private readonly Dictionary<CaptureMode, long> _frames = new Dictionary<CaptureMode, long>();
        private readonly Dictionary<CaptureMode, double> _seconds = new Dictionary<CaptureMode, double>();
        private readonly Dictionary<CaptureMode, Dictionary<string, OwnerSum>> _owners = new Dictionary<CaptureMode, Dictionary<string, OwnerSum>>();
        private readonly Dictionary<string, PlaceSum> _places = new Dictionary<string, PlaceSum>();
        private readonly Dictionary<string, int> _causes = new Dictionary<string, int>();
        private readonly List<(DateTime when, int block, CaptureMode mode, SpikeRecord spike, string folder)> _worst = new List<(DateTime, int, CaptureMode, SpikeRecord, string)>();
        private readonly List<string> _censusLines = new List<string>();
        private readonly StringBuilder _rows = new StringBuilder();
        private int _slowFrames, _errors;

        private const string BlocksHeader = "block,capture,mode,start,minutes_counted,fps,fps_1pct_low,frame_ms_p99,mods_per_frame,game_per_frame,unit,gc_per_min,slow_frames,log_errors,places,top_mods,folder\n";
        private static readonly Regex Pieces = new Regex(@"(\d+) pieces within 50 m", RegexOptions.Compiled);
        /// <summary>Building pieces within 50 m from which a spot counts as a base.</summary>
        private const int BasePieces = 50;

        public PlayRecording(Plugin p)
        {
            Cycle = new List<(CaptureMode, float)>();
            if (p.CfgPlayTiming.Value > 0) Cycle.Add((CaptureMode.Timing, p.CfgPlayTiming.Value));
            if (p.CfgPlayAlloc.Value > 0 && AllocCounter.Available) Cycle.Add((CaptureMode.Allocations, p.CfgPlayAlloc.Value));
            if (p.CfgPlayTrace.Value > 0) Cycle.Add((CaptureMode.Trace, p.CfgPlayTrace.Value));
            if (Cycle.Count == 0) Cycle.Add((CaptureMode.Timing, 10f));
            CensusMinutes = Mathf.Max(0, p.CfgPlayCensus.Value);
            _nextCensus = CensusMinutes > 0 ? Time.realtimeSinceStartup + CensusMinutes * 60 : float.MaxValue;
            Label = "play " + Started.ToString("HHmm", Csv.Inv);
            _summaryPath = Path.Combine(p.SessionFolder, "play_summary.txt");
            _blocksPath = Path.Combine(p.SessionFolder, "play_blocks.csv");
        }

        public string Describe() => string.Join(" -> ", Cycle.Select(c => $"{c.mode} {c.minutes:0.#} min").ToArray()) +
                                    (CensusMinutes > 0 ? $", memory census every {CensusMinutes:0} min" : "");

        public string Status(Plugin p)
        {
            var c = p.Current;
            if (OwnsCensus) return "Full play recording: memory census between blocks";
            if (c == null || c != _block) return p.TestRunning != null ? $"Full play recording: paused while {p.TestRunning} runs" : "Full play recording: waiting for the world";
            return $"Full play recording: {c.Mode} block {Blocks} · {(Time.realtimeSinceStartup - _blockStart) / 60:0}/{_blockMinutes:0.#} min";
        }

        /// <summary>Starts, ends and rotates blocks. Called every frame from Plugin.Tick.</summary>
        public void Tick(Plugin p, bool inWorld)
        {
            if (OwnsCensus && MemoryCensus.Running == null) OwnsCensus = false;
            if (p.TestRunning != null) return; // a test (or this recording's census) runs; carry on after it
            var c = p.Current;
            if (c != null && c == _block)
            {
                if (!inWorld || Time.realtimeSinceStartup - _blockStart >= _blockMinutes * 60)
                {
                    p.FinishCapture();
                    p.GoIdle();
                    _block = null;
                }
                return;
            }
            if (c != null || !inWorld || !p.WorldReady) return;

            float now = Time.realtimeSinceStartup;
            if (Blocks > 0 && now >= _nextCensus)
            {
                _nextCensus = now + CensusMinutes * 60;
                var mc = p.StartMemoryCensus(100);
                if (mc.Error == null) { OwnsCensus = true; return; }
                p.Log.LogInfo("Full play recording: memory census skipped: " + mc.Error);
            }
            var (mode, minutes) = Cycle[_next % Cycle.Count];
            _next++;
            p.StartCapture(mode, Label);
            if (p.Current == null) return; // could not start (the world is still settling); tried again next frame
            p.Current.PlayBlock = true;
            _block = p.Current;
            _blockStart = now;
            _blockMinutes = minutes;
            Blocks++;
        }

        /// <summary>A block's capture was saved (from Plugin.FinishCapture): add it to the combined summary.</summary>
        public void BlockFinished(Capture c, Plugin p)
        {
            if (c == _block) _block = null;
            var s = Report.Summarize(c.Stats);
            var mode = c.Mode;
            _frames[mode] = (_frames.TryGetValue(mode, out long f) ? f : 0) + c.Stats.TotFrames;
            _seconds[mode] = (_seconds.TryGetValue(mode, out double sec) ? sec : 0) + c.MeasuredSeconds;
            if (!_owners.TryGetValue(mode, out var owners)) _owners[mode] = owners = new Dictionary<string, OwnerSum>();
            var rows = c.OwnerRows();
            foreach (var r in rows)
            {
                if (r.Total <= 0) continue;
                if (!owners.TryGetValue(r.Mod.Name, out var o)) owners[r.Mod.Name] = o = new OwnerSum { Name = r.Mod.Name, Game = r.Mod.IsGame };
                o.Total += r.Total;
                o.Max = Math.Max(o.Max, r.Max);
            }

            // Frame rate by place: Timing blocks only (Allocations and Trace add their own cost to the frame).
            var places = new List<string>();
            foreach (var pt in c.Points)
            {
                string place = Place(pt.Context);
                if (place == null) continue;
                if (!places.Contains(place)) places.Add(place);
                if (mode != CaptureMode.Timing || pt.Frames == 0) continue;
                if (!_places.TryGetValue(place, out var ps)) _places[place] = ps = new PlaceSum();
                ps.Frames += pt.Frames;
                ps.FrameMs += pt.FrameAvg * pt.Frames;
                ps.Mods += pt.Mods * pt.Frames;
                ps.Seconds += pt.FrameAvg * pt.Frames / 1000;
                ps.WorstP99 = Math.Max(ps.WorstP99, (float)pt.P99);
            }

            foreach (var kv in c.Stats.CauseCounts) _causes[kv.Key] = (_causes.TryGetValue(kv.Key, out int n) ? n : 0) + kv.Value;
            _slowFrames += c.Stats.SpikeCount;
            DateTime now = DateTime.Now;
            string rel = Path.GetFileName(c.Folder);
            foreach (var sp in c.AllSpikes.OrderByDescending(x => x.CorrectedMs).Take(20))
                _worst.Add((now.AddSeconds(sp.SessionSeconds - p.SessionSeconds), Blocks, mode, sp, rel));
            _worst.Sort((a, b) => b.spike.CorrectedMs.CompareTo(a.spike.CorrectedMs));
            if (_worst.Count > 20) _worst.RemoveRange(20, _worst.Count - 20);

            var log = LogWatch.ForCapture(c, p.Registry);
            _errors += log.errors;

            string unit = c.Stats.Unit;
            string top = mode == CaptureMode.Baseline ? "" : string.Join("; ", rows.Where(r => !r.Mod.IsGame && r.Avg > 0).Take(3).Select(r => $"{r.Mod.Name} {r.Avg:0.000} {unit}").ToArray());
            _rows.Append(Blocks).Append(',').Append(c.Number).Append(',').Append(mode).Append(',').Append(c.Started.ToString("HH:mm:ss", Csv.Inv)).Append(',')
                 .Append(Csv.F(c.MeasuredSeconds / 60, "0.0")).Append(',').Append(Csv.F(s.Fps, "0.0")).Append(',').Append(Csv.F(s.Fps1Low, "0.0")).Append(',')
                 .Append(Csv.F(s.P99, "0.0")).Append(',').Append(Csv.F(s.Mods, "0.000")).Append(',').Append(Csv.F(s.Game, "0.000")).Append(',').Append(unit).Append(',')
                 .Append(Csv.F(s.GcPerMin, "0.0")).Append(',').Append(c.Stats.SpikeCount).Append(',').Append(log.errors).Append(',')
                 .Append(Csv.Q(string.Join("; ", places.ToArray()))).Append(',').Append(Csv.Q(top)).Append(',').Append(Csv.Q(rel)).Append('\n');
            Write(p);
        }

        public void CensusFinished(MemoryCensus mc)
        {
            _censusLines.Add($"  {mc.FinishedAt:HH:mm} ({Path.GetFileName(mc.File)}):");
            foreach (var l in mc.Plain()) _censusLines.Add("    " + l);
        }

        /// <summary>"Meadows" or "Meadows (at a base)" from a context line; null when not in a world.</summary>
        private static string Place(string context)
        {
            if (string.IsNullOrEmpty(context)) return null;
            int comma = context.IndexOf(',');
            if (comma <= 0) return null;
            string biome = context.Substring(0, comma);
            var m = Pieces.Match(context);
            bool atBase = m.Success && int.TryParse(m.Groups[1].Value, out int pieces) && pieces >= BasePieces;
            return atBase ? biome + " (at a base)" : biome;
        }

        public void Write(Plugin p)
        {
            p.Writer.Write(_blocksPath, BlocksHeader + _rows);
            p.Writer.Write(_summaryPath, string.Join("\n", Summary(p).ToArray()) + "\n");
        }

        public List<string> Summary(Plugin p)
        {
            var l = new List<string>();
            double total = (DateTime.Now - Started).TotalMinutes;
            string perMode = string.Join(", ", _seconds.Select(kv => $"{kv.Key} {kv.Value / 60:0} min").ToArray());
            l.Add($"Full play recording, started {Started:yyyy-MM-dd HH:mm}, {Minutes(total)} so far, {Blocks} blocks{(perMode.Length > 0 ? " (" + perMode + " counted)" : "")}.");
            l.Add($"Cycle: {Describe()}.");
            l.Add("Recorded during normal play, so the numbers follow what you were doing. Good for finding what goes wrong; prove a fix with an A/B test.");
            l.Add("");

            if (_places.Count > 0)
            {
                l.Add("Frame rate by place (Timing blocks; a base = 50+ building pieces within 50 m):");
                foreach (var kv in _places.OrderByDescending(kv => kv.Value.Seconds))
                {
                    var ps = kv.Value;
                    double avg = ps.FrameMs / ps.Frames;
                    l.Add($"  {kv.Key,-28} {Minutes(ps.Seconds / 60),8}  {1000 / avg,5:0} FPS, mods {ps.Mods / ps.Frames:0.00} ms per frame, worst 10 s stretch: 1 frame in 100 over {ps.WorstP99:0} ms");
                }
                l.Add("");
            }

            AddOwners(l, CaptureMode.Timing, false, "Heaviest mods (CPU ms per frame, Timing blocks; worst single frame in brackets):", "ms");
            AddOwners(l, CaptureMode.Timing, true, "Heaviest game systems (same):", "ms");
            AddOwners(l, CaptureMode.Allocations, false, "Most garbage per frame (KB, Allocations blocks; garbage leads to collection stutters):", "KB");

            double hours = Math.Max(1.0 / 60, _seconds.Values.Sum() / 3600);
            l.Add($"Slow frames (over {p.CfgFrameSpikeMs.Value:0} ms): {_slowFrames} in all, {_slowFrames / hours:0} per hour of play" +
                  (_causes.Count > 0 ? $". Likely causes: {string.Join(", ", _causes.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Value} {k.Key}").ToArray())}." : "."));
            if (_worst.Count > 0)
            {
                l.Add("  Worst frames (time, block, how long, likely cause, busiest owner, where; flame graph if a Trace block caught it):");
                foreach (var w in _worst.Take(12))
                {
                    var sp = w.spike;
                    string owner = string.IsNullOrEmpty(sp.Owner1) ? "" : $", {sp.Owner1} {sp.Value1:0.0}";
                    l.Add($"  {w.when:HH:mm:ss}  block {w.block} {w.mode,-11} {sp.CorrectedMs,6:0} ms  {sp.Cause}{owner}; {sp.Context}" +
                          (string.IsNullOrEmpty(sp.TraceFile) ? "" : $"  [{w.folder}\\{Path.GetFileName(sp.TraceFile)}]"));
                }
            }
            l.Add("");

            var mem = p.Memory;
            if (mem != null && mem.TrendWindow(out var first, out var last))
            {
                l.Add($"Memory from {Minutes(first.SessionSeconds / 60)} to {Minutes(last.SessionSeconds / 60)} into the session: managed heap {first.HeapMb:0} -> {last.HeapMb:0} MB, " +
                      $"total in use {first.TotalUsedMb:0} -> {last.TotalUsedMb:0} MB, game objects {first.GameObjects:N0} -> {last.GameObjects:N0}, " +
                      $"materials {first.Materials:N0} -> {last.Materials:N0} (memory.csv).");
                var grow = mem.GrowingMods(5);
                if (grow.Count > 0) l.Add("  Mods whose live objects grew most: " + string.Join(", ", grow.Select(g => $"{g.mod} {g.from:N0} -> {g.to:N0}").ToArray()) + ".");
            }
            if (_censusLines.Count > 0)
            {
                l.Add("Memory censuses (what keeps memory, and what grew since the one before):");
                l.AddRange(_censusLines);
            }
            l.Add("");

            if (p.Link != null && p.Link.Connected)
            {
                l.AddRange(p.Link.Summary(p.Link.MinutesConnected));
                l.Add("  Every second of the visit: connection.csv; hitches: server_hitches.csv.");
                l.Add("");
            }

            var w2 = LogWatch.Instance;
            l.Add($"Log: {_errors} errors during the blocks{(w2 != null ? $"; {w2.Errors} errors and {w2.Warnings} warnings this session" : "")} (log_errors.csv names the mods).");
            var slow = ObjectLoads.Top(5, byMax: true).Where(r => r.MaxMs >= 10).ToList();
            if (slow.Count > 0)
                l.Add("Slowest objects to spawn: " + string.Join(", ", slow.Select(r => $"{r.Prefab} {r.MaxMs:0} ms (avg {r.AvgMs:0.0} ms x{r.Count})").ToArray()) + " (slow_objects.csv).");
            l.Add("");
            l.Add("Files in this folder: play_blocks.csv (one row per block); each block's own folder has report.html, frames.csv (every 10 s), timeline.csv (per mod every 10 s), spikes.csv and network.csv.");
            return l;
        }

        private void AddOwners(List<string> l, CaptureMode mode, bool game, string title, string unit)
        {
            if (!_owners.TryGetValue(mode, out var owners) || !_frames.TryGetValue(mode, out long frames) || frames == 0) return;
            var top = owners.Values.Where(o => o.Game == game).OrderByDescending(o => o.Total).Take(game ? 6 : 12).ToList();
            if (top.Count == 0) return;
            l.Add(title);
            foreach (var o in top) l.Add($"  {o.Total / frames,8:0.000} {unit}  {o.Name} ({o.Max:0.0})");
            l.Add("");
        }

        private static string Minutes(double minutes) => minutes >= 60 ? $"{(int)(minutes / 60)} h {minutes % 60:00} min" : $"{minutes:0} min";
    }
}
