using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Public.Patching;

namespace ValPerfMonitor
{
    /// <summary>
    /// HarmonyX never lets go of a method once it has patched it: removing the last patch rebuilds the method with no
    /// patches and keeps MonoMod's hook and its full copy of the method's code (Mono.Cecil objects, ~500 per method).
    /// After the monitor's probes come off, ~3,000 such copies (1.5 million objects) stayed alive, and every full
    /// garbage collection has to walk them: about 75 ms more per collection, in every later Baseline too.
    /// For a method with no patches left at all, this throws the hook away (the method runs its own code again,
    /// as before anything patched it) and clears Harmony's reference, so a later patch simply makes a new one.
    /// Checks Harmony looks as analysed (ManagedMethodPatcher.ilHook, an IDisposable) and stands down if not.
    /// </summary>
    internal static class HarmonyCopies
    {
        private static readonly FieldInfo HookField = AccessTools.Field(typeof(ManagedMethodPatcher), "ilHook");
        // The patcher also keeps the last method body it built (only for error messages): a second copy of the same
        // code, which kept 900,000 objects alive after the hooks were gone (2026-10-26 test).
        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(ManagedMethodPatcher), "hookBody");
        private static bool _broken, _reported;

        /// <summary>Frees the copies of these methods that no patch uses any more. Returns how many were freed.</summary>
        public static int FreeUnused(IEnumerable<MethodBase> methods, ManualLogSource log)
        {
            if (_broken) return 0;
            if (HookField == null || !typeof(IDisposable).IsAssignableFrom(HookField.FieldType) || BodyField == null || BodyField.FieldType.IsValueType)
            {
                StandDown(log, "ManagedMethodPatcher.ilHook or hookBody is not there");
                return 0;
            }
            int freed = 0;
            foreach (var m in methods)
            {
                if (m == null) continue;
                try
                {
                    var info = Harmony.GetPatchInfo(m);
                    if (info != null && (info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count + info.ILManipulators.Count) > 0)
                        continue; // still patched by someone
                    if (!(m.GetMethodPatcher() is ManagedMethodPatcher patcher)) continue;
                    if (!(HookField.GetValue(patcher) is IDisposable hook)) continue;
                    hook.Dispose();
                    HookField.SetValue(patcher, null);
                    BodyField.SetValue(patcher, null);
                    freed++;
                }
                catch (Exception e)
                {
                    StandDown(log, $"{Instrumenter.Describe(m)}: {e.GetType().Name} {e.Message}");
                    break;
                }
            }
            return freed;
        }

        /// <summary>Same, with a log line saying how many copies were freed and how long it took.</summary>
        public static void FreeAndReport(IEnumerable<MethodBase> methods, ManualLogSource log, string what)
        {
            long t0 = Stopwatch.GetTimestamp();
            int n = FreeUnused(methods, log);
            if (n > 0)
                log.LogInfo($"Freed Harmony's copies of {n} methods {what} ({(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:0} ms), so they no longer slow down garbage collection.");
        }

        private static void StandDown(ManualLogSource log, string why)
        {
            _broken = true;
            if (_reported) return;
            _reported = true;
            log.LogInfo($"Harmony is not as expected ({why}): its copies of methods the monitor stopped timing are kept (they make garbage collection a little slower until the game restarts).");
        }
    }
}
