using System;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfMonitor
{
    /// <summary>
    /// While the test panel is open, tell Valheim a text box is showing. The game then frees the mouse
    /// cursor (GameCamera.UpdateMouseCapture) and ignores movement, camera and attack input
    /// (Player/PlayerController.TakeInput), exactly as when typing in a sign or chat.
    /// The patch exists only while the panel is open, so it never touches a measurement.
    /// </summary>
    internal sealed class InputBlock
    {
        private readonly Harmony _harmony = new Harmony(Plugin.Guid + ".panel");
        private readonly ManualLogSource _log;
        private static bool _active;

        public InputBlock(ManualLogSource log) => _log = log;

        public void Set(bool on)
        {
            if (on == _active) return;
            _active = on;
            try
            {
                if (on)
                    _harmony.Patch(AccessTools.Method(typeof(TextInput), nameof(TextInput.IsVisible)),
                        postfix: new HarmonyMethod(typeof(InputBlock), nameof(Postfix)));
                else
                    _harmony.UnpatchSelf();
            }
            catch (Exception e) { _log.LogWarning($"Could not {(on ? "free" : "restore")} the mouse cursor: {e.Message}"); }
        }

        private static void Postfix(ref bool __result)
        {
            if (_active) __result = true;
        }
    }
}
