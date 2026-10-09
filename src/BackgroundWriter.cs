using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace ValPerfMonitor
{
    /// <summary>
    /// All disk I/O happens on this thread, so a slow disk can never show up as a frame spike.
    /// The main thread only hands over finished strings.
    /// </summary>
    internal sealed class BackgroundWriter : IDisposable
    {
        private readonly Queue<Action> _queue = new Queue<Action>();
        private readonly Thread _thread;
        private readonly ManualLogSource _log;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _stop;
        private volatile bool _busy;

        /// <summary>True while the writer thread is building or writing something (its allocations can trigger a GC).</summary>
        public bool Busy => _busy;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public BackgroundWriter(ManualLogSource log)
        {
            _log = log;
            _thread = new Thread(Run) { IsBackground = true, Name = "ValPerfMonitor writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Append(string path, string text) => Enqueue(() => Save(path, text, true));
        public void Write(string path, string text) => Enqueue(() => Save(path, text, false));

        /// <summary>Build content off the main thread (e.g. a large trace), then write it.</summary>
        public void Job(Func<(string path, string text)> build) => Enqueue(() =>
        {
            var (path, text) = build();
            Save(path, text, false);
        });

        private void Enqueue(Action a)
        {
            lock (_queue) _queue.Enqueue(a);
            _signal.Set();
        }

        private static void Save(string path, string text, bool append)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (append) File.AppendAllText(path, text, Utf8);
            else File.WriteAllText(path, text, Utf8);
        }

        private void Run()
        {
            while (true)
            {
                _signal.WaitOne(500);
                while (true)
                {
                    Action item;
                    lock (_queue)
                    {
                        if (_queue.Count == 0) break;
                        item = _queue.Dequeue();
                    }
                    _busy = true;
                    try { item(); }
                    catch (Exception e) { _log.LogWarning($"Log write failed: {e.Message}"); }
                    finally { _busy = false; }
                }
                if (_stop) return;
            }
        }

        /// <summary>Finish everything queued (used on quit).</summary>
        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            _thread.Join(5000);
        }
    }

    internal static class Csv
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly char[] Special = { ',', '"', '\n', '\r' };

        public static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.IndexOfAny(Special) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public static string F(double v, string fmt) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString(fmt, Inv);

        public static string Json(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break; // safe to embed in <script>
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string JNum(double v, string fmt = "0.####") => double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString(fmt, Inv);
    }
}
