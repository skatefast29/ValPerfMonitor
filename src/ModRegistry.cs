using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace ValPerfMonitor
{
    /// <summary>A row in the results: one mod, or one game system (a vanilla class's update loops).</summary>
    internal sealed class ModEntry
    {
        public int Index;
        public string Name;
        public string Guid;
        public string Version;
        public bool Hidden;      // calibration bucket, never shown or logged
        public bool IsPlugin;    // a real BepInEx plugin (vs. a library assembly that owns patches)
        public bool IsGame;      // a vanilla Valheim system, not a mod

        // What we found while scanning.
        public int Prefixes, Postfixes, Finalizers, Transpilers, UnityCallbacks, Coroutines, Instrumented, Failed;

        // Rolling window (overlay). Values are in the capture's display unit (ms or KB).
        public float[] Ring;
        public int[] CallRing;
        public float WindowAvg, WindowMax, WindowCallsPerFrame;
        public long WindowCalls;

        // Current log interval.
        public double IvSum; public float IvMax; public long IvCalls; public int IvSpikeFrames;

        // Since the capture started.
        public double TotSum; public float TotMax; public long TotCalls;

        public int PatchCount => Prefixes + Postfixes + Finalizers + Transpilers;
        public string Kind => IsGame ? "game" : IsPlugin ? "mod" : "library";

        public void ResetValues()
        {
            IvSum = 0; IvMax = 0; IvCalls = 0; IvSpikeFrames = 0;
            TotSum = 0; TotMax = 0; TotCalls = 0;
            WindowAvg = 0; WindowMax = 0; WindowCalls = 0; WindowCallsPerFrame = 0;
            Array.Clear(Ring, 0, Ring.Length);
            Array.Clear(CallRing, 0, CallRing.Length);
        }
    }

    internal sealed class SourceInfo
    {
        public int Id = -1;
        public ModEntry Mod;
        public MethodBase Method;    // the method that carries the probe
        public string Kind;          // Prefix / Postfix / Finalizer / Update / ... / Game
        public string Name;          // Type.Method
        public bool Attached;
        public bool LiveClass;       // its class has live objects (or is core game code): already loaded, safe to hook
        public readonly List<string> Targets = new List<string>(); // game methods it is attached to
        public readonly List<MethodBase> TargetMethods = new List<MethodBase>();
    }

    /// <summary>Maps assemblies, Harmony owner ids and game classes to the row they are reported under.</summary>
    internal sealed class ModRegistry
    {
        public readonly List<ModEntry> Mods = new List<ModEntry>();
        public readonly List<SourceInfo> Sources = new List<SourceInfo>();
        public readonly List<Assembly> PluginAssemblies = new List<Assembly>();

        private readonly Dictionary<Assembly, ModEntry> _byAssembly = new Dictionary<Assembly, ModEntry>();
        private readonly Dictionary<string, ModEntry> _byGuid = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModEntry> _byLooseName = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Type, ModEntry> _gameByType = new Dictionary<Type, ModEntry>();
        private readonly int _ringSize;

        public ModRegistry(int ringSize)
        {
            _ringSize = ringSize;
            Add("(calibration)", "", "", hidden: true, isPlugin: false, isGame: false); // index 0
        }

        public ModEntry Calibration => Mods[0];

        public IEnumerable<ModEntry> Visible => Mods.Where(m => !m.Hidden);

        public void LoadPlugins(Assembly self)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                Assembly asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                if (asm == null && !string.IsNullOrEmpty(info.Location))
                    asm = loaded.FirstOrDefault(a => !a.IsDynamic && SafeLocation(a) == info.Location);
                if (asm == null || asm == self)
                    continue;

                if (_byAssembly.TryGetValue(asm, out var existing))
                {
                    // Several plugins in one DLL: keep one row, remember every GUID.
                    _byGuid[info.Metadata.GUID] = existing;
                    continue;
                }
                var mod = Add(info.Metadata.Name, info.Metadata.GUID, info.Metadata.Version?.ToString() ?? "", false, true, false);
                _byAssembly[asm] = mod;
                PluginAssemblies.Add(asm);
            }
        }

        /// <summary>Find the mod that owns a patch method: by its assembly first, then by Harmony owner id.</summary>
        public ModEntry Resolve(Assembly asm, string harmonyOwner)
        {
            if (asm != null && _byAssembly.TryGetValue(asm, out var mod))
                return mod;
            if (!string.IsNullOrEmpty(harmonyOwner) && _byGuid.TryGetValue(harmonyOwner, out mod))
                return mod;

            string name = asm?.GetName().Name ?? harmonyOwner ?? "(unknown)";
            if (asm != null && (name == "BepInEx" || name.StartsWith("BepInEx.") || name == "0Harmony" || name.StartsWith("MonoMod")))
                name = "BepInEx / Harmony (core)";
            else
                name = "[lib] " + name;

            if (!_byLooseName.TryGetValue(name, out mod))
            {
                mod = Add(name, harmonyOwner ?? "", "", false, false, false);
                _byLooseName[name] = mod;
            }
            if (asm != null)
                _byAssembly[asm] = mod;
            return mod;
        }

        public ModEntry ForPluginAssembly(Assembly asm) => _byAssembly.TryGetValue(asm, out var m) ? m : null;

        /// <summary>The row for a vanilla class, e.g. "Game: WearNTear".</summary>
        public ModEntry ForGameType(Type t)
        {
            if (!_gameByType.TryGetValue(t, out var mod))
            {
                mod = Add("Game: " + t.Name, "", "", false, false, true);
                _gameByType[t] = mod;
            }
            return mod;
        }

        /// <summary>Index a source by its probe id once it has one.</summary>
        public void Store(SourceInfo s)
        {
            while (Sources.Count <= s.Id)
                Sources.Add(null);
            Sources[s.Id] = s;
        }

        private ModEntry Add(string name, string guid, string version, bool hidden, bool isPlugin, bool isGame)
        {
            var mod = new ModEntry
            {
                Index = Mods.Count, Name = name, Guid = guid, Version = version,
                Hidden = hidden, IsPlugin = isPlugin, IsGame = isGame,
                Ring = new float[_ringSize], CallRing = new int[_ringSize],
            };
            Mods.Add(mod);
            if (!string.IsNullOrEmpty(guid))
                _byGuid[guid] = mod;
            Probe.EnsureModCapacity(Mods.Count);
            return mod;
        }

        private static string SafeLocation(Assembly a)
        {
            try { return a.Location; } catch { return null; }
        }
    }
}
