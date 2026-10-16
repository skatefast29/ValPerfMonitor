using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// Counts the errors and warnings written to the BepInEx log, per mod. A mod that throws the same exception
    /// every frame costs CPU (each one builds a message and a stack trace) and buries real problems in the log.
    /// Registered by the early-start plugin, so errors while the other mods load are counted too.
    ///
    /// Only counts: one dictionary entry per distinct message (first line), nothing written per message.
    /// Which mod a message belongs to is worked out only when asked (on the main thread): a mod's own log name,
    /// or for the game's and Unity's messages ("Unity Log") the first mod method in the exception's stack trace.
    /// </summary>
    internal sealed class LogWatch : ILogListener
    {
        public sealed class Entry
        {
            public string Source, Level, Message, Stack;
            public bool Exception;
            public int Count;
            public double FirstSeen, LastSeen;
        }

        public sealed class ModRow
        {
            public string Mod;
            public int Errors, Warnings, Exceptions;
            public double LastSeen;
            public Entry Top;
        }

        public static LogWatch Instance;
        private const int MaxMessages = 400;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _messages = new Dictionary<string, Entry>();
        private int _errors, _warnings, _dropped;
        // Type names from stack traces -> the mod they belong to (null = not a mod). Main thread only.
        private static readonly Dictionary<string, string> _typeOwner = new Dictionary<string, string>();

        /// <summary>Seconds since the monitor started counting (the clock messages are stamped with).</summary>
        public static double Now => Clock.Elapsed.TotalSeconds;

        public static void Start()
        {
            if (Instance != null) return;
            Instance = new LogWatch();
            Logger.Listeners.Add(Instance);
            TimeLogWrites();
        }

        public int Errors { get { lock (_lock) return _errors; } }
        public int Warnings { get { lock (_lock) return _warnings; } }

        public void LogEvent(object sender, LogEventArgs e)
        {
            bool error = (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
            bool warning = (e.Level & LogLevel.Warning) != 0;
            try
            {
                string source = e.Source?.SourceName ?? "?";
                string text = e.Data as string ?? e.Data?.ToString() ?? "";
                int nl = text.IndexOf('\n');
                CountFlood(source, text, nl, error ? "error" : warning ? "warning" : "info");
                if (!error && !warning) return;
                string first = (nl < 0 ? text : text.Substring(0, nl)).Trim();
                if (first.Length > 240) first = first.Substring(0, 240);
                string level = error ? "error" : "warning";
                string key = source + "|" + level + "|" + first;
                double now = Now;
                lock (_lock)
                {
                    if (error) _errors++; else _warnings++;
                    if (!_messages.TryGetValue(key, out var entry))
                    {
                        if (_messages.Count >= MaxMessages) { _dropped++; return; }
                        entry = new Entry
                        {
                            Source = source, Level = level, Message = first, FirstSeen = now,
                            Exception = text.IndexOf("Exception", StringComparison.Ordinal) >= 0,
                            Stack = nl < 0 ? "" : StackOf(text.Substring(nl + 1)),
                        };
                        _messages[key] = entry;
                    }
                    entry.Count++;
                    entry.LastSeen = now;
                }
            }
            catch { }
        }

        public void Dispose() { }

        // ---- Log floods: one message repeated many times a second -------------------------------

        public sealed class Flood
        {
            public string Source, Level, Message;
            public int PeakPerSecond, FloodSeconds;
            public long Total;             // all lines of this message while it flooded or since
            public double FirstFlood, LastFlood, LastWarned = double.NegativeInfinity;
        }

        /// <summary>Lines of one message per second that count as a flood (setting "Log flood: lines per second").</summary>
        public static int FloodThreshold = 20;
        private long _second = -1;
        private readonly Dictionary<string, int> _thisSecond = new Dictionary<string, int>();
        private readonly Dictionary<string, Flood> _floods = new Dictionary<string, Flood>();
        private readonly Queue<Flood> _newFloods = new Queue<Flood>();
        private long _lines;

        /// <summary>All log lines (any level) since the start.</summary>
        public long Lines { get { lock (_lock) return _lines; } }

        // Any thread. Counts lines per message within the current second; a message passing the threshold is queued
        // for the main thread to warn about (Plugin.Tick -> Alarms). This monitor's own lines are left out.
        private void CountFlood(string source, string text, int nl, string level)
        {
            lock (_lock)
            {
                _lines++;
                if (source.StartsWith(Plugin.Name, StringComparison.Ordinal)) return;
                long sec = (long)Now;
                if (sec != _second) { _second = sec; _thisSecond.Clear(); }
                int len = Math.Min(nl < 0 ? text.Length : nl, 160);
                string key = source + "|" + level + "|" + text.Substring(0, len);
                _thisSecond.TryGetValue(key, out int n);
                _thisSecond[key] = ++n;
                if (_floods.TryGetValue(key, out var f))
                {
                    f.Total++;
                    if (n > f.PeakPerSecond) f.PeakPerSecond = n;
                    if (n == FloodThreshold + 1) { f.FloodSeconds++; f.LastFlood = Now; _newFloods.Enqueue(f); }
                }
                else if (n > FloodThreshold && _floods.Count < 200)
                {
                    f = new Flood { Source = source, Level = level, Message = text.Substring(0, len).Trim(), PeakPerSecond = n, FloodSeconds = 1, Total = n, FirstFlood = Now, LastFlood = Now };
                    _floods[key] = f;
                    _newFloods.Enqueue(f);
                }
            }
        }

        /// <summary>Floods that started (or came back) since the last call. Main thread.</summary>
        public List<Flood> TakeNewFloods()
        {
            lock (_lock)
            {
                var l = new List<Flood>();
                while (_newFloods.Count > 0) { var f = _newFloods.Dequeue(); if (!l.Contains(f)) l.Add(f); }
                return l;
            }
        }

        public List<Flood> Floods() { lock (_lock) return _floods.Values.OrderByDescending(f => f.Total).ToList(); }

        /// <summary>The mod a flood belongs to: a mod's own log name, else its log name.</summary>
        public static string FloodOwner(Flood f, ModRegistry registry)
        {
            var named = registry.Visible.FirstOrDefault(m => m.IsPlugin && m.Name == f.Source);
            if (named != null) return named.Name;
            // Game and Unity lines: the error list may know the mod from the stack trace.
            if (Instance != null)
            {
                Entry e;
                lock (Instance._lock) e = Instance._messages.Values.FirstOrDefault(x => x.Source == f.Source && x.Message.StartsWith(f.Message, StringComparison.Ordinal));
                if (e != null) return Instance.Owner(e, registry);
            }
            return f.Source == "Unity Log" ? "Valheim / Unity" : f.Source;
        }

        // ---- Main-thread time spent writing log lines --------------------------------------------

        private static int _mainThread = -1, _depth;
        private static long _startTicks;
        /// <summary>Main-thread Stopwatch ticks spent handing log lines to BepInEx's listeners (disk, console, ...).</summary>
        public static long MainThreadTicks;
        public static long MainThreadLines;
        public static bool Timed;

        /// <summary>
        /// Times BepInEx's hand-out of each log line to its listeners (writing LogOutput.log, the console, this
        /// monitor) on the main thread. Unity's own cost of building an error (its stack trace) comes before that
        /// and is not included, so this is a lower bound. Logger.InternalLogEvent is too small to patch (Mono inlines
        /// it); the listener collection's SendLogEvent is patched instead.
        /// </summary>
        private static void TimeLogWrites()
        {
            if (Timed) return;
            try
            {
                _mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                var t = AccessTools.Inner(typeof(Logger), "LogListenerCollection");
                var m = t == null ? null : AccessTools.Method(t, "SendLogEvent", new[] { typeof(object), typeof(LogEventArgs) });
                if (m == null) { Logger.CreateLogSource(Plugin.Name).LogInfo("BepInEx's log code is not as expected: time spent writing log lines is not measured."); return; }
                new Harmony(Plugin.Guid + ".logtime").Patch(m, prefix: new HarmonyMethod(typeof(LogWatch), nameof(SendPrefix)), finalizer: new HarmonyMethod(typeof(LogWatch), nameof(SendFinalizer)));
                Timed = true;
            }
            catch (Exception e) { Logger.CreateLogSource(Plugin.Name).LogInfo($"Time spent writing log lines is not measured: {e.Message}"); }
        }

        private static void SendPrefix()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            if (_depth++ == 0) _startTicks = Stopwatch.GetTimestamp();
        }

        private static void SendFinalizer()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread || _depth == 0) return;
            if (--_depth == 0) { MainThreadTicks += Stopwatch.GetTimestamp() - _startTicks; MainThreadLines++; }
        }

        public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // The first few lines that look like stack frames.
        private static string StackOf(string rest)
        {
            var lines = rest.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != "Stack trace:" && !l.StartsWith("---")).Take(8);
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>How often each message has been seen so far (to count only what comes after).</summary>
        public Dictionary<string, int> Snapshot()
        {
            lock (_lock) return _messages.ToDictionary(k => k.Key, k => k.Value.Count);
        }

        private List<(Entry entry, int count)> Since(Dictionary<string, int> before)
        {
            lock (_lock)
                return _messages.Select(k => (k.Value, k.Value.Count - (before != null && before.TryGetValue(k.Key, out int c) ? c : 0)))
                                .Where(x => x.Item2 > 0).ToList();
        }

        public int Dropped { get { lock (_lock) return _dropped; } }

        /// <summary>Errors and warnings per mod (since a snapshot, or the whole session), most errors first.</summary>
        public List<ModRow> ByMod(ModRegistry registry, Dictionary<string, int> since = null)
        {
            var rows = new Dictionary<string, ModRow>();
            foreach (var (entry, count) in Since(since))
            {
                string mod = Owner(entry, registry);
                if (!rows.TryGetValue(mod, out var row)) rows[mod] = row = new ModRow { Mod = mod };
                if (entry.Level == "error") row.Errors += count; else row.Warnings += count;
                if (entry.Exception) row.Exceptions += count;
                row.LastSeen = Math.Max(row.LastSeen, entry.LastSeen);
                if (row.Top == null || (entry.Level == "error" && row.Top.Level != "error") || (entry.Level == row.Top.Level && entry.Count > row.Top.Count))
                    row.Top = entry;
            }
            return rows.Values.OrderByDescending(r => r.Errors).ThenByDescending(r => r.Warnings).ToList();
        }

        /// <summary>The most repeated messages (since a snapshot, or the whole session).</summary>
        public List<(Entry entry, int count, string mod)> TopMessages(ModRegistry registry, int n, Dictionary<string, int> since = null) =>
            Since(since).OrderByDescending(x => x.entry.Level == "error").ThenByDescending(x => x.count).Take(n)
                        .Select(x => (x.entry, x.count, Owner(x.entry, registry))).ToList();

        /// <summary>
        /// The mod a message belongs to. A mod's own log name is its plugin name. Messages from the game, Unity or
        /// Harmony are put on the first stack frame whose code is in a mod; otherwise they stay on their log name.
        /// </summary>
        public string Owner(Entry e, ModRegistry registry)
        {
            var named = registry.Visible.FirstOrDefault(m => m.IsPlugin && m.Name == e.Source);
            if (named != null) return named.Name;
            foreach (var frame in e.Stack.Split('\n'))
            {
                string mod = FrameOwner(frame, registry, includeSelf: false);
                if (mod != null) return mod;
            }
            return e.Source == "Unity Log" ? "Valheim / Unity" : e.Source;
        }

        /// <summary>The first mod (or ValPerfMonitor itself) among stack frames, innermost first; null if only game/Unity/system code.</summary>
        public static string FirstMod(IEnumerable<string> frames, ModRegistry registry)
        {
            foreach (var frame in frames)
            {
                string mod = FrameOwner(frame, registry, includeSelf: true);
                if (mod != null) return mod;
            }
            return null;
        }

        private static string FrameOwner(string frame, ModRegistry registry, bool includeSelf)
        {
            string type = FrameType(frame);
            if (type == null) return null;
            if (includeSelf)
            {
                Type self = null;
                try { self = typeof(Plugin).Assembly.GetType(type, false); } catch { }
                if (self != null) return Plugin.Name;
            }
            if (_typeOwner.TryGetValue(type, out var owner)) return owner;
            owner = null;
            try
            {
                var t = AccessTools.TypeByName(type);
                Assembly asm = t?.Assembly;
                if (asm != null && asm != typeof(ZNet).Assembly && asm != typeof(Plugin).Assembly)
                {
                    var mod = registry.ForPluginAssembly(asm);
                    string name = asm.GetName().Name;
                    if (mod != null) owner = mod.Name;
                    else if (!(name.StartsWith("UnityEngine") || name.StartsWith("Unity.") || name.StartsWith("System") || name == "mscorlib" || name.StartsWith("assembly_")
                               || name == "BepInEx" || name.StartsWith("BepInEx.") || name == "0Harmony" || name.StartsWith("MonoMod") || name.StartsWith("Mono.")))
                        owner = "[lib] " + name; // a library shipped inside a mod's folder
                }
            }
            catch { }
            _typeOwner[type] = owner;
            return owner;
        }

        // "MyMod.Patches.Postfix (Character __instance) (at ...)", "  at MyMod.Foo.Bar () [0x0] in ...",
        // "UnityEngine.Debug:Log (object)" -> the type's full name. Patched game methods (DMD wrappers) give null.
        private static string FrameType(string frame)
        {
            string f = frame.Trim();
            if (f.StartsWith("at ")) f = f.Substring(3);
            if (f.StartsWith("(wrapper")) return null;
            int paren = f.IndexOf(" (", StringComparison.Ordinal);
            if (paren < 0) paren = f.IndexOf('(');
            if (paren > 0) f = f.Substring(0, paren);
            int colon = f.IndexOf(':');
            int dot = colon >= 0 ? colon : f.LastIndexOf('.');
            if (dot <= 0) return null;
            string type = f.Substring(0, dot);
            // Nested compiler-generated classes ("Plugin+<>c", "Plugin/<Load>d__3"): the outer class.
            int cut = type.IndexOfAny(new[] { '<', '/' });
            if (cut > 0) type = type.Substring(0, cut).TrimEnd('+', '.');
            return type.Length == 0 ? null : type;
        }

        /// <summary>Errors and warnings during a capture, errors per minute, and the mod with the most errors ("Mod (n)").</summary>
        public static (int errors, int warnings, double perMin, string top) ForCapture(Capture c, ModRegistry registry)
        {
            if (Instance == null || c.LogBefore == null) return (0, 0, double.NaN, "");
            var rows = Instance.ByMod(registry, c.LogBefore);
            int errors = rows.Sum(r => r.Errors), warnings = rows.Sum(r => r.Warnings);
            double minutes = Math.Max(1.0 / 60, (DateTime.Now - c.Started).TotalMinutes);
            var top = rows.FirstOrDefault(r => r.Errors > 0);
            return (errors, warnings, errors / minutes, top == null ? "" : $"{top.Mod} ({top.Errors})");
        }

        public string Csv(ModRegistry registry)
        {
            var sb = new StringBuilder("mod,log_name,level,count,exception,first_seen_s,last_seen_s,message,stack\n");
            List<Entry> all;
            lock (_lock) all = _messages.Values.ToList();
            foreach (var e in all.OrderByDescending(x => x.Level == "error").ThenByDescending(x => x.Count))
                sb.Append(ValPerfMonitor.Csv.Q(Owner(e, registry))).Append(',').Append(ValPerfMonitor.Csv.Q(e.Source)).Append(',').Append(e.Level).Append(',')
                  .Append(e.Count.ToString(ValPerfMonitor.Csv.Inv)).Append(',').Append(e.Exception ? "yes" : "").Append(',')
                  .Append(ValPerfMonitor.Csv.F(e.FirstSeen, "0")).Append(',').Append(ValPerfMonitor.Csv.F(e.LastSeen, "0")).Append(',')
                  .Append(ValPerfMonitor.Csv.Q(e.Message)).Append(',').Append(ValPerfMonitor.Csv.Q(e.Stack.Replace('\n', '|'))).Append('\n');
            return sb.ToString();
        }
    }
}
