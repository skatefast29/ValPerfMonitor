using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace ValPerfMonitor.Preloader
{
    /// <summary>
    /// Keeps a copy of the previous session's LogOutput.log, and says when that session did not close normally.
    /// BepInEx empties that file when it starts the chainloader, which is after preloader patchers start, so the
    /// whole log of the last session (including one that ended in a crash) is still there at this point.
    ///
    /// Crash check: a marker file is written here at every launch and deleted by ValPerfMonitor when the game
    /// quits normally. If the marker is still there at the next launch, the session ended some other way: a
    /// crash (Unity then leaves a crash report in %TEMP%\IronGate\Valheim\Crashes), a freeze that was closed,
    /// Task Manager, or the PC shutting down. The findings go to last_crash.txt, and the main plugin logs them.
    ///
    /// On by default; the switches live in the main plugin's config file, which is only read here (the main
    /// plugin binds them too, so they show up in config managers).
    /// </summary>
    internal static class SessionLogs
    {
        // Same names as in ValPerfMonitor's Plugin.cs.
        internal const string Section = "3 - Logging";
        internal const string KeepKey = "Keep past session logs";
        internal const string DaysKey = "Days to keep past session logs";
        // AppDomain data the main plugin reads: the crash report text, when the last session did not close normally.
        internal const string CrashKey = "ValPerfMonitor.LastCrash";
        internal const string MarkerFile = "session-running.txt";
        internal const string CrashFile = "last_crash.txt";

        public static void SaveLast()
        {
            var log = Logger.CreateLogSource("ValPerfMonitor");
            try
            {
                string root = Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor");
                string folder = Path.Combine(root, "session_logs");
                var source = new FileInfo(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"));
                bool hasLog = source.Exists && source.Length > 0;

                // Did the last session close normally? Then write this session's marker.
                string marker = Path.Combine(root, MarkerFile);
                string previous = null;
                try { if (File.Exists(marker)) previous = File.ReadAllText(marker); } catch { }
                Directory.CreateDirectory(root);
                File.WriteAllText(marker, "started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\nphase=preloader\n");
                Crash crash = previous == null ? null : CheckCrash(previous, hasLog ? source : null);

                var config = new ConfigFile(Path.Combine(Paths.ConfigPath, "valheimmods.ValPerfMonitor.cfg"), false);
                config.SaveOnConfigSet = false;
                bool keep = config.Bind(Section, KeepKey, true).Value;
                int days = config.Bind(Section, DaysKey, 14).Value;

                string saved = null;
                if (keep && hasLog)
                {
                    Directory.CreateDirectory(folder);
                    // Named by when the session ended (the log's last write), marked when it didn't close normally.
                    string stamp = source.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                    string suffix = crash == null ? "" : crash.Folder != null ? "_crashed" : "_not-closed";
                    if (!Directory.GetFiles(folder, stamp + "*.log").Any())
                    {
                        saved = Path.Combine(folder, stamp + suffix + ".log");
                        File.Copy(source.FullName, saved);
                        log.LogInfo($"Past session logs: saved the previous session's log as session_logs\\{Path.GetFileName(saved)}.");
                    }
                    CopyToSessionFolder(root, source, suffix, log);
                }

                if (crash != null)
                {
                    string text = crash.Report(saved);
                    File.WriteAllText(Path.Combine(root, CrashFile), text);
                    AppDomain.CurrentDomain.SetData(CrashKey, text);
                }

                if (keep && days > 0 && Directory.Exists(folder))
                {
                    var cutoff = DateTime.Now.AddDays(-days);
                    foreach (var file in new DirectoryInfo(folder).GetFiles("*.log"))
                        if (file.LastWriteTime < cutoff)
                            file.Delete();
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"Past session logs: could not check or save the previous session's log - {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                log.Dispose();
            }
        }

        /// <summary>
        /// Also a copy in that session's own data folder (named by the monitor in last-session-folder.txt), so one folder
        /// holds the session's CSVs and its log. Only a folder under BepInEx\ValPerfMonitor that exists; the copy in
        /// session_logs stays (the F11 panel and perf/ commands read those, and they are cleaned up after N days).
        /// </summary>
        private static void CopyToSessionFolder(string root, FileInfo source, string suffix, ManualLogSource log)
        {
            string pointer = Path.Combine(root, "last-session-folder.txt");
            if (!File.Exists(pointer)) return;
            try
            {
                string target = File.ReadAllText(pointer).Trim();
                string full = Path.GetFullPath(target);
                if (full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                {
                    string copy = Path.Combine(full, "LogOutput" + suffix + ".log");
                    if (!File.Exists(copy))
                    {
                        File.Copy(source.FullName, copy);
                        log.LogInfo($"Past session logs: also saved it in that session's folder, {Path.GetFileName(full)}.");
                    }
                }
            }
            catch (Exception e) { log.LogWarning($"Past session logs: could not copy the log into its session folder - {e.Message}"); }
            finally { try { File.Delete(pointer); } catch { } }
        }

        private sealed class Crash
        {
            public DateTime Started, Ended;
            public bool PluginStarted;
            public string Folder;                                   // Unity's crash report folder, if there is one
            public List<string> CodeFrames = new List<string>();    // managed frames from Unity's crash stack trace
            public List<string> LastErrors = new List<string>();
            public List<string> LastLines = new List<string>();

            public string Report(string savedLog)
            {
                var sb = new StringBuilder();
                sb.Append($"The game session from {Started:yyyy-MM-dd HH:mm} to {Ended:HH:mm:ss} did not close normally");
                sb.Append(PluginStarted ? ".\n" : " (it ended while the mods were still loading).\n");
                if (Folder != null)
                    sb.Append("It crashed: Unity left a crash report in ").Append(Folder).Append("\n");
                else
                    sb.Append("No crash report from Unity: the game froze and was closed, was ended from Task Manager, or the PC shut down.\n");
                if (savedLog != null) sb.Append("Full log of that session: ").Append(savedLog).Append("\n");
                if (CodeFrames.Count > 0)
                    sb.Append("\nCode running when it crashed (innermost first):\n  ").Append(string.Join("\n  ", CodeFrames.ToArray())).Append("\n");
                if (LastErrors.Count > 0)
                    sb.Append("\nLast errors in the log:\n").Append(string.Join("\n", LastErrors.ToArray())).Append("\n");
                if (LastLines.Count > 0)
                    sb.Append("\nLast lines of the log:\n").Append(string.Join("\n", LastLines.ToArray())).Append("\n");
                return sb.ToString();
            }
        }

        /// <summary>The marker of the last session was left behind: work out how it ended. Null when it can't be told.</summary>
        private static Crash CheckCrash(string marker, FileInfo log)
        {
            var c = new Crash { PluginStarted = marker.Contains("phase=plugin") };
            foreach (var line in marker.Split('\n'))
                if (line.StartsWith("started=") && DateTime.TryParseExact(line.Substring(8).Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    c.Started = t;
            c.Ended = log != null ? log.LastWriteTime : DateTime.Now;
            if (c.Started == default) c.Started = c.Ended;

            // Unity's crash handler writes Crash_<time> folders with a copy of Player.log; take one from that session.
            try
            {
                string crashes = Path.Combine(Path.Combine(Path.Combine(Path.GetTempPath(), "IronGate"), "Valheim"), "Crashes");
                if (Directory.Exists(crashes))
                {
                    var dir = new DirectoryInfo(crashes).GetDirectories("Crash_*")
                        .Where(d => d.CreationTime >= c.Started.AddMinutes(-1) && d.CreationTime <= DateTime.Now)
                        .OrderByDescending(d => d.CreationTime).FirstOrDefault();
                    if (dir != null)
                    {
                        c.Folder = dir.FullName;
                        string player = Path.Combine(dir.FullName, "Player.log");
                        if (File.Exists(player)) c.CodeFrames = CrashFrames(player);
                    }
                }
            }
            catch { }

            // A session that never got as far as the monitor's plugin may simply not have had it on: only report it with a crash report.
            if (!c.PluginStarted && c.Folder == null) return null;

            if (log != null)
            {
                try
                {
                    var lines = ReadShared(log.FullName);
                    c.LastLines = lines.Where(l => l.Trim().Length > 0).Skip(Math.Max(0, lines.Count(l => l.Trim().Length > 0) - 12)).ToList();
                    // The last few errors, each with the lines that follow it (stack trace).
                    var errors = new List<string>();
                    for (int i = lines.Count - 1; i >= 0 && errors.Count < 3; i--)
                    {
                        if (!(lines[i].StartsWith("[Error") || lines[i].StartsWith("[Fatal"))) continue;
                        var block = new List<string> { lines[i] };
                        for (int k = i + 1; k < lines.Count && k <= i + 6 && !lines[k].StartsWith("["); k++)
                            if (lines[k].Trim().Length > 0) block.Add(lines[k]);
                        errors.Insert(0, string.Join("\n", block.ToArray()));
                    }
                    c.LastErrors = errors;
                }
                catch { }
            }
            return c;
        }

        // "0x00000275 (Mono JIT Code) MonoMod.RuntimeDetour.Detour:Apply ()" -> "MonoMod.RuntimeDetour.Detour:Apply ()"
        private static List<string> CrashFrames(string playerLog)
        {
            var frames = new List<string>();
            bool inside = false;
            foreach (var line in ReadShared(playerLog))
            {
                if (line.Contains("OUTPUTTING STACK TRACE")) { inside = true; continue; }
                if (!inside) continue;
                if (line.Contains("END OF STACKTRACE")) break;
                int at = line.IndexOf("(Mono JIT Code)", StringComparison.Ordinal);
                if (at < 0) continue;
                frames.Add(line.Substring(at + 15).Trim());
                if (frames.Count >= 20) break;
            }
            return frames;
        }

        private static List<string> ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs))
            {
                var lines = new List<string>();
                string l;
                while ((l = r.ReadLine()) != null) lines.Add(l);
                return lines;
            }
        }
    }
}
