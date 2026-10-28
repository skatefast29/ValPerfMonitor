using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;

namespace ValPerfMonitor
{
    /// <summary>
    /// Server hosts take plain file uploads, and BepInEx\patchers is easy to miss (2026-10-28: the patcher was left out
    /// of a server for days, so no past session logs were kept). The plugin carries a copy of ValPerfMonitor.Preloader.dll
    /// and writes it to BepInEx\patchers\ValPerfMonitor when no copy is there; it works from the next restart.
    /// </summary>
    internal static class PreloaderInstall
    {
        private const string FileName = "ValPerfMonitor.Preloader.dll";

        public static void EnsureOnServer(ManualLogSource log)
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ValPerfMonitor.Preloader");
                if (loaded != null) { UpdateIfOld(loaded, log); return; }
                // A copy that is there but didn't load (disabled as .dll.old, or broken): the owner's choice, left alone.
                var existing = Directory.Exists(Paths.PatcherPluginPath)
                    ? Directory.GetFiles(Paths.PatcherPluginPath, FileName + "*", SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (existing != null)
                {
                    log.LogWarning($"The monitor's patcher is at {existing} but didn't load, so past session logs and per-mod startup times are off.");
                    return;
                }
                using (var res = typeof(PreloaderInstall).Assembly.GetManifestResourceStream(FileName))
                {
                    if (res == null) return;
                    string folder = Path.Combine(Paths.PatcherPluginPath, "ValPerfMonitor");
                    Directory.CreateDirectory(folder);
                    string target = Path.Combine(folder, FileName);
                    using (var file = File.Create(target)) res.CopyTo(file);
                    log.LogInfo($"The monitor's patcher was missing, so it was installed at {target}. From the next restart the server keeps copies of past logs in BepInEx/ValPerfMonitor/session_logs.");
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"The monitor's patcher is missing or old and could not be installed ({e.Message}). Upload {FileName} to BepInEx/patchers/ValPerfMonitor to keep past session logs.");
            }
        }

        /// <summary>The patcher that loaded is a different build from the one this plugin carries: replace the file (used from the next restart).</summary>
        private static void UpdateIfOld(System.Reflection.Assembly loaded, ManualLogSource log)
        {
            string path;
            try { path = loaded.Location; } catch { return; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            using (var res = typeof(PreloaderInstall).Assembly.GetManifestResourceStream(FileName))
            {
                if (res == null) return;
                var carried = new byte[res.Length];
                int read = 0;
                while (read < carried.Length) { int n = res.Read(carried, read, carried.Length - read); if (n <= 0) break; read += n; }
                if (File.ReadAllBytes(path).SequenceEqual(carried)) return;
                File.WriteAllBytes(path, carried);
                log.LogInfo($"The monitor's patcher at {path} was from another version, so it was replaced with this one's (used from the next restart).");
            }
        }
    }
}
