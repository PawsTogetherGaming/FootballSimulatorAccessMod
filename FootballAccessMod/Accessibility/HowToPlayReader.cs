using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    internal struct ControlEntry
    {
        internal string Button;
        internal string Instruction;
        internal ControlEntry(string button, string instruction)
        { Button = button; Instruction = instruction; }
    }

    /// <summary>
    /// Reads the How To Play screen aloud. Navigation is purely ParadeButton-based.
    /// On entry and on every tab change we read the screen title, tab name,
    /// and all visible control instructions paired with their button names.
    /// Button identity is read from the DynamicButtonIconContext.button enum field.
    /// </summary>
    public static class HowToPlayReader
    {
        private static bool _wasActive = false;
        private static int  _lastHash  = 0;

        // Reflection cache for DynamicButtonIconContext
        private static bool       _reflDone   = false;
        private static Type?      _dbicType   = null;
        private static FieldInfo? _buttonField = null;

        public static void Poll()
        {
            var screen = GameObject.Find("How To Play");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                _wasActive = false;
                _lastHash  = 0;
                return;
            }

            EnsureReflection();

            if (!_wasActive)
            {
                _wasActive = true;
                SpeakControls(screen);
                return;
            }

            // Use the same hash function as SpeakControls to avoid mismatch re-triggering
            int hash = HashInstructions(screen);
            if (hash != _lastHash)
                SpeakControls(screen);
        }

        private static void SpeakControls(GameObject screen)
        {
            // Set hash BEFORE building controls so Poll won't re-trigger immediately
            _lastHash = HashInstructions(screen);

            var controls = GetActiveControls(screen);
            string tab = GetTabName(screen);

            var sb = new System.Text.StringBuilder();
            sb.Append("How to play.");
            if (!string.IsNullOrWhiteSpace(tab)) sb.Append($" {tab}.");
            sb.Append(" ");

            foreach (var c in controls)
            {
                if (!string.IsNullOrWhiteSpace(c.Button))
                    sb.Append($"{c.Button}: {c.Instruction}. ");
                else
                    sb.Append($"{c.Instruction}. ");
            }

            SpeechManager.Speak(sb.ToString().TrimEnd());
        }

        // ---- Reflection setup ----

        private static void EnsureReflection()
        {
            if (_reflDone) return;
            _reflDone = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Assembly-CSharp") continue;
                    _dbicType = asm.GetType("DynamicButtonIconContext");
                    break;
                }
                if (_dbicType != null)
                    _buttonField = _dbicType.GetField("button",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch { }
        }

        // ---- Tab name ----

        private static string GetTabName(GameObject screen)
        {
            Transform xbox = screen.transform.Find("Xbox");
            if (xbox != null)
            {
                foreach (Transform child in xbox)
                {
                    if (!child.gameObject.activeInHierarchy) continue;
                    string n = child.gameObject.name;
                    if (n.Contains("Offense")) return "Offense";
                    if (n.Contains("Defense")) return "Defense";
                    if (n.Contains("Replay"))  return "Replay";
                }
            }

            Transform header = screen.transform.Find("Header/Tabs");
            if (header != null)
            {
                foreach (Transform tab in header)
                {
                    var tmp = tab.GetComponent<TextMeshProUGUI>();
                    if (tmp == null) continue;
                    string t = (tmp.text ?? "").Trim();
                    if (t.Length > 0 && t == t.ToUpper()) return t;
                }
            }

            return "";
        }

        // ---- Control collection ----

        private static List<ControlEntry> GetActiveControls(GameObject screen)
        {
            var results = new List<ControlEntry>();

            foreach (var tmp in screen.GetComponentsInChildren<TextMeshProUGUI>(false))
            {
                if (tmp.gameObject.name != "Instruction") continue;
                string ins = Clean(tmp);
                if (string.IsNullOrWhiteSpace(ins) || ins == "Back") continue;

                string btnLabel = "";
                Transform parent = tmp.transform.parent;
                if (parent != null)
                {
                    Transform btnTransform = parent.Find("Button");
                    if (btnTransform != null)
                        btnLabel = GetButtonLabel(btnTransform.gameObject);
                }

                results.Add(new ControlEntry(btnLabel, ins));
            }

            return results;
        }

        // ---- Button label resolution ----

        private static string GetButtonLabel(GameObject btnGo)
        {
            // Primary: read the Button enum field from DynamicButtonIconContext
            if (_dbicType != null && _buttonField != null)
            {
                var dbic = btnGo.GetComponent(_dbicType);
                if (dbic != null)
                {
                    try
                    {
                        object val = _buttonField.GetValue(dbic);
                        if (val != null)
                        {
                            string label = ButtonEnumToLabel(val.ToString());
                            if (!string.IsNullOrEmpty(label)) return label;
                        }
                    }
                    catch { }
                }
            }

            // Fallback: read sprite name from Image component
            var img = btnGo.GetComponent<Image>();
            if (img != null && img.sprite != null)
                return SpriteNameToLabel(img.sprite.name);

            return "";
        }

        // ---- Button enum → friendly name ----
        // Known values from dump: Start, R1, A_South (and inferred siblings)

        private static string ButtonEnumToLabel(string enumValue)
        {
            switch (enumValue)
            {
                case "A_South":    return "A button";
                case "B_East":     return "B button";
                case "X_West":     return "X button";
                case "Y_North":    return "Y button";
                case "R1":         return "Right bumper";
                case "L1":         return "Left bumper";
                case "R2":         return "Right trigger";
                case "L2":         return "Left trigger";
                case "Start":      return "Start";
                case "Select":     return "Select";
                case "Back":       return "Back";
                case "LS":         case "L3": return "Left stick";
                case "RS":         case "R3": return "Right stick";
                case "DPad_Up":    return "D-pad up";
                case "DPad_Down":  return "D-pad down";
                case "DPad_Left":  return "D-pad left";
                case "DPad_Right": return "D-pad right";
                default:           return "";  // fall through to sprite name
            }
        }

        // ---- Sprite name → friendly name (fallback) ----
        // "game buttons_N" sprite names confirmed from dump; "360_*" names also used.

        private static string SpriteNameToLabel(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return "";

            // "game buttons_N" style — confirmed mapping from dump cross-reference
            switch (spriteName)
            {
                case "game buttons_0": return "A button";
                case "game buttons_1": return "B button";
                case "game buttons_2": return "Y button";
                case "game buttons_3": return "X button";
                case "game buttons_4": return "Left trigger";
                case "game buttons_5": return "Right trigger";
                case "game buttons_6": return "Left bumper";
                case "game buttons_7": return "Right bumper";
                case "game buttons_8": return "D-pad";
            }

            // "360_X" style names
            string s = spriteName.ToLowerInvariant();
            if (s == "360_a")  return "A button";
            if (s == "360_b")  return "B button";
            if (s == "360_x")  return "X button";
            if (s == "360_y")  return "Y button";
            if (s == "360_rb" || s == "360_r1") return "Right bumper";
            if (s == "360_lb" || s == "360_l1") return "Left bumper";
            if (s == "360_rt" || s == "360_r2") return "Right trigger";
            if (s == "360_lt" || s == "360_l2") return "Left trigger";
            if (s == "360_rs") return "Right stick";
            if (s == "360_ls") return "Left stick";
            if (s == "360_start") return "Start";
            if (s == "360_back")  return "Back";
            if (s.Contains("dpad_left"))  return "D-pad left";
            if (s.Contains("dpad_right")) return "D-pad right";
            if (s.Contains("dpad_up"))    return "D-pad up";
            if (s.Contains("dpad_down"))  return "D-pad down";
            if (s.Contains("dpad"))       return "D-pad";

            // Unknown — speak raw so user can report it
            return spriteName;
        }

        // ---- Hashing (single function used by both Poll and SpeakControls) ----

        private static int HashInstructions(GameObject screen)
        {
            int h = 17;
            foreach (var tmp in screen.GetComponentsInChildren<TextMeshProUGUI>(false))
            {
                if (tmp.gameObject.name != "Instruction") continue;
                h = h * 31 + (tmp.text ?? "").GetHashCode();
            }
            return h;
        }

        // ---- Text clean ----

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char ch in (comp.text ?? ""))
            {
                if (ch == '<') { inTag = true;  continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return System.Text.RegularExpressions.Regex.Replace(
                sb.ToString().Trim(), @"\s+", " ");
        }
    }
}
