using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// The test panel (F11: frees the mouse, blocks game input, never counted in results) and the small
    /// live HUD shown while playing. Layout of the live view borrows from RimWorld's Dub's Performance
    /// Analyzer and ProfilerValheim: a frame-time graph with 60/30 FPS lines and a ranked table.
    /// Table data is rebuilt twice a second, not every frame; all UI time is measured and removed.
    /// </summary>
    internal sealed class Overlay
    {
        private enum SortBy { Avg, Peak, Calls, Capture }
        private enum Filter { Mods, Game, All }
        private enum Tab { Run, Live, Results, LoadMemory }
        private enum RunKind { Suite, Single, Continuous, Objects, Memory }

        private static readonly Dictionary<CaptureMode, string> ModeHelp = new Dictionary<CaptureMode, string>
        {
            [CaptureMode.Baseline] = "No hooks at all. The game's true speed with this monitor present: FPS, 1% lows, stutters, GC, draw calls. The reference everything else is compared to.",
            [CaptureMode.Timing] = "CPU time of every mod and ~200 game systems, per frame. Answers: which mod costs the most frame time?",
            [CaptureMode.Allocations] = "Memory each mod and game system allocates per frame. Allocation causes garbage collection, the usual reason for regular stutters.",
            [CaptureMode.Trace] = "Timing plus a complete call breakdown of every slow frame, saved as a flame graph (open at speedscope.app). Answers: what exactly happened in that stutter?",
        };

        private readonly Plugin _plugin;
        private Rect _panel, _hud;
        private Tab _activeTab = Tab.Run;
        private RunKind _kind = RunKind.Suite;
        private CaptureMode _mode = CaptureMode.Timing;
        private SortBy _sort = SortBy.Avg;
        private Filter _filter = Filter.Mods;
        private ModEntry _expanded;
        private List<ModEntry> _rows = new List<ModEntry>();
        private readonly List<(string label, string avg, string calls, string max)> _expandedRows = new List<(string, string, string, string)>();
        private float _nextRefresh;
        private string _header = "", _header2 = "", _status = "";
        private Vector2 _scrollLive, _scrollResults;

        private GUIStyle _statusStyle, _statusShadow;
        private GUIStyle _box, _label, _bold, _right, _small, _row, _title, _tab, _tabOn, _h2, _card, _cardOn, _big, _wrap;
        private Texture2D _white, _bg, _cardBg, _cardOnBg;
        private int _fontSize;

        private static readonly Color Green = new Color(0.35f, 0.85f, 0.4f);
        private static readonly Color Yellow = new Color(0.95f, 0.85f, 0.3f);
        private static readonly Color Orange = new Color(1f, 0.55f, 0.15f);
        private static readonly Color Blue = new Color(0.35f, 0.65f, 0.95f);
        private static readonly Color Grey = new Color(0.55f, 0.55f, 0.6f);
        private static readonly Color Red = new Color(1f, 0.3f, 0.3f);

        public bool PanelOpen;

        public Overlay(Plugin plugin)
        {
            _plugin = plugin;
            _hud = new Rect(plugin.CfgPosX.Value, plugin.CfgPosY.Value, 600, 200);
            _panel = new Rect(0, 0, 800, 200);
        }

        private Capture Cap => _plugin.Current;
        private bool Alloc => Cap?.Mode == CaptureMode.Allocations;
        private string Unit => Cap?.Stats.Unit ?? "ms";
        private float HeatScale => Alloc ? 16f : 1f; // heat thresholds: ms per frame, or KB per frame

        // ---- Data -------------------------------------------------------------------------------

        public void Refresh()
        {
            _status = StatusText();
            var cap = Cap;
            if (cap == null) { _rows.Clear(); _header = _header2 = ""; return; }
            var stats = cap.Stats;
            stats.RefreshWindow();

            IEnumerable<ModEntry> rows = _plugin.Registry.Visible.Where(m => m.WindowCalls > 0 || m.TotCalls > 0);
            if (_filter == Filter.Mods) rows = rows.Where(m => !m.IsGame);
            else if (_filter == Filter.Game) rows = rows.Where(m => m.IsGame);
            switch (_sort)
            {
                case SortBy.Peak: rows = rows.OrderByDescending(m => m.WindowMax); break;
                case SortBy.Calls: rows = rows.OrderByDescending(m => m.WindowCallsPerFrame); break;
                case SortBy.Capture: rows = rows.OrderByDescending(m => m.TotSum); break;
                default: rows = rows.OrderByDescending(m => m.WindowAvg); break;
            }
            _rows = rows.Take(Math.Max(3, _plugin.CfgRows.Value)).ToList();
            if (_expanded != null && !_rows.Contains(_expanded))
                _rows.Add(_expanded);

            float frame = stats.WindowFrameAvg;
            string split = cap.Mode == CaptureMode.Baseline ? "" :
                $"   <b>mods {stats.WindowModsAvg:0.00} {Unit}</b>   <color=#7fb4ff>game {stats.WindowGameAvg:0.00} {Unit}</color>";
            _header = $"<b>{(frame > 0 ? 1000f / frame : 0):0} FPS</b>   frame {frame:0.0} ms (worst {stats.WindowFrameMax:0.0}){split}";
            _header2 = $"capture {cap.Number} · {cap.Mode}{(cap.Label.Length > 0 ? " · " + cap.Label : "")} · {cap.MeasuredSeconds:0}s counted · {cap.Stats.TotExcluded} frames excluded";

            _expandedRows.Clear();
            if (_expanded != null && cap.Mode != CaptureMode.Baseline)
            {
                long frames = Math.Max(1, stats.TotFrames);
                double perCall = Alloc ? 1 : 1e6 / Stopwatch.Frequency;
                foreach (var s in _plugin.Registry.Sources
                             .Where(s => s != null && s.Mod == _expanded && Probe.SourceCalls[s.Id] > 0)
                             .OrderByDescending(s => Probe.SourceSelf[s.Id]).Take(8))
                {
                    double total = Math.Max(0, Probe.SourceSelf[s.Id]) * stats.ValuePerRaw;
                    string target = s.Kind == "Game" || s.Targets.Count == 0 ? "" : s.Targets.Count == 1 ? $" → {s.Targets[0]}" : $" → {s.Targets[0]} (+{s.Targets.Count - 1})";
                    _expandedRows.Add(($"{s.Kind} {s.Name}{target}", (total / frames).ToString(Alloc ? "0.00" : "0.000"),
                        ((double)Probe.SourceCalls[s.Id] / frames).ToString("0.0"), (Probe.SourceMax[s.Id] * perCall).ToString("0")));
                }
            }
        }

        private string StatusText()
        {
            var p = _plugin;
            var mc = MemoryCensus.Running;
            if (mc != null)
                return $"<color=#ffcc55>● Memory census</color>   {mc.Status}";
            if (p.Current == null)
                return "<color=#aaaaaa>■ Idle</color> – not recording. Start a test in the Run tab.";
            string state = p.Instrumenter.Pending > 0 ? $"<color=#ffcc55>● Hooking {p.Instrumenter.Pending} methods…</color>"
                : p.WarmingUp ? $"<color=#ffcc55>● Warming up {p.WarmupLeft:0}s</color> (not counted yet)"
                : "<color=#ff5555>● Recording</color>";
            string what = p.Suite != null ? p.Suite.Status(p) : ObjectCosts.Running != null ? ObjectCosts.Running.Status : p.Ab != null ? $"{p.Ab.Status} · {p.Current.MeasuredSeconds:0}/{p.Ab.Seconds:0}s"
                        : $"{p.Current.Mode}, continuous · {p.Current.MeasuredSeconds:0}s counted";
            return $"{state}   {what}";
        }

        // ---- Drawing entry ----------------------------------------------------------------------

        public void OnGUI(bool timedTestRunning)
        {
            if (_plugin.Instrumenter == null)
                return;
            EnsureStyles();
            // Only on the Layout pass: Unity lays out, then handles the event (Repaint, mouse drag...) with
            // the same content. Changing the rows in between made the panel flicker, most of all while dragging.
            if (Event.current.type == EventType.Layout && Time.unscaledTime >= _nextRefresh)
            {
                bool overlayOn = _plugin.CfgOverlay.Value != OverlayMode.Off;
                _nextRefresh = Time.unscaledTime + (PanelOpen || overlayOn ? 0.5f : 1f);
                if (PanelOpen || (overlayOn && !timedTestRunning)) Refresh();
                else _status = StatusText();
            }

            float scale = Mathf.Clamp(_plugin.CfgScale.Value, 0.5f, 3f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            if (PanelOpen)
            {
                DrawRect(new Rect(0, 0, Screen.width / scale, Screen.height / scale), new Color(0, 0, 0, 0.45f));
                if (Event.current.type == EventType.Layout) _panel.width = 800; // only when measuring, like the height below: a reset on other passes made the right edge jump
                if (Event.current.type == EventType.Layout) _panel.height = 0; // shrink to fit, but only when Unity measures; a zero height on a drag or repaint pass hid the window
                if (_panel.x == 0 && _panel.y == 0)
                {
                    _panel.x = (Screen.width / scale - _panel.width) / 2;
                    _panel.y = Mathf.Max(20, Screen.height / scale * 0.08f);
                }
                _panel = GUILayout.Window(0x4D504E, _panel, DrawPanel, GUIContent.none, _box);
            }
            else if (timedTestRunning)
            {
                // Minimal status only, so the UI doesn't influence the measurement.
                bool overlayOn = _plugin.CfgOverlay.Value != OverlayMode.Off;
                if (!ReferenceEquals(_status, _statusLineFrom) || overlayOn != _statusLineOverlay)
                {
                    _statusLineFrom = _status;
                    _statusLineOverlay = overlayOn;
                    _statusLine = new GUIContent(StripTags(_status) + "    (F11: panel)" + (overlayOn
                        ? "    (live overlay hidden during tests: untick \"show only the status line\" in F11 to keep it)" : ""));
                }
                DrawStatusLine(_statusLine);
            }
            else if (_plugin.CfgOverlay.Value != OverlayMode.Off && Cap == null)
            {
                DrawStatusLine(IdleLine);
            }
            else if (_plugin.CfgOverlay.Value != OverlayMode.Off)
            {
                if (Event.current.type == EventType.Layout) _hud.width = _plugin.CfgOverlay.Value == OverlayMode.Full ? 640 : 460;
                if (Event.current.type == EventType.Layout) _hud.height = 0;
                _hud = GUILayout.Window(0x4D504D, _hud, DrawHud, GUIContent.none, _box);
                // Save the position once the drag ends: every config set rewrites the .cfg file,
                // and doing that each frame of a drag made the window stutter.
                if (!Input.GetMouseButton(0) && ((int)_hud.x != _plugin.CfgPosX.Value || (int)_hud.y != _plugin.CfgPosY.Value))
                {
                    _plugin.CfgPosX.Value = (int)_hud.x;
                    _plugin.CfgPosY.Value = (int)_hud.y;
                }
            }

            if (!PanelOpen && Time.realtimeSinceStartup < _plugin.ToastUntil && _plugin.Toast.Length > 0)
            {
                var r = new Rect(Screen.width / scale / 2 - 300, 40, 600, 30);
                DrawRect(r, new Color(0.07f, 0.07f, 0.09f, 0.85f));
                GUI.Label(new Rect(r.x + 12, r.y + 5, r.width - 24, 22), _plugin.Toast, _label);
            }
            GUI.matrix = old;
        }

        /// <summary>The one-line status in the top left: large, on a dark backing so it reads over any scene.</summary>
        private static readonly GUIContent IdleLine = new GUIContent("Live overlay: nothing is recording. Ctrl+F11 starts a live recording, Alt+F11 turns the overlay off.");
        private GUIContent _statusLine;
        private string _statusLineFrom;
        private bool _statusLineOverlay;

        private void DrawStatusLine(GUIContent content)
        {
            Vector2 size = _statusStyle.CalcSize(content);
            var r = new Rect(10, 8, size.x + 20, size.y + 8);
            DrawRect(r, new Color(0, 0, 0, 0.7f));
            DrawRect(new Rect(r.x, r.y, 3, r.height), Orange);
            GUI.Label(new Rect(r.x + 11, r.y + 5, size.x, size.y), content, _statusShadow);
            GUI.Label(new Rect(r.x + 10, r.y + 4, size.x, size.y), content, _statusStyle);
        }

        private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");

        // ---- Panel ------------------------------------------------------------------------------

        private void DrawPanel(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>ValPerfMonitor</b>", _title);
            GUILayout.FlexibleSpace();
            GUILayout.Label("<color=#888888>game input is paused while this is open · F11 closes</color>", _small, GUILayout.Width(330));
            if (GUILayout.Button("✕", GUILayout.Width(30))) _plugin.SetPanel(false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(_status, _label);
            GUILayout.FlexibleSpace();
            bool testRunning = _plugin.Suite != null || _plugin.Ab != null || ObjectCosts.Running != null || MemoryCensus.Running != null;
            if ((Cap != null || MemoryCensus.Running != null) && GUILayout.Button(testRunning ? "■ Stop test" : "■ Stop & save", GUILayout.Width(120)))
                _plugin.StopTest();
            GUILayout.EndHorizontal();
            if (Time.realtimeSinceStartup < _plugin.ToastUntil && _plugin.Toast.Length > 0)
                GUILayout.Label("<color=#ffcc55>" + _plugin.Toast + "</color>", _small);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            TabButton(Tab.Run, "Run a test");
            TabButton(Tab.Live, "Live view");
            TabButton(Tab.Results, $"Results ({_plugin.Results.Count})");
            TabButton(Tab.LoadMemory, "Load times & memory");
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            switch (_activeTab)
            {
                case Tab.Run: DrawRunTab(); break;
                case Tab.Live: DrawLiveTab(); break;
                case Tab.LoadMemory: DrawLoadMemoryTab(); break;
                default: DrawResultsTab(); break;
            }
            GUI.DragWindow(new Rect(0, 0, 10000, 28));
        }

        private void TabButton(Tab t, string text)
        {
            if (GUILayout.Button(text, _activeTab == t ? _tabOn : _tab, GUILayout.Width(150)))
            {
                _activeTab = t;
                _nextRefresh = 0;
            }
        }

        private void DrawRunTab()
        {
            var p = _plugin;
            GUILayout.Label("1. What to run", _h2);
            float secs = _kind == RunKind.Suite ? p.CfgSuiteSeconds.Value : p.CfgBenchmarkSeconds.Value;
            int suiteSteps = 2 + (p.CfgSuiteAlloc.Value && AllocCounter.Available ? 1 : 0) + (p.CfgSuiteTrace.Value ? 1 : 0);
            KindCard(RunKind.Suite, "Full suite  (recommended)",
                $"Baseline → Timing{(suiteSteps > 2 && p.CfgSuiteAlloc.Value && AllocCounter.Available ? " → Allocations" : "")}{(p.CfgSuiteTrace.Value ? " → Trace" : "")}, " +
                $"{p.CfgSuiteSeconds.Value:0} s each. About {Math.Ceiling(suiteSteps * (p.CfgSuiteSeconds.Value + p.CfgWarmup.Value + 10) / 60):0} min. Stays in one spot for comparable numbers.");
            KindCard(RunKind.Single, "Single test", $"One mode for {p.CfgBenchmarkSeconds.Value:0} s, then stops and saves.");
            KindCard(RunKind.Continuous, "Continuous recording", "One mode until you press Stop. Good for recording a whole play session (logs every 10 s).");
            KindCard(RunKind.Objects, "Most expensive objects", "Times each object's per-frame code (creatures, fires, smelters, mod objects) for 20 s and ranks objects and prefabs. Results in the log and object_costs.csv.");
            KindCard(RunKind.Memory, "Memory census", "Counts which mods, game systems and Harmony patches keep the most objects in memory, times a full garbage collection, and shows what grew since the last census. Results in the log and a memory_census file.");

            if (_kind == RunKind.Objects)
            {
                GUILayout.Space(8);
                GUILayout.Label("Stand where the game feels slow. The normal per-mod recording pauses while this runs, then stops.", _wrap);
                GUILayout.Space(6);
                if (GUILayout.Button("▶  Find the most expensive objects" + (Cap != null ? "   (ends the current recording)" : ""), _big, GUILayout.Height(38)))
                {
                    var oc = p.StartObjectCosts(20f);
                    if (oc.Error != null) p.ShowToast("Object costs: " + oc.Error);
                    p.SetPanel(false);
                }
                return;
            }

            if (_kind == RunKind.Memory)
            {
                GUILayout.Space(8);
                GUILayout.Label("Stand still. The game stutters for a few seconds while the census counts. Run it again later in the same spot: " +
                                "the second census shows what grew since the first (a mod whose objects keep growing is likely leaking memory).", _wrap);
                var last = MemoryCensus.Last;
                if (last != null && last.Done && last.Error == null)
                    GUILayout.Label($"<color=#aaaaaa>Last census: {(DateTime.Now - last.FinishedAt).TotalMinutes:0} min ago, full garbage collection {last.GcMedianMs:0} ms, {MemoryCensus.Millions(last.Objects)} objects.</color>", _wrap);
                GUILayout.Space(6);
                GUI.enabled = MemoryCensus.Running == null;
                if (GUILayout.Button("▶  Count what fills memory", _big, GUILayout.Height(38)))
                {
                    var mc = p.StartMemoryCensus(100);
                    if (mc.Error != null) p.ShowToast("Memory census: " + mc.Error);
                    p.SetPanel(false);
                }
                GUI.enabled = true;
                return;
            }

            if (_kind != RunKind.Suite)
            {
                GUILayout.Label("2. Mode", _h2);
                GUILayout.BeginHorizontal();
                foreach (CaptureMode m in Enum.GetValues(typeof(CaptureMode)))
                {
                    GUI.enabled = p.CanUse(m);
                    if (GUILayout.Button(m.ToString(), _mode == m ? _tabOn : _tab, GUILayout.Width(120))) _mode = m;
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(ModeHelp[_mode], _wrap);
            }

            GUILayout.Label(_kind == RunKind.Suite ? "2. Settings" : "3. Settings", _h2);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Label", _label, GUILayout.Width(120));
            string label = GUILayout.TextField(p.CfgLabel.Value ?? "", 60, GUILayout.Width(280));
            if (label != p.CfgLabel.Value) p.CfgLabel.Value = label;
            GUILayout.Label("<color=#888888>e.g. \"all mods\", \"without Seasons\"</color>", _small);
            GUILayout.EndHorizontal();

            if (_kind != RunKind.Continuous)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(_kind == RunKind.Suite ? "Seconds per mode" : "Seconds", _label, GUILayout.Width(120));
                float v = GUILayout.HorizontalSlider(secs, 15, 300, GUILayout.Width(280));
                v = Mathf.Round(v / 15f) * 15f;
                if (Math.Abs(v - secs) > 0.1f)
                {
                    if (_kind == RunKind.Suite) p.CfgSuiteSeconds.Value = v; else p.CfgBenchmarkSeconds.Value = v;
                }
                GUILayout.Label($"{v:0} s counted (warm-up not included)", _small);
                GUILayout.EndHorizontal();
            }
            if (_kind == RunKind.Suite)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(124);
                GUI.enabled = AllocCounter.Available;
                p.CfgSuiteAlloc.Value = GUILayout.Toggle(p.CfgSuiteAlloc.Value && AllocCounter.Available, " include Allocations", GUILayout.Width(180));
                GUI.enabled = true;
                p.CfgSuiteTrace.Value = GUILayout.Toggle(p.CfgSuiteTrace.Value, " include Trace", GUILayout.Width(150));
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(124);
            p.CfgSuiteHideOverlay.Value = GUILayout.Toggle(p.CfgSuiteHideOverlay.Value, " during timed tests show only the status line (recommended)", GUILayout.Width(440));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Live overlay", _label, GUILayout.Width(120));
            foreach (OverlayMode m in Enum.GetValues(typeof(OverlayMode)))
                if (GUILayout.Button(m.ToString(), p.CfgOverlay.Value == m ? _tabOn : _tab, GUILayout.Width(90)))
                    p.SetOverlay(m);
            GUILayout.Label("<color=#888888>shown while you play, mouse not needed · Alt+F11 cycles it</color>", _small);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Before starting: go to the spot you want to test. For before/after comparisons use the same spot and do the same thing " +
                            "(standing still is the most repeatable). The panel closes when the test starts. Counting begins after hooking and a short warm-up.", _wrap);
            GUILayout.Space(6);
            string go = _kind == RunKind.Suite ? "▶  Start full suite" : _kind == RunKind.Single ? $"▶  Start {_mode} test" : $"▶  Start recording {_mode}";
            if (Cap != null) go += "   (ends the current recording)";
            if (GUILayout.Button(go, _big, GUILayout.Height(38)))
            {
                if (_kind == RunKind.Continuous) p.StartContinuous(_mode);
                else p.StartTimed(_kind == RunKind.Suite, _mode);
                p.SetPanel(false);
            }
        }

        private void KindCard(RunKind kind, string title, string text)
        {
            bool on = _kind == kind;
            if (GUILayout.Button($"{(on ? "◉" : "○")}  <b>{title}</b>\n<color=#aaaaaa>{text}</color>", on ? _cardOn : _card))
                _kind = kind;
        }

        private void DrawLiveTab()
        {
            if (Cap == null)
            {
                GUILayout.Label("Not recording. Start a test in the Run tab.", _wrap);
                return;
            }
            GUILayout.Label(_header, _label);
            GUILayout.Label(_header2, _small);
            DrawGraph(GUILayoutUtility.GetRect(760, 80));
            if (Cap.Mode == CaptureMode.Baseline)
            {
                GUILayout.Label("Baseline: no hooks attached, so nothing is broken down by mod. The FPS above is the reference for the other modes.", _wrap);
                return;
            }
            GUILayout.BeginHorizontal();
            foreach (Filter f in Enum.GetValues(typeof(Filter)))
                if (GUILayout.Button(f == Filter.Game ? "Game systems" : f.ToString(), _filter == f ? _tabOn : _tab, GUILayout.Width(120)))
                {
                    _filter = f;
                    _nextRefresh = 0;
                }
            GUILayout.FlexibleSpace();
            GUILayout.Label(Alloc ? "KB allocated per frame" : "ms of CPU per frame (self time)", _small);
            GUILayout.EndHorizontal();
            _scrollLive = GUILayout.BeginScrollView(_scrollLive, GUILayout.Height(Mathf.Min(420, 40 + _rows.Count * 20 + _expandedRows.Count * 18)));
            DrawTable(interactive: true, width: 740);
            GUILayout.EndScrollView();
            GUILayout.Label("Click a row to see its heaviest methods. Numbers on this tab update while the panel is open, but frames with the panel open are not counted in the results.", _small);
        }

        private void DrawResultsTab()
        {
            var p = _plugin;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open comparison page (all runs)", GUILayout.Width(240))) Plugin.OpenPath(Path.Combine(p.RootFolder, "compare.html"));
            if (GUILayout.Button("Open logs folder", GUILayout.Width(150))) Plugin.OpenPath(p.RootFolder);
            GUILayout.EndHorizontal();
            GUILayout.Label("The comparison page lists every run from every session: pick two to see the FPS change and which mods differ between them.", _small);
            GUILayout.Space(6);
            if (p.Results.Count == 0)
            {
                GUILayout.Label("No finished captures in this session yet. A capture is saved when a test ends, when you press Stop, or when the game closes.", _wrap);
                return;
            }
            GUILayout.BeginHorizontal();
            Head("#", 28); Head("Mode", 90); Head("Label", 170); Head("Secs", 45); Head("FPS", 50); Head("1% low", 55); Head("Mods/frame", 85); Head("Heaviest mod", 150);
            GUILayout.EndHorizontal();
            _scrollResults = GUILayout.BeginScrollView(_scrollResults, GUILayout.Height(Mathf.Min(380, 24 * p.Results.Count + 10)));
            foreach (var r in Enumerable.Reverse(p.Results))
            {
                GUILayout.BeginHorizontal();
                Cell(r.Number.ToString(), 28); Cell(r.Mode.ToString(), 90); Cell(r.Label, 170); Cell(r.Seconds.ToString("0"), 45);
                Cell(r.Fps.ToString("0.0"), 50); Cell(r.Fps1Low.ToString("0.0"), 55);
                Cell(double.IsNaN(r.ModsPerFrame) ? "–" : $"{r.ModsPerFrame:0.00} {r.Unit}", 85); Cell(r.Top, 150);
                if (GUILayout.Button("report", GUILayout.Width(60))) Plugin.OpenPath(r.Report);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private Vector2 _scrollLoad;

        private void DrawLoadMemoryTab()
        {
            var p = _plugin;
            var l = p.Loads;
            _scrollLoad = GUILayout.BeginScrollView(_scrollLoad, GUILayout.Height(Mathf.Min(560, Screen.height / Mathf.Max(0.5f, p.CfgScale.Value) - 220)));

            GUILayout.Label("Startup", _h2);
            if (l == null || !l.StartupDone)
                GUILayout.Label("Measured once the main menu has settled after launch.", _small);
            else
            {
                GUILayout.Label($"<b>{Sec(l.TotalToMenu)}</b> from launching the game to a settled main menu.", _label);
                Pair("Unity and BepInEx starting", l.LaunchToBepInEx);
                Pair("BepInEx preparing (finding mods)", l.BepInExToMods);
                Pair($"Loading {l.Mods.Count} mods (their setup code)", l.ModLoading);
                Pair("After mods loaded, until the main menu shows", l.ModsToMenu);
                Pair("Main menu hitching until it settled", l.MenuSettle);
                if (!l.HavePreloader)
                    GUILayout.Label("Per-mod startup times need ValPerfMonitor.Preloader.dll in BepInEx\\patchers.", _small);
                else
                {
                    GUILayout.Label("Slowest mods to load", _bold);
                    foreach (var m in l.Mods.OrderByDescending(m => m.Seconds).Take(10))
                        Pair("   " + m.Name, m.Seconds);
                }
                Steps("Slowest game steps after the mods loaded (each includes mods' patches on it)", l.StartupSteps);
            }

            GUILayout.Label("World loads this session", _h2);
            if (l == null || l.WorldLoads.Count == 0)
                GUILayout.Label("None yet. Timed from the loading screen until the game stops hitching (3 s without a frame over 100 ms).", _small);
            else
            {
                GUILayout.BeginHorizontal();
                Head("When", 80); Head("In world", 90); Head("Screen gone", 100); Head("Hitches stop", 110); Head("Hitches", 70);
                GUILayout.EndHorizontal();
                foreach (var w in l.WorldLoads)
                {
                    GUILayout.BeginHorizontal();
                    Cell(w.When, 80); Cell(Sec(w.SpawnSeconds), 90); Cell(Sec(w.ScreenSeconds), 100);
                    Cell(w.GaveUp ? $"> {Sec(w.SmoothSeconds)}" : Sec(w.SmoothSeconds), 110); Cell(w.Hitches.ToString(), 70);
                    GUILayout.EndHorizontal();
                }
            }
            if (l != null && l.WorldLoads.Count > 0)
                Steps("Slowest steps of the last world load (each includes mods' patches on it)", l.WorldLoads[l.WorldLoads.Count - 1].Steps);
            if (l != null && l.Logouts.Count > 0)
                GUILayout.Label("Logouts: " + string.Join(", ", l.Logouts.Select(x => $"{x.when} {Sec(x.seconds)}")), _small);

            GUILayout.Label("Memory (leak check)", _h2);
            var mem = p.Memory;
            if (mem == null || mem.Samples.Count == 0)
                GUILayout.Label("Checked every minute (never during timed tests). First check about 30 s after launch.", _small);
            else
            {
                bool trend = mem.TrendWindow(out var a, out var b);
                if (!trend) { a = mem.Samples[0]; b = mem.Samples[mem.Samples.Count - 1]; }
                GUILayout.Label(trend
                    ? $"First check in a world ({a.Time}) vs latest ({b.Time}). A leak is a count that keeps rising while objects loaded stays about the same."
                    : "A trend needs two checks in a world at least 5 minutes apart. Showing first vs latest check so far.", _small);
                GUILayout.BeginHorizontal();
                Head("", 230); Head(a.Time, 110); Head(b.Time, 110); Head("Change", 110);
                GUILayout.EndHorizontal();
                Row("Objects loaded around you", a.Instances, b.Instances);
                Row("Game objects", a.GameObjects, b.GameObjects);
                Row("Components (scripts)", a.Components, b.Components);
                Row("Textures", a.Textures, b.Textures);
                Row("Meshes", a.Meshes, b.Meshes);
                Row("Materials", a.Materials, b.Materials);
                Row("Sounds", a.Audio, b.Audio);
                Row("Managed memory MB", a.HeapMb, b.HeapMb);
                Row("Unity memory MB", a.TotalUsedMb, b.TotalUsedMb);
                Row("Graphics memory MB", a.GfxUsedMb, b.GfxUsedMb);
                var growing = mem.GrowingMods(6);
                if (growing.Count > 0)
                {
                    GUILayout.Label("Mods whose components grew the most", _bold);
                    foreach (var (mod, from, to) in growing)
                        Row("   " + mod, from, to);
                }
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open logs folder", GUILayout.Width(150))) Plugin.OpenPath(p.RootFolder);
            GUILayout.Label("startup_times.csv, world_load_times.csv (all sessions) · memory.csv, startup_mods.csv (this session's folder)", _small);
            GUILayout.EndHorizontal();
        }

        private void Steps(string title, List<(string label, int calls, double seconds)> steps)
        {
            if (steps == null || steps.Count == 0) return;
            GUILayout.Label(title, _bold);
            foreach (var (label, calls, seconds) in steps.Take(8))
                Pair("   " + label + (calls > 1 ? $" ×{calls}" : ""), seconds);
        }

        private static string Sec(double s) => double.IsNaN(s) ? "–" : s < 10 ? $"{s:0.00} s" : $"{s:0.0} s";

        private void Pair(string name, double seconds)
        {
            GUILayout.BeginHorizontal();
            Cell(name, 360); Cell(Sec(seconds), 90);
            GUILayout.EndHorizontal();
        }

        private void Row(string name, double from, double to)
        {
            bool known = !double.IsNaN(from) && !double.IsNaN(to) && from >= 0 && to >= 0;
            double d = to - from;
            string change = !known ? "" : from > 0 ? $"{(d >= 0 ? "+" : "")}{d:0} ({100 * d / from:+0;-0}%)" : $"{(d >= 0 ? "+" : "")}{d:0}";
            GUILayout.BeginHorizontal();
            Cell(name, 230); Cell(from < 0 || double.IsNaN(from) ? "–" : from.ToString("0"), 110); Cell(to < 0 || double.IsNaN(to) ? "–" : to.ToString("0"), 110);
            Cell(known && d > 0 && from > 0 && d / from > 0.2 ? $"<color=#ffb347>{change}</color>" : change, 110);
            GUILayout.EndHorizontal();
        }

        private void Head(string t, float w) => GUILayout.Label(t, _bold, GUILayout.Width(w));
        private void Cell(string t, float w) => GUILayout.Label(t ?? "", _label, GUILayout.Width(w));

        // ---- HUD --------------------------------------------------------------------------------

        private void DrawHud(int id)
        {
            bool full = _plugin.CfgOverlay.Value == OverlayMode.Full;
            float w = full ? 616 : 436;
            GUILayout.Label(_status, _small);
            GUILayout.Label(_header, _label);
            if (full)
            {
                GUILayout.Label(_header2, _small);
                DrawGraph(GUILayoutUtility.GetRect(w, 70));
            }
            if (Cap.Mode != CaptureMode.Baseline)
                DrawTable(interactive: false, width: w, maxRows: full ? int.MaxValue : 6);
            GUILayout.Label("<color=#888888>F11 test panel · Alt+F11 overlay size/off · overlay cost is removed from results</color>", _small);
            GUI.DragWindow();
        }

        // ---- Shared pieces ----------------------------------------------------------------------

        private void DrawTable(bool interactive, float width, int maxRows = int.MaxValue)
        {
            float nameW = width - 18 - 66 - 50 - 72 - 62 - 66 - 10;
            GUILayout.BeginHorizontal();
            GUILayout.Label("", _small, GUILayout.Width(18));
            GUILayout.Label(_filter == Filter.Game ? "Game system" : "Mod", _bold, GUILayout.Width(nameW));
            SortHeader($"Avg {Unit}", SortBy.Avg, 66, interactive);
            GUILayout.Label(Alloc ? "" : "% frm", _right, GUILayout.Width(50));
            SortHeader($"Peak {Unit}", SortBy.Peak, 72, interactive);
            SortHeader("Calls/f", SortBy.Calls, 62, interactive);
            SortHeader("Capture", SortBy.Capture, 66, interactive);
            GUILayout.EndHorizontal();

            var stats = Cap.Stats;
            float frame = stats.WindowFrameAvg;
            long totFrames = Math.Max(1, stats.TotFrames);
            float maxAvg = Math.Max(0.05f * HeatScale, _rows.Count > 0 ? _rows.Max(r => r.WindowAvg) : 0.05f);
            string fmt = Alloc ? "0.0" : "0.000";
            int rank = 0;
            foreach (var m in _rows)
            {
                if (rank >= maxRows) break;
                rank++;
                Rect r = GUILayoutUtility.GetRect(width, 19);
                var bar = new Rect(r.x + 18, r.y + 2, nameW * Mathf.Clamp01(m.WindowAvg / maxAvg), r.height - 4);
                DrawRect(bar, (m.IsGame ? Blue : Heat(m.WindowAvg / HeatScale)) * new Color(1, 1, 1, 0.35f));
                if (interactive && GUI.Button(r, GUIContent.none, _row))
                {
                    _expanded = _expanded == m ? null : m;
                    _nextRefresh = 0;
                }
                float x = r.x;
                GUI.Label(new Rect(x, r.y, 18, r.height), rank.ToString(), _small); x += 18;
                GUI.Label(new Rect(x + 3, r.y, nameW - 3, r.height), (interactive ? (_expanded == m ? "▼ " : "▶ ") : "") + m.Name, _label); x += nameW;
                Col(ref x, r, 66, m.WindowAvg.ToString(fmt), Heat(m.WindowAvg / HeatScale));
                Col(ref x, r, 50, Alloc || frame <= 0 ? "" : (100f * m.WindowAvg / frame).ToString("0.0"), Color.white);
                Col(ref x, r, 72, m.WindowMax.ToString(Alloc ? "0" : "0.00"), Heat(m.WindowMax / 4f / HeatScale));
                Col(ref x, r, 62, m.WindowCallsPerFrame.ToString("0"), Color.white);
                Col(ref x, r, 66, (m.TotSum / totFrames).ToString(fmt), Grey);
                if (interactive && _expanded == m)
                    DrawExpanded();
            }
            if (_rows.Count == 0)
                GUILayout.Label(_plugin.WarmingUp ? "Warming up…" : "No activity measured yet.", _small);
        }

        private void DrawExpanded()
        {
            if (_expandedRows.Count == 0)
            {
                GUILayout.Label("      (no calls in this capture yet)", _small);
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("      Heaviest methods in this capture", _small, GUILayout.Width(470));
            GUILayout.Label($"{Unit}/frame", _right, GUILayout.Width(80));
            GUILayout.Label("calls/f", _right, GUILayout.Width(60));
            GUILayout.Label(Alloc ? "max B" : "max µs", _right, GUILayout.Width(80));
            GUILayout.EndHorizontal();
            foreach (var (label, avg, calls, max) in _expandedRows)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("      " + label, _small, GUILayout.Width(470));
                GUILayout.Label(avg, _right, GUILayout.Width(80));
                GUILayout.Label(calls, _right, GUILayout.Width(60));
                GUILayout.Label(max, _right, GUILayout.Width(80));
                GUILayout.EndHorizontal();
            }
        }

        private void DrawGraph(Rect r)
        {
            DrawRect(r, new Color(0, 0, 0, 0.35f));
            var s = Cap.Stats;
            int n = s.RingFilled;
            if (n < 2)
            {
                GUI.Label(new Rect(r.x + 6, r.y + 4, 400, 18), "<color=#888888>graph starts after warm-up</color>", _small);
                return;
            }
            bool timeUnit = !Alloc;
            float top = Math.Max(40f, s.WindowFrameMax * 1.1f);
            float scaleY = r.height / top;
            int columns = Mathf.Min(n, (int)r.width / 2);
            if (columns < 1) return; // layout pass hands out a zero-width rect
            float colW = r.width / columns;
            int per = Mathf.Max(1, n / columns);
            for (int c = 0; c < columns; c++)
            {
                // oldest on the left; take the worst frame in each bucket so spikes stay visible
                float f = 0, m = 0, g = 0;
                for (int k = 0; k < per; k++)
                {
                    int idx = (s.RingPos - n + c * per + k + s.RingSize * 2) % s.RingSize;
                    if (s.FrameRing[idx] > f) { f = s.FrameRing[idx]; m = s.ModsRing[idx]; g = s.GameRing[idx]; }
                }
                float fh = Mathf.Min(r.height, f * scaleY);
                DrawRect(new Rect(r.x + c * colW, r.yMax - fh, colW - 0.5f, fh), f > 33.4f ? new Color(1, 0.3f, 0.3f, 0.55f) : new Color(0.6f, 0.6f, 0.7f, 0.45f));
                if (timeUnit)
                {
                    float gh = Mathf.Min(fh, g * scaleY);
                    float mh = Mathf.Min(fh - gh, m * scaleY);
                    DrawRect(new Rect(r.x + c * colW, r.yMax - gh, colW - 0.5f, gh), Blue);
                    DrawRect(new Rect(r.x + c * colW, r.yMax - gh - mh, colW - 0.5f, mh), Orange);
                }
            }
            // Bars are frame time: taller = slower. The 30 fps line (33 ms) sits above the 60 fps line (16.7 ms).
            GuideLine(r, 1000f / 60f, scaleY, Green, "16.7 ms = 60 fps");
            GuideLine(r, 1000f / 30f, scaleY, Yellow, "33 ms = 30 fps");
            // Dark backing so a guide line passing through the legend doesn't strike it out.
            DrawRect(new Rect(r.x + 2, r.y + 2, timeUnit ? 400 : 230, 16), new Color(0, 0, 0, 0.75f));
            GUI.Label(new Rect(r.x + 4, r.y + 2, 420, 16),
                timeUnit ? "<color=#bbbbbb>frame time (taller = slower)</color>  <color=#ff8c26>■ mods</color>  <color=#5aa7ea>■ game systems</color>" : "<color=#bbbbbb>frame time (taller = slower)</color>", _small);
        }

        private void GuideLine(Rect r, float ms, float scaleY, Color c, string text)
        {
            float y = r.yMax - ms * scaleY;
            if (y < r.y) return;
            DrawRect(new Rect(r.x, y, r.width, 1), c * new Color(1, 1, 1, 0.7f));
            GUI.Label(new Rect(r.xMax - 120, y - 14, 120, 14), text, _small);
        }

        private void SortHeader(string text, SortBy sort, float width, bool interactive)
        {
            string t = _sort == sort ? $"<b>{text} ▾</b>" : text;
            if (!interactive)
            {
                GUILayout.Label(t, _right, GUILayout.Width(width));
                return;
            }
            if (GUILayout.Button(t, _right, GUILayout.Width(width)))
            {
                _sort = sort;
                _nextRefresh = 0;
            }
        }

        private void Col(ref float x, Rect r, float w, string text, Color c)
        {
            var prev = GUI.contentColor;
            GUI.contentColor = c;
            GUI.Label(new Rect(x, r.y, w, r.height), text, _right);
            GUI.contentColor = prev;
            x += w;
        }

        private static Color Heat(float ms)
        {
            if (ms < 0.1f) return Green;
            if (ms < 0.5f) return Yellow;
            if (ms < 1.0f) return Orange;
            return Red;
        }

        private void DrawRect(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        private void EnsureStyles()
        {
            int fs = Mathf.Clamp(_plugin.CfgFontSize.Value, 9, 24);
            if (_box != null && fs == _fontSize)
                return;
            _fontSize = fs;
            if (_white == null)
            {
                _white = MakeTex(Color.white);
                _bg = MakeTex(new Color(0.07f, 0.07f, 0.09f, 0.94f));
                _cardBg = MakeTex(new Color(1, 1, 1, 0.05f));
                _cardOnBg = MakeTex(new Color(1f, 0.55f, 0.15f, 0.18f));
            }
            _box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
            _box.normal.background = _bg;
            _box.onNormal.background = _bg;
            _label = new GUIStyle(GUI.skin.label) { fontSize = fs, richText = true, wordWrap = false, clipping = TextClipping.Clip };
            _label.normal.textColor = Color.white;
            _title = new GUIStyle(_label) { fontSize = fs + 3 };
            _h2 = new GUIStyle(_label) { fontSize = fs + 1, fontStyle = FontStyle.Bold, margin = new RectOffset(4, 4, 8, 2) };
            _h2.normal.textColor = Orange;
            _bold = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
            _right = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight };
            _small = new GUIStyle(_label) { fontSize = Math.Max(9, fs - 2), wordWrap = true };
            _small.normal.textColor = new Color(0.8f, 0.8f, 0.85f);
            _statusStyle = new GUIStyle(_label) { fontSize = fs + 2, fontStyle = FontStyle.Bold, richText = false };
            _statusShadow = new GUIStyle(_statusStyle);
            _statusShadow.normal.textColor = Color.black;
            _wrap = new GUIStyle(_label) { wordWrap = true };
            _wrap.normal.textColor = new Color(0.85f, 0.85f, 0.9f);
            _row = new GUIStyle(GUIStyle.none);
            _row.hover.background = MakeTex(new Color(1, 1, 1, 0.06f));
            _tab = new GUIStyle(GUI.skin.button) { fontSize = Math.Max(9, fs - 1), richText = true };
            _tabOn = new GUIStyle(_tab) { fontStyle = FontStyle.Bold };
            _tabOn.normal.textColor = Orange;
            _tabOn.hover.textColor = Orange;
            _card = new GUIStyle(GUI.skin.button) { fontSize = fs, richText = true, alignment = TextAnchor.MiddleLeft, wordWrap = true, padding = new RectOffset(12, 12, 8, 8) };
            _card.normal.background = _cardBg;
            _card.hover.background = MakeTex(new Color(1, 1, 1, 0.09f));
            _card.normal.textColor = Color.white;
            _card.hover.textColor = Color.white;
            _cardOn = new GUIStyle(_card);
            _cardOn.normal.background = _cardOnBg;
            _cardOn.hover.background = _cardOnBg;
            _big = new GUIStyle(GUI.skin.button) { fontSize = fs + 2, fontStyle = FontStyle.Bold, richText = true };
            _big.normal.textColor = Orange;
            _big.hover.textColor = Color.white;
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
