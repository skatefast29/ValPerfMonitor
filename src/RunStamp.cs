using System;
using System.IO;
using System.Linq;

namespace ValPerfMonitor
{
    /// <summary>
    /// Game version and a short fingerprint of the mod list, saved with every run so runs from before and
    /// after a game or mod update aren't compared as if nothing changed.
    /// </summary>
    internal static class RunStamp
    {
        private static string _gameVersion;
        private static readonly System.Collections.Generic.HashSet<string> Checked = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string GameVersion
        {
            get
            {
                if (_gameVersion == null)
                {
                    try { _gameVersion = global::Version.CurrentVersion.ToString(); }
                    catch { _gameVersion = ""; }
                }
                return _gameVersion;
            }
        }

        /// <summary>8 hex characters from every plugin's name and version: same mods and versions, same code.</summary>
        public static string ModsFingerprint(ModRegistry reg)
        {
            uint h = 2166136261;
            foreach (var s in reg.Visible.Where(m => m.IsPlugin).Select(m => m.Name + " " + m.Version).OrderBy(s => s, StringComparer.Ordinal))
            {
                foreach (char ch in s) { h ^= ch; h *= 16777619; }
                h ^= '\n'; h *= 16777619;
            }
            return h.ToString("x8");
        }

        /// <summary>
        /// A CSV file started by an older version has an older header: replace that first line so the new
        /// columns are named (older rows just leave them empty). Call before appending.
        /// </summary>
        public static void UpgradeHeader(string path, string header)
        {
            if (!Checked.Add(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                string want = header.TrimEnd('\n');
                if (lines.Length == 0 || lines[0] == want) return;
                lines[0] = want;
                File.WriteAllLines(path, lines);
            }
            catch { }
        }
    }
}
