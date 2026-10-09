using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValPerfMonitor
{
    /// <summary>
    /// Confirms a suspected garbage source. In this game the allocation counter is approximate (total heap
    /// size, which other threads also move), so a small method that runs very often can be blamed for garbage
    /// it doesn't make. This calls the method itself many times in a row, from one compiled loop with no
    /// per-call overhead, and divides the heap growth by the number of calls.
    /// Arguments are filled in from the live game: a component of the parameter's type, the player's position
    /// and zone, the session id, the time. The method really runs, with its normal side effects.
    /// </summary>
    internal static class AllocCheck
    {
        public sealed class Result
        {
            public string Method = "", Instance = "", Error;
            public int Calls, GcRuns;
            public double BytesPerCall, MicrosPerCall;
        }

        /// <param name="timeArg">How float arguments are filled: "now" (Time.time), "future" (Time.time + 1e6, so
        /// "is it due yet?" checks always pass) or a number.</param>
        public static Result Run(string typeName, string methodName, int calls, string timeArg)
        {
            var r = new Result();
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) { r.Error = $"type {typeName} not found"; return r; }
                var methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == methodName && !m.IsAbstract && !m.ContainsGenericParameters).ToList();
                if (methods.Count == 0) { r.Error = $"{typeName}.{methodName} not found"; return r; }
                if (methods.Count > 1) { r.Error = $"{methodName} has {methods.Count} overloads; not supported"; return r; }
                var method = methods[0];
                r.Method = $"{type.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

                object target = null;
                if (!method.IsStatic)
                {
                    target = FindInstance(type);
                    if (target == null) { r.Error = $"no live {type.Name} found"; return r; }
                    r.Instance = Describe(target);
                }
                var ps = method.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType.IsByRef && !ps[i].IsOut) { r.Error = $"ref parameter {ps[i].Name} not supported"; return r; }
                    if (!Fill(ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType, timeArg, out args[i]))
                    { r.Error = $"can't fill parameter {ps[i].Name} ({ps[i].ParameterType.Name})"; return r; }
                    if (args[i] is Object uo && i == 0 && method.IsStatic) r.Instance = Describe(uo);
                }

                var loop = BuildLoop(method);
                loop(target, args, 20); // compile and warm up
                calls = Mathf.Clamp(calls, 10, 1000000);
                int gc0 = GC.CollectionCount(0);
                long before = GC.GetTotalMemory(false);
                var sw = Stopwatch.StartNew();
                loop(target, args, calls);
                sw.Stop();
                long after = GC.GetTotalMemory(false);
                r.GcRuns = GC.CollectionCount(0) - gc0;
                r.Calls = calls;
                r.BytesPerCall = r.GcRuns > 0 ? double.NaN : (double)(after - before) / calls;
                r.MicrosPerCall = sw.Elapsed.TotalMilliseconds * 1000.0 / calls;
                if (r.GcRuns > 0) r.Error = "a garbage collection ran during the check; run it again";
            }
            catch (Exception e) { r.Error = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            return r;
        }

        private static object FindInstance(Type type)
        {
            foreach (var name in new[] { "instance", "m_instance", "Instance", "m_localPlayer" })
            {
                var f = AccessTools.Field(type, name);
                if (f != null && f.IsStatic && type.IsAssignableFrom(f.FieldType)) { var v = f.GetValue(null); if (v != null && !(v is Object o && o == null)) return v; }
                var p = AccessTools.Property(type, name);
                if (p != null && p.GetGetMethod(true)?.IsStatic == true && type.IsAssignableFrom(p.PropertyType)) { var v = p.GetValue(null, null); if (v != null && !(v is Object o && o == null)) return v; }
            }
            if (typeof(Object).IsAssignableFrom(type))
            {
                var all = Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                var player = Player.m_localPlayer;
                if (all.Length == 0) return null;
                if (player == null || !typeof(Component).IsAssignableFrom(type)) return all[0];
                // The nearest one, so it is inside the active area like the ones the game updates.
                return all.Cast<Component>().OrderBy(c => (c.transform.position - player.transform.position).sqrMagnitude).First();
            }
            return null;
        }

        private static bool Fill(Type t, string timeArg, out object value)
        {
            var player = Player.m_localPlayer;
            Vector3 pos = player != null ? player.transform.position : Vector3.zero;
            value = null;
            if (t == typeof(float))
            {
                if (timeArg == "future") value = Time.time + 1e6f;
                else if (float.TryParse(timeArg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) value = f;
                else value = Time.time;
                return true;
            }
            if (t == typeof(Vector3)) { value = pos; return true; }
            if (t == typeof(Vector2s)) { value = ZoneSystem.GetZone(pos); return true; }
            if (t == typeof(long)) { value = ZDOMan.GetSessionID(); return true; }
            if (t == typeof(int)) { value = 0; return true; }
            if (t == typeof(bool)) { value = false; return true; }
            if (t == typeof(string)) { value = ""; return true; }
            if (!t.IsValueType) { value = FindInstance(t); return value != null; }
            return false;
        }

        private static string Describe(object o) => o is Component c ? $"{c.name} at {c.transform.position}" : o.GetType().Name;

        private delegate void Loop(object target, object[] args, int count);

        /// <summary>
        /// Compiles: unpack the arguments into typed locals once, then call the method count times directly
        /// (no reflection, no boxing per call), dropping any return value.
        /// </summary>
        private static Loop BuildLoop(MethodInfo method)
        {
            var ps = method.GetParameters();
            var dm = new DynamicMethod("ValPerfMonitor_AllocCheck", typeof(void), new[] { typeof(object), typeof(object[]), typeof(int) }, method.DeclaringType, true);
            var il = dm.GetILGenerator();
            LocalBuilder self = null;
            if (!method.IsStatic)
            {
                self = il.DeclareLocal(method.DeclaringType);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(method.DeclaringType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.DeclaringType);
                il.Emit(OpCodes.Stloc, self);
            }
            var locals = new LocalBuilder[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
                locals[i] = il.DeclareLocal(pt);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(pt.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, pt);
                il.Emit(OpCodes.Stloc, locals[i]);
            }
            var counter = il.DeclareLocal(typeof(int));
            var check = il.DefineLabel();
            var body = il.DefineLabel();
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, counter);
            il.Emit(OpCodes.Br, check);
            il.MarkLabel(body);
            if (self != null)
                il.Emit(method.DeclaringType.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, self);
            for (int i = 0; i < ps.Length; i++)
                il.Emit(ps[i].ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc, locals[i]);
            il.Emit(method.IsStatic || method.DeclaringType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, method);
            if (method.ReturnType != typeof(void))
                il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc, counter);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, counter);
            il.MarkLabel(check);
            il.Emit(OpCodes.Ldloc, counter);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Blt, body);
            il.Emit(OpCodes.Ret);
            return (Loop)dm.CreateDelegate(typeof(Loop));
        }
    }
}
