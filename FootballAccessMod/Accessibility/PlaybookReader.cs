using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Patches FormationSelector.SelectIndicator (fires when formation/play indicator
    /// is highlighted) and FootballGameplayMenu.AdjustForPlaybook (fires when playbook
    /// UI is adjusted). Logs all text found on the OffenseBox to help discover how
    /// play names are rendered.
    /// </summary>
    public static class PlaybookReader
    {
        private static string _lastSpoken = "";

        public static void ApplyPatches(Harmony harmony)
        {
            int patched = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;

                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == "FormationSelector")
                    {
                        TryPatch(harmony, type, "SelectIndicator",
                            typeof(FormationSelector_Patch).GetMethod("Postfix",
                                BindingFlags.Static | BindingFlags.NonPublic),
                            ref patched);
                    }

                    if (type.Name == "FootballGameplayMenu")
                    {
                        TryPatch(harmony, type, "AdjustForPlaybook",
                            typeof(AdjustForPlaybook_Patch).GetMethod("Postfix",
                                BindingFlags.Static | BindingFlags.NonPublic),
                            ref patched);
                    }
                }
                break;
            }
            Plugin.Log.LogInfo($"[PlaybookReader] {patched} patches applied.");
        }

        private static void TryPatch(Harmony harmony, Type type, string methodName,
            MethodInfo postfix, ref int count)
        {
            try
            {
                var method = type.GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    Plugin.Log.LogWarning($"[PlaybookReader] {type.Name}.{methodName} not found.");
                    return;
                }
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                Plugin.Log.LogInfo($"[PlaybookReader] Patched {type.Name}.{methodName}");
                count++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[PlaybookReader] Patch {type.Name}.{methodName} failed: {ex.Message}");
            }
        }

        // Called by both patches to scan and announce play-related text
        internal static void ScanAndAnnounce(GameObject instanceGO)
        {
            if (instanceGO == null) return;

            // Build a text snapshot from TMP + legacy Text on this GO and children
            string text = ExtractAllText(instanceGO);
            if (string.IsNullOrWhiteSpace(text) || text == _lastSpoken) return;
            _lastSpoken = text;

            Plugin.Log.LogInfo($"[PlaybookReader] Text on {instanceGO.name}: {text}");
        }

        // Called by AdjustForPlaybook_Patch to also scan the OffenseBox
        internal static void ScanOffenseBox()
        {
            var box = GameObject.Find("OffenseBox");
            if (box == null) return;

            var sb = new StringBuilder();
            sb.Append("[OffenseBox TMP] ");
            foreach (var t in box.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                string v = (t.text ?? "").Trim();
                if (!string.IsNullOrEmpty(v))
                    sb.Append($"{t.gameObject.name}={v}; ");
            }
            sb.Append("[LegacyText] ");
            foreach (var t in box.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                string v = (t.text ?? "").Trim();
                if (!string.IsNullOrEmpty(v))
                    sb.Append($"{t.gameObject.name}={v}; ");
            }
            Plugin.Log.LogInfo($"[PlaybookReader] {sb}");
        }

        private static string ExtractAllText(GameObject go)
        {
            var sb = new StringBuilder();
            foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                string v = (t.text ?? "").Trim();
                if (!string.IsNullOrEmpty(v)) sb.Append(v + " ");
            }
            foreach (var t in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                string v = (t.text ?? "").Trim();
                if (!string.IsNullOrEmpty(v)) sb.Append(v + " ");
            }
            return sb.ToString().Trim();
        }

        // ---- Harmony patches ----

        private static class FormationSelector_Patch
        {
            static void Postfix(object __instance)
            {
                if (__instance is Component comp)
                    ScanAndAnnounce(comp.gameObject);
            }
        }

        private static class AdjustForPlaybook_Patch
        {
            static void Postfix(object __instance)
            {
                ScanOffenseBox();
                if (__instance is Component comp)
                    ScanAndAnnounce(comp.gameObject);
            }
        }
    }
}
