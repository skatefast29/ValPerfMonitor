using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ValPerfMonitor
{
    /// <summary>Builds the per-capture report.html, the runs log, and the cross-run compare.html.</summary>
    internal static class Report
    {
        // ---- Data -------------------------------------------------------------------------------

        public sealed class Summary
        {
            public double Fps, Fps1Low, P50, P95, P99, Max, RawAvg, Monitor, Overhead, Mods, Game, GcPerMin;
        }

        public static Summary Summarize(Stats s)
        {
            long n = Math.Max(1, s.TotFrames);
            double avg = s.TotCorrSum / n;
            double secs = Math.Max(0.001, s.TotRawSum / 1000.0);
            return new Summary
            {
                Fps = avg > 0 ? 1000 / avg : 0,
                Fps1Low = 1000 / Math.Max(0.01, Stats.WorstAverage(s.FrameTimes, 1)),
                P50 = Stats.Percentile(s.FrameTimes, 50), P95 = Stats.Percentile(s.FrameTimes, 95),
                P99 = Stats.Percentile(s.FrameTimes, 99), Max = Stats.Percentile(s.FrameTimes, 100),
                RawAvg = s.TotRawSum / n, Monitor = s.TotMonitorSum / n, Overhead = s.TotOverheadSum / n,
                Mods = s.TotModsSum / n, Game = s.TotGameSum / n, GcPerMin = s.TotGcFrames / secs * 60,
            };
        }

        public static string CaptureJson(Capture c, string sessionName, ModRegistry reg, Instrumenter ins, UnityCounters counters, GameContext ctx)
        {
            var s = c.Stats;
            var sum = Summarize(s);
            var j = new StringBuilder(64 * 1024);
            j.Append('{');
            P(j, "session", sessionName); P(j, "capture", c.Number); P(j, "mode", c.Mode.ToString()); P(j, "unit", s.Unit); P(j, "label", c.Label);
            P(j, "started", c.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            P(j, "measuredSeconds", c.MeasuredSeconds); P(j, "frames", s.TotFrames); P(j, "excluded", s.TotExcluded);
            P(j, "fps", sum.Fps); P(j, "fps1low", sum.Fps1Low); P(j, "p50", sum.P50); P(j, "p95", sum.P95); P(j, "p99", sum.P99); P(j, "max", sum.Max);
            P(j, "rawAvg", sum.RawAvg); P(j, "monitorMs", sum.Monitor); P(j, "overheadMs", sum.Overhead);
            P(j, "mods", c.Mode == CaptureMode.Baseline ? double.NaN : sum.Mods); P(j, "game", c.Mode == CaptureMode.Baseline ? double.NaN : sum.Game);
            P(j, "gcPerMin", sum.GcPerMin); P(j, "spikeCount", s.SpikeCount); P(j, "traces", c.TracesWritten);
            P(j, "allocMethod", AllocCounter.Method); P(j, "allocExact", AllocCounter.Exact ? 1 : 0); P(j, "garbageNote", GarbageConfidence.Explain);
            P(j, "overheadUs", c.Mode == CaptureMode.Baseline ? 0 : ins.CallOverheadTicks[(int)c.Mode] * 1e6 / Stopwatch.Frequency);
            P(j, "measuredMethods", c.Mode == CaptureMode.Baseline ? 0 : ins.Instrumented);
            P(j, "probeBytesPerCall", AllocCounter.Exact && c.Mode != CaptureMode.Baseline ? ins.ChildOverhead[(int)CaptureMode.Allocations] : double.NaN);
            P(j, "context", ctx.Short);
            P(j, "limiter", Plugin.FrameLimiter());
            var log = LogWatch.ForCapture(c, reg);
            P(j, "logErrors", log.errors); P(j, "logWarnings", log.warnings); P(j, "logTop", log.top);
            j.Append("\"counters\":{");
            bool first = true;
            foreach (var cn in counters.Counters)
            {
                string v = UnityCounters.Avg(cn, false);
                if (v.Length == 0) continue;
                if (!first) j.Append(','); first = false;
                j.Append(Csv.Json(cn.Column)).Append(':').Append(v);
            }
            foreach (var (name, idx) in new[] { ("cpu_main_ms", 0), ("cpu_render_ms", 1), ("gpu_ms", 2), ("present_wait_ms", 3) })
            {
                string v = counters.Timing(false, idx);
                if (v.Length == 0) continue;
                if (!first) j.Append(','); first = false;
                j.Append(Csv.Json(name)).Append(':').Append(v);
            }
            j.Append("},");

            j.Append("\"points\":[");
            for (int i = 0; i < c.Points.Count; i++)
            {
                var p = c.Points[i];
                if (i > 0) j.Append(',');
                j.Append('[').Append(Csv.JNum(p.T, "0.0")).Append(',').Append(Csv.JNum(p.Fps, "0.0")).Append(',').Append(Csv.JNum(p.FrameAvg, "0.00")).Append(',')
                 .Append(Csv.JNum(p.P99, "0.00")).Append(',').Append(Csv.JNum(p.Mods, "0.000")).Append(',').Append(Csv.JNum(p.Game, "0.000")).Append(',')
                 .Append(p.Gc).Append(',').Append(Csv.JNum(p.HeapMb, "0")).Append(',').Append(Csv.Json(p.Draw)).Append(',').Append(Csv.Json(p.Gpu)).Append(',')
                 .Append(Csv.Json(p.Context)).Append(']');
            }
            j.Append("],");

            j.Append("\"owners\":[");
            first = true;
            if (c.Mode != CaptureMode.Baseline)
            {
                foreach (var r in c.OwnerRows())
                {
                    if (!first) j.Append(','); first = false;
                    j.Append("{\"n\":").Append(Csv.Json(r.Mod.Name)).Append(",\"k\":").Append(Csv.Json(r.Mod.Kind))
                     .Append(",\"avg\":").Append(Csv.JNum(r.Avg)).Append(",\"pct\":").Append(Csv.JNum(r.Pct, "0.00"))
                     .Append(",\"max\":").Append(Csv.JNum(r.Max)).Append(",\"calls\":").Append(Csv.JNum(r.CallsPerFrame, "0.0"))
                     .Append(",\"patches\":").Append(r.Mod.PatchCount).Append(",\"transpilers\":").Append(r.Mod.Transpilers)
                     .Append(",\"top\":").Append(Csv.Json(r.Top == null ? "" : $"{r.Top.Kind} {r.Top.Name}"))
                     .Append(",\"sure\":").Append(Csv.Json(GarbageConfidence.ForMod(c.Mode, reg.Sources.Where(x => x != null && x.Mod == r.Mod), r.Avg))).Append('}');
                }
            }
            j.Append("],");

            j.Append("\"methods\":[");
            first = true;
            if (c.Mode != CaptureMode.Baseline)
            {
                long frames = Math.Max(1, s.TotFrames);
                bool alloc = c.Mode == CaptureMode.Allocations;
                double perCallScale = alloc ? 1 : 1e6 / Stopwatch.Frequency;
                foreach (var x in reg.Sources.Where(x => x != null && !x.Mod.Hidden && Probe.SourceCalls[x.Id] > 0)
                                             .OrderByDescending(x => Probe.SourceSelf[x.Id]).Take(400))
                {
                    long calls = Probe.SourceCalls[x.Id];
                    long self = Math.Max(0, Probe.SourceSelf[x.Id]);
                    if (!first) j.Append(','); first = false;
                    j.Append("{\"o\":").Append(Csv.Json(x.Mod.Name)).Append(",\"k\":").Append(Csv.Json(x.Mod.Kind)).Append(",\"h\":").Append(Csv.Json(x.Kind))
                     .Append(",\"m\":").Append(Csv.Json(x.Name)).Append(",\"t\":").Append(Csv.Json(x.Targets.Count == 0 ? "" : x.Targets[0] + (x.Targets.Count > 1 ? $" (+{x.Targets.Count - 1})" : "")))
                     .Append(",\"calls\":").Append(Csv.JNum((double)calls / frames, "0.00"))
                     .Append(",\"avg\":").Append(Csv.JNum(self * s.ValuePerRaw / frames))
                     .Append(",\"per\":").Append(Csv.JNum(self * perCallScale / calls, "0.00"))
                     .Append(",\"max\":").Append(Csv.JNum(Probe.SourceMax[x.Id] * perCallScale, "0.0"))
                     .Append(",\"sure\":").Append(Csv.Json(GarbageConfidence.ForSource(c.Mode, x.Id, self * s.ValuePerRaw / frames))).Append('}');
                }
            }
            j.Append("],");

            j.Append("\"spikes\":[");
            for (int i = 0; i < c.AllSpikes.Count; i++)
            {
                var r = c.AllSpikes[i];
                if (i > 0) j.Append(',');
                j.Append('[').Append(Csv.JNum(r.SessionSeconds, "0.00")).Append(',').Append(Csv.JNum(r.CorrectedMs, "0.0")).Append(',').Append(r.Gc ? 1 : 0).Append(',')
                 .Append(Csv.Json(r.Owner1)).Append(',').Append(Csv.JNum(r.Value1, "0.00")).Append(',')
                 .Append(Csv.Json(r.Owner2)).Append(',').Append(Csv.JNum(r.Value2, "0.00")).Append(',')
                 .Append(Csv.Json(r.Owner3)).Append(',').Append(Csv.JNum(r.Value3, "0.00")).Append(',')
                 .Append(Csv.Json(r.Context)).Append(',').Append(Csv.Json(r.TraceFile ?? "")).Append(',').Append(Csv.Json(r.Cause ?? "")).Append(']');
            }
            j.Append("],");

            j.Append("\"causes\":[");
            first = true;
            foreach (var kv in s.CauseCounts.OrderByDescending(kv => kv.Value))
            {
                if (!first) j.Append(','); first = false;
                j.Append('[').Append(Csv.Json(kv.Key)).Append(',').Append(kv.Value).Append(']');
            }
            j.Append("],");

            var net = c.Net;
            double secs = Math.Max(0.001, c.MeasuredSeconds);
            P(j, "netOutKbs", net.TotSamples > 0 ? net.TotOutBytesSec / net.TotSamples / 1024 : double.NaN);
            P(j, "netInKbs", net.TotSamples > 0 ? net.TotInBytesSec / net.TotSamples / 1024 : double.NaN);
            P(j, "netPeers", net.Peers);
            j.Append("\"network\":[");
            first = true;
            foreach (var e in net.Top(200))
            {
                if (!first) j.Append(','); first = false;
                j.Append("{\"k\":").Append(Csv.Json(e.Kind)).Append(",\"n\":").Append(Csv.Json(e.Name)).Append(",\"o\":").Append(Csv.Json(e.Owner))
                 .Append(",\"s\":").Append(Csv.JNum(e.SentCalls / secs, "0.00")).Append(",\"sb\":").Append(Csv.JNum(e.SentBytes / secs / 1024, "0.000"))
                 .Append(",\"r\":").Append(Csv.JNum(e.RecvCalls / secs, "0.00")).Append(",\"rb\":").Append(Csv.JNum(e.RecvBytes / secs / 1024, "0.000")).Append('}');
            }
            j.Append("],");

            j.Append("\"plugins\":[");
            first = true;
            foreach (var m in reg.Visible.Where(m => m.IsPlugin).OrderBy(m => m.Name))
            {
                if (!first) j.Append(','); first = false;
                j.Append(Csv.Json(m.Name + " " + m.Version));
            }
            j.Append("]}");
            return j.ToString();
        }

        private static void P(StringBuilder j, string k, string v) => j.Append(Csv.Json(k)).Append(':').Append(Csv.Json(v)).Append(',');
        private static void P(StringBuilder j, string k, double v) => j.Append(Csv.Json(k)).Append(':').Append(Csv.JNum(v)).Append(',');
        private static void P(StringBuilder j, string k, long v) => j.Append(Csv.Json(k)).Append(':').Append(v.ToString(CultureInfo.InvariantCulture)).Append(',');

        /// <summary>One line for runs.csv (all captures from all sessions, for Excel).</summary>
        public static string RunCsvHeader =>
            "started,session,capture,mode,label,measured_s,frames,excluded_frames,fps,fps_1pct_low,frame_ms_p50,frame_ms_p95,frame_ms_p99,frame_ms_max," +
            "mods_per_frame,game_per_frame,unit,monitor_ms_per_frame,probe_overhead_ms_per_frame,gc_per_min,spikes,context,plugin_count,top_owners,folder,game_version,mods_fingerprint\n";

        public static string RunCsvLine(Capture c, string sessionName, ModRegistry reg, GameContext ctx, string folderRel)
        {
            var s = Summarize(c.Stats);
            bool b = c.Mode == CaptureMode.Baseline;
            string top = b ? "" : string.Join("; ", c.OwnerRows().Take(5).Select(r => $"{r.Mod.Name} {r.Avg:0.000}"));
            return string.Join(",",
                c.Started.ToString("yyyy-MM-dd HH:mm:ss", Csv.Inv), Csv.Q(sessionName), c.Number.ToString(Csv.Inv), c.Mode.ToString(), Csv.Q(c.Label),
                Csv.F(c.MeasuredSeconds, "0"), c.Stats.TotFrames.ToString(Csv.Inv), c.Stats.TotExcluded.ToString(Csv.Inv),
                Csv.F(s.Fps, "0.0"), Csv.F(s.Fps1Low, "0.0"), Csv.F(s.P50, "0.00"), Csv.F(s.P95, "0.00"), Csv.F(s.P99, "0.00"), Csv.F(s.Max, "0.0"),
                b ? "" : Csv.F(s.Mods, "0.000"), b ? "" : Csv.F(s.Game, "0.000"), c.Stats.Unit,
                Csv.F(s.Monitor, "0.000"), Csv.F(s.Overhead, "0.000"), Csv.F(s.GcPerMin, "0.0"), c.Stats.SpikeCount.ToString(Csv.Inv),
                Csv.Q(ctx.Short), reg.Visible.Count(m => m.IsPlugin).ToString(Csv.Inv), Csv.Q(top), Csv.Q(folderRel),
                Csv.Q(RunStamp.GameVersion), Csv.Q(RunStamp.ModsFingerprint(reg))) + "\n";
        }

        // ---- HTML -------------------------------------------------------------------------------

        private const string Css = @"
:root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--muted:#677084;--line:#e3e6ec;--accent:#e8731a;--game:#3b82c4;--good:#2f9e5b;--bad:#d64545;--warn:#c99a00}
@media (prefers-color-scheme:dark){:root:not([data-theme=light]){--bg:#121419;--card:#1b1e25;--ink:#e6e8ee;--muted:#9aa2b3;--line:#2c313c;--accent:#ff8c3a;--game:#5aa7ea;--good:#4cc27c;--bad:#ff6b6b;--warn:#e6c14d}}
:root[data-theme=dark]{--bg:#121419;--card:#1b1e25;--ink:#e6e8ee;--muted:#9aa2b3;--line:#2c313c;--accent:#ff8c3a;--game:#5aa7ea;--good:#4cc27c;--bad:#ff6b6b;--warn:#e6c14d}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.45 system-ui,Segoe UI,sans-serif}
main{max-width:1180px;margin:0 auto;padding:20px 16px 60px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 10px}
.sub{color:var(--muted);margin-bottom:16px}.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 12px}.card b{display:block;font-size:20px}.card span{color:var(--muted);font-size:12px}
.panel{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px;overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:13px}th,td{padding:5px 8px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}
th{cursor:pointer;color:var(--muted);font-weight:600;position:sticky;top:0;background:var(--card)}td.l,th.l{text-align:left}td.wrap{white-space:normal;text-align:left}
.bar{height:8px;border-radius:4px;background:var(--accent);display:inline-block;vertical-align:middle}.game .bar{background:var(--game)}
.tag{font-size:11px;padding:1px 6px;border-radius:8px;border:1px solid var(--line);color:var(--muted)}
.note{color:var(--muted);font-size:12.5px}.tabs button{background:var(--card);color:var(--ink);border:1px solid var(--line);border-radius:6px;padding:4px 10px;margin-right:6px;cursor:pointer}
.tabs button.on{border-color:var(--accent);color:var(--accent)}svg text{fill:var(--muted);font-size:11px}input[type=search]{background:var(--card);color:var(--ink);border:1px solid var(--line);border-radius:6px;padding:4px 8px}
.pos{color:var(--good)}.neg{color:var(--bad)}a{color:var(--accent)}";

        private const string Js = @"
const D=JSON.parse(document.getElementById('data').textContent);
const $=s=>document.querySelector(s);const f=(v,d=2)=>v==null||isNaN(v)?'–':(+v).toFixed(d);
function esc(s){return String(s??'').replace(/[&<>""]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c]))}
function table(el,cols,rows,sortKey){let key=sortKey,asc=false;const draw=()=>{const r=[...rows].sort((a,b)=>{const x=a[key],y=b[key];return (typeof x==='string'?x.localeCompare(y):(x??-1e99)-(y??-1e99))*(asc?1:-1)});
el.innerHTML='<table><thead><tr>'+cols.map(c=>`<th class=""${c.l?'l':''}"" data-k=""${c.k}"">${c.t}${c.k===key?(asc?' ▴':' ▾'):''}</th>`).join('')+'</tr></thead><tbody>'+
r.map(x=>'<tr class=""'+(x.k==='game'?'game':'')+'"">'+cols.map(c=>`<td class=""${c.l?'l':''} ${c.w?'wrap':''}"">${c.f?c.f(x):esc(x[c.k])}</td>`).join('')+'</tr>').join('')+'</tbody></table>';
el.querySelectorAll('th').forEach(th=>th.onclick=()=>{const k=th.dataset.k;if(k===key)asc=!asc;else{key=k;asc=false}draw()})};draw()}
function chart(el,series,opts){const W=el.clientWidth||900,H=opts.h||200,pl=44,pr=10,pt=10,pb=24;const pts=D.points;if(pts.length<2){el.innerHTML='<p class=note>Not enough intervals yet.</p>';return}
const xs=pts.map(p=>p[0]),x0=Math.min(...xs),x1=Math.max(...xs);let ymax=0;series.forEach(s=>pts.forEach(p=>{const v=+s.get(p);if(!isNaN(v))ymax=Math.max(ymax,v)}));ymax=ymax*1.1||1;
const X=x=>pl+(x-x0)/(x1-x0||1)*(W-pl-pr),Y=y=>pt+(1-y/ymax)*(H-pt-pb);let g=`<svg width=""${W}"" height=""${H}"">`;
for(let i=0;i<=4;i++){const v=ymax*i/4;g+=`<line x1=""${pl}"" x2=""${W-pr}"" y1=""${Y(v)}"" y2=""${Y(v)}"" stroke=""var(--line)""/><text x=""4"" y=""${Y(v)+4}"">${v.toFixed(v<10?1:0)}</text>`}
(opts.guides||[]).forEach(gd=>{if(gd.v<ymax)g+=`<line x1=""${pl}"" x2=""${W-pr}"" y1=""${Y(gd.v)}"" y2=""${Y(gd.v)}"" stroke=""${gd.c}"" stroke-dasharray=""4 4""/><text x=""${W-pr-60}"" y=""${Y(gd.v)-3}"">${gd.t}</text>`});
series.forEach(s=>{const d=pts.map((p,i)=>{const v=+s.get(p);return isNaN(v)?'':(i?'L':'M')+X(p[0]).toFixed(1)+' '+Y(v).toFixed(1)}).join(' ');g+=`<path d=""${d}"" fill=""none"" stroke=""${s.c}"" stroke-width=""2""/>`});
g+=`<text x=""${pl}"" y=""${H-6}"">${x0.toFixed(0)}s</text><text x=""${W-pr-40}"" y=""${H-6}"">${x1.toFixed(0)}s</text>`;
g+=series.map((s,i)=>`<rect x=""${pl+8+i*130}"" y=""${pt}"" width=""10"" height=""10"" fill=""${s.c}""/><text x=""${pl+22+i*130}"" y=""${pt+9}"">${s.n}</text>`).join('');el.innerHTML=g+'</svg>'}
";

        public static string CaptureHtml(string json, string title)
        {
            var sb = new StringBuilder(json.Length + 16000);
            sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>")
              .Append(System.Net.WebUtility.HtmlEncode(title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>")
              .Append("<h1 id=title></h1><div class=sub id=sub></div><div class=cards id=cards></div>")
              .Append("<h2>Over time</h2><div class=panel><div id=c1></div><div id=c2></div></div>")
              .Append("<h2 id=ownersH>Mods and game systems</h2><div class=tabs id=tabs></div><p class=note id=ownersNote></p><div class=panel id=owners></div>")
              .Append("<h2>Heaviest methods</h2><p><input type=search id=q placeholder='filter by mod or method'></p><div class=panel id=methods style='max-height:520px'></div>")
              .Append("<h2>Slow frames</h2><p class=note id=spikesNote></p><div class=panel id=spikes style='max-height:420px'></div>")
              .Append("<h2>Network messages</h2><p class=note id=netNote></p><div class=panel id=net style='max-height:420px'></div>")
              .Append("<h2>How to read this</h2><div class='panel note' id=notes></div>")
              .Append("<script id=data type=application/json>").Append(json).Append("</script><script>").Append(Js).Append(@"
const U=D.unit, alloc=D.mode==='Allocations', base=D.mode==='Baseline';
$('#title').textContent=`${D.mode} capture ${D.capture}${D.label?' – '+D.label:''}`;
$('#sub').textContent=`${D.started} · ${f(D.measuredSeconds,0)} s measured · ${D.frames} frames counted, ${D.excluded} excluded · ${D.context}`;
const cards=[[f(D.fps,1),'FPS (monitor removed)'],[f(D.fps1low,1),'1% low FPS'],[f(D.p99,1)+' ms','99th pct frame'],[f(D.max,0)+' ms','worst frame']];
if(!base){cards.push([f(D.mods,alloc?1:2)+' '+U,(alloc?'mods alloc / frame':'mods / frame')]);cards.push([f(D.game,alloc?1:2)+' '+U,(alloc?'game alloc / frame':'game systems / frame')])}
cards.push([f(D.gcPerMin,1),'GCs per minute']);cards.push([f(D.monitorMs+D.overheadMs,3)+' ms','monitor cost / frame (removed)']);
for(const k in D.counters)cards.push([D.counters[k],k.replace(/_/g,' ')]);
if(D.logErrors!=null)cards.push([D.logErrors+(D.logWarnings?' <small>+'+D.logWarnings+' warn.</small>':''),'log errors'+(D.logTop?' (most: '+esc(D.logTop)+')':'')]);
$('#cards').innerHTML=cards.map(c=>`<div class=card><b>${c[0]}</b><span>${c[1]}</span></div>`).join('');
chart($('#c1'),[{n:'FPS',c:'var(--good)',get:p=>p[1]},{n:'99th pct ms',c:'var(--bad)',get:p=>p[3]}],{h:200,guides:[{v:60,c:'var(--good)',t:'60'},{v:30,c:'var(--warn)',t:'30'}]});
if(!base)chart($('#c2'),[{n:'mods '+U,c:'var(--accent)',get:p=>p[4]},{n:'game '+U,c:'var(--game)',get:p=>p[5]}],{h:180});
let view='mod';const tabs=[['mod','Mods'],['game','Game systems'],['all','All']];
function drawOwners(){$('#tabs').innerHTML=tabs.map(t=>`<button class=""${t[0]===view?'on':''}"" data-v=""${t[0]}"">${t[1]}</button>`).join('');
$('#tabs').querySelectorAll('button').forEach(b=>b.onclick=()=>{view=b.dataset.v;drawOwners()});
const rows=D.owners.filter(o=>view==='all'||(view==='mod'?o.k!=='game':o.k==='game'));const mx=Math.max(...rows.map(r=>r.avg),1e-9);
table($('#owners'),[{t:'',k:'avg',f:x=>`<span class=bar style=""width:${Math.max(2,x.avg/mx*120)}px""></span>`},{t:'Name',k:'n',l:1},{t:'Kind',k:'k',f:x=>`<span class=tag>${x.k}</span>`},
{t:`Avg ${U}/frame`,k:'avg',f:x=>f(x.avg,alloc?2:3)},...(alloc?[]:[{t:'% frame',k:'pct',f:x=>f(x.pct,1)}]),{t:`Worst frame ${U}`,k:'max',f:x=>f(x.max,2)},
{t:'Calls/frame',k:'calls',f:x=>f(x.calls,0)},{t:'Patches',k:'patches'},{t:'Heaviest method',k:'top',l:1},...(alloc?[{t:'How sure',k:'sure',l:1}]:[])],rows,'avg')}
if(base){$('#ownersH').textContent='No per-mod data in Baseline';$('#ownersNote').textContent='Baseline has no hooks attached on purpose: it is the reference for how fast the game runs with nothing measured. Compare it with Timing captures in compare.html.';}
else{$('#ownersNote').textContent=alloc?'Managed memory each one allocates per frame. Allocation drives garbage collection, the usual cause of periodic stutter. Counter: '+D.allocMethod+'. '+D.garbageNote:'Self time: time inside a mod method or game system, excluding anything it called that is measured separately, and excluding this monitor\'s overhead.';drawOwners()}
const mrows=D.methods;function drawMethods(){const q=$('#q').value.toLowerCase();const r=mrows.filter(m=>!q||(m.o+' '+m.m+' '+m.t).toLowerCase().includes(q));
table($('#methods'),[{t:'Owner',k:'o',l:1},{t:'Hook',k:'h',l:1},{t:'Method',k:'m',l:1},{t:'Attached to',k:'t',l:1},{t:`Avg ${U}/frame`,k:'avg',f:x=>f(x.avg,alloc?2:4)},
{t:'Calls/frame',k:'calls',f:x=>f(x.calls,1)},{t:alloc?'Bytes/call':'µs/call',k:'per',f:x=>f(x.per,alloc?0:2)},{t:alloc?'Max bytes':'Max µs',k:'max',f:x=>f(x.max,0)},...(alloc?[{t:'How sure',k:'sure',l:1}]:[])],r,'avg')}
if(!base){$('#q').oninput=drawMethods;drawMethods()}else $('#q').parentElement.style.display='none';
const sp=D.spikes.map(s=>({t:s[0],ms:s[1],gc:s[2],o1:s[3]?`${s[3]} (${f(s[4],2)})`:'',o2:s[5]?`${s[5]} (${f(s[6],2)})`:'',o3:s[7]?`${s[7]} (${f(s[8],2)})`:'',ctx:s[9],tr:s[10],cause:s[11]||''}));
$('#spikesNote').textContent=`${D.spikeCount} frames over the spike threshold${sp.length<D.spikeCount?' (first '+sp.length+' listed)':''}. GC = a garbage collection finished in that frame, which usually explains the spike by itself.`+(D.causes&&D.causes.length?' Main causes: '+D.causes.map(c=>c[1]+' × '+c[0]).join(', ')+'.':'')+(D.traces?` ${D.traces} flame graphs saved: open them at speedscope.app (drag the file in).`:'');
table($('#spikes'),[{t:'Time s',k:'t',f:x=>f(x.t,1)},{t:'Frame ms',k:'ms',f:x=>f(x.ms,1)},{t:'GC',k:'gc',f:x=>x.gc?'GC':''},{t:'Likely cause',k:'cause',l:1,w:1},{t:'Top owner',k:'o1',l:1},{t:'2nd',k:'o2',l:1},{t:'3rd',k:'o3',l:1},
{t:'Where',k:'ctx',l:1},{t:'Trace',k:'tr',l:1,f:x=>x.tr?`<a href=""${esc(x.tr)}"">flame graph</a>`:''}],sp,'ms');
const nt=(D.network||[]).map(e=>Object.assign({tot:e.s+e.r},e));
$('#netNote').textContent=(base?'Baseline has no hooks, so messages are not counted; only the connection totals are. ':'')+(D.netPeers===0||D.netOutKbs===null?'No connection to other players or a server in this capture (single player): all messages below stay on this computer, but they still cost CPU. ':`Connection: ${f(D.netOutKbs,2)} KB/s out, ${f(D.netInKbs,2)} KB/s in. `)+'Routed = RPCs mods and the game send between players (including to themselves); peer = what actually crosses the connection (ZDOData = world object sync). Owner = the mod whose code handles the message.';
if(nt.length)table($('#net'),[{t:'Kind',k:'k',l:1},{t:'Message',k:'n',l:1},{t:'Owner',k:'o',l:1},{t:'Sent /s',k:'s',f:x=>f(x.s,2)},{t:'Sent KB/s',k:'sb',f:x=>f(x.sb,3)},{t:'Handled /s',k:'r',f:x=>f(x.r,2)},{t:'Received KB/s',k:'rb',f:x=>f(x.rb,3)}],nt,'tot');
else $('#net').innerHTML='<p class=note>No messages counted.</p>';
$('#notes').innerHTML=(D.limiter&&D.limiter!=='uncapped'?`<p><b>Frame limiter: ${esc(D.limiter)}.</b> While the game sits at its cap, frames finish early and wait, so FPS can't show CPU savings. Compare the mods/game ms per frame between runs instead, or set the FPS limit to unlimited and vsync off for testing.</p>`:'')+`<p><b>FPS and frame times exclude this monitor.</b> Its own work (${f(D.monitorMs,3)} ms/frame) and the measured cost of its probes (${f(D.overheadMs,3)} ms/frame, ${f(D.overheadUs,3)} µs per call) are subtracted. Frames where it attached hooks, wrote reports, or was warming up are left out (${D.excluded} frames).</p>
<p><b>Per-owner numbers are self cost</b> with the probe overhead of nested calls removed, so one mod is never charged for another mod's or the monitor's work. Harmony's own call plumbing (a few ns per patched call) counts toward the method that made the call.</p>
${D.probeBytesPerCall>8?`<p><b>Note:</b> on this runtime each probe call allocates about ${f(D.probeBytesPerCall,0)} bytes. That is removed from the numbers above, but it adds garbage-collection work, so take GC rates and GC spikes from a Baseline capture.</p>`:''}
<p>This monitor allocated ${f(D.monitorKbPerMin,1)} KB per minute for its own bookkeeping (overlay, logs).</p>
<p><b>Not measured:</b> transpiler edits (counted, not timed), background threads, coroutines, and GPU cost. Render counters above (draw calls, triangles, GPU time when available) cover the graphics side.</p>
<p>Mods in this run: ${D.plugins.length}. ${esc(D.plugins.join(', '))}</p>`;
</script></main></body></html>");
            return sb.ToString();
        }

        public static string CompareHtml(IEnumerable<string> runJsonLines)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Run Comparison</title><style>")
              .Append(Css).Append("</style></head><body><main><h1>Run comparison</h1><div class=sub>Every capture you have recorded. Pick a reference run (A) and a run to compare (B).</div>")
              .Append("<div class=panel id=runs style='max-height:460px'></div><h2 id=cmpH>Comparison</h2><div class=cards id=cmpCards></div>")
              .Append("<h2>Mods added or removed</h2><div class='panel note' id=diff></div><h2>Per mod / game system</h2><p class=note id=ownNote></p><div class=panel id=own></div>")
              .Append("<script id=data type=application/json>[").Append(string.Join(",", runJsonLines)).Append("]</script><script>")
              .Append(Js.Replace("const D=JSON.parse(document.getElementById('data').textContent);", "const RUNS=JSON.parse(document.getElementById('data').textContent);const D={points:[]};"))
              .Append(@"
RUNS.forEach((r,i)=>r.i=i);let A=RUNS.findIndex(r=>r.mode==='Baseline');if(A<0)A=0;let B=RUNS.length-1;
function draw(){table($('#runs'),[{t:'A',k:'i',f:x=>`<input type=radio name=a ${x.i===A?'checked':''} data-i=${x.i}>`},{t:'B',k:'i',f:x=>`<input type=radio name=b ${x.i===B?'checked':''} data-i=${x.i}>`},
{t:'Started',k:'started',l:1},{t:'Mode',k:'mode',l:1},{t:'Label',k:'label',l:1},{t:'Secs',k:'measuredSeconds',f:x=>f(x.measuredSeconds,0)},{t:'FPS',k:'fps',f:x=>f(x.fps,1)},{t:'1% low',k:'fps1low',f:x=>f(x.fps1low,1)},
{t:'p99 ms',k:'p99',f:x=>f(x.p99,1)},{t:'Mods',k:'mods',f:x=>x.mods==null?'–':f(x.mods,3)+' '+x.unit},{t:'Game',k:'game',f:x=>x.game==null?'–':f(x.game,3)+' '+x.unit},{t:'GC/min',k:'gcPerMin',f:x=>f(x.gcPerMin,1)},
{t:'Errors/min',k:'logErrorsPerMin',f:x=>x.logErrorsPerMin==null?'–':`<span title=""${esc(x.logTop?'most: '+x.logTop:'')}"">${f(x.logErrorsPerMin,1)}</span>`},{t:'Plugins',k:'n',f:x=>x.plugins.length},{t:'Valheim',k:'gameVersion',l:1,f:x=>esc(x.gameVersion||'?')},{t:'Where',k:'context',l:1},{t:'',k:'folder',f:x=>`<a href=""${esc(x.folder)}/report.html"">report</a>`}],RUNS,'started');
document.querySelectorAll('input[name=a]').forEach(e=>e.onchange=()=>{A=+e.dataset.i;draw()});document.querySelectorAll('input[name=b]').forEach(e=>e.onchange=()=>{B=+e.dataset.i;draw()});cmp()}
function d(a,b,dec,higherBetter){if(a==null||b==null||isNaN(a)||isNaN(b))return '–';const x=b-a;const good=higherBetter?x>0:x<0;return `<span class=""${Math.abs(x)<1e-9?'':good?'pos':'neg'}"">${x>0?'+':''}${x.toFixed(dec)}</span>`}
function cmp(){const a=RUNS[A],b=RUNS[B];if(!a||!b)return;$('#cmpH').textContent=`A: ${a.mode} ${a.label||''} (${a.started})  →  B: ${b.mode} ${b.label||''} (${b.started})`;
$('#cmpCards').innerHTML=[['FPS',a.fps,b.fps,1,1],['1% low FPS',a.fps1low,b.fps1low,1,1],['p99 frame ms',a.p99,b.p99,1,0],['GC / min',a.gcPerMin,b.gcPerMin,1,0],['Log errors / min',a.logErrorsPerMin,b.logErrorsPerMin,1,0]]
.map(c=>`<div class=card><b>${f(c[2],c[3])} <small>${d(c[1],c[2],c[3],c[4])}</small></b><span>${c[0]} (A ${f(c[1],c[3])})</span></div>`).join('');
const pa=new Set(a.plugins),pb=new Set(b.plugins);const rem=[...pa].filter(x=>!pb.has(x)),add=[...pb].filter(x=>!pa.has(x));
$('#diff').innerHTML=((a.gameVersion||'?')!==(b.gameVersion||'?')?`<p><b>Different game version:</b> A ${esc(a.gameVersion||'unknown')}, B ${esc(b.gameVersion||'unknown')}. Differences may come from the game update.</p>`:'')+(rem.length?`<p><b>Only in A:</b> ${esc(rem.join(', '))}</p>`:'')+(add.length?`<p><b>Only in B:</b> ${esc(add.join(', '))}</p>`:'')+(!rem.length&&!add.length?'<p>Same mod list in both runs.</p>':'');
if(a.mode!==b.mode||a.mode==='Baseline'){$('#ownNote').textContent='Per-mod comparison needs two runs of the same measuring mode (e.g. Timing vs Timing).';$('#own').innerHTML='';return}
$('#ownNote').textContent=`Avg ${a.unit} per frame. Negative = cheaper in B.`;const m=new Map();a.owners.forEach(o=>m.set(o.n,{n:o.n,k:o.k,a:o.avg,b:null}));b.owners.forEach(o=>{const e=m.get(o.n)||{n:o.n,k:o.k,a:null};e.b=o.avg;m.set(o.n,e)});
const rows=[...m.values()].map(r=>({...r,dl:(r.b??0)-(r.a??0)}));table($('#own'),[{t:'Name',k:'n',l:1},{t:'Kind',k:'k',f:x=>`<span class=tag>${x.k}</span>`},{t:'A',k:'a',f:x=>f(x.a,3)},{t:'B',k:'b',f:x=>f(x.b,3)},
{t:'Change',k:'dl',f:x=>d(x.a??0,x.b??0,3,0)}],rows,'a')}
draw();</script></main></body></html>");
            return sb.ToString();
        }

        /// <summary>Compact record of a capture for compare.html (one JSON line in runs.jsonl).</summary>
        public static string RunJson(Capture c, string sessionName, ModRegistry reg, GameContext ctx, string folderRel)
        {
            var s = Summarize(c.Stats);
            bool b = c.Mode == CaptureMode.Baseline;
            var j = new StringBuilder();
            j.Append('{');
            P(j, "started", c.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)); P(j, "session", sessionName); P(j, "capture", c.Number);
            P(j, "mode", c.Mode.ToString()); P(j, "label", c.Label); P(j, "unit", c.Stats.Unit); P(j, "measuredSeconds", c.MeasuredSeconds);
            P(j, "fps", s.Fps); P(j, "fps1low", s.Fps1Low); P(j, "p99", s.P99); P(j, "gcPerMin", s.GcPerMin);
            var log = LogWatch.ForCapture(c, reg);
            P(j, "logErrorsPerMin", log.perMin); P(j, "logTop", log.top);
            P(j, "mods", b ? double.NaN : s.Mods); P(j, "game", b ? double.NaN : s.Game);
            P(j, "context", ctx.Short); P(j, "folder", folderRel.Replace('\\', '/'));
            P(j, "gameVersion", RunStamp.GameVersion); P(j, "modsFingerprint", RunStamp.ModsFingerprint(reg));
            j.Append("\"plugins\":[").Append(string.Join(",", reg.Visible.Where(m => m.IsPlugin).OrderBy(m => m.Name).Select(m => Csv.Json(m.Name + " " + m.Version)))).Append("],");
            j.Append("\"owners\":[");
            if (!b)
                j.Append(string.Join(",", c.OwnerRows().Where(r => r.Avg > 0).Take(150)
                    .Select(r => "{\"n\":" + Csv.Json(r.Mod.Name) + ",\"k\":" + Csv.Json(r.Mod.Kind) + ",\"avg\":" + Csv.JNum(r.Avg) + "}")));
            j.Append("]}");
            return j.ToString();
        }
    }
}
