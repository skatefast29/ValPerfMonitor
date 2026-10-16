using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValPerfMonitor
{
    /// <summary>
    /// How much a world-generation mod adds to the game's alternative biome check (AltBiomeWorldData,
    /// several seconds of every world load). That check asks the world generator for the biome and height
    /// of every point of a 2048 x 2048 map; timing each of those millions of calls would distort them, so
    /// instead a spread-out sample of the same calls is timed right now, with the mod's world-generator
    /// patches on and then briefly taken off (in the same frame, nothing else runs in between), and scaled
    /// up to the full map.
    /// </summary>
    internal static class WorldGenCost
    {
        public sealed class Result
        {
            public string Error, Mod, Note = "";
            public int Points, FullPoints, PatchesTakenOff;
            public double MicrosWith = double.NaN, MicrosWithout = double.NaN, SecondsWith = double.NaN, SecondsWithout = double.NaN;
            public List<string> Patches = new List<string>();
        }

        private sealed class Removed { public MethodBase Original; public Patch Patch; public HarmonyPatchType Type; }

        private const int Size = 2048;
        private const float MaxSqr = 110250000f; // outside this the game skips the point (ocean)

        public static Result Run(string modName, int points, ModRegistry registry, string monitorGuid, Instrumenter instrumenter)
        {
            var r = new Result();
            var wg = WorldGenerator.instance;
            if (wg == null) { r.Error = "not in a world yet"; return r; }
            var mod = FindMod(modName, registry, r);
            if (mod == null) return r;
            r.Mod = mod.Name;

            var removed = FindPatches(mod, registry, monitorGuid);
            r.PatchesTakenOff = removed.Count;
            r.Patches = removed.Select(x => $"{x.Type} {Instrumenter.Describe(x.Patch.PatchMethod)} on {Instrumenter.Describe(x.Original)}").ToList();
            if (removed.Count == 0) { r.Error = $"{mod.Name} has no patches on the world generator"; return r; }

            // Same points as the game's loop, every stride-th row and column.
            r.FullPoints = CountInside(1);
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)r.FullPoints / Math.Max(1000, points))));
            r.Points = CountInside(stride);

            // The monitor's probes on the mod's patch methods would be timed with the mod but not without it,
            // making the mod look slower than it is: they come off for the measurement.
            var probesOff = instrumenter?.TakeProbesOff(removed.Select(x => x.Patch.PatchMethod)) ?? new List<MethodBase>();
            try
            {
                try
                {
                    Sweep(wg, 16); // compile and warm up first
                    r.MicrosWith = Sweep(wg, stride) / r.Points;
                }
                catch (Exception e) { r.Error = "the world generator failed: " + e.Message; return r; }

                try
                {
                    foreach (var x in removed) new Harmony(x.Patch.owner).Unpatch(x.Original, x.Patch.PatchMethod);
                    Sweep(wg, 16);
                    r.MicrosWithout = Sweep(wg, stride) / r.Points;
                }
                catch (Exception e)
                {
                    r.Note = $"Without {mod.Name}'s patches the game's own world generator failed ({e.Message}): it needs the mod's data, so it can't be compared this way.";
                }
                finally
                {
                    PutBack(removed, r);
                }
            }
            finally
            {
                instrumenter?.PutProbesBack(probesOff);
            }

            r.SecondsWith = r.MicrosWith * r.FullPoints / 1e6;
            if (!double.IsNaN(r.MicrosWithout)) r.SecondsWithout = r.MicrosWithout * r.FullPoints / 1e6;
            return r;
        }

        /// <summary>Microseconds for the game's per-point work at every stride-th point.</summary>
        private static double Sweep(WorldGenerator wg, int stride)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = stride / 2; i < Size; i += stride)
                for (int j = stride / 2; j < Size; j += stride)
                {
                    float x = AltBiomeWorldData.MapSpaceToWorldSpace(j), y = AltBiomeWorldData.MapSpaceToWorldSpace(i);
                    if (x * x + y * y > MaxSqr) continue;
                    var biome = wg.GetBiome(x, y);
                    wg.GetBiomeHeight(biome, x, y, out _);
                }
            return (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }

        private static int CountInside(int stride)
        {
            int n = 0;
            for (int i = stride / 2; i < Size; i += stride)
                for (int j = stride / 2; j < Size; j += stride)
                {
                    float x = AltBiomeWorldData.MapSpaceToWorldSpace(j), y = AltBiomeWorldData.MapSpaceToWorldSpace(i);
                    if (x * x + y * y <= MaxSqr) n++;
                }
            return Math.Max(1, n);
        }

        private static ModEntry FindMod(string name, ModRegistry registry, Result r)
        {
            name = (name ?? "").Trim();
            var mods = registry.Visible.Where(m => !m.IsGame).ToList();
            var hit = mods.Where(m => string.Equals(m.Guid, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0) hit = mods.Where(m => m.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count == 1) return hit[0];
            r.Error = hit.Count == 0 ? $"no mod called '{name}'" : $"'{name}' matches several mods: {string.Join(", ", hit.Take(8).Select(m => m.Name))}";
            return null;
        }

        private static List<Removed> FindPatches(ModEntry mod, ModRegistry registry, string monitorGuid)
        {
            var list = new List<Removed>();
            foreach (var original in Harmony.GetAllPatchedMethods().Where(m => m.DeclaringType == typeof(WorldGenerator)).ToList())
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(original); } catch { continue; }
                if (info == null) continue;
                void Take(IEnumerable<Patch> patches, HarmonyPatchType type)
                {
                    foreach (var p in patches)
                        if (p.PatchMethod != null && !p.owner.StartsWith(monitorGuid, StringComparison.Ordinal) &&
                            registry.Resolve(p.PatchMethod.DeclaringType?.Assembly, p.owner) == mod)
                            list.Add(new Removed { Original = original, Patch = p, Type = type });
                }
                Take(info.Prefixes, HarmonyPatchType.Prefix);
                Take(info.Postfixes, HarmonyPatchType.Postfix);
                Take(info.Transpilers, HarmonyPatchType.Transpiler);
                Take(info.Finalizers, HarmonyPatchType.Finalizer);
                Take(info.ILManipulators, HarmonyPatchType.ILManipulator);
            }
            return list;
        }

        /// <summary>Put every patch back exactly as it was (same owner, priority and ordering).</summary>
        private static void PutBack(List<Removed> removed, Result r)
        {
            int failed = 0;
            foreach (var x in removed)
            {
                var p = x.Patch;
                try
                {
                    var info = Harmony.GetPatchInfo(x.Original);
                    if (info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators)
                            .Any(y => y.PatchMethod == p.PatchMethod)) continue;
                }
                catch { }
                var hm = new HarmonyMethod(p.PatchMethod) { priority = p.priority, before = p.before, after = p.after, debug = p.debug };
                try
                {
                    var h = new Harmony(p.owner);
                    switch (x.Type)
                    {
                        case HarmonyPatchType.Prefix: h.Patch(x.Original, prefix: hm); break;
                        case HarmonyPatchType.Postfix: h.Patch(x.Original, postfix: hm); break;
                        case HarmonyPatchType.Transpiler: h.Patch(x.Original, transpiler: hm); break;
                        case HarmonyPatchType.Finalizer: h.Patch(x.Original, finalizer: hm); break;
                        case HarmonyPatchType.ILManipulator: h.Patch(x.Original, ilmanipulator: hm); break;
                    }
                }
                catch { failed++; }
            }
            if (failed > 0)
                r.Note += $" {failed} patch(es) could not be put back: restart the game before playing on.";
        }
    }
}
