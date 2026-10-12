using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// Starts before every other mod (the GUID sorts first) so the startup of chosen mods can be split
    /// into their own methods. Does nothing unless "Split startup" is set in ValPerfMonitor's config.
    /// </summary>
    [BepInPlugin(Guid, "ValPerfMonitor early start", Plugin.Version)]
    public sealed class EarlyStart : BaseUnityPlugin
    {
        public const string Guid = "__valheimmods.ValPerfMonitor.Early";

        private void Awake()
        {
            try { StartupDetail.Begin(Logger); }
            catch (Exception e) { Logger.LogWarning($"Startup split not started: {e.Message}"); }
        }
    }

    /// <summary>
    /// Splits chosen mods' startup into their own methods: when the game loads a chosen mod's DLL (just
    /// before its Awake runs), every method of the mod's own code gets a timer. Once the main menu has
    /// settled the timers are all removed again, so play and in-world tests are unaffected.
    /// Bundled helper libraries (ItemManager, ServerSync, ...) are left out: they are shared by many mods
    /// and hooking them has crashed the game before; their time shows inside the mod method that calls them.
    /// </summary>
    internal static class StartupDetail
    {
        public const string Section = "6 - Startup split";
        public const string ModsKey = "Split startup of these mods";
        public const string SlowestKey = "Split startup of the slowest mods";
        public const string ModsHelp = "Comma-separated mod names or GUIDs (as in startup_mods.csv) whose startup is split into their own methods on the next launch, e.g. 'RtDOcean, Bestiary'. Results: the log, startup_methods.csv and perf/load_times. Adds a little to those mods' startup while on; the timers are removed once the main menu has settled.";
        public const string SlowestHelp = "Also split the startup of this many of the slowest mods from the last launch (0 = off).";

        private static readonly string[] Libraries =
        {
            "ItemManager", "PieceManager", "ServerSync", "LocalizationManager", "CreatureManager", "StatusEffectManager",
            "SkillManager", "LocationManager", "AnimationReplaceManager", "Groups", "fastJSON", "YamlDotNet", "Newtonsoft",
            "Jotunn", "Microsoft", "System", "Mono", "JetBrains", "Costura", "MonoMod", "HarmonyLib", "BepInEx", "UnityEngine",
        };

        public sealed class MethodStat { public string Mod, Name; public int Calls; public long Total, Self; internal int Depth; }
        public sealed class ModResult { public string Name, Guid; public int Methods, Skipped; public double HookSeconds; }

        private static ManualLogSource _log;
        private static Harmony _harmony;
        private static SafetyGuard _guard;
        private static HashSet<string> _wanted;
        private static readonly HashSet<string> _found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _mainThread;
        private static bool _active;
        private static readonly HashSet<Assembly> _seen = new HashSet<Assembly>();
        private static readonly Dictionary<MethodBase, MethodStat> _byMethod = new Dictionary<MethodBase, MethodStat>();
        public static readonly List<ModResult> Mods = new List<ModResult>();
        public static List<MethodStat> Results = new List<MethodStat>();
        public static bool Finished;

        // Call stack of timed methods on the main thread (start time, time spent in timed callees).
        private static MethodStat[] _stack = new MethodStat[256];
        private static long[] _start = new long[256], _child = new long[256];
        private static int _depth;

        public static bool Active => _active;

        public static void Begin(ManualLogSource log)
        {
            _log = log;
            var cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, Plugin.Guid + ".cfg"), true);
            string names = cfg.Bind(Section, ModsKey, "", ModsHelp).Value;
            int slowest = cfg.Bind(Section, SlowestKey, 0, new ConfigDescription(SlowestHelp, new AcceptableValueRange<int>(0, 20))).Value;

            _wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names.Split(','))
                if (n.Trim().Length > 0) _wanted.Add(Norm(n));
            if (slowest > 0)
                foreach (var g in SlowestLastTime(slowest))
                    _wanted.Add(Norm(g));
            _wanted.Remove(Norm(Plugin.Guid));
            _wanted.Remove(Norm(Plugin.Name));
            if (_wanted.Count == 0) return;

            _active = true;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _harmony = new Harmony(Plugin.Guid + ".startupsplit");
            _guard = new SafetyGuard(Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor"), log);
            _log.LogInfo($"Splitting startup of: {string.Join(", ", _wanted.ToArray())}.");
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Consider(e.LoadedAssembly, early: false);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                Consider(asm, early: true);
        }

        // "RtDOcean 2.2.54" -> "rtdocean"; "More_World_Locations_AIO" -> "moreworldlocationsaio"
        private static string Norm(string s)
        {
            s = s.Trim();
            int sp = s.LastIndexOf(' ');
            if (sp > 0 && System.Version.TryParse(s.Substring(sp + 1), out _)) s = s.Substring(0, sp);
            var sb = new StringBuilder();
            foreach (char c in s)
                if (char.IsLetterOrDigit(c) || c == '.') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>GUIDs of the slowest mods in the most recent session that recorded per-mod startup times.</summary>
        private static IEnumerable<string> SlowestLastTime(int n)
        {
            try
            {
                var root = Path.Combine(Paths.BepInExRootPath, "ValPerfMonitor");
                if (!Directory.Exists(root)) return Array.Empty<string>();
                var file = Directory.GetDirectories(root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                    .Select(d => Path.Combine(d, "startup_mods.csv")).FirstOrDefault(File.Exists);
                if (file == null) return Array.Empty<string>();
                // rank,mod,guid,load_s,pct (the mod name may be quoted and contain commas)
                return File.ReadAllLines(file).Skip(1).Select(SplitCsv).Where(c => c.Count >= 3 && c[2].Length > 0)
                    .Select(c => c[2]).Where(g => !string.Equals(g, Plugin.Guid, StringComparison.OrdinalIgnoreCase)
                                                  && !string.Equals(g, EarlyStart.Guid, StringComparison.OrdinalIgnoreCase))
                    .Take(n).ToList();
            }
            catch (Exception e) { _log.LogWarning($"Could not read last launch's startup times: {e.Message}"); return Array.Empty<string>(); }
        }

        private static List<string> SplitCsv(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static void Consider(Assembly asm, bool early)
        {
            if (!_active || asm == null || asm.IsDynamic || !_seen.Add(asm)) return;
            try
            {
                string location;
                try { location = asm.Location; } catch { return; }
                if (string.IsNullOrEmpty(location) || !location.StartsWith(Paths.PluginPath, StringComparison.OrdinalIgnoreCase)) return;
                var types = SafeTypes(asm);
                foreach (var t in types)
                {
                    BepInPlugin meta;
                    try { meta = t.GetCustomAttributes(typeof(BepInPlugin), false).OfType<BepInPlugin>().FirstOrDefault(); } catch { continue; }
                    if (meta == null || !(_wanted.Contains(Norm(meta.GUID)) || _wanted.Contains(Norm(meta.Name)))) continue;
                    _found.Add(Norm(meta.GUID));
                    _found.Add(Norm(meta.Name));
                    if (early)
                        _log.LogWarning($"Startup split: {meta.Name} was already loaded before the monitor started, so it can't be split.");
                    else
                        Hook(asm, types, $"{meta.Name} {meta.Version}", meta.GUID);
                    return;
                }
            }
            catch (Exception e) { _log.LogWarning($"Startup split: could not check {asm.GetName().Name}: {e.Message}"); }
        }

        private static Type[] SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
        }

        private static bool IsLibrary(string ns)
        {
            if (string.IsNullOrEmpty(ns)) return false;
            int dot = ns.IndexOf('.');
            string root = dot < 0 ? ns : ns.Substring(0, dot);
            return Libraries.Any(l => string.Equals(l, root, StringComparison.Ordinal));
        }

        private static void Hook(Assembly asm, Type[] types, string modName, string guid)
        {
            long t0 = Stopwatch.GetTimestamp();
            var result = new ModResult { Name = modName, Guid = guid };
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(StartupDetail), nameof(Enter))) { priority = Priority.First };
            var finalizer = new HarmonyMethod(AccessTools.Method(typeof(StartupDetail), nameof(Leave))) { priority = Priority.Last };
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                try
                {
                    if (t.IsInterface || t.ContainsGenericParameters || IsLibrary(t.Namespace)) continue;
                    bool coroutine = false;
                    if (t.Name.StartsWith("<"))
                    {
                        // Only coroutine steps, and not ones nested in another compiler-generated class
                        // (those fail to compile when patched; see Instrumenter.ScanCoroutines).
                        if (!t.IsNested || t.DeclaringType == null || t.DeclaringType.Name.StartsWith("<") || t.IsValueType) continue;
                        if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t) || typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) continue;
                        coroutine = true;
                    }
                    bool insideGenerated = false;
                    for (var outer = t.DeclaringType; outer != null && !coroutine; outer = outer.DeclaringType)
                        insideGenerated |= outer.Name.StartsWith("<");
                    if (insideGenerated) continue;
                    foreach (var m in t.GetMethods(declared))
                    {
                        if (coroutine ? m.Name != "MoveNext" : (m.Name.Contains("<") || m.Name.StartsWith("get_") || m.Name.StartsWith("set_"))) continue;
                        if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
                        if (_guard.IsSkipped(m) || !_guard.Validate(m, runClassConstructor: false)) { result.Skipped++; continue; }
                        string name = coroutine ? CoroutineName(t) : $"{t.Name}.{m.Name}";
                        _byMethod[m] = new MethodStat { Mod = modName, Name = name };
                        try
                        {
                            _guard.Before('S', m);
                            _harmony.Patch(m, prefix: prefix, finalizer: finalizer);
                            result.Methods++;
                        }
                        catch (Exception e)
                        {
                            _byMethod.Remove(m);
                            result.Skipped++;
                            _log.LogDebug($"Startup split: could not time {name}: {e.Message}");
                        }
                    }
                }
                catch { }
            }
            _guard.AllDone();
            _guard.Rejected.Clear();
            result.HookSeconds = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            Mods.Add(result);
            _log.LogInfo($"Startup split: timing {result.Methods} methods of {modName} ({result.HookSeconds:0.00} s to attach, left out of its load time; {result.Skipped} skipped).");
        }

        // "<LoadAssets>d__12" nested in "MyMod.Loader" -> "Loader.LoadAssets (coroutine)"
        private static string CoroutineName(Type t)
        {
            string n = t.Name;
            int end = n.IndexOf('>');
            return $"{t.DeclaringType?.Name}.{(end > 1 ? n.Substring(1, end - 1) : n)} (coroutine)";
        }

        /// <summary>Time spent attaching timers to a mod (by GUID), to take out of its measured load time.</summary>
        public static double HookSeconds(string guid) =>
            Mods.Where(m => string.Equals(m.Guid, guid, StringComparison.OrdinalIgnoreCase)).Sum(m => m.HookSeconds);

        private static void Enter(MethodBase __originalMethod)
        {
            if (Finished || Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            if (!_byMethod.TryGetValue(__originalMethod, out var s)) return;
            if (_depth == _stack.Length)
            {
                Array.Resize(ref _stack, _depth * 2);
                Array.Resize(ref _start, _depth * 2);
                Array.Resize(ref _child, _depth * 2);
            }
            _stack[_depth] = s;
            _child[_depth] = 0;
            s.Depth++;
            _start[_depth++] = Stopwatch.GetTimestamp();
        }

        private static void Leave(MethodBase __originalMethod)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread || _depth == 0) return;
            if (!_byMethod.TryGetValue(__originalMethod, out var s) || _stack[_depth - 1] != s) return;
            int d = --_depth;
            long elapsed = Stopwatch.GetTimestamp() - _start[d];
            s.Self += elapsed - _child[d];
            s.Calls++;
            if (--s.Depth == 0) s.Total += elapsed; // recursion: only the outermost call counts towards the total
            if (d > 0) _child[d - 1] += elapsed;
        }

        /// <summary>Main menu settled: keep the results and remove every timer.</summary>
        public static void Finish()
        {
            if (!_active || Finished) return;
            Finished = true;
            Results = _byMethod.Values.Where(s => s.Calls > 0).OrderByDescending(s => s.Total).ToList();
            int stuck = 0;
            foreach (var m in _byMethod.Keys.ToList())
            {
                try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); }
                catch { stuck++; }
            }
            _byMethod.Clear();
            var missing = _wanted.Where(w => !_found.Contains(w)).ToList();
            if (missing.Count > 0) _log.LogWarning($"Startup split: no loaded mod matched {string.Join(", ", missing.ToArray())} (use the name or GUID as in startup_mods.csv).");
            if (stuck > 0) _log.LogWarning($"Startup split: {stuck} timers could not be removed (they now do nothing).");
        }

        public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        public static string Csv()
        {
            var sb = new StringBuilder("mod,method,calls,total_s,own_s\n");
            foreach (var s in Results)
                sb.Append(ValPerfMonitor.Csv.Q(s.Mod)).Append(',').Append(ValPerfMonitor.Csv.Q(s.Name)).Append(',')
                  .Append(s.Calls.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(ValPerfMonitor.Csv.F(Seconds(s.Total), "0.000")).Append(',').Append(ValPerfMonitor.Csv.F(Seconds(s.Self), "0.000")).Append('\n');
            return sb.ToString();
        }
    }
}
