using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Applies Harmony patches manually (not via PatchAll) so that a missing method
    /// skips just that one patch instead of crashing the entire plugin.
    /// </summary>
    public static class MenuReader
    {
        private static string _lastAnnounced = "";
        private static bool _uiButtonDumped = false;

        // ---- Per-frame polling state (driven by FootballMenu.Update patch) ----
        private static bool   _pollFirstRun  = true;
        private static float  _pollAccum     = 0f;
        private const  float  POLL_INTERVAL  = 0.12f;
        private static Type?  _uiButtonType;
        private static FieldInfo? _btnSelectedField;
        private static bool   _reflectionDone = false;
        private static string _lastPolled = "";

        private struct PatchEntry
        {
            public string target;
            public string postfixName;
            public string prefixName;   // optional — null means no prefix
        }

        public static void ApplyPatches(Harmony harmony)
        {
            int ok = 0, skipped = 0;

            var patches = new PatchEntry[]
            {
                new PatchEntry { target = "UIButton:Select",                         postfixName = nameof(OnButtonSelect) },
                new PatchEntry { target = "UIButton:SelectSelf",                     postfixName = nameof(OnButtonSelect) },
                new PatchEntry { target = "UIButton:Activate",                       postfixName = nameof(OnButtonActivate) },
                new PatchEntry { target = "UIScreen:SelectButton",                   postfixName = nameof(OnScreenSelectButton) },
                new PatchEntry { target = "UISlider:ChangeRatingText",               postfixName = nameof(OnControl) },
                new PatchEntry { target = "UISlider:SetValue",                       postfixName = nameof(OnControl) },
                new PatchEntry { target = "UIToggle:SetToggle",                      postfixName = nameof(OnControl) },
                new PatchEntry { target = "UIStretchWithPips:SetSliderValueAndText", postfixName = nameof(OnControl) },
                new PatchEntry { target = "UIScreen:PressedDirection",               postfixName = nameof(OnScreenNav) },
                new PatchEntry { target = "UIScreen:ProcessInputs",                  postfixName = nameof(OnScreenProcessInputs) },
                new PatchEntry { target = "FootballMenu:ControllerSelect_PressedDirection", postfixName = nameof(OnScreenNav) },
                new PatchEntry { target = "FootballMenu:EnableNewScreen",            postfixName = nameof(OnNewScreen) },
                // Update patches — guaranteed to fire every frame in menus
                new PatchEntry { target = "FootballMenu:Update",                     postfixName = nameof(OnGameUpdate) },
                new PatchEntry { target = "FootballMainMenu:Update",                 postfixName = nameof(OnGameUpdate) },
                new PatchEntry { target = "SettingsUI:OnHover",                      postfixName = nameof(OnSettingsHover) },
                new PatchEntry { target = "HighlightMenu:ActivateHighlight",         postfixName = nameof(OnHighlight) },
                new PatchEntry { target = "OnScreenKeyboard:SetSelectedButton",      postfixName = nameof(OnKeyboardSelect) },
            };

            foreach (var entry in patches)
            {
                string target = entry.target;
                HarmonyMethod postfix = Post(entry.postfixName);
                HarmonyMethod prefix  = null; // no prefix suppression — mod menu is keyboard-only
                try
                {
                    MethodBase method = AccessTools.Method(target);
                    if (method == null)
                    {
                        Plugin.Log.LogWarning("[MenuReader] Method not found: " + target);
                        skipped++;
                        continue;
                    }
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    Plugin.Log.LogInfo("[MenuReader] Patched: " + target);
                    ok++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[MenuReader] Skipping " + target + ": " + ex.Message);
                    skipped++;
                }
            }

            Plugin.Log.LogInfo("[MenuReader] " + ok + " patches applied, " + skipped + " skipped.");
        }

        private static HarmonyMethod Post(string name) =>
            new HarmonyMethod(typeof(MenuReader).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

        // ---- Patch implementations (must be static, accessible by HarmonyMethod) ----

        public static void OnButtonSelect(object __instance)
        {
            // DIAGNOSTIC: always log, even if speech doesn't fire
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string textFound = "";

            if (__instance is MonoBehaviour mb)
            {
                goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
                textFound = GetText(mb.gameObject);

                // One-time UIButton structure dump
                if (!_uiButtonDumped)
                {
                    _uiButtonDumped = true;
                    DumpUIButtonStructure(__instance);
                }

                Announce(textFound, "button");
            }

            Plugin.Log.LogInfo("[PATCH] OnButtonSelect fired | type=" + typeName
                + " | go=" + goName + " | text=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        public static void OnButtonActivate(object __instance)
        {
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string textFound = "";

            if (__instance is MonoBehaviour mb)
            {
                goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
                textFound = GetText(mb.gameObject);
                Announce(textFound, "pressed");
            }

            Plugin.Log.LogInfo("[PATCH] OnButtonActivate fired | type=" + typeName
                + " | go=" + goName + " | text=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        public static void OnScreenSelectButton(object[] __args)
        {
            string argInfo = (__args != null) ? ("args.Length=" + __args.Length) : "args=null";
            string goName = "";
            string textFound = "";

            if (__args != null && __args.Length > 0 && __args[0] is MonoBehaviour btn)
            {
                goName = (btn.gameObject != null) ? btn.gameObject.name : "<no gameobject>";
                textFound = GetText(btn.gameObject);
                Announce(textFound, "button");
            }

            Plugin.Log.LogInfo("[PATCH] OnScreenSelectButton fired | " + argInfo
                + " | go=" + goName + " | text=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        public static void OnControl(object __instance)
        {
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string textFound = "";

            if (!(__instance is MonoBehaviour))
            {
                Plugin.Log.LogInfo("[PATCH] OnControl fired | type=" + typeName + " | NOT a MonoBehaviour");
                return;
            }

            MonoBehaviour mb = (MonoBehaviour)__instance;
            goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
            string label = GetParentText(mb.gameObject);
            string value = GetText(mb.gameObject);
            textFound = string.IsNullOrEmpty(label) ? value : label + ": " + value;
            if (!string.IsNullOrWhiteSpace(textFound)) Speak(textFound);

            Plugin.Log.LogInfo("[PATCH] OnControl fired | type=" + typeName
                + " | go=" + goName + " | label=" + label + " | value=" + value);
        }

        public static void OnNewScreen(object __instance)
        {
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string screenName = "";

            if (!(__instance is MonoBehaviour))
            {
                Plugin.Log.LogInfo("[PATCH] OnNewScreen fired | type=" + typeName + " | NOT a MonoBehaviour");
                return;
            }

            MonoBehaviour mb = (MonoBehaviour)__instance;
            goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
            screenName = CleanName(mb.gameObject.name);
            if (!string.IsNullOrEmpty(screenName))
            {
                _lastAnnounced = ""; // reset dedup so first button on new screen is always spoken
                SpeechManager.Speak(screenName + " screen");
            }

            Plugin.Log.LogInfo("[PATCH] OnNewScreen fired | type=" + typeName
                + " | go=" + goName + " | screenName=" + screenName);
        }

        public static void OnSettingsHover(object[] __args)
        {
            string argInfo = (__args != null) ? ("args.Length=" + __args.Length) : "args=null";
            string goName = "";
            string textFound = "";

            if (__args != null && __args.Length > 0 && __args[0] is MonoBehaviour obj)
            {
                goName = (obj.gameObject != null) ? obj.gameObject.name : "<no gameobject>";
                textFound = GetText(obj.gameObject);
                Announce(textFound, "");
            }

            Plugin.Log.LogInfo("[PATCH] OnSettingsHover fired | " + argInfo
                + " | go=" + goName + " | text=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        public static void OnHighlight(object __instance)
        {
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string textFound = "";

            if (__instance is MonoBehaviour mb)
            {
                goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
                textFound = GetText(mb.gameObject);
                if (!string.IsNullOrEmpty(textFound)) SpeechManager.Speak(textFound);
            }

            Plugin.Log.LogInfo("[PATCH] OnHighlight fired | type=" + typeName
                + " | go=" + goName + " | text=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        // ---- Per-frame poll driven by FootballMenu.Update / FootballMainMenu.Update ----
        public static void OnGameUpdate()
        {
            // First-ever call: write proof to file and speak
            if (_pollFirstRun)
            {
                _pollFirstRun = false;
                try { File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss}] GAME UPDATE PATCH FIRING\n"); } catch { }
                Plugin.Log.LogInfo("[MenuReader] OnGameUpdate first call — patch confirmed firing.");
                SpeechManager.Speak("Football Simulator accessibility mod loaded.");
            }

            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0f;

            // Suppress menu scanning entirely during active gameplay —
            // Football Match UI being active means a game is in progress.
            var matchUI = GameObject.Find("Football Match UI");
            if (matchUI != null && matchUI.activeInHierarchy) return;

            // Init reflection once
            if (!_reflectionDone) InitPollReflection();

            // Strategy 1: EventSystem (works for any Unity UI)
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null)
                {
                    string t = GetText(es.currentSelectedGameObject);
                    if (string.IsNullOrWhiteSpace(t)) t = es.currentSelectedGameObject.name;
                    if (!string.IsNullOrWhiteSpace(t) && t != _lastPolled)
                    {
                        _lastPolled = t;
                        try { File.AppendAllText(@"C:\football\speech_log.txt",
                            $"[{DateTime.Now:HH:mm:ss}] ES: {t}\n"); } catch { }
                        SpeechManager.Speak(t + ", button");
                        return;
                    }
                }
            }
            catch { }

            // Strategy 2: UIButton / UIButtonLinker scan
            if (_uiButtonType != null)
            {
                var buttons = UnityEngine.Object.FindObjectsOfType(_uiButtonType);
                foreach (var raw in buttons)
                {
                    var mb = raw as MonoBehaviour;
                    if (mb == null || !mb.gameObject.activeInHierarchy) continue;
                    if (!IsButtonSelected(mb)) continue;

                    string t = GetText(mb.gameObject);
                    if (string.IsNullOrWhiteSpace(t) || t == _lastPolled) continue;

                    _lastPolled = t;
                    try { File.AppendAllText(@"C:\football\speech_log.txt",
                        $"[{DateTime.Now:HH:mm:ss}] BTN: {t}\n"); } catch { }
                    SpeechManager.Speak(t + ", button");
                    return;
                }
            }
        }

        private static void InitPollReflection()
        {
            _reflectionDone = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "UIButton" || t.Name == "UIButtonLinker")
                    {
                        if (_uiButtonType == null) _uiButtonType = t;
                    }
                }
                break;
            }

            if (_uiButtonType != null)
            {
                string[] boolFields = { "selected", "isSelected", "_selected",
                                        "highlighted", "isHighlighted", "_highlighted" };
                foreach (string name in boolFields)
                {
                    var f = _uiButtonType.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f?.FieldType == typeof(bool)) { _btnSelectedField = f; break; }
                }

                Plugin.Log.LogInfo("[MenuReader] UIButton fields:");
                foreach (var f in _uiButtonType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    Plugin.Log.LogInfo($"  {f.FieldType.Name} {f.Name}");
            }
        }

        private static bool IsButtonSelected(MonoBehaviour mb)
        {
            if (_btnSelectedField != null)
                try { if (_btnSelectedField.GetValue(mb) is bool b && b) return true; } catch { }
            Vector3 s = mb.transform.localScale;
            if (s.x > 1.05f || s.y > 1.05f) return true;
            var img = mb.GetComponent<UnityEngine.UI.Image>();
            if (img != null && img.color.r > 0.9f && img.color.g > 0.9f
                && img.color.b > 0.9f && img.color.a > 0.5f) return true;
            return false;
        }

        // Fires after UIScreen.ProcessInputs() — scan all child UIButtons for selected state
        public static void OnScreenProcessInputs(object __instance)
        {
            if (!(__instance is MonoBehaviour screen)) return;
            if (screen.gameObject == null) return;

            // Find any UIButton child that looks selected (scale > 1 or bright color)
            var children = screen.gameObject.GetComponentsInChildren<MonoBehaviour>(false);
            foreach (var mb in children)
            {
                if (mb == null || mb.GetType().Name != "UIButton") continue;

                bool selected = false;
                // Check scale
                Vector3 sc = mb.transform.localScale;
                if (sc.x > 1.05f || sc.y > 1.05f) selected = true;
                // Check Image color
                if (!selected)
                {
                    var img = mb.GetComponent<UnityEngine.UI.Image>();
                    if (img != null && img.color.r > 0.9f && img.color.g > 0.9f
                        && img.color.b > 0.9f && img.color.a > 0.5f) selected = true;
                }

                if (!selected) continue;

                string text = GetText(mb.gameObject);
                Announce(text, "button");
                return;
            }
        }

        public static void OnScreenNav(object __instance)
        {
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string textFound = "";

            if (!(__instance is MonoBehaviour))
            {
                Plugin.Log.LogInfo("[PATCH] OnScreenNav fired | type=" + typeName + " | NOT a MonoBehaviour");
                return;
            }

            MonoBehaviour mb = (MonoBehaviour)__instance;
            goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
            textFound = GetFieldText(mb,
                "selectedButton", "currentButton", "activeButton",
                "_selectedButton", "_currentButton", "highlightedButton");
            if (!string.IsNullOrEmpty(textFound)) Speak(textFound);

            Plugin.Log.LogInfo("[PATCH] OnScreenNav fired | type=" + typeName
                + " | go=" + goName + " | fieldText=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        public static void OnKeyboardSelect(object __instance)
        {
            string typeName = (__instance != null) ? __instance.GetType().Name : "<null>";
            string goName = "";
            string textFound = "";

            if (!(__instance is MonoBehaviour))
            {
                Plugin.Log.LogInfo("[PATCH] OnKeyboardSelect fired | type=" + typeName + " | NOT a MonoBehaviour");
                return;
            }

            MonoBehaviour mb = (MonoBehaviour)__instance;
            goName = (mb.gameObject != null) ? mb.gameObject.name : "<no gameobject>";
            textFound = GetFieldText(mb, "selectedButton", "currentButton", "_selectedButton");
            if (!string.IsNullOrEmpty(textFound)) Speak(textFound);

            Plugin.Log.LogInfo("[PATCH] OnKeyboardSelect fired | type=" + typeName
                + " | go=" + goName + " | fieldText=" + (string.IsNullOrEmpty(textFound) ? "<empty>" : textFound));
        }

        // ---- UIButton structure dumper ----

        public static void DumpUIButtonStructure(object instance)
        {
            try
            {
                if (instance == null)
                {
                    Plugin.Log.LogInfo("[UIButtonDump] instance is null");
                    return;
                }

                Type t = instance.GetType();
                Plugin.Log.LogInfo("[UIButtonDump] === UIButton Structure Dump ===");

                // Type hierarchy
                var hierarchy = new List<string>();
                Type cur = t;
                while (cur != null)
                {
                    hierarchy.Add(cur.FullName);
                    cur = cur.BaseType;
                }
                Plugin.Log.LogInfo("[UIButtonDump] Type hierarchy: " + string.Join(" -> ", hierarchy.ToArray()));

                // All instance fields
                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Plugin.Log.LogInfo("[UIButtonDump] Fields (" + fields.Length + "):");
                foreach (var f in fields)
                {
                    string val = "<err>";
                    try
                    {
                        object fval = f.GetValue(instance);
                        val = (fval == null) ? "<null>" : fval.ToString();
                        if (val.Length > 80) val = val.Substring(0, 80) + "...";
                    }
                    catch { }
                    Plugin.Log.LogInfo("[UIButtonDump]   " + f.FieldType.Name + " " + f.Name + " = " + val);
                }

                // Child GameObjects and their text
                if (instance is MonoBehaviour mb && mb.gameObject != null)
                {
                    var children = mb.gameObject.GetComponentsInChildren<Transform>(true);
                    Plugin.Log.LogInfo("[UIButtonDump] Child GameObjects (" + children.Length + "):");
                    foreach (var child in children)
                    {
                        var tmp = child.GetComponent<TextMeshProUGUI>();
                        string childText = (tmp != null) ? (" text='" + (tmp.text ?? "") + "'") : "";
                        Plugin.Log.LogInfo("[UIButtonDump]   GO: " + child.gameObject.name + childText);
                    }
                }

                Plugin.Log.LogInfo("[UIButtonDump] === End UIButton Dump ===");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[UIButtonDump] Exception: " + ex.Message);
            }
        }

        // ---- Helpers ----

        private static void Announce(string text, string suffix)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string msg = string.IsNullOrEmpty(suffix) ? text : text + ", " + suffix;
            Speak(msg);
        }

        private static void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text == _lastAnnounced) return;
            _lastAnnounced = text;
            SpeechManager.Speak(text);
        }

        // Text content or GameObject names we never want to read aloud
        private static readonly HashSet<string> _noiseText = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Hair Color", "Default Text", "Option A", "Option B", "Option C", "Option D",
            "Control Tooltip Text", "SIM",
        };

        public static string GetText(GameObject obj)
        {
            if (obj == null) return "";
            var comps = obj.GetComponentsInChildren<TextMeshProUGUI>(true);
            var parts = new List<string>();
            foreach (var c in comps)
            {
                // Skip decorative/noise components by object name or text content
                if (_noiseText.Contains(c.gameObject.name)) continue;
                string t = (c.text != null) ? c.text.Trim() : "";
                if (string.IsNullOrEmpty(t)) continue;
                if (_noiseText.Contains(t)) continue;
                // Strip TMP rich-text tags before adding
                t = StripRichText(t);
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
            }
            return string.Join(" ", parts.ToArray());
        }

        private static string StripRichText(string text)
        {
            if (!text.Contains("<")) return text;
            var sb = new StringBuilder();
            bool inTag = false;
            foreach (char ch in text)
            {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        private static string GetParentText(GameObject obj)
        {
            if (obj == null || obj.transform == null || obj.transform.parent == null) return "";
            return GetText(obj.transform.parent.gameObject);
        }

        private static string GetFieldText(MonoBehaviour instance, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    var f = instance.GetType().GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.GetValue(instance) is MonoBehaviour btn)
                        return GetText(btn.gameObject);
                }
                catch { }
            }
            return "";
        }

        private static string CleanName(string raw)
        {
            if (raw == null) return "";
            return raw.Replace("Screen", "").Replace("Panel", "")
                      .Replace("(Clone)", "").Replace("UI", "").Trim();
        }
    }

    /// <summary>
    /// Polls all active UIScreen instances every 100ms and speaks when the selected button changes.
    /// Also writes a diagnostic file every 3 seconds with full state information.
    /// </summary>
    public class MenuPoller : MonoBehaviour
    {
        private static readonly string[] _fieldNames = {
            "selectedButton", "currentButton", "activeButton",
            "_selectedButton", "_currentButton", "highlightedButton"
        };

        private float _pollTimer = 0f;
        private float _diagTimer = 0f;
        private float _logTimer = 0f;         // throttle "poller is alive" messages
        private const float POLL_INTERVAL = 0.1f;
        private const float DIAG_INTERVAL = 3f;
        private const float LOG_INTERVAL  = 5f;

        private string _lastSeen = "";
        private Type _uiScreenType;
        private FieldInfo _selectedField;

        private static readonly string DiagFilePath = @"C:\football\accessibility_state.txt";

        private void Start()
        {
            Plugin.Log.LogInfo("[MenuPoller] Start() called — searching for UIScreen type");

            // Find UIScreen type — search by short name in case it's in a namespace
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "UIScreen") { _uiScreenType = t; break; }
                }
                break;
            }

            if (_uiScreenType == null)
            {
                Plugin.Log.LogWarning("[MenuPoller] UIScreen type NOT FOUND in Assembly-CSharp");
                return;
            }

            Plugin.Log.LogInfo("[MenuPoller] Found UIScreen type: " + _uiScreenType.FullName);

            // Try to find the selected-button field by name
            foreach (string name in _fieldNames)
            {
                _selectedField = _uiScreenType.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_selectedField != null)
                {
                    Plugin.Log.LogInfo("[MenuPoller] Watching UIScreen field: " + name
                        + " (type=" + _selectedField.FieldType.Name + ")");
                    break;
                }
            }

            if (_selectedField == null)
            {
                Plugin.Log.LogWarning("[MenuPoller] Could not find selected-button field by name. Dumping ALL UIScreen fields:");
            }
            else
            {
                Plugin.Log.LogInfo("[MenuPoller] Will also dump all UIScreen fields for reference:");
            }

            // Always dump all fields so we can see what's available
            var allFields = _uiScreenType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Plugin.Log.LogInfo("[MenuPoller] UIScreen has " + allFields.Length + " instance fields:");
            foreach (var f in allFields)
                Plugin.Log.LogInfo("[MenuPoller]   field: " + f.FieldType.Name + " " + f.Name);

            // Also dump properties
            var allProps = _uiScreenType.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Plugin.Log.LogInfo("[MenuPoller] UIScreen has " + allProps.Length + " instance properties:");
            foreach (var p in allProps)
                Plugin.Log.LogInfo("[MenuPoller]   prop: " + p.PropertyType.Name + " " + p.Name);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _pollTimer += dt;
            _diagTimer += dt;
            _logTimer  += dt;

            // Periodic "I'm alive" log (every 5 seconds)
            if (_logTimer >= LOG_INTERVAL)
            {
                _logTimer = 0f;
                string fieldName = (_selectedField != null) ? _selectedField.Name : "<none found>";
                Plugin.Log.LogInfo("[MenuPoller] Alive | uiScreenType="
                    + (_uiScreenType != null ? _uiScreenType.Name : "null")
                    + " | watchingField=" + fieldName
                    + " | lastSeen=" + (string.IsNullOrEmpty(_lastSeen) ? "<nothing>" : _lastSeen));
            }

            // Poll for button changes
            if (_pollTimer >= POLL_INTERVAL)
            {
                _pollTimer = 0f;
                PollScreens();
            }

            // Write diagnostic file
            if (_diagTimer >= DIAG_INTERVAL)
            {
                _diagTimer = 0f;
                WriteDiagFile();
            }
        }

        private void PollScreens()
        {
            if (_uiScreenType == null) return;

            var screens = FindObjectsOfType(_uiScreenType);

            if (_selectedField == null)
            {
                // No field found yet — log what we find but try all fields each time
                if (screens.Length > 0)
                {
                    // Try to find a field that holds a MonoBehaviour on the first screen
                    var allFields = _uiScreenType.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var f in allFields)
                    {
                        try
                        {
                            object val = f.GetValue(screens[0]);
                            if (val is MonoBehaviour)
                            {
                                // Found a candidate — use it
                                _selectedField = f;
                                Plugin.Log.LogInfo("[MenuPoller] Auto-detected selected field: "
                                    + f.FieldType.Name + " " + f.Name);
                                break;
                            }
                        }
                        catch { }
                    }
                }
                return;
            }

            foreach (var obj in screens)
            {
                try
                {
                    var val = _selectedField.GetValue(obj);
                    if (!(val is MonoBehaviour)) continue;

                    MonoBehaviour btn = (MonoBehaviour)val;
                    if (!btn.gameObject.activeInHierarchy) continue;

                    string text = MenuReader.GetText(btn.gameObject);
                    if (string.IsNullOrEmpty(text) || text == _lastSeen) continue;

                    Plugin.Log.LogInfo("[MenuPoller] Selection changed: '" + _lastSeen + "' -> '" + text + "'");
                    _lastSeen = text;
                    SpeechManager.Speak(text + ", button");
                }
                catch { }
            }
        }

        private void WriteDiagFile()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== Football Accessibility State ===");
                sb.AppendLine("Time: " + DateTime.Now.ToString("HH:mm:ss"));
                sb.AppendLine("Last spoken: " + (_lastSeen.Length > 0 ? _lastSeen : "<nothing>"));
                sb.AppendLine();

                if (_uiScreenType == null)
                {
                    sb.AppendLine("UIScreen type: NOT FOUND");
                }
                else
                {
                    sb.AppendLine("UIScreen type: " + _uiScreenType.FullName);
                    sb.AppendLine("Watching field: " + (_selectedField != null ? _selectedField.Name : "<none found>"));
                    sb.AppendLine();

                    var screens = FindObjectsOfType(_uiScreenType);
                    sb.AppendLine("Active UIScreen instances: " + screens.Length);

                    foreach (var obj in screens)
                    {
                        try
                        {
                            MonoBehaviour scr = obj as MonoBehaviour;
                            string scrName = (scr != null && scr.gameObject != null)
                                ? scr.gameObject.name : obj.ToString();
                            sb.AppendLine("  Screen: " + scrName);

                            if (_selectedField != null)
                            {
                                object val = _selectedField.GetValue(obj);
                                if (val == null)
                                {
                                    sb.AppendLine("    selectedButton: <null>");
                                }
                                else if (val is MonoBehaviour btn)
                                {
                                    string btnName = (btn.gameObject != null) ? btn.gameObject.name : "?";
                                    string btnText = MenuReader.GetText(btn.gameObject);
                                    sb.AppendLine("    selectedButton GO: " + btnName);
                                    sb.AppendLine("    selectedButton text: " + (btnText.Length > 0 ? btnText : "<empty>"));
                                    sb.AppendLine("    selectedButton active: " + btn.gameObject.activeInHierarchy);
                                }
                                else
                                {
                                    sb.AppendLine("    selectedButton value (not MB): " + val.GetType().Name + " = " + val);
                                }
                            }

                            // Scan all visible text on this screen's GameObject
                            if (scr != null && scr.gameObject != null)
                            {
                                var texts = scr.gameObject.GetComponentsInChildren<TextMeshProUGUI>(false);
                                sb.AppendLine("    Visible texts (" + texts.Length + "):");
                                foreach (var t in texts)
                                {
                                    string tv = (t.text != null) ? t.text.Trim() : "";
                                    if (!string.IsNullOrEmpty(tv))
                                        sb.AppendLine("      [" + t.gameObject.name + "] " + tv);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("  (error reading screen: " + ex.Message + ")");
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine("=== End State ===");

                File.WriteAllText(DiagFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[MenuPoller] Could not write diag file: " + ex.Message);
            }
        }
    }
}
