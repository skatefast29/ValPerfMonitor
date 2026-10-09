using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;

namespace ValPerfMonitor
{
    /// <summary>
    /// Keeps hooking from crashing the game.
    ///
    /// Attaching a probe makes Unity's Mono runtime compile the method. If the method's class can't fully
    /// load (typically a mod's compatibility code for another mod that isn't installed), Mono aborts the
    /// whole game instead of throwing. Two defences:
    ///  1. Only hook classes that are known to be loaded (live Unity objects, core game classes), and for
    ///     mod patch methods initialise the class first through a path that throws instead of aborting.
    ///  2. Breadcrumbs: the method about to be hooked is written to disk first. If the game dies while
    ///     hooking, the next launch finds the breadcrumb, adds that method to a permanent skip list and
    ///     carries on, so a crash can happen at most once per bad method.
    /// </summary>
    internal sealed class SafetyGuard
    {
        private readonly ManualLogSource _log;
        private readonly string _crumbPath, _skipPath;
        private readonly HashSet<string> _skip = new HashSet<string>(StringComparer.Ordinal);
        // Many mods bundle the same helper libraries (ItemManager, PieceManager, ...), often in different
        // versions. A method that crashed is also skipped in every other mod carrying that library method:
        // same class, method name and parameter count.
        private readonly Dictionary<string, string> _skipCopies = new Dictionary<string, string>(StringComparer.Ordinal);
        private StreamWriter _crumb;
        public readonly List<string> Rejected = new List<string>();

        public SafetyGuard(string folder, ManualLogSource log)
        {
            _log = log;
            _crumbPath = Path.Combine(folder, "hooking-in-progress.txt");
            _skipPath = Path.Combine(folder, "skip-methods.txt");
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(_skipPath))
                    foreach (var line in File.ReadAllLines(_skipPath))
                        if (line.Length > 0 && !line.StartsWith("#")) _skip.Add(line.Trim());

                if (File.Exists(_crumbPath))
                {
                    string last = File.ReadAllLines(_crumbPath).LastOrDefault(l => l.Length > 2);
                    File.Delete(_crumbPath);
                    if (last != null)
                    {
                        string key = last.Substring(2);
                        if (_skip.Add(key))
                        {
                            File.AppendAllText(_skipPath,
                                (File.Exists(_skipPath) ? "" : "# Methods ValPerfMonitor will never hook (added automatically after a crash). Delete a line to retry it.\n") + key + "\n");
                        }
                        _log.LogWarning($"The game closed unexpectedly last time while hooking: {key}. That method will be skipped from now on (see {_skipPath}).");
                    }
                }
            }
            catch (Exception e) { _log.LogWarning($"Safety files could not be read: {e.Message}"); }
        }

        public static string Key(MethodBase m)
        {
            var t = m.DeclaringType;
            string ps;
            try { ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)); }
            catch { ps = "?"; }
            return $"{t?.Assembly.GetName().Name}|{t?.FullName}|{m.Name}({ps})";
        }

        public bool IsSkipped(MethodBase m)
        {
            if (_skip.Count == 0) return false;
            if (_skip.Contains(Key(m))) return true;
            string sig = CopySignature(m);
            if (sig != null && _skipCopies.TryGetValue(sig, out var original))
            {
                _log.LogInfo($"Skipping {Key(m)}: same library method as {original}, which crashed the game when hooked.");
                return true;
            }
            return false;
        }

        private static string CopySignature(MethodBase m)
        {
            try
            {
                return $"{m.DeclaringType?.FullName}|{m.Name}|{m.GetParameters().Length}";
            }
            catch { return null; }
        }

        /// <summary>Resolve skip-list entries to their code signatures (call once all plugins are loaded).</summary>
        public void Prime()
        {
            if (_skip.Count == 0) return;
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var key in _skip)
            {
                try
                {
                    var parts = key.Split('|');
                    if (parts.Length < 3) continue;
                    var asm = loaded.FirstOrDefault(a => a.GetName().Name == parts[0]);
                    var type = asm?.GetType(parts[1], false);
                    if (type == null) continue;
                    string name = parts[2].Substring(0, parts[2].IndexOf('('));
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                 .Where(x => x.Name == name && Key(x) == key))
                    {
                        string sig = CopySignature(m);
                        if (sig != null) _skipCopies[sig] = key;
                    }
                }
                catch { }
            }
        }

        /// <summary>Write the breadcrumb before a risky operation ('P' = attach probe, 'R' = rebuild).</summary>
        public void Before(char kind, MethodBase m)
        {
            try
            {
                if (_crumb == null)
                    _crumb = new StreamWriter(_crumbPath, false) { AutoFlush = true };
                _crumb.WriteLine(kind + " " + Key(m));
            }
            catch { }
        }

        /// <summary>All queued hooking finished without a crash: the breadcrumb is no longer needed.</summary>
        public void AllDone()
        {
            if (_crumb == null) return; // nothing hooked since the last cleanup
            try
            {
                _crumb?.Dispose();
                _crumb = null;
                if (File.Exists(_crumbPath)) File.Delete(_crumbPath);
            }
            catch { }
        }

        /// <summary>
        /// Make sure the method's class can be initialized before anything compiles the method. On Mono,
        /// RunClassConstructor reports a broken class (missing base type, field type or dependency) as an
        /// ordinary exception, whereas compiling a method of that class aborts the game. Runs the class's
        /// static constructor if it hasn't run yet, which is what calling the method would do anyway.
        /// </summary>
        /// <param name="runClassConstructor">false before the game is ready: a mod's static setup run that early could fail and break the mod for the session.</param>
        public bool Validate(MethodBase m, bool runClassConstructor = true)
        {
            try
            {
                var t = m.DeclaringType;
                if (t == null) return Reject(m, "no declaring type");
                if (t.ContainsGenericParameters) return Reject(m, "open generic class");
                for (var b = t; b != null; b = b.BaseType) { _ = b.TypeHandle; }
                if (runClassConstructor) RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                foreach (var p in m.GetParameters()) _ = p.ParameterType;
                if (m is MethodInfo mi) _ = mi.ReturnType;
                if (m.GetMethodBody() == null) return Reject(m, "no method body");
                return true;
            }
            catch (Exception e)
            {
                var inner = e;
                while ((inner is TargetInvocationException || inner is TypeInitializationException) && inner.InnerException != null)
                    inner = inner.InnerException;
                return Reject(m, $"{inner.GetType().Name}: {inner.Message}");
            }
        }

        private bool Reject(MethodBase m, string why)
        {
            Rejected.Add($"{Key(m)}  ({why})");
            return false;
        }
    }
}
