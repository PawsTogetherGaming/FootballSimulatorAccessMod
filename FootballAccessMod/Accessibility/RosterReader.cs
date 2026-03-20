using System;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Monitors the Roster screen. The currently highlighted player is always the
    /// no-suffix "Name" child of the Names list. We watch FIRSTNAME, LASTNAME, OVR
    /// for changes and speak them. Also announces the PlayerOptions popup on entry.
    /// </summary>
    public static class RosterReader
    {
        private static bool _wasActive = false;

        // Cached refs for the selected player row (no-suffix "Name" child)
        private static TextMeshProUGUI _firstName;
        private static TextMeshProUGUI _lastName;
        private static TextMeshProUGUI _ovr;
        private static TextMeshProUGUI _position;   // from Content Scroll View player (no suffix)
        private static TextMeshProUGUI _jerseyId;   // ID field on the Name row

        // PlayerOptions popup tracking
        public static bool PopupIsActive { get; private set; }
        private static string _lastPopupButton = "";

        // Last announced player
        private static string _lastAnnounced = "";

        public static void Poll()
        {
            var screen = GameObject.Find("Roster_Screen");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                _wasActive = false;
                PopupIsActive = false;
                _lastAnnounced = "";
                _lastPopupButton = "";
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                CacheRefs(screen);
                SpeechManager.Speak("Roster. Use up and down to browse players.");
                return;
            }

            // Check PlayerOptions popup first (takes focus priority)
            if (CheckPopup(screen)) return;

            // Check if selected player changed
            CheckPlayerChange();
        }

        private static bool CheckPopup(GameObject screen)
        {
            // Recursive search — popup may not be a direct child of Roster_Screen
            Transform popupTransform = null;
            foreach (Transform t in screen.GetComponentsInChildren<Transform>(true))
            {
                if (t.gameObject.name == "PlayerOptionsScreen")
                {
                    popupTransform = t;
                    break;
                }
            }

            if (popupTransform == null)
            {
                PopupIsActive = false;
                _lastPopupButton = "";
                return false;
            }

            bool popupActive = popupTransform.gameObject.activeInHierarchy;
            if (!popupActive)
            {
                if (PopupIsActive)
                {
                    PopupIsActive = false;
                    _lastPopupButton = "";
                    _lastAnnounced = ""; // allow re-announcement when returning to player list
                }
                return false;
            }

            // Popup is now active
            if (!PopupIsActive)
            {
                PopupIsActive = true;

                // Seed _lastPopupButton with the currently highlighted button so the
                // per-button scan below doesn't immediately re-announce it on top of entry.
                _lastPopupButton = "";
                foreach (var mb2 in popupTransform.GetComponentsInChildren<MonoBehaviour>(false))
                {
                    if (mb2 == null) continue;
                    string tn = mb2.GetType().Name;
                    if (tn != "UIButtonStretch" && tn != "UIButton") continue;
                    if (!IsPopupButtonSelected(mb2)) continue;
                    foreach (var tmp2 in mb2.GetComponentsInChildren<TextMeshProUGUI>(false))
                    {
                        string t2 = Clean(tmp2);
                        if (!string.IsNullOrWhiteSpace(t2) && !IsNumericOnly(t2)) { _lastPopupButton = t2; break; }
                    }
                    break;
                }

                // Announce all non-numeric option labels on entry
                var labels = new System.Collections.Generic.List<string>();
                foreach (var tmp in popupTransform.GetComponentsInChildren<TextMeshProUGUI>(false))
                {
                    string lbl = Clean(tmp);
                    if (!string.IsNullOrWhiteSpace(lbl) && !IsNumericOnly(lbl)) labels.Add(lbl);
                }
                string msg = labels.Count > 0
                    ? "Player options: " + string.Join(", ", labels.ToArray())
                    : "Player options.";
                SpeechManager.Speak(msg);
            }

            // Track which popup button is currently highlighted
            foreach (var mb in popupTransform.GetComponentsInChildren<MonoBehaviour>(false))
            {
                if (mb == null) continue;
                string typeName = mb.GetType().Name;
                if (typeName != "UIButtonStretch" && typeName != "UIButton") continue;
                if (!IsPopupButtonSelected(mb)) continue;

                // Read only non-numeric TMP text from this button
                string label = "";
                foreach (var tmp in mb.GetComponentsInChildren<TextMeshProUGUI>(false))
                {
                    string t = Clean(tmp);
                    if (!string.IsNullOrWhiteSpace(t) && !IsNumericOnly(t)) { label = t; break; }
                }
                if (string.IsNullOrWhiteSpace(label) || label == _lastPopupButton) continue;
                _lastPopupButton = label;
                SpeechManager.Speak(label + ", button");
                return true;
            }

            return true; // popup is active — suppress CheckPlayerChange
        }

        // Checks all boolean fields with "select", "focus", or "current" in name, plus scale
        private static bool IsPopupButtonSelected(MonoBehaviour mb)
        {
            var flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic;
            foreach (var f in mb.GetType().GetFields(flags))
            {
                if (f.FieldType != typeof(bool)) continue;
                string n = f.Name.ToLower();
                if (!n.Contains("select") && !n.Contains("focus") && !n.Contains("current")) continue;
                try { if (f.GetValue(mb) is bool b && b) return true; } catch { }
            }
            Vector3 s = mb.transform.localScale;
            return s.x > 1.05f || s.y > 1.05f;
        }

        private static bool IsNumericOnly(string s)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^\d+$");
        }

        private static void CheckPlayerChange()
        {
            if (_firstName == null) return;

            string first = Clean(_firstName);
            string last  = Clean(_lastName);
            string ovr   = Clean(_ovr);
            string pos   = Clean(_position);
            string id    = Clean(_jerseyId);

            string full = $"{first} {last}".Trim();
            if (string.IsNullOrWhiteSpace(full) || full == _lastAnnounced) return;

            _lastAnnounced = full;

            // Build announcement: "Jerry Rice, wide receiver, number 80, OVR 92"
            string announcement = full;
            if (!string.IsNullOrWhiteSpace(pos))  announcement += $", {pos}";
            if (!string.IsNullOrWhiteSpace(id))   announcement += $", number {id}";
            if (!string.IsNullOrWhiteSpace(ovr))  announcement += $", overall {ovr}";
            SpeechManager.Speak(announcement);
        }

        private static void CacheRefs(GameObject screen)
        {
            // Navigate to the no-suffix "Name" child in the Names list
            var namesContainer = screen.transform.Find("Name Scroll View/Names Viewport/Names");
            if (namesContainer != null)
            {
                var nameRow = namesContainer.Find("Name");
                if (nameRow != null)
                {
                    _firstName = FindTMP(nameRow.gameObject, "FIRSTNAME");
                    _lastName  = FindTMP(nameRow.gameObject, "LASTNAME");
                    _jerseyId  = FindTMP(nameRow.gameObject, "ID");
                    _ovr       = FindTMP(nameRow.gameObject, "OVR");
                }
            }

            // Position is on the no-suffix player row in the Content Scroll View
            var viewport = screen.transform.Find("Content Scroll View/Viewport/Players");
            if (viewport != null)
            {
                var playerRow = viewport.Find("player");
                if (playerRow != null)
                    _position = FindTMP(playerRow.gameObject, "POS");
            }

            // Seed last values so we don't double-announce on entry
            _lastAnnounced = $"{Clean(_firstName)} {Clean(_lastName)}".Trim();
        }

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            string raw = comp.text ?? "";
            // Strip TMP rich text
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char ch in raw)
            {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return System.Text.RegularExpressions.Regex.Replace(
                sb.ToString().Trim(), @"\s+", " ");
        }

        private static TextMeshProUGUI FindTMP(GameObject root, string name)
        {
            foreach (TextMeshProUGUI t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t.gameObject.name == name) return t;
            }
            return null;
        }
    }
}
