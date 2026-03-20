using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Scans the live Unity scene every 150ms for all active TextMeshProUGUI text.
    /// Detects new/changed text that looks like a menu item and speaks it via NVDA.
    /// Also tracks UIButton selection state via reflection and speaks the selected button.
    /// Writes a human-readable state snapshot to C:\football\accessibility_state.txt every 2s.
    /// </summary>
    public class SceneScanner : MonoBehaviour
    {
        // ---- Timing ----
        private const float SCAN_INTERVAL      = 0.15f;  // 150ms — scene scan
        private const float DUMP_INTERVAL      = 2.0f;   // 2s  — file dump
        private const float SPEAK_COOLDOWN     = 0.20f;  // 200ms — minimum gap between NVDA calls
        private const float BUTTON_POLL_INTERVAL = 0.15f;

        private float _scanTimer   = 0f;
        private float _dumpTimer   = 0f;
        private float _speakTimer  = 0f; // time elapsed since last Speak call

        // ---- State tracking ----
        // Key: InstanceID of the TMP component, Value: last text seen
        private readonly Dictionary<int, string> _prevTexts = new Dictionary<int, string>();

        // Last item spoken (for deduplication)
        private string _lastSpoken = "";

        // ---- UIButton reflection ----
        // We look these up once and cache them
        private Type _uiButtonType;
        private FieldInfo _selectedField;   // bool "selected" / "isSelected" etc.
        private FieldInfo _colorField;      // Color field for visual state
        private bool _buttonReflectionReady = false;

        // Track previously selected button by instance ID
        private int _lastSelectedButtonId = -1;

        // ---- File output ----
        private const string STATE_FILE = @"C:\football\accessibility_state.txt";

        // ---- Candidate text filter ----
        // Max length for text we consider "menu-like"
        private const int MAX_MENU_TEXT_LENGTH = 120;

        // ---- Unity lifecycle ----

        private void Start()
        {
            Plugin.Log.LogInfo("[SceneScanner] Starting — will scan every 150ms.");
            InitButtonReflection();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _scanTimer  += dt;
            _dumpTimer  += dt;
            _speakTimer += dt;

            if (_scanTimer >= SCAN_INTERVAL)
            {
                _scanTimer = 0f;
                ScanScene();
            }

            if (_dumpTimer >= DUMP_INTERVAL)
            {
                _dumpTimer = 0f;
                DumpStateToFile();
            }
        }

        // ---- Reflection bootstrap ----

        private void InitButtonReflection()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;

                // Search by short name in case the type is in a namespace
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "UIButton") { _uiButtonType = t; break; }
                }
                if (_uiButtonType == null)
                {
                    Plugin.Log.LogWarning("[SceneScanner] UIButton type not found in Assembly-CSharp.");
                    return;
                }

                // Search for a boolean "selected" field
                string[] boolCandidates = new string[] {
                    "selected", "isSelected", "_selected", "Selected",
                    "IsSelected", "highlighted", "isHighlighted", "_highlighted"
                };
                foreach (string name in boolCandidates)
                {
                    FieldInfo f = _uiButtonType.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        _selectedField = f;
                        Plugin.Log.LogInfo("[SceneScanner] UIButton bool field found: " + name);
                        break;
                    }
                }

                // Log all fields if we couldn't find a bool — helps diagnosis
                if (_selectedField == null)
                {
                    Plugin.Log.LogWarning("[SceneScanner] No bool selected-field found on UIButton. All fields:");
                    foreach (FieldInfo f in _uiButtonType.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        Plugin.Log.LogInfo("[SceneScanner]   " + f.FieldType.Name + " " + f.Name);
                    }
                }

                _buttonReflectionReady = true;
                Plugin.Log.LogInfo("[SceneScanner] UIButton reflection ready.");
                break;
            }
        }

        // ---- Scene scan ----

        private void ScanScene()
        {
            // Step 1: Find all active TMP components
            UnityEngine.Object[] rawComps =
                UnityEngine.Object.FindObjectsOfType(typeof(TextMeshProUGUI));

            // Build a set of IDs seen this frame
            var currentIds = new HashSet<int>();
            // Collect (instanceId, text, gameObject) for active components
            var activeTexts = new List<TmpEntry>();

            foreach (UnityEngine.Object raw in rawComps)
            {
                TextMeshProUGUI tmp = raw as TextMeshProUGUI;
                if (tmp == null) continue;
                if (!tmp.gameObject.activeInHierarchy) continue;

                string text = (tmp.text ?? "").Trim();
                int id = tmp.GetInstanceID();
                currentIds.Add(id);
                activeTexts.Add(new TmpEntry(id, text, tmp.gameObject));
            }

            // Step 2: Detect new or changed text
            foreach (TmpEntry entry in activeTexts)
            {
                string prev;
                bool existed = _prevTexts.TryGetValue(entry.Id, out prev);

                bool isNew     = !existed;
                bool isChanged = existed && prev != entry.Text;

                if ((isNew || isChanged) && IsMenuLikeText(entry.Text))
                {
                    // Only speak if meaningful text appeared
                    if (!string.IsNullOrWhiteSpace(entry.Text))
                    {
                        TrySpeak(entry.Text);
                    }
                }

                _prevTexts[entry.Id] = entry.Text;
            }

            // Step 3: Remove stale IDs
            var toRemove = new List<int>();
            foreach (int id in _prevTexts.Keys)
            {
                if (!currentIds.Contains(id))
                    toRemove.Add(id);
            }
            foreach (int id in toRemove)
                _prevTexts.Remove(id);

            // Step 4: Check UIButton selection state
            if (_buttonReflectionReady && _uiButtonType != null)
                CheckButtonSelection(activeTexts);
        }

        // ---- UIButton selection tracking ----

        private void CheckButtonSelection(List<TmpEntry> activeTexts)
        {
            // Build a lookup: gameObject -> text, for objects that have a UIButton
            UnityEngine.Object[] buttons =
                UnityEngine.Object.FindObjectsOfType(_uiButtonType);

            foreach (UnityEngine.Object raw in buttons)
            {
                MonoBehaviour mb = raw as MonoBehaviour;
                if (mb == null) continue;
                if (!mb.gameObject.activeInHierarchy) continue;

                bool isSelected = IsButtonSelected(mb);

                if (isSelected)
                {
                    int id = mb.GetInstanceID();
                    if (id != _lastSelectedButtonId)
                    {
                        _lastSelectedButtonId = id;
                        string text = GetTextFromGameObject(mb.gameObject);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            TrySpeak(text + ", button");
                        }
                    }
                    // Once we've found the selected one, no need to check more
                    return;
                }
            }
        }

        private bool IsButtonSelected(MonoBehaviour mb)
        {
            // Strategy 1: use the cached bool field
            if (_selectedField != null)
            {
                try
                {
                    object val = _selectedField.GetValue(mb);
                    if (val is bool b) return b;
                }
                catch { }
            }

            // Strategy 2: check scale — selected buttons are often scaled up
            Vector3 scale = mb.transform.localScale;
            if (scale.x > 1.05f || scale.y > 1.05f) return true;

            // Strategy 3: check color of first Image/SpriteRenderer child
            //   Selected items often brighten or become white
            UnityEngine.UI.Image img = mb.GetComponent<UnityEngine.UI.Image>();
            if (img != null)
            {
                Color c = img.color;
                // A fully white or near-white color often indicates selection highlight
                if (c.r > 0.9f && c.g > 0.9f && c.b > 0.9f && c.a > 0.5f) return true;
            }

            return false;
        }

        // ---- Text helpers ----

        private static string GetTextFromGameObject(GameObject obj)
        {
            TextMeshProUGUI[] comps = obj.GetComponentsInChildren<TextMeshProUGUI>(true);
            var parts = new List<string>();
            foreach (TextMeshProUGUI c in comps)
            {
                string t = (c.text ?? "").Trim();
                if (!string.IsNullOrEmpty(t)) parts.Add(t);
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// Returns true if the text looks like a UI label (not a body of prose or empty).
        /// Filters out very long strings, pure numbers that look like coordinates, etc.
        /// </summary>
        private static bool IsMenuLikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length > MAX_MENU_TEXT_LENGTH) return false;
            // Skip pure whitespace-only changes
            if (text.Trim().Length == 0) return false;
            return true;
        }

        // ---- Speech throttle / dedup ----

        private void TrySpeak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Dedup: skip if same as last announced
            if (text == _lastSpoken) return;
            // Throttle: enforce 200ms minimum gap
            if (_speakTimer < SPEAK_COOLDOWN) return;

            _lastSpoken = text;
            _speakTimer = 0f;
            SpeechManager.Speak(text);
        }

        // ---- File state dump ----

        private void DumpStateToFile()
        {
            try
            {
                // Collect all active TMP objects grouped by root UI hierarchy
                UnityEngine.Object[] rawComps =
                    UnityEngine.Object.FindObjectsOfType(typeof(TextMeshProUGUI));

                // Group by root transform name
                var groups = new Dictionary<string, List<HierarchyEntry>>();

                foreach (UnityEngine.Object raw in rawComps)
                {
                    TextMeshProUGUI tmp = raw as TextMeshProUGUI;
                    if (tmp == null) continue;
                    if (!tmp.gameObject.activeInHierarchy) continue;

                    string text = (tmp.text ?? "").Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // Determine hierarchy path (up to 4 levels deep from root)
                    string rootName = GetRootName(tmp.transform);
                    string path     = GetHierarchyPath(tmp.transform);
                    bool hasButton  = HasUIButtonInHierarchy(tmp.gameObject);
                    bool selected   = hasButton && IsAncestorButtonSelected(tmp.gameObject);

                    if (!groups.ContainsKey(rootName))
                        groups[rootName] = new List<HierarchyEntry>();

                    groups[rootName].Add(new HierarchyEntry(path, text, hasButton, selected));
                }

                // Determine current "screen name" — the active root canvas or largest group
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== Football Accessibility State ===");
                sb.AppendLine("Time   : " + DateTime.Now.ToString("HH:mm:ss"));
                sb.AppendLine("Scene  : " + sceneName);
                sb.AppendLine("Last spoken: " + (_lastSpoken ?? "(none)"));
                sb.AppendLine();

                foreach (string rootName in groups.Keys)
                {
                    List<HierarchyEntry> entries = groups[rootName];
                    sb.AppendLine("--- " + rootName + " ---");
                    foreach (HierarchyEntry e in entries)
                    {
                        string marker = e.IsSelected ? " [SELECTED]" : (e.HasButton ? " [button]" : "");
                        sb.AppendLine("  " + e.Path + ": " + e.Text + marker);
                    }
                    sb.AppendLine();
                }

                File.WriteAllText(STATE_FILE, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SceneScanner] Failed to write state file: " + ex.Message);
            }
        }

        private static string GetRootName(Transform t)
        {
            Transform current = t;
            while (current.parent != null)
                current = current.parent;
            return current.gameObject.name;
        }

        private static string GetHierarchyPath(Transform t)
        {
            // Build path from root down, max 4 segments to keep it readable
            var parts = new List<string>();
            Transform current = t;
            while (current != null && parts.Count < 4)
            {
                parts.Insert(0, current.gameObject.name);
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private bool HasUIButtonInHierarchy(GameObject obj)
        {
            if (_uiButtonType == null) return false;
            // Check self and parents
            Transform t = obj.transform;
            while (t != null)
            {
                Component c = t.gameObject.GetComponent(_uiButtonType);
                if (c != null) return true;
                t = t.parent;
            }
            return false;
        }

        private bool IsAncestorButtonSelected(GameObject obj)
        {
            if (_uiButtonType == null) return false;
            Transform t = obj.transform;
            while (t != null)
            {
                Component c = t.gameObject.GetComponent(_uiButtonType);
                if (c != null)
                {
                    MonoBehaviour mb = c as MonoBehaviour;
                    if (mb != null && IsButtonSelected(mb)) return true;
                }
                t = t.parent;
            }
            return false;
        }

        // ---- Inner types (net46-compatible plain structs/classes) ----

        private struct TmpEntry
        {
            public readonly int        Id;
            public readonly string     Text;
            public readonly GameObject GameObject;

            public TmpEntry(int id, string text, GameObject go)
            {
                Id         = id;
                Text       = text;
                GameObject = go;
            }
        }

        private struct HierarchyEntry
        {
            public readonly string Path;
            public readonly string Text;
            public readonly bool   HasButton;
            public readonly bool   IsSelected;

            public HierarchyEntry(string path, string text, bool hasButton, bool isSelected)
            {
                Path       = path;
                Text       = text;
                HasButton  = hasButton;
                IsSelected = isSelected;
            }
        }
    }
}
