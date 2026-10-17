using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// Runs a ValAgentBridge test script (test/run_file or test/run) from the monitor, without referencing the bridge:
    /// the bridge's tool list is ValAgentBridgePlugin.Tools, a Lib.GAB registry with CallToolAsync(name, parameters).
    /// A script waits on the game's main thread step by step, so it is started on a worker thread and polled.
    /// </summary>
    internal sealed class BridgeScript
    {
        public readonly string Spec;
        private Task<object> _task;
        public string Error;
        public int Runs, Failed;
        public string LastFailure;

        /// <param name="spec">A saved script's name (as test/run_file takes it), or a script as JSON (starting with '{').</param>
        public BridgeScript(string spec) { Spec = spec.Trim(); }

        public bool Running => _task != null && !_task.IsCompleted;

        /// <summary>The bridge's tool registry, or null (with the reason) when the bridge isn't there or not started.</summary>
        private static object Registry(out MethodInfo call, out string error)
        {
            call = null; error = null;
            var plugin = AccessTools.TypeByName("ValAgentBridge.ValAgentBridgePlugin");
            var tools = plugin == null ? null : AccessTools.Property(plugin, "Tools");
            if (tools == null) { error = "ValAgentBridge is not installed (or not as expected)"; return null; }
            object registry = tools.GetValue(null, null);
            if (registry == null) { error = "ValAgentBridge has not started (start the game through GABS)"; return null; }
            call = AccessTools.Method(registry.GetType(), "CallToolAsync", new[] { typeof(string), typeof(object) });
            if (call == null) { error = "ValAgentBridge's tool registry is not as expected"; return null; }
            return registry;
        }

        public static string Check() { Registry(out _, out string e); return e; }

        /// <summary>Starts one run of the script; false (with Error) if it can't.</summary>
        public bool Start()
        {
            if (Running) return true;
            Collect();
            var registry = Registry(out var call, out Error);
            if (registry == null) return false;
            bool inline = Spec.StartsWith("{");
            string tool = inline ? "test/run" : "test/run_file";
            // Lib.GAB reads a JSON string as the arguments.
            string args = inline ? "{\"script\":" + Spec + "}" : "{\"name\":\"" + Spec.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
            _task = Task.Run(() => (Task<object>)call.Invoke(registry, new object[] { tool, args }));
            Runs++;
            return true;
        }

        /// <summary>After a run ends: notes whether it failed. Main thread.</summary>
        public void Collect()
        {
            if (_task == null || !_task.IsCompleted) return;
            var t = _task;
            _task = null;
            string why = null;
            if (t.IsFaulted) why = t.Exception?.GetBaseException().Message ?? "failed";
            else if (t.IsCanceled) why = "cancelled";
            else
            {
                string text = Describe(t.Result);
                if (Regex.IsMatch(text, "\"(passed|success)\"\\s*:\\s*false", RegexOptions.IgnoreCase))
                {
                    var m = Regex.Match(text, "\"error\"\\s*:\\s*\"([^\"]{0,200})");
                    why = m.Success ? m.Groups[1].Value : "a step failed (see the bridge's test-results folder)";
                }
            }
            if (why != null) { Failed++; LastFailure = why; }
        }

        // JObject.ToString() is its JSON; a dictionary is listed as key/value pairs.
        private static string Describe(object o)
        {
            if (o == null) return "";
            if (o is IDictionary d)
            {
                var sb = new System.Text.StringBuilder("{");
                foreach (DictionaryEntry e in d) sb.Append('"').Append(e.Key).Append("\":").Append(e.Value is bool b ? (b ? "true" : "false") : "\"" + e.Value + "\"").Append(',');
                return sb.Append('}').ToString();
            }
            return o.ToString();
        }
    }
}
