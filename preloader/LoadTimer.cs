using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using Mono.Cecil;

namespace ValPerfMonitor.Preloader
{
    /// <summary>
    /// Runs before any mod loads (BepInEx preloader patcher) and timestamps BepInEx's own log lines:
    /// "Chainloader started", one "Loading [Mod x.y]" per mod, and "Chainloader startup complete".
    /// The time between two "Loading" lines is what loading the first mod took (its DLL, its static
    /// setup and its Awake). ValPerfMonitor reads the timestamps once it starts.
    /// Patches nothing.
    /// </summary>
    public static class LoadTimer
    {
        /// <summary>AppDomain data key; the value is a List&lt;string&gt; of "stopwatchTicks\tutcTicks\ttext".</summary>
        public const string Key = "ValPerfMonitor.LoadEvents";

        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            var events = new List<string>();
            Listener.Record(events, "Preloader patcher started");
            AppDomain.CurrentDomain.SetData(Key, events);
            Logger.Listeners.Add(new Listener(events));
        }
    }

    internal sealed class Listener : ILogListener
    {
        private readonly List<string> _events;
        private bool _done;

        public Listener(List<string> events) => _events = events;

        public void LogEvent(object sender, LogEventArgs e)
        {
            if (_done || e.Source == null || e.Source.SourceName != "BepInEx")
                return;
            string text = e.Data as string ?? e.Data?.ToString();
            if (text == null)
                return;
            if (text.StartsWith("Loading [") || text.StartsWith("Chainloader started") || text.StartsWith("Preloader finished"))
                Record(_events, text);
            else if (text.StartsWith("Chainloader startup complete"))
            {
                Record(_events, text);
                _done = true; // stays registered (removing a listener while BepInEx is calling it would throw), but does nothing more
            }
        }

        internal static void Record(List<string> events, string text)
        {
            lock (events)
                events.Add(Stopwatch.GetTimestamp() + "\t" + DateTime.UtcNow.Ticks + "\t" + text);
        }

        public void Dispose() { }
    }
}
