using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace ValPerfMonitor.Preloader
{
    /// <summary>
    /// Keeps a copy of the previous session's LogOutput.log. BepInEx empties that file when it starts the
    /// chainloader, which is after preloader patchers start, so the whole log of the last session (including one
    /// that ended in a crash) is still there at this point. On by default; the switches live in the main plugin's
    /// config file, which is only read here (the main plugin binds them too, so they show up in config managers).
    /// </summary>
    internal static class SessionLogs
    {
        // Same names as in ValPerfMonitor's Plugin.cs.
        internal const string Section = "3 - Logging";
        internal const string KeepKey = "Keep past session logs";
        internal const string DaysKey = "Days to keep past session logs";

        public static void SaveLast()
        {
            var log = Logger.CreateLogSource("ValPerfMonitor");
            try
            {
                var config = new ConfigFile(Path.Combine(Paths.ConfigPath, "valheimmods.ValPerfMonitor.cfg"), false);
                config.SaveOnConfigSet = false;
                if (!config.Bind(Section, KeepKey, true).Value)
                    return;
                int days = config.Bind(Section, DaysKey, 14).Value;

                string folder = Path.Combine(Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor"), "session_logs");
                var source = new FileInfo(Path.Combine(Paths.BepInExRootPath, "LogOutput.log"));
                if (source.Exists && source.Length > 0)
                {
                    Directory.CreateDirectory(folder);
                    // Named by when the session ended (the log's last write).
                    string target = Path.Combine(folder, source.LastWriteTime.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
                    if (!File.Exists(target))
                    {
                        File.Copy(source.FullName, target);
                        log.LogInfo($"Past session logs: saved the previous session's log as session_logs\\{Path.GetFileName(target)}.");
                    }
                }

                if (days > 0 && Directory.Exists(folder))
                {
                    var cutoff = DateTime.Now.AddDays(-days);
                    foreach (var file in new DirectoryInfo(folder).GetFiles("*.log"))
                        if (file.LastWriteTime < cutoff)
                            file.Delete();
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"Past session logs: could not save the previous session's log - {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
