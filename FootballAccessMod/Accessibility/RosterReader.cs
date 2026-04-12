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
        private static float _debugTimer = 0f;
        private static bool _bumperJustPressed = false;
        private static bool _l1WasDown = false;
        private static bool _r1WasDown = false;
        private static string _lastAboveListButton = "";

        // Cached refs for the selected player row (no-suffix "Name" child)
        private static TextMeshProUGUI _firstName;
        private static TextMeshProUGUI _lastName;
        private static TextMeshProUGUI _ovr;
        private static TextMeshProUGUI _position;   // from Content Scroll View player (no suffix)
        private static TextMeshProUGUI _jerseyId;   // ID field on the Name row

        // Team name tracking — announced when L2/R2 switches teams
        private static TextMeshProUGUI _teamNameTMP;
        private static string _lastTeamName = "";

        // Position group tracking — announced when triggers cycle position groups
        private static string _lastPlayerPos = "";

        // PlayerOptions popup tracking
        public static bool PopupIsActive { get; private set; }

        // Character Editor tracking
        public static bool EditorIsActive { get; private set; }
        public static Transform EditorRoot { get; private set; }
        private static TextMeshProUGUI _editorFirstNameTMP;
        private static TextMeshProUGUI _editorLastNameTMP;
        private static string _lastEditorFirstName = "";
        private static string _lastEditorLastName = "";
        private static string _lastKeyLabel = "";
        private static bool _keyboardWasActive = false;
        private static string _lastEditorFieldLabel = ""; // tracks selected field button label
        private static string _lastStatLabel = "";        // tracks Animator-selected stat row label
        private static GameObject _sliderStatGO = null;   // stat being edited via slider
        private static TextMeshProUGUI _sliderText2TMP = null;
        private static string _lastSliderValue = "";

        // True while the roster screen is active
        public static bool RosterIsActive { get; private set; }

        // True when a player row is the current selection — used to suppress button scanner
        public static bool PlayerInFocus { get; private set; }
        private static string _lastPopupButton = "";

        // Last announced player
        private static string _lastAnnounced = "";

        public static void Poll()
        {
            // Check the character editor first — the game hides Roster_Screen while it is open,
            // so we must detect it before the roster active-check bails out.
            var editorScreen = GameObject.Find("CharacterEditor_Screen");
            bool nowEditorActive = editorScreen != null && editorScreen.activeInHierarchy;
            if (nowEditorActive)
            {
                RosterIsActive = true; // keeps button scanner suppressed
                // Don't fire entry immediately — CharacterEditor_Screen is pre-loaded
                // and becomes active before the user actually selects "Player Editor".
                // Entry is fired inside CheckEditorTyping on first real field detection.
                if (!EditorIsActive)
                {
                    EditorRoot = editorScreen.transform;
                    CacheEditorRefs(editorScreen);
                }
                CheckEditorTyping();
                return;
            }
            if (EditorIsActive)
            {
                // Editor just closed
                EditorIsActive = false;
                EditorRoot = null;
                _editorFirstNameTMP = null;
                _editorLastNameTMP = null;
                _lastKeyLabel = "";
                _lastAnnounced = "";
                _keyboardWasActive = false;
                _lastEditorFieldLabel = "";
                _lastStatLabel = "";
                _sliderStatGO = null;
                _sliderText2TMP = null;
                _lastSliderValue = "";
            }

            var screen = GameObject.Find("Roster_Screen");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                _wasActive = false;
                RosterIsActive = false;
                PlayerInFocus = false;
                PopupIsActive = false;
                _bumperJustPressed = false;
                _l1WasDown = false;
                _r1WasDown = false;
                _lastAnnounced = "";
                _lastPopupButton = "";
                _lastAboveListButton = "";
                _lastTeamName = "";
                _teamNameTMP = null;
                _lastPlayerPos = "";
                return;
            }

            RosterIsActive = true;

            // Detect L1/R1 (bumpers) via manual state tracking — GetKeyDown can miss
            // frames depending on when in the player loop we're called.
            bool l1Down = UnityEngine.Input.GetKey(UnityEngine.KeyCode.JoystickButton4);
            bool r1Down = UnityEngine.Input.GetKey(UnityEngine.KeyCode.JoystickButton5);
            bool bumperPressed = (!_l1WasDown && l1Down) || (!_r1WasDown && r1Down);
            _l1WasDown = l1Down;
            _r1WasDown = r1Down;
            if (bumperPressed)
            {
                _bumperJustPressed = true;
                _lastPlayerPos = "";  // force posChanged on next check
                _lastAnnounced = ""; // force player re-announce with correct position
            }

            if (!_wasActive)
            {
                _wasActive = true;
                CacheRefs(screen);
                string teamOnEntry = ReadTeamName();
                _lastTeamName = teamOnEntry;
                string entryMsg = "Roster.";
                if (!string.IsNullOrWhiteSpace(teamOnEntry))
                    entryMsg += $" {teamOnEntry}.";
                entryMsg += " Use up and down to browse players. Triggers to switch position groups.";
                SpeechManager.Speak(entryMsg);
                return;
            }

            // Check PlayerOptions popup first (takes focus priority)
            if (CheckPopup(screen)) return;


            // Check for above-list button selections first — takes priority.
            // This runs even when a player row appears visually selected, because
            // the game keeps the last player row highlighted while focus is elsewhere.
            if (CheckAboveList(screen)) return;

            // Combined check: team, position, and player in one announcement
            // so they don't interrupt each other.
            CheckAll();
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

        // Checks Animator state for d-pad selection on stat rows.
        // -1687521327 is the "selected" state hash discovered from logging on this game's UIButtonStretch Animator.
        // Name-based checks are kept as fallbacks for other button types.
        private static bool IsAnimatorSelected(GameObject go)
        {
            var anim = go.GetComponent<Animator>();
            if (anim == null || !anim.isActiveAndEnabled) return false;
            try
            {
                var info = anim.GetCurrentAnimatorStateInfo(0);
                int h = info.shortNameHash;
                // Known hash: -1687521327 = selected state for UIButtonStretch in this game
                if (h == -1687521327) return true;
                // Standard Unity button Animator state names
                if (info.IsName("Selected")   || info.IsName("Highlighted") ||
                    info.IsName("Pressed")     || info.IsName("Focused")    ||
                    info.IsName("Focus")       || info.IsName("Active")     ||
                    info.IsName("On")          || info.IsName("Current")    ||
                    info.IsName("Hover")       || info.IsName("Select"))
                    return true;
                // Log unknown hashes (85681099 = known unselected state, exclude from spam)
                if (h != 0 && h != 85681099 &&
                    h != Animator.StringToHash("Normal") &&
                    h != Animator.StringToHash("Idle") &&
                    h != Animator.StringToHash("Empty"))
                    Log($"ANIM_HASH go={go.name} h={h}");
            }
            catch { }
            return false;
        }

        private static bool IsNumericOnly(string s)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^\d+$");
        }

        private static string ReadTeamName()
        {
            // Re-read from cached TMP first
            if (_teamNameTMP != null)
            {
                try
                {
                    string val = Clean(_teamNameTMP);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
                catch { _teamNameTMP = null; }
            }
            return "";
        }

        private static void CheckAll()
        {
            var screen = GameObject.Find("Roster_Screen");
            if (screen == null) return;

            // Re-find team name TMP each poll
            var t = screen.transform.Find("Team Color Panel/Team Name Text");
            if (t != null)
            {
                var tmp = t.GetComponent<TextMeshProUGUI>();
                if (tmp != null) _teamNameTMP = tmp;
            }

            // Check team change INDEPENDENTLY — don't gate it on player TMP state.
            string team = ReadTeamName();
            bool teamChanged = !string.IsNullOrWhiteSpace(team) && team != _lastTeamName;
            if (teamChanged)
            {
                _lastTeamName = team;
                _lastAnnounced = ""; // reset so first player on new team announces
            }

            // Re-find player TMPs each poll (game rebuilds on team/position switch)
            RefreshPlayerRefs(screen);

            // Debug: log state once per 2 seconds so we can diagnose failures
            _debugTimer -= UnityEngine.Time.unscaledDeltaTime;
            if (_debugTimer <= 0f)
            {
                _debugTimer = 2f;
                Log($"team={ReadTeamName()} lastTeam={_lastTeamName} _fn={(_firstName != null ? Clean(_firstName) : "NULL")} _ln={(_lastName != null ? Clean(_lastName) : "NULL")} pos={(_position != null ? Clean(_position) : "NULL")} lastPos={_lastPlayerPos} lastAnnounced={_lastAnnounced}");
            }

            string first = Clean(_firstName);
            string last  = Clean(_lastName);
            string ovr   = Clean(_ovr);
            string pos   = Clean(_position);
            string id    = Clean(_jerseyId);

            string full = $"{first} {last}".Trim();

            bool wasFocused = PlayerInFocus;
            PlayerInFocus = !string.IsNullOrWhiteSpace(full);
            if (PlayerInFocus && !wasFocused) _lastAboveListButton = ""; // returned to list

            if (string.IsNullOrWhiteSpace(full))
            {
                // TMPs still rebuilding — announce team name alone if it just changed
                if (teamChanged) SpeechManager.Speak(team);
                return;
            }

            // Skip dedup when a bumper was just pressed — position group changed
            bool forcedByBumper = _bumperJustPressed;
            _bumperJustPressed = false;

            if (!forcedByBumper && full == _lastAnnounced) return;
            _lastAnnounced = full;

            // Detect position group change
            bool posChanged = !string.IsNullOrWhiteSpace(pos) && pos != _lastPlayerPos;
            if (posChanged) _lastPlayerPos = pos;

            // Build one atomic announcement so nothing gets interrupted:
            // Team+pos change: "Atlanta. RB. Khale Smith, number 78, overall 80"
            // Position change: "TE. Carl Reeves, number 2, overall 77"
            // Same group:      "Malcolm Attaochu, QB, number 1, overall 80"
            string announcement = "";
            if (teamChanged)
                announcement += $"{team}. ";
            if (posChanged)
                announcement += $"{pos}. ";
            announcement += full;
            if (!posChanged && !string.IsNullOrWhiteSpace(pos))
                announcement += $", {pos}";
            if (!string.IsNullOrWhiteSpace(id))   announcement += $", number {id}";
            if (!string.IsNullOrWhiteSpace(ovr))  announcement += $", overall {ovr}";
            SpeechManager.Speak(announcement);
        }

        // Returns true if an above-list button was selected (and handled), false otherwise.
        private static bool CheckAboveList(GameObject screen)
        {
            var namesContainer = screen.transform.Find("Name Scroll View/Names Viewport/Names");

            foreach (var mb in screen.GetComponentsInChildren<MonoBehaviour>(false))
            {
                if (mb == null) continue;
                string tn = mb.GetType().Name;
                if (tn != "UIButton" && tn != "UIButtonStretch" && tn != "UIButtonLinker" &&
                    tn != "UIStretchWithPips") continue;

                // Skip buttons inside the player list.
                if (namesContainer != null && mb.transform.IsChildOf(namesContainer)) continue;

                if (!IsPopupButtonSelected(mb)) continue;

                string label = BuildStatLabel(mb.gameObject);
                if (string.IsNullOrWhiteSpace(label)) continue;

                PlayerInFocus = false;
                if (label != _lastAboveListButton)
                {
                    _lastAboveListButton = label;
                    SpeechManager.Speak(label);
                }
                return true;
            }

            return false;
        }

        // Builds an announcement label for a button or stat row.
        // Looks for TitleText + Text2 children (used by all editor/rating buttons and stat rows).
        // Falls back to the first non-empty TMP, then GO name.
        private static string BuildStatLabel(GameObject go)
        {
            string titleText = "";
            string text2     = "";
            foreach (var tmp in go.GetComponentsInChildren<TextMeshProUGUI>(false))
            {
                if (tmp.gameObject.name == "TitleText" && string.IsNullOrWhiteSpace(titleText))
                    titleText = Clean(tmp);
                else if (tmp.gameObject.name == "Text2" && string.IsNullOrWhiteSpace(text2))
                    text2 = Clean(tmp);
            }
            if (!string.IsNullOrWhiteSpace(titleText))
                return string.IsNullOrWhiteSpace(text2) ? titleText : $"{titleText}: {text2}";
            // Fallback: first non-empty TMP
            foreach (var tmp in go.GetComponentsInChildren<TextMeshProUGUI>(false))
            {
                string t = Clean(tmp);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            return go.name;
        }

        private static void CacheEditorRefs(GameObject editorScreen)
        {
            _editorFirstNameTMP = FindEditorFieldTMP(editorScreen, "FirstName Btn");
            _editorLastNameTMP  = FindEditorFieldTMP(editorScreen, "LastName Btn");
            _lastEditorFirstName = Clean(_editorFirstNameTMP);
            _lastEditorLastName  = Clean(_editorLastNameTMP);
        }

        // Finds the "Text2" TMP inside the named field button (e.g. "FirstName Btn")
        private static TextMeshProUGUI FindEditorFieldTMP(GameObject root, string fieldButtonName)
        {
            foreach (var tmp in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp.gameObject.name != "Text2") continue;
                Transform t = tmp.transform.parent;
                while (t != null)
                {
                    if (t.gameObject.name == fieldButtonName) return tmp;
                    t = t.parent;
                }
            }
            return null;
        }

        private static void CheckEditorTyping()
        {
            // Re-cache TMPs if needed
            if (_editorFirstNameTMP == null || _editorLastNameTMP == null)
            {
                var editorScr = GameObject.Find("CharacterEditor_Screen");
                if (editorScr != null) CacheEditorRefs(editorScr);
            }

            // --- Keyboard mode detection ---
            // KeyboardNumPad stays active in scene always; detect mode by finding a selected key.
            string hoveredKey = null;
            var keyboard = GameObject.Find("KeyboardNumPad");
            if (keyboard != null)
            {
                foreach (var mb in keyboard.GetComponentsInChildren<MonoBehaviour>(false))
                {
                    if (mb == null) continue;
                    string tn = mb.GetType().Name;
                    if (tn != "UIButton" && tn != "UIButtonStretch" && tn != "UIButtonLinker") continue;
                    if (!IsPopupButtonSelected(mb)) continue;
                    string lbl = MenuReader.GetText(mb.gameObject);
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = mb.gameObject.name;
                    hoveredKey = NormalizeKeyLabel(lbl);
                    break;
                }
            }
            bool inKeyboardMode = !string.IsNullOrWhiteSpace(hoveredKey);

            // --- Field / slider / stat detection (three-pass) ---
            string fieldAnnouncement = null;  // named field button (FirstName Btn, Overall, etc.)
            string animStatLabel   = null;    // Animator-selected stat row (d-pad navigation)
            bool   inSliderMode    = false;
            GameObject newSliderStatGO = null;

            if (!inKeyboardMode)
            {
                var editorScreen = GameObject.Find("CharacterEditor_Screen");
                if (editorScreen != null)
                {
                    // Pass 1 — Animator-selected stat row (d-pad navigation in Ratings tab).
                    // Stat rows are direct children of the "Content" ScrollRect container.
                    // Hash -1687521327 = "selected" state for UIButtonStretch in this game.
                    foreach (var mb in editorScreen.GetComponentsInChildren<MonoBehaviour>(false))
                    {
                        if (mb == null) continue;
                        string tn = mb.GetType().Name;
                        if (tn != "UIButton" && tn != "UIButtonStretch" && tn != "UIButtonLinker" &&
                            tn != "UIStretchWithPips") continue;
                        if (keyboard != null && mb.transform.IsChildOf(keyboard.transform)) continue;
                        if (mb.gameObject.name == "SliderArea") continue;
                        // Only stat rows: direct child of "Content", not the Overall header or spacers
                        var parentName = mb.transform.parent?.gameObject.name;
                        if (parentName != "Content") continue;
                        if (mb.gameObject.name == "Overall" ||
                            mb.gameObject.name.StartsWith("Spacer")) continue;
                        if (!IsAnimatorSelected(mb.gameObject)) continue;
                        animStatLabel = BuildStatLabel(mb.gameObject);
                        break;
                    }

                    // Pass 2 — bool-field/scale scan: named field buttons, Overall, SliderArea.
                    // When a SliderArea is selected (user pressed A to edit), walk up to the
                    // parent field/stat button and announce that instead.
                    if (animStatLabel == null)
                    {
                        foreach (var mb in editorScreen.GetComponentsInChildren<MonoBehaviour>(false))
                        {
                            if (mb == null) continue;
                            string tn = mb.GetType().Name;
                            if (tn != "UIButton" && tn != "UIButtonStretch" && tn != "UIButtonLinker" &&
                                tn != "UIStretchWithPips") continue;
                            if (keyboard != null && mb.transform.IsChildOf(keyboard.transform)) continue;
                            if (!IsPopupButtonSelected(mb)) continue;

                            if (mb.gameObject.name == "SliderArea")
                            {
                                // SliderArea is selected (user pressed A to edit).
                                // Walk up: SliderArea → Contents → FieldBtn/StatGO
                                var fieldBtnGO = mb.transform.parent?.parent?.gameObject;
                                if (fieldBtnGO != null)
                                {
                                    fieldAnnouncement = GetEditorButtonAnnouncement(fieldBtnGO);
                                    inSliderMode = true;
                                    newSliderStatGO = fieldBtnGO;
                                }
                                break;
                            }

                            fieldAnnouncement = GetEditorButtonAnnouncement(mb.gameObject);
                            break;
                        }
                    }
                }
            }

            // Update slider GO ref when we entered a new slider/numpad field
            if (inSliderMode && newSliderStatGO != null && newSliderStatGO != _sliderStatGO)
            {
                _sliderStatGO    = newSliderStatGO;
                _sliderText2TMP  = FindTMP(_sliderStatGO, "Text2");
                _lastSliderValue = Clean(_sliderText2TMP);
            }
            if (!inSliderMode) { _sliderStatGO = null; _sliderText2TMP = null; _lastSliderValue = ""; }

            // --- Entry message (deferred until we confirm editor is truly in focus) ---
            bool hasDetection = inKeyboardMode || inSliderMode ||
                                animStatLabel != null || fieldAnnouncement != null;
            if (!EditorIsActive && hasDetection)
            {
                EditorIsActive = true;
                string editorMsg = "Character card editor.";
                if (!string.IsNullOrWhiteSpace(_lastEditorFirstName))
                    editorMsg += $" First name: {_lastEditorFirstName}.";
                if (!string.IsNullOrWhiteSpace(_lastEditorLastName))
                    editorMsg += $" Last name: {_lastEditorLastName}.";
                editorMsg += " Navigate to a field and press A to edit. Press B to cancel.";
                SpeechManager.Speak(editorMsg);
                _lastEditorFieldLabel = fieldAnnouncement ?? animStatLabel ?? "";
                _lastStatLabel = animStatLabel ?? "";
                return;
            }

            // --- Keyboard transitions ---
            if (inKeyboardMode && !_keyboardWasActive)
            {
                _keyboardWasActive = true;
                _lastKeyLabel = "";
                string msg = string.IsNullOrWhiteSpace(_lastEditorFieldLabel)
                    ? "Keyboard."
                    : $"Editing {_lastEditorFieldLabel}.";
                msg += " Navigate to Back to delete characters.";
                SpeechManager.Speak(msg);
            }
            else if (!inKeyboardMode && _keyboardWasActive)
            {
                _keyboardWasActive = false;
                _lastKeyLabel = "";
                _lastEditorFieldLabel = "";
                _lastStatLabel = "";
            }

            // --- Per-frame announcements ---
            if (inKeyboardMode)
            {
                if (hoveredKey != _lastKeyLabel)
                {
                    _lastKeyLabel = hoveredKey;
                    SpeechManager.Speak(hoveredKey);
                }
            }
            else if (inSliderMode)
            {
                // On entry to slider/numpad: announce "Editing [field name]" once
                if (!string.IsNullOrWhiteSpace(fieldAnnouncement) &&
                    fieldAnnouncement != _lastEditorFieldLabel)
                {
                    _lastEditorFieldLabel = fieldAnnouncement;
                    _lastSliderValue = Clean(_sliderText2TMP);
                    SpeechManager.Speak($"Editing {fieldAnnouncement}");
                }
                // Monitor value changes (slider drag or numpad confirm)
                if (_sliderText2TMP != null)
                {
                    string newVal = Clean(_sliderText2TMP);
                    if (!string.IsNullOrWhiteSpace(newVal) && newVal != _lastSliderValue)
                    {
                        _lastSliderValue = newVal;
                        SpeechManager.Speak(newVal);
                    }
                }
            }
            else if (animStatLabel != null && animStatLabel != _lastStatLabel)
            {
                // D-pad navigated to a new stat row
                _lastStatLabel = animStatLabel;
                _lastEditorFieldLabel = animStatLabel;
                SpeechManager.Speak(animStatLabel);
            }
            else if (fieldAnnouncement != null && fieldAnnouncement != _lastEditorFieldLabel)
            {
                _lastEditorFieldLabel = fieldAnnouncement;
                _lastStatLabel = "";
                SpeechManager.Speak(fieldAnnouncement + ", button");
            }

            // --- Text delta (runs regardless of mode) ---
            if (_editorFirstNameTMP != null)
            {
                string firstName = Clean(_editorFirstNameTMP);
                AnnounceTextDelta(_lastEditorFirstName, firstName);
                _lastEditorFirstName = firstName;
            }
            if (_editorLastNameTMP != null)
            {
                string lastName = Clean(_editorLastNameTMP);
                AnnounceTextDelta(_lastEditorLastName, lastName);
                _lastEditorLastName = lastName;
            }
        }

        // Returns a friendly announcement for a character editor field/button.
        private static string GetEditorButtonAnnouncement(GameObject go)
        {
            string goName = go.name;
            if (goName == "FirstName Btn")
            {
                string val = Clean(_editorFirstNameTMP);
                return $"First name: {(string.IsNullOrWhiteSpace(val) ? "empty" : val)}";
            }
            if (goName == "LastName Btn")
            {
                string val = Clean(_editorLastNameTMP);
                return $"Last name: {(string.IsNullOrWhiteSpace(val) ? "empty" : val)}";
            }
            return BuildStatLabel(go);
        }

        // Normalizes raw key labels to screen-reader-friendly names.
        private static string NormalizeKeyLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            string trimmed = raw.Trim();
            // Common backspace/delete representations
            if (trimmed == "←" || trimmed == "<-" || trimmed == "DEL" ||
                trimmed.ToLower() == "del" || trimmed.ToLower() == "delete" ||
                trimmed.ToLower() == "back" || trimmed.ToLower() == "backspace" ||
                trimmed == "⌫")
                return "Back";
            // Space bar
            if (trimmed.ToLower() == "space" || trimmed == " " || trimmed == "___")
                return "Space";
            return trimmed;
        }

        // Speaks what changed between oldText and newText:
        // added character(s), "delete" on backspace, or full new value on big change.
        private static void AnnounceTextDelta(string oldText, string newText)
        {
            if (newText == oldText) return;

            // Character added at end
            if (newText.Length == oldText.Length + 1 && newText.StartsWith(oldText))
            {
                string added = newText.Substring(newText.Length - 1);
                SpeechManager.Speak(added == " " ? "space" : added);
                return;
            }
            // Character deleted from end
            if (newText.Length == oldText.Length - 1 && oldText.StartsWith(newText))
            {
                SpeechManager.Speak("delete");
                return;
            }
            // Larger change (paste, clear, position switch) — speak the whole new value
            if (!string.IsNullOrWhiteSpace(newText))
                SpeechManager.Speak(newText);
        }

        private static void Log(string msg)
        {
            try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                $"[{DateTime.Now:HH:mm:ss.fff}] ROSTER: {msg}\n"); } catch { }
        }

        private static void RefreshPlayerRefs(GameObject screen)
        {
            // Find the selected player row using the same method as the button scanner:
            // scan for a UIButton/UIButtonStretch/UIButtonLinker component with a "selected"
            // boolean field, falling back to the largest-scale child.
            var namesContainer = screen.transform.Find("Name Scroll View/Names Viewport/Names");
            if (namesContainer != null)
            {
                Transform selectedRow = null;
                float maxScale = 1.0f;

                for (int i = 0; i < namesContainer.childCount; i++)
                {
                    Transform child = namesContainer.GetChild(i);
                    bool selected = false;
                    foreach (var mb in child.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        string tn = mb.GetType().Name;
                        if (tn != "UIButton" && tn != "UIButtonStretch" && tn != "UIButtonLinker") continue;
                        if (IsPopupButtonSelected(mb)) { selected = true; break; }
                    }
                    if (selected) { selectedRow = child; break; }

                    float s = child.localScale.x;
                    if (s > maxScale) { maxScale = s; selectedRow = child; }
                }

                if (selectedRow != null)
                {
                    _firstName = FindTMP(selectedRow.gameObject, "FIRSTNAME");
                    _lastName  = FindTMP(selectedRow.gameObject, "LASTNAME");
                    _jerseyId  = FindTMP(selectedRow.gameObject, "ID");
                    _ovr       = FindTMP(selectedRow.gameObject, "OVR");
                }
            }

            // Position always comes from OtherInfo — it reflects the selected player
            // regardless of which row is highlighted, and survives list rebuilds.
            var posTransform = screen.transform.Find("OtherInfo/POSITION");
            if (posTransform != null)
                _position = posTransform.GetComponent<TextMeshProUGUI>();
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

            // Position from OtherInfo — always reflects the selected player
            var posTransform = screen.transform.Find("OtherInfo/POSITION");
            if (posTransform != null)
                _position = posTransform.GetComponent<TextMeshProUGUI>();

            // Team name — try known paths first, then scan by GO name
            _teamNameTMP = null;
            string[] teamPaths = {
                "Team Color Panel/Team Name Text",
                "Team Name", "TeamName", "TEAM", "Team",
                "Team Name Text", "TeamName Text"
            };
            foreach (string path in teamPaths)
            {
                Transform t = screen.transform.Find(path);
                if (t != null)
                {
                    var tmp = t.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) { _teamNameTMP = tmp; break; }
                }
            }

            // Fallback: scan all TMPs for one whose GO name contains "team" (case-insensitive)
            if (_teamNameTMP == null)
            {
                foreach (var tmp in screen.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    string goName = tmp.gameObject.name.ToLower();
                    if (goName.Contains("team") && !goName.Contains("teammate"))
                    {
                        _teamNameTMP = tmp;
                        break;
                    }
                }
            }

            // Seed last values so we don't double-announce on entry
            _lastAnnounced = $"{Clean(_firstName)} {Clean(_lastName)}".Trim();
            _lastPlayerPos = Clean(_position);
        }

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            try
            {
                // Accessing .text on a destroyed Unity object throws
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
            catch { return ""; }
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
