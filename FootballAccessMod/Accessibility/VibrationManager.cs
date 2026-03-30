using System;
using System.Reflection;
using UnityEngine;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Controller vibration via the game's own XInputDotNetPure.dll,
    /// which is already loaded in the process.
    ///
    /// Left motor  = low-frequency rumble (proximity warnings).
    /// Right motor = high-frequency buzz  (positional / under-ball cues).
    ///
    /// Uses GamePad.SetVibration(PlayerIndex.One, leftMotor, rightMotor)
    /// from XInputDotNet via reflection so we don't need a direct reference.
    /// </summary>
    public static class VibrationManager
    {
        private static bool   _initDone       = false;
        private static bool   _available      = false;
        private static object _playerIndexOne = null;   // PlayerIndex.One enum value
        private static MethodInfo _methSetVib = null;   // GamePad.SetVibration

        private static float  _left           = 0f;
        private static float  _right          = 0f;

        // ---- Public API ----

        public static void SetLeft(float value)
        {
            _left = Mathf.Clamp01(value);
            Apply();
        }

        public static void SetRight(float value)
        {
            _right = Mathf.Clamp01(value);
            Apply();
        }

        public static void Set(float left, float right)
        {
            _left  = Mathf.Clamp01(left);
            _right = Mathf.Clamp01(right);
            Apply();
        }

        public static void Stop()
        {
            _left = _right = 0f;
            Apply();
        }

        // ---- Internal ----

        private static void EnsureInit()
        {
            if (_initDone) return;
            _initDone = true;
            try
            {
                // XInputDotNetPure is already loaded by the game
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("XInputDotNet", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // GamePad.SetVibration(PlayerIndex playerIndex, float leftMotor, float rightMotor)
                    var gamePadType = asm.GetType("XInputDotNet.GamePad")
                                   ?? asm.GetType("GamePad");
                    if (gamePadType == null) continue;

                    _methSetVib = gamePadType.GetMethod("SetVibration",
                        BindingFlags.Static | BindingFlags.Public,
                        null,
                        new[] { asm.GetType("XInputDotNet.PlayerIndex") ?? asm.GetType("PlayerIndex"),
                                typeof(float), typeof(float) },
                        null);

                    if (_methSetVib == null) continue;

                    var playerIndexType = _methSetVib.GetParameters()[0].ParameterType;
                    _playerIndexOne = Enum.Parse(playerIndexType, "One");

                    _available = true;
                    Plugin.Log.LogInfo("[VibrationManager] XInputDotNet ready.");
                    break;
                }

                if (!_available)
                    Plugin.Log.LogWarning("[VibrationManager] XInputDotNet not found — vibration disabled.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VibrationManager] Init failed: {ex.Message}");
            }
        }

        private static void Apply()
        {
            EnsureInit();
            if (!_available) return;
            try
            {
                _methSetVib.Invoke(null, new object[] { _playerIndexOne, _left, _right });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VibrationManager] SetVibration failed: {ex.Message}");
                _available = false;
            }
        }
    }
}
