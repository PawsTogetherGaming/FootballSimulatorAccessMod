using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
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
    /// How To Play — button learning mode.
    ///
    /// On entry: speaks help text explaining the mode.
    /// While active: pressing any controller button announces that button's
    ///   in-game function for the current section (Offense / Defense / Replay).
    /// The game's own bumper controls switch sections; we detect the change and
    ///   announce the new section name automatically.
    ///
    /// PollInput() is called every Unity frame (before the 120ms throttle) so
    /// that GetKeyDown is reliable.  Poll() is called at the normal 120ms rate
    /// for screen detection, section hash checks, and dictionary building.
    /// </summary>
    public static class HowToPlayReader
    {
        // ---- Help text ----
        private const string ENTRY_HELP =
            "How to play. Button learning mode. " +
            "Press any button to hear its in-game function. " +
            "Tilt the right stick left or right to switch between sections. " +
            "Note: pressing B will exit this screen.";

        // ---- Joystick button → friendly label mapping ----
        private struct BtnEntry { internal KeyCode Key; internal string Label;
            internal BtnEntry(KeyCode k, string l) { Key = k; Label = l; } }

        private static readonly BtnEntry[] POLL_BUTTONS =
        {
            new BtnEntry(KeyCode.JoystickButton0,  "A button"),
            new BtnEntry(KeyCode.JoystickButton1,  "B button"),
            new BtnEntry(KeyCode.JoystickButton2,  "X button"),
            new BtnEntry(KeyCode.JoystickButton3,  "Y button"),
            new BtnEntry(KeyCode.JoystickButton4,  "Left bumper"),
            new BtnEntry(KeyCode.JoystickButton5,  "Right bumper"),
            new BtnEntry(KeyCode.JoystickButton6,  "Back"),
            new BtnEntry(KeyCode.JoystickButton7,  "Start"),
            new BtnEntry(KeyCode.JoystickButton8,  "Left stick"),
            new BtnEntry(KeyCode.JoystickButton9,  "Right stick"),
            new BtnEntry(KeyCode.JoystickButton10, "D-pad up"),
            new BtnEntry(KeyCode.JoystickButton11, "D-pad down"),
            new BtnEntry(KeyCode.JoystickButton12, "D-pad left"),
            new BtnEntry(KeyCode.JoystickButton13, "D-pad right"),
        };

        // ---- State ----
        private static bool       _wasActive   = false;
        private static int        _lastHash    = 0;
        private static string     _lastTabName = "";   // last announced section name
        private static GameObject _screenRef   = null; // set by Poll(), read by PollInput()

        // Right stick X — edge detection for section navigation
        private static float _rsXPrev        = 0f;
        private static float _rsLastNavTime  = -10f;  // time of last NavigateSection call
        private const  float RS_THRESHOLD    = 0.3f;
        private const  float RS_NAV_COOLDOWN = 0.5f;  // min seconds between section switches

        // Delayed section announcement — prevents bumper function speech from
        // being cut off by an immediate section-change announcement.
        private static string _pendingTabName = "";
        private static float  _pendingTabTime = -1f;
        private const  float  SECTION_DELAY  = 1.5f;

        // Track when a function was last spoken (for the delay above)
        private static float _lastFunctionSpeakTime = -10f;

        // Harmony patches
        private static bool _htpHarmonyPatched         = false;
        private static bool _htpLateUpdatePatched      = false;

        // HowToPlayMenu reflection — RightTab / LeftTab
        private static MonoBehaviour _howToPlayMenu   = null;
        private static MethodInfo    _rightTabMethod  = null;
        private static MethodInfo    _leftTabMethod   = null;

        // Button function lookup: label (lowercase) → instruction
        private static readonly Dictionary<string, string> _btnFn =
            new Dictionary<string, string>();

        // Reflection cache for DynamicButtonIconContext
        private static bool       _reflDone    = false;
        private static Type?      _dbicType    = null;
        private static FieldInfo? _buttonField = null;

        // =========================================================
        // PollInput — called every Unity frame, BEFORE the 120ms throttle.
        // Uses GetKeyDown so each physical press is caught exactly once.
        // No section-advance logic here — the game handles that natively.
        // =========================================================

        public static void PollInput()
        {
            if (!_wasActive || _screenRef == null) return;

            // ---- Right stick left/right: navigate sections ----
            // Joy1Axis4 maps to a trigger on this game (reads -1 at rest).
            // Skip it if pegged at ±1; fall back to Joy1Axis5 (right stick X).
            float rsX = 0f;
            try { rsX = Input.GetAxis("Joy1Axis4"); } catch { }
            if (Mathf.Abs(rsX) > 0.9f) rsX = 0f;   // trigger at rest = -1, not the stick
            if (rsX == 0f) { try { rsX = Input.GetAxis("Joy1Axis5"); } catch { } }
            if (rsX == 0f) { try { rsX = Input.GetAxis("RightStickX"); } catch { } }
            if (Mathf.Abs(rsX - _rsXPrev) > 0.1f)
                Plugin.Log.LogInfo($"[HTP] rsX={rsX:F3} prev={_rsXPrev:F3} (axis5)");

            bool navReady = (Time.unscaledTime - _rsLastNavTime) >= RS_NAV_COOLDOWN;
            if (navReady)
            {
                if (rsX > RS_THRESHOLD && _rsXPrev <= RS_THRESHOLD)
                {
                    NavigateSection(_screenRef, +1);
                    _rsLastNavTime = Time.unscaledTime;
                    _rsXPrev = rsX;
                    return;
                }
                else if (rsX < -RS_THRESHOLD && _rsXPrev >= -RS_THRESHOLD)
                {
                    NavigateSection(_screenRef, -1);
                    _rsLastNavTime = Time.unscaledTime;
                    _rsXPrev = rsX;
                    return;
                }
            }

            _rsXPrev = rsX;

            // ---- All buttons: announce function on press ----
            // B exits the screen via the game's own unpatched handler, but it still
            // announces its function first (our prefix fires before the exit path).
            foreach (var btn in POLL_BUTTONS)
            {
                if (!Input.GetKeyDown(btn.Key)) continue;

                string fn = LookupFn(btn.Label);
                _lastFunctionSpeakTime = Time.unscaledTime;
                SpeechManager.Speak(!string.IsNullOrEmpty(fn)
                    ? $"{btn.Label}: {fn}."
                    : $"{btn.Label}: not listed in this section.");

                return; // only one button per frame
            }
        }

        // ---- Section navigation (right stick left/right) ----
        // Calls HowToPlayMenu.RightTab() or LeftTab() via reflection so the game
        // handles all panel/content switching correctly (including Xbox_Defense,
        // Xbox_Offense, Xbox_Replay sub-panels inside Controls_Image).

        private static void NavigateSection(GameObject screen, int direction)
        {
            // Cache HowToPlayMenu reference and its tab methods on first call
            if (_howToPlayMenu == null)
            {
                foreach (var mb in screen.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    if (mb.GetType().Name != "HowToPlayMenu") continue;
                    _howToPlayMenu  = mb;
                    var t           = mb.GetType();
                    _rightTabMethod = t.GetMethod("RightTab",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _leftTabMethod  = t.GetMethod("LeftTab",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Plugin.Log.LogInfo($"[HTP] NavigateSection: cached HowToPlayMenu, RightTab={_rightTabMethod != null}, LeftTab={_leftTabMethod != null}");
                    break;
                }
            }

            if (_howToPlayMenu == null) return;

            try
            {
                if (direction > 0 && _rightTabMethod != null)
                {
                    _rightTabMethod.Invoke(_howToPlayMenu, null);
                    Plugin.Log.LogInfo("[HTP] NavigateSection: called RightTab()");
                }
                else if (direction < 0 && _leftTabMethod != null)
                {
                    _leftTabMethod.Invoke(_howToPlayMenu, null);
                    Plugin.Log.LogInfo("[HTP] NavigateSection: called LeftTab()");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HTP] NavigateSection: {ex.Message}");
            }
        }

        // =========================================================
        // Poll — called at the 120ms throttled rate.
        // Handles screen detection, entry message, section-hash
        // change detection, and dictionary building.
        // =========================================================

        public static void Poll()
        {
            var screen = GameObject.Find("How To Play");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                _wasActive             = false;
                _screenRef             = null;
                _lastHash              = 0;
                _lastTabName           = "";
                _rsXPrev               = 0f;
                _rsLastNavTime         = -10f;
                _howToPlayMenu         = null;
                _rightTabMethod        = null;
                _leftTabMethod         = null;
                _pendingTabName        = "";
                _pendingTabTime        = -1f;
                _lastFunctionSpeakTime = -10f;
                _btnFn.Clear();
                // _htpHarmonyPatched / _htpLateUpdatePatched stay true — patches are permanent for this session
                return;
            }

            EnsureReflection();
            _screenRef = screen;

            if (!_wasActive)
            {
                _wasActive   = true;
                _lastHash    = HashSection(screen);
                _lastTabName = GetTabName(screen);
                BuildDictionary(screen);
                SpeechManager.Speak(ENTRY_HELP);

                // Diagnostic: log section hierarchy + MB types for B-button patch
                LogSectionDiag(screen);
                TryApplyHtpPatch(screen);
                return;
            }

            // Detect section change (game switches tab with bumpers).
            // The hash may oscillate within a section (animated tab indicator) so we
            // only announce when the resolved section NAME actually changes.
            // Announcement is delayed by SECTION_DELAY so a bumper function
            // announcement isn't cut off mid-sentence.
            int hash = HashSection(screen);
            if (hash != _lastHash)
            {
                _lastHash = hash;
                BuildDictionary(screen);   // always rebuild so button lookups stay current
                string tab = GetTabName(screen);

                if (tab != _lastTabName)
                {
                    _lastTabName    = tab;
                    _pendingTabName = tab;
                    // Delay at least SECTION_DELAY, or until function speech has had time to finish
                    float speakFinishEstimate = _lastFunctionSpeakTime + SECTION_DELAY;
                    _pendingTabTime = Mathf.Max(Time.unscaledTime + 0.3f, speakFinishEstimate);
                    Plugin.Log.LogInfo($"[HTP] Section → '{tab}' (pending in {_pendingTabTime - Time.unscaledTime:F1}s)");
                }
            }

            // Fire pending section announcement once delay has elapsed
            if (!string.IsNullOrEmpty(_pendingTabName) && Time.unscaledTime >= _pendingTabTime)
            {
                string name = _pendingTabName;
                _pendingTabName = "";
                SpeechManager.Speak(string.IsNullOrEmpty(name)
                    ? "New section. Press any button to hear its function."
                    : $"{name}. Press any button to hear its function.");
            }
        }

        // =========================================================
        // Section hash — detects when the game switches between
        // Offense / Defense / Replay.
        //
        // Strategy 1: search entire hierarchy for GameObjects whose
        //   name contains "Offense", "Defense", or "Replay", skipping
        //   pure TMP labels, and hash activeSelf + CanvasGroup alpha.
        //   This works regardless of whether the container is "Xbox"
        //   or something else.
        //
        // Strategy 2 (fallback): hash the set of button labels that
        //   GetActiveControls() currently sees.  This changes whenever
        //   the visible instruction set changes.
        // =========================================================

        private static int HashSection(GameObject screen)
        {
            // Primary: hash the active states of Xbox sub-panels inside Controls_Image.
            // These are what RightTab/LeftTab toggle: Xbox_Offense, Xbox_Defense, Xbox_Replay.
            Transform ctrl = screen.transform.Find("Controls_Image");
            if (ctrl != null)
            {
                Transform xbox = ctrl.Find("Xbox");
                if (xbox != null)
                {
                    int h = 17;
                    foreach (Transform child in xbox)
                        h = h * 31 + (child.gameObject.name + (child.gameObject.activeSelf ? "1" : "0")).GetHashCode();
                    if (h != 17) return h;
                }
            }

            // Fallback: hash the visible button labels
            int hFallback = 17;
            foreach (var entry in GetActiveControls(screen))
                if (!string.IsNullOrEmpty(entry.Button))
                    hFallback = hFallback * 31 + entry.Button.GetHashCode();
            return hFallback;
        }

        // ---- Tab name ----
        // Finds the currently active section name.
        // Searches for active (non-TMP-label) GameObjects whose name
        // contains the section keyword, at any depth.

        private static string GetTabName(GameObject screen)
        {
            // Primary: check which Xbox sub-panel is active inside Controls_Image.
            // RightTab/LeftTab activate Xbox_Offense, Xbox_Defense, or Xbox_Replay.
            Transform ctrl = screen.transform.Find("Controls_Image");
            if (ctrl != null)
            {
                Transform xbox = ctrl.Find("Xbox");
                if (xbox != null)
                {
                    foreach (Transform child in xbox)
                    {
                        if (!child.gameObject.activeSelf) continue;
                        string n = child.gameObject.name;
                        if (n.Contains("Defense"))                              return "Defense";
                        if (n.Contains("Replay"))                               return "Replay";
                        if (n.Contains("Offense") || n.Contains("OffenseContainer")) return "Offense";
                    }
                }
            }

            // Fallback: tab labels in Header/Tabs
            Transform header = screen.transform.Find("Header/Tabs");
            if (header != null)
            {
                foreach (Transform tab in header)
                {
                    var tmp = tab.GetComponent<TextMeshProUGUI>();
                    if (tmp == null || !tab.gameObject.activeSelf) continue;
                    string t = (tmp.text ?? "").Trim().ToUpperInvariant();
                    if (t == "OFFENSE") return "Offense";
                    if (t == "DEFENSE") return "Defense";
                    if (t == "REPLAY")  return "Replay";
                }
            }

            return "";
        }

        // ---- Diagnostic ----

        private static void LogSectionDiag(GameObject screen)
        {
            try
            {
                // Log direct children of "Xbox" (if it exists)
                Transform xbox = screen.transform.Find("Xbox");
                if (xbox != null)
                {
                    Plugin.Log.LogInfo("[HTP] 'Xbox' container found");
                    foreach (Transform c in xbox)
                        Plugin.Log.LogInfo($"[HTP]   '{c.gameObject.name}' activeSelf={c.gameObject.activeSelf} activeInHierarchy={c.gameObject.activeInHierarchy}");
                }
                else
                {
                    Plugin.Log.LogInfo("[HTP] 'Xbox' not found — listing root children:");
                    foreach (Transform c in screen.transform)
                        Plugin.Log.LogInfo($"[HTP]   '{c.gameObject.name}' activeSelf={c.gameObject.activeSelf}");
                }

                // Log the controls found in the current section
                var controls = GetActiveControls(screen);
                Plugin.Log.LogInfo($"[HTP] {controls.Count} controls in current section:");
                foreach (var e in controls)
                    Plugin.Log.LogInfo($"[HTP]   btn='{e.Button}' ins='{e.Instruction}'");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HTP] LogSectionDiag: {ex.Message}");
            }
        }

        // ---- Dictionary ----

        private static void BuildDictionary(GameObject screen)
        {
            _btnFn.Clear();
            var controls = GetActiveControls(screen);
            Plugin.Log.LogInfo($"[HTP] BuildDictionary: {controls.Count} controls found");
            foreach (var entry in controls)
            {
                Plugin.Log.LogInfo($"[HTP]   btn='{entry.Button}' ins='{entry.Instruction}'");
                if (!string.IsNullOrWhiteSpace(entry.Button)
                    && !string.IsNullOrWhiteSpace(entry.Instruction))
                {
                    _btnFn[entry.Button.ToLowerInvariant()] = entry.Instruction;
                }
            }

            if (controls.Count == 0)
                Plugin.Log.LogInfo("[HTP] BuildDictionary: 0 controls after tab switch — game may still be animating");

        }

        private static string LookupFn(string label)
        {
            _btnFn.TryGetValue(label.ToLowerInvariant(), out string fn);
            return fn ?? "";
        }

        private static void DumpHierarchy(Transform t, int depth, bool includeInactive = false)
        {
            if (!includeInactive && !t.gameObject.activeSelf && depth > 0) return;
            string indent = new string(' ', depth * 2);
            var tmp = t.GetComponent<TextMeshProUGUI>();
            string tmpSnip = tmp != null ? $" TMP='{((tmp.text ?? "").Length > 30 ? tmp.text.Substring(0, 30) + "…" : tmp.text.Trim())}'" : "";
            Plugin.Log.LogInfo($"[HTP] {indent}'{t.gameObject.name}' active={t.gameObject.activeSelf}{tmpSnip}");
            foreach (Transform child in t)
                DumpHierarchy(child, depth + 1, includeInactive);
        }

        // ---- Pane text parser ----
        // Parses "- ButtonName / AltName: Action" lines from Pane 1 / Pane 2 text blocks.

        private static List<ControlEntry> ParsePaneText(string text)
        {
            var results = new List<ControlEntry>();
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().TrimStart('-').Trim();
                int colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                string btnPart = line.Substring(0, colonIdx).Trim();
                string action  = line.Substring(colonIdx + 1).Trim();
                if (string.IsNullOrWhiteSpace(action)) continue;

                foreach (string label in PaneButtonToLabels(btnPart))
                    if (!string.IsNullOrEmpty(label))
                        results.Add(new ControlEntry(label, action));
            }
            return results;
        }

        // Maps a button text token (from the pane text format) to our standard label(s).
        // The game's HTP text uses Xbox/PS mixed naming where L1=Left bumper, R1=Right bumper,
        // L2=Left trigger, R2=Right trigger.
        private static IEnumerable<string> PaneButtonToLabels(string btnPart)
        {
            string u = btnPart.ToUpperInvariant();

            // Compound: both bumpers
            if (u.Contains("BUMPER") || (u.Contains("L1") && u.Contains("R1")))
            {
                yield return "Left bumper";
                yield return "Right bumper";
                yield break;
            }

            // Compound: both triggers
            if (u.Contains("L2") && u.Contains("R2"))
            {
                yield return "Left trigger";
                yield return "Right trigger";
                yield break;
            }

            // Compound: Y and B together (e.g. "Y/B Triangle/Circle")
            bool hasY = u.StartsWith("Y/") || u.StartsWith("Y ") || u == "Y";
            bool hasB = u.Contains("/B ") || u.Contains("/B/") || u.EndsWith("/B") || u.Contains("B/C");
            if (hasY && hasB) { yield return "Y button"; yield return "B button"; yield break; }

            // Single button — check most specific patterns first
            if (u.Contains("LEFT TRIGGER") || (u.Contains("L1") && !u.Contains("R1")))
                { yield return "Left bumper"; yield break; }
            if (u.Contains("RIGHT TRIGGER") || (u.Contains("R1") && !u.Contains("L1")))
                { yield return "Right bumper"; yield break; }
            if (u.Contains("L2")) { yield return "Left trigger";  yield break; }
            if (u.Contains("R2")) { yield return "Right trigger"; yield break; }

            if (u.Contains("LEFT STICK"))  { yield return "Left stick";  yield break; }
            if (u.Contains("RIGHT STICK")) { yield return "Right stick"; yield break; }

            // Single face buttons — only match at word boundary to avoid
            // false positives from words like "SQUARE" or "CIRCLE"
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\bA\b")) { yield return "A button"; yield break; }
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\bB\b")) { yield return "B button"; yield break; }
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\bX\b")) { yield return "X button"; yield break; }
            if (System.Text.RegularExpressions.Regex.IsMatch(u, @"\bY\b")) { yield return "Y button"; yield break; }

            if (u.Contains("START"))  { yield return "Start"; yield break; }
            if (u.Contains("SELECT") || u.Contains("BACK")) { yield return "Back"; yield break; }
            if (u.Contains("D-PAD") || u.Contains("DPAD"))  { yield return "D-pad"; yield break; }
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

            var img = btnGo.GetComponent<Image>();
            if (img != null && img.sprite != null)
                return SpriteNameToLabel(img.sprite.name);

            return "";
        }

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
                case "Select":     return "Back";
                case "Back":       return "Back";
                case "LS": case "L3": return "Left stick";
                case "RS": case "R3": return "Right stick";
                case "DPad_Up":    return "D-pad up";
                case "DPad_Down":  return "D-pad down";
                case "DPad_Left":  return "D-pad left";
                case "DPad_Right": return "D-pad right";
                default:           return "";
            }
        }

        private static string SpriteNameToLabel(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return "";

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

            return spriteName;
        }

        // =========================================================
        // Harmony B-button suppression
        //
        // The game's MonoBehaviour.Update runs BEFORE our PlayerLoop,
        // so we can't prevent B from exiting HTP by setting a flag first.
        // Instead we Harmony-patch the HTP screen's MB Update method:
        //   • B tap (held < 0.5 s) → skip Update so the game doesn't exit
        //   • B hold (≥ 0.5 s)    → let Update run, game exits naturally
        // =========================================================

        private static void TryApplyHtpPatch(GameObject screen)
        {
            if (_htpHarmonyPatched) return;
            var harmony = Plugin.HarmonyInstance;
            if (harmony == null) return;

            // Check root first, then fall back to all children.
            // The HTP tab-switching MB may be on a child object, not the root.
            // Filter to Assembly-CSharp only to avoid patching Unity/BepInEx internals.
            MonoBehaviour[] rootMbs = screen.GetComponents<MonoBehaviour>();
            MonoBehaviour[] allMbs  = screen.GetComponentsInChildren<MonoBehaviour>(true);

            bool foundOnRoot = false;
            foreach (var mb in rootMbs)
            {
                if (mb == null) continue;
                var t2 = mb.GetType();
                if (t2.Assembly.GetName().Name != "Assembly-CSharp") continue;
                var u2 = t2.GetMethod("Update",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (u2 != null) { foundOnRoot = true; break; }
            }

            IEnumerable<MonoBehaviour> candidates = foundOnRoot
                ? (IEnumerable<MonoBehaviour>)rootMbs
                : allMbs;

            // Also patch EventSystem.Update — Unity maps JoystickButton1 (B) to "Cancel"
            // via the StandaloneInputModule and processes it independently of MonoBehaviour
            // Update/LateUpdate.  We suppress it with the same B-hold-to-exit logic.
            try
            {
                Type esType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    esType = asm.GetType("UnityEngine.EventSystems.EventSystem");
                    if (esType != null) break;
                }
                if (esType != null)
                {
                    var esUpdate = esType.GetMethod("Update",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (esUpdate != null)
                    {
                        harmony.Patch(esUpdate, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(HowToPlayReader), nameof(HtpLateUpdatePrefix))));
                        Plugin.Log.LogInfo("[HTP] EventSystem.Update patch applied");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HTP] EventSystem patch failed: {ex.Message}");
            }

            foreach (var mb in candidates)
            {
                if (mb == null) continue;
                var type = mb.GetType();
                if (type.Assembly.GetName().Name != "Assembly-CSharp") continue;
                Plugin.Log.LogInfo($"[HTP] Candidate MB: {type.FullName} (on '{mb.gameObject.name}')");

                // Patch Update (UIScreen) — suppresses B-button exit and general input
                if (!_htpHarmonyPatched)
                {
                    var updateMethod = type.GetMethod("Update",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (updateMethod != null)
                    {
                        try
                        {
                            harmony.Patch(updateMethod, prefix: new HarmonyMethod(
                                AccessTools.Method(typeof(HowToPlayReader), nameof(HtpUpdatePrefix))));
                            Plugin.Log.LogInfo($"[HTP] Update patch applied → {type.FullName}");
                            _htpHarmonyPatched = true;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HTP] Update patch failed for {type.FullName}: {ex.Message}");
                        }
                    }
                }

                // Patch LateUpdate on every game MB that has one — we need to suppress
                // tab navigation on HowToPlayMenu.LateUpdate specifically, but UIScreen
                // may also have LateUpdate, so we patch all of them.
                {
                    var lateUpdateMethod = type.GetMethod("LateUpdate",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (lateUpdateMethod != null)
                    {
                        try
                        {
                            harmony.Patch(lateUpdateMethod, prefix: new HarmonyMethod(
                                AccessTools.Method(typeof(HowToPlayReader), nameof(HtpLateUpdatePrefix))));
                            Plugin.Log.LogInfo($"[HTP] LateUpdate patch applied → {type.FullName}");
                            _htpLateUpdatePatched = true;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HTP] LateUpdate patch failed for {type.FullName}: {ex.Message}");
                        }
                    }
                }
            }
        }

        // Harmony prefix — called BEFORE the game's HTP MonoBehaviour.Update.
        //
        // We suppress ALL input processing in the game's Update while HTP is
        // active.  This stops the game from:
        //   • Switching sections via LB / RB
        //   • Re-interpreting the right stick axis as tab navigation
        //   • Exiting on a B tap
        //
        // The ONLY case where we let Update run is when B has been held long
        // enough to exit (≥ B_HOLD_EXIT seconds), so the game can close the screen.
        private static bool HtpUpdatePrefix()
        {
            if (!_wasActive) return true;  // HTP not open — run normally
            return false;
        }

        // Harmony prefix for HowToPlayMenu.LateUpdate — suppresses the game's own
        // bumper/tab navigation while HTP is active (we handle it ourselves via
        // RightTab/LeftTab reflection calls triggered by the right stick).
        //
        // Uses the same B-hold logic as HtpUpdatePrefix: LateUpdate is allowed to
        // run only once B has been held long enough to exit, so the game can close
        // the screen.  All other input (bumpers, right stick, B tap) is suppressed.
        private static bool HtpLateUpdatePrefix()
        {
            if (!_wasActive) return true; // HTP not open — run normally
            return false;
        }

        // ---- Text clean ----

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            var sb = new StringBuilder();
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
