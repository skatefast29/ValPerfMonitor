using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;

namespace ValPerfMonitor
{
    /// <summary>
    /// The plugin's half of the crash check (the preloader's SessionLogs does the rest): marks the session as
    /// running once the monitor has loaded, removes the mark when the game quits normally, and at startup reports
    /// what the preloader found if the last session did not close normally.
    /// </summary>
    internal static class SessionCheck
    {
        // Same names as in the preloader's SessionLogs.
        private const string CrashKey = "ValPerfMonitor.LastCrash";
        private const string MarkerFile = "session-running.txt";
        private const string CrashFile = "last_crash.txt";

        private static string Root => Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor");

        /// <summary>The report of the last session that did not close normally (this launch only), or null.</summary>
        public static string LastCrash { get; private set; }
        public static string LikelyMod { get; private set; }

        /// <summary>Plugin Awake: the monitor got this far, so a session that ends without Closed() is reported next launch.</summary>
        public static void Running()
        {
            try
            {
                string path = Path.Combine(Root, MarkerFile);
                Directory.CreateDirectory(Root);
                string text = File.Exists(path) ? File.ReadAllText(path) : "";
                if (!text.StartsWith("started="))
                    text = "started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\n";
                File.WriteAllText(path, text.Replace("phase=preloader", "").TrimEnd() + "\nphase=plugin\n");
            }
            catch { }
        }

        /// <summary>
        /// This session's data folder, so the preloader can put a copy of this session's log in it at the next launch
        /// (one folder per session with everything in it). Same file name as in the preloader's SessionLogs.
        /// </summary>
        public static void RememberFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, "last-session-folder.txt"), folder);
            }
            catch { }
        }

        /// <summary>The game is quitting normally.</summary>
        public static void Closed()
        {
            try { File.Delete(Path.Combine(Root, MarkerFile)); } catch { }
        }

        /// <summary>Plugin Start (mods are known by now): log what the preloader found about the last session.</summary>
        public static void Report(ModRegistry registry, ManualLogSource log)
        {
            string text = AppDomain.CurrentDomain.GetData(CrashKey) as string;
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Split('\n');
            // The code frames from Unity's crash report, else the stack of the last error in the log.
            var frames = Section(lines, "Code running when it crashed");
            string likely = frames.Count > 0 ? LogWatch.FirstMod(frames, registry) : null;
            // Only a real crash points at a mod; a session closed from outside (freeze, Task Manager) says nothing
            // about the last errors, which may be minutes old.
            if (likely == null && text.Contains("It crashed:"))
            {
                var errors = Section(lines, "Last errors in the log");
                var named = errors.Select(ErrorSource).Where(s => s != null)
                                  .Select(s => registry.Visible.FirstOrDefault(m => m.IsPlugin && m.Name == s)?.Name).LastOrDefault(s => s != null);
                likely = named ?? LogWatch.FirstMod(errors.AsEnumerable().Reverse(), registry);
            }
            if (likely != null)
            {
                text += $"\nMost likely involved: {likely} (the first mod in the {(frames.Count > 0 ? "crash's code" : "last errors")}). " +
                        "That is where it happened, not always the cause.\n";
                try { File.WriteAllText(Path.Combine(Root, CrashFile), text); } catch { }
            }
            LastCrash = text;
            LikelyMod = likely;
            log.LogWarning("Last session did not close normally. Details are also in " + Path.Combine(Root, CrashFile) + "\n" + text);
        }

        public sealed class PastSession
        {
            public string File, Status;
            public DateTime Ended;
            public double SizeKb;
            public int Errors, Warnings, Lines;
            public List<(string name, int errors)> ErrorSources = new List<(string, int)>();
        }

        public static string LogFolder => Path.Combine(Root, "session_logs");
        public static string CrashReportPath => Path.Combine(Root, CrashFile);

        /// <summary>The saved session logs, newest first (only the file names: see Summarize).</summary>
        public static List<FileInfo> SavedLogs()
        {
            if (!Directory.Exists(LogFolder)) return new List<FileInfo>();
            return new DirectoryInfo(LogFolder).GetFiles("*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>One saved log: how it ended (from its name) and its errors and warnings per log name.</summary>
        public static PastSession Summarize(FileInfo file)
        {
            var s = new PastSession
            {
                File = file.Name, Ended = file.LastWriteTime, SizeKb = Math.Round(file.Length / 1024.0, 0),
                Status = file.Name.Contains("_crashed") ? "crashed" : file.Name.Contains("_not-closed") ? "did not close normally (froze and was closed, Task Manager, or PC shut down)" : "closed normally",
            };
            var sources = new Dictionary<string, int>();
            foreach (var line in ReadLines(file.FullName))
            {
                s.Lines++;
                if (line.StartsWith("[Warning")) s.Warnings++;
                else if (line.StartsWith("[Error") || line.StartsWith("[Fatal"))
                {
                    s.Errors++;
                    string src = ErrorSource(line) ?? "?";
                    sources[src] = sources.TryGetValue(src, out int n) ? n + 1 : 1;
                }
            }
            s.ErrorSources = sources.OrderByDescending(k => k.Value).Take(5).Select(k => (k.Key, k.Value)).ToList();
            return s;
        }

        public static IEnumerable<string> ReadLines(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
            {
                string l;
                while ((l = r.ReadLine()) != null) yield return l;
            }
        }

        // The indented or bracketed lines under a heading of the report.
        private static List<string> Section(string[] lines, string heading)
        {
            var result = new List<string>();
            int start = Array.FindIndex(lines, l => l.StartsWith(heading));
            if (start < 0) return result;
            for (int i = start + 1; i < lines.Length && lines[i].Trim().Length > 0; i++)
                result.Add(lines[i].Trim());
            return result;
        }

        // "[Error  :Some Mod] message" -> "Some Mod"
        private static string ErrorSource(string line)
        {
            if (!line.StartsWith("[")) return null;
            int colon = line.IndexOf(':'), close = line.IndexOf(']');
            return colon > 0 && close > colon ? line.Substring(colon + 1, close - colon - 1).Trim() : null;
        }
    }
}
