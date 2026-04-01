using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using FootballAccessMod.Accessibility;
using FootballAccessMod.Speech;
using FootballAccessMod.Utils;
using FootballAccessMod.Utils;

namespace FootballAccessMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        private Harmony _harmony = null!;
        internal static Harmony HarmonyInstance = null!;

        // ---- Selection polling (runs in Plugin.Update so we know it fires) ----
        private Type?     _uiButtonType;
        private Type?     _uiButtonLinkerType;
        private FieldInfo? _selectedField;   // bool field on UIButton
        private bool      _buttonReflectionDone;
        private string    _lastSpokenButton = "";
        private float     _pollTimer;
        private const float POLL_INTERVAL = 0.12f; // every 120ms

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loading...");
            ModSettings.Init(Config);

            try
            {
                SpeechManager.Initialize(Logger);

                // Dump class names BEFORE patching
                DumpMenuClasses();

                FranchiseReader.Initialize();

                _harmony      = new Harmony(PluginInfo.PLUGIN_GUID);
                HarmonyInstance = _harmony;
                MenuReader.ApplyPatches(_harmony);
                PlaybookReader.ApplyPatches(_harmony);

                // Attach runtime components (diagnostics / gameplay)
                gameObject.AddComponent<GameDiscovery>();
                gameObject.AddComponent<MenuPoller>();
                gameObject.AddComponent<SceneScanner>();

                Log.LogInfo("Patches applied. Football Simulator is now accessible!");

                // Inject into Unity's PlayerLoop — runs every frame guaranteed,
                // bypasses MonoBehaviour lifecycle entirely.
                InjectPlayerLoop();

                StartCoroutine(DelayedStartupSpeak());
                StartCoroutine(AutoDumpAfterDelay(8f));
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin.Awake] FATAL: {ex}");
            }
        }

        private static bool _loopDiagDone = false;
        private static float _loopPollAccum = 0f;
        private static bool _loopReflectionDone = false;
        private static Type? _loopUiButtonType;
        private static Type? _loopUiButtonLinkerType;
        private static Type? _loopUiButtonStretchType;
        private static FieldInfo? _loopSelectedField;
        private static string _loopLastSpoken = "";
        private static float  _lastButtonSpeakTime = 0f;
        private const  float  BUTTON_SPEAK_MIN_GAP = 0.3f;
        private static bool _esNullLogged = false;
        private static string _lastSceneName = "";
        private static bool _dumpKeyWasDown = false;

        private static void InjectPlayerLoop()
        {
            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                // Find the Update phase and append our subsystem
                for (int i = 0; i < loop.subSystemList.Length; i++)
                {
                    if (loop.subSystemList[i].type == typeof(Update))
                    {
                        var subs = loop.subSystemList[i].subSystemList;
                        var newSubs = new PlayerLoopSystem[subs.Length + 1];
                        Array.Copy(subs, newSubs, subs.Length);
                        newSubs[subs.Length] = new PlayerLoopSystem
                        {
                            type           = typeof(Plugin),
                            updateDelegate = AccessibilityLoop
                        };
                        loop.subSystemList[i].subSystemList = newSubs;
                        break;
                    }
                }
                PlayerLoop.SetPlayerLoop(loop);
                Log.LogInfo("[Plugin] PlayerLoop injection successful.");
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss}] PLAYERLOOP INJECTED\n"); } catch { }
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] PlayerLoop injection failed: {ex.Message}");
            }
        }

        private static void AccessibilityLoop()
        {
            if (!_loopDiagDone)
            {
                _loopDiagDone = true;
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss}] LOOP RUNNING timeScale={Time.timeScale}\n"); } catch { }
                Log.LogInfo("[Plugin] AccessibilityLoop first tick.");
                SpeechManager.Speak("Football Simulator accessibility mod loaded.");
            }

            // F11 hotkey: manual scene dump
            bool f11 = Input.GetKey(KeyCode.F11);
            if (f11 && !_dumpKeyWasDown) { DumpSceneToFile("MANUAL"); SpeechManager.Speak("Dumped."); }
            _dumpKeyWasDown = f11;

            // Per-frame input polling (must run before the throttle gate)
            SettingsMenuReader.PollInput();
            if (SettingsMenuReader.IsOpen) return;  // suppress all other input while settings are open
            HowToPlayReader.PollInput();
            TeamSelectReader.PollInput();
            GameplayReader.PollInput();

            _loopPollAccum += Time.unscaledDeltaTime;
            if (_loopPollAccum < 0.12f) return;
            _loopPollAccum = 0f;

            // Settings menu takes over input — skip all gameplay/screen readers while open
            if (SettingsMenuReader.IsOpen) return;

            if (!_loopReflectionDone) InitLoopReflection();

            // Auto-dump when scene changes
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sceneName != _lastSceneName)
            {
                _lastSceneName = sceneName;
                DumpSceneToFile("SCENE_CHANGE:" + sceneName);
            }

            // EventSystem — works for any Unity UI
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null && !_esNullLogged)
                {
                    _esNullLogged = true;
                    try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                        $"[{DateTime.Now:HH:mm:ss}] EventSystem NULL\n"); } catch { }
                }
                if (es != null && es.currentSelectedGameObject != null)
                {
                    string t = MenuReader.GetText(es.currentSelectedGameObject);
                    if (string.IsNullOrWhiteSpace(t)) t = es.currentSelectedGameObject.name;
                    if (!string.IsNullOrWhiteSpace(t) && t != _loopLastSpoken)
                    {
                        _loopLastSpoken = t;
                        try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                            $"[{DateTime.Now:HH:mm:ss}] ES: {t}\n"); } catch { }
                        SpeechManager.Speak(t + ", button");
                        return;
                    }
                }
            }
            catch { }

            // Screen-specific value-change monitors
            LoadingScreenReader.Poll();
            HowToPlayReader.Poll();
            TeamSelectReader.Poll();
            NewSeasonReader.Poll();
            LoadSeasonReader.Poll();
            SeasonHubReader.Poll();
            RosterReader.Poll();
            GameplayReader.Poll();
            DefenseReader.Poll();
            OffenseProximityReader.Poll();
            OffensiveAssistReader.Poll();
            ReceiverReader.Poll();
            KickingReader.Poll();

            // UIButton / UIButtonLinker / UIButtonStretch fallback
            // Suppress UIButton/Linker when roster popup is open — the player row button
            // stays "selected" behind the popup and causes alternating speech with popup buttons.
            if (_loopUiButtonType != null && !RosterReader.PopupIsActive)
                PollButtonsLoop(_loopUiButtonType);
            if (_loopUiButtonLinkerType != null && !RosterReader.PopupIsActive)
                PollButtonsLoop(_loopUiButtonLinkerType);
            // UIButtonStretch always runs — used by the popup buttons themselves
            if (_loopUiButtonStretchType != null)
                PollButtonsLoop(_loopUiButtonStretchType);
        }

        private static FieldInfo? _loopSelectedByControllerField;

        private static void DumpSceneToFile(string trigger)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== SCENE DUMP [{trigger}] {DateTime.Now:HH:mm:ss} ===");
                sb.AppendLine($"Scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
                sb.AppendLine($"EventSystem: {UnityEngine.EventSystems.EventSystem.current?.name ?? "NULL"}");
                sb.AppendLine();

                // All active TMP text in the scene with hierarchy path
                var tmps = UnityEngine.Object.FindObjectsOfType<TMPro.TextMeshProUGUI>();
                sb.AppendLine($"--- TMP Text ({tmps.Length} components) ---");
                foreach (var tmp in tmps)
                {
                    if (!tmp.gameObject.activeInHierarchy) continue;
                    string text = (tmp.text ?? "").Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    string path = GetHierarchyPath(tmp.transform, 5);
                    sb.AppendLine($"  {path}: \"{text}\"");
                }

                // All active legacy UI.Text in the scene
                var legacyTexts = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Text>();
                sb.AppendLine($"\n--- Legacy UI.Text ({legacyTexts.Length} components) ---");
                foreach (var lt in legacyTexts)
                {
                    if (!lt.gameObject.activeInHierarchy) continue;
                    string text = (lt.text ?? "").Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    string path = GetHierarchyPath(lt.transform, 5);
                    sb.AppendLine($"  {path}: \"{text}\"");
                }

                // All active UIButtons and their selection state
                if (_loopUiButtonType != null)
                {
                    var buttons = UnityEngine.Object.FindObjectsOfType(_loopUiButtonType);
                    sb.AppendLine($"\n--- UIButtons ({buttons.Length} total) ---");
                    foreach (var raw in buttons)
                    {
                        var mb = raw as MonoBehaviour;
                        if (mb == null || !mb.gameObject.activeInHierarchy) continue;
                        bool sel = IsLoopButtonSelected(mb);
                        string label = MenuReader.GetText(mb.gameObject);
                        string path = GetHierarchyPath(mb.transform, 4);
                        sb.AppendLine($"  [{(sel ? "SEL" : "   ")}] {path}: \"{label}\"");
                    }
                }

                // Deep hierarchy of OffenseBox — reveals play list structure including inactive objects
                var offenseBox = GameObject.Find("OffenseBox");
                if (offenseBox != null)
                {
                    sb.AppendLine("\n--- OffenseBox Deep Hierarchy (all depths, incl. inactive) ---");
                    DumpHierarchyDeep(offenseBox.transform, sb, 0, 12);
                }

                // Deep hierarchy of How To Play — reveals button label siblings next to Instruction TMPs
                var howToPlay = GameObject.Find("How To Play");
                if (howToPlay != null && howToPlay.activeInHierarchy)
                {
                    sb.AppendLine("\n--- How To Play Deep Hierarchy (active only) ---");
                    DumpHierarchyDeep(howToPlay.transform, sb, 0, 10);

                    // Dump all DynamicButtonIconContext fields to find button name field
                    sb.AppendLine("\n--- DynamicButtonIconContext Fields ---");
                    Type? dbicType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        if (asm.GetName().Name == "Assembly-CSharp")
                        { dbicType = asm.GetType("DynamicButtonIconContext"); break; }
                    if (dbicType != null)
                    {
                        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        var dbicComps = howToPlay.GetComponentsInChildren(dbicType, false);
                        int shown = 0;
                        foreach (var comp in dbicComps)
                        {
                            if (shown++ > 2) break; // show first 3 as sample
                            var mb = comp as MonoBehaviour;
                            if (mb == null) continue;
                            sb.AppendLine($"  [{mb.gameObject.name} on {mb.transform.parent?.name}]");
                            foreach (var f in dbicType.GetFields(flags))
                            {
                                try { sb.AppendLine($"    {f.FieldType.Name} {f.Name} = {f.GetValue(comp)}"); }
                                catch { }
                            }
                            foreach (var p in dbicType.GetProperties(flags))
                            {
                                try { sb.AppendLine($"    (prop) {p.PropertyType.Name} {p.Name} = {p.GetValue(comp)}"); }
                                catch { }
                            }
                        }
                    }
                    else sb.AppendLine("  DynamicButtonIconContext type not found.");
                }

                // Deep hierarchy of Settings_Screen — reveals settings structure
                var settingsScreen = GameObject.Find("Settings_Screen");
                if (settingsScreen != null && settingsScreen.activeInHierarchy)
                {
                    sb.AppendLine("\n--- Settings_Screen Deep Hierarchy (active only) ---");
                    DumpHierarchyDeep(settingsScreen.transform, sb, 0, 8);
                }

                // Reflection dump of FootballGameplayMenu — find play list fields
                DumpGameplayMenuFields(sb);

                sb.AppendLine("=== END DUMP ===");
                System.IO.File.AppendAllText(@"C:\football\scene_dump.txt", sb.ToString());
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss}] DUMP written ({trigger})\n"); } catch { }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss}] DUMP failed: {ex.Message}\n"); } catch { }
            }
        }

        private static void DumpHierarchyDeep(Transform t, System.Text.StringBuilder sb, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            string indent = new string(' ', depth * 2);
            string active = t.gameObject.activeSelf ? "" : " [OFF]";
            sb.Append($"{indent}{t.gameObject.name}{active}");

            // Show text content of any text-like component on this GO
            foreach (var comp in t.GetComponents<Component>())
            {
                if (comp == null) continue;
                if (comp is TMPro.TextMeshProUGUI tui)
                { if (!string.IsNullOrEmpty(tui.text)) sb.Append($" [TMP:\"{tui.text.Replace("\n"," ")}\"]"); }
                else if (comp is TMPro.TextMeshPro t3d)
                { if (!string.IsNullOrEmpty(t3d.text)) sb.Append($" [TM3D:\"{t3d.text.Replace("\n"," ")}\"]"); }
                else if (comp is UnityEngine.UI.Text ut)
                { if (!string.IsNullOrEmpty(ut.text)) sb.Append($" [Text:\"{ut.text.Replace("\n"," ")}\"]"); }
                else if (comp is UnityEngine.UI.Image img)
                { sb.Append(img.sprite != null ? $" [Image:\"{img.sprite.name}\"]" : " [Image]"); }
                else if (!(comp is Transform))
                { sb.Append($" [{comp.GetType().Name}]"); }
            }
            sb.AppendLine();
            foreach (Transform child in t)
                DumpHierarchyDeep(child, sb, depth + 1, maxDepth);
        }

        private static void DumpGameplayMenuFields(System.Text.StringBuilder sb)
        {
            try
            {
                // Find FootballGameplayMenu type from game assembly
                Type? fgmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Assembly-CSharp") continue;
                    fgmType = asm.GetType("FootballGameplayMenu");
                    break;
                }
                if (fgmType == null) { sb.AppendLine("\n--- FootballGameplayMenu: type not found ---"); return; }

                // Find live instance
                var instances = UnityEngine.Object.FindObjectsOfType(fgmType);
                if (instances == null || instances.Length == 0)
                { sb.AppendLine("\n--- FootballGameplayMenu: no instance in scene ---"); return; }

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Dump FootballGameplayMenu fields — look for footballMatch + dataManager refs
                object? footballMatchInst = null;
                object? dataManagerInst = null;
                sb.AppendLine($"\n--- FootballGameplayMenu Fields ({instances.Length} instance(s)) ---");
                foreach (var inst in instances)
                {
                    sb.AppendLine($"  [Instance on: {(inst as MonoBehaviour)?.gameObject.name ?? inst.ToString()}]");
                    foreach (var field in fgmType.GetFields(flags))
                    {
                        try
                        {
                            object? val = field.GetValue(inst);
                            if (field.Name == "footballMatch") footballMatchInst = val;
                            if (field.Name == "dataManager")   dataManagerInst   = val;
                            sb.AppendLine($"    {field.FieldType.Name} {field.Name} = {DescribeValue(val)}");
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"    {field.FieldType.Name} {field.Name} = [ERR: {ex.Message}]");
                        }
                    }
                }

                // Drill into FootballMatch — look for playbook + oc fields
                if (footballMatchInst != null)
                {
                    sb.AppendLine($"\n--- FootballMatch Fields (type: {footballMatchInst.GetType().FullName}) ---");
                    object? playbookInst = null;
                    object? ocInst = null;
                    object? receiversInst = null;
                    object? audiblesInst = null;
                    foreach (var f in footballMatchInst.GetType().GetFields(flags))
                    {
                        try
                        {
                            object? v = f.GetValue(footballMatchInst);
                            if (f.Name == "playbook")   playbookInst   = v;
                            if (f.Name == "oc")         ocInst         = v;
                            if (f.Name == "receivers")  receiversInst  = v;
                            if (f.Name == "audibles")   audiblesInst   = v;
                            sb.AppendLine($"  {f.FieldType.Name} {f.Name} = {DescribeValue(v)}");
                        }
                        catch (Exception ex) { sb.AppendLine($"  {f.FieldType.Name} {f.Name} = [ERR: {ex.Message}]"); }
                    }

                    // Dump Audibles component fields + drill into each PlaybookPage for play names
                    if (audiblesInst != null)
                    {
                        sb.AppendLine($"\n--- Audibles Fields (type: {audiblesInst.GetType().FullName}) ---");
                        var pageFields = new[] { "offensiveAudibleL", "offensiveCurrentPlay", "offensiveAudibleR",
                                                 "defensiveAudibleL", "defensiveCurrentPlay", "defensiveAudibleR" };
                        foreach (var af in audiblesInst.GetType().GetFields(flags))
                        {
                            try
                            {
                                object? av = af.GetValue(audiblesInst);
                                sb.AppendLine($"  {af.FieldType.Name} {af.Name} = {DescribeValue(av)}");
                                // Drill into PlaybookPage objects to get play name
                                if (av != null && System.Array.IndexOf(pageFields, af.Name) >= 0)
                                {
                                    sb.AppendLine($"    -- {af.Name} PlaybookPage Fields --");
                                    foreach (var pf in av.GetType().GetFields(flags))
                                    {
                                        try { sb.AppendLine($"      {pf.FieldType.Name} {pf.Name} = {DescribeValue(pf.GetValue(av))}"); }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // Drill into Football.Playbook — contains all formations + plays
                    if (playbookInst != null)
                    {
                        sb.AppendLine($"\n--- Playbook Fields (type: {playbookInst.GetType().FullName}) ---");
                        DumpObjectFields(playbookInst, sb, flags, depth: 0, maxDepth: 2);
                    }

                    // Drill into OffensiveCoordinator — tracks current formation/play selection
                    if (ocInst != null)
                    {
                        sb.AppendLine($"\n--- OffensiveCoordinator Fields (type: {ocInst.GetType().FullName}) ---");
                        DumpObjectFields(ocInst, sb, flags, depth: 0, maxDepth: 1);
                    }

                    // Dump QB logic — has ATarget/BTarget/XTarget/YTarget/L1Target button assignments
                    var fldQb = footballMatchInst.GetType().GetField("qb", flags);
                    object? qbInst = fldQb?.GetValue(footballMatchInst);
                    if (qbInst != null)
                    {
                        object? qbLogicInst = null;
                        foreach (var qf in qbInst.GetType().GetFields(flags))
                            if (qf.Name == "logic") try { qbLogicInst = qf.GetValue(qbInst); } catch { }
                        if (qbLogicInst != null)
                        {
                            sb.AppendLine($"\n--- QB Logic Fields ---");
                            foreach (var lf in qbLogicInst.GetType().GetFields(flags))
                            {
                                string n = lf.Name;
                                if (n != "ATarget" && n != "BTarget" && n != "XTarget" &&
                                    n != "YTarget" && n != "L1Target" && n != "ReceiverNumber" &&
                                    n != "AI_ReceiverToThrowTo" && n != "CanThrowBall") continue;
                                try { sb.AppendLine($"  {lf.FieldType.Name} {n} = {DescribeValue(lf.GetValue(qbLogicInst))}"); }
                                catch { }
                            }
                        }
                    }

                    // Drill into each receiver FootballPlayer and their FootballPlayerLogic
                    if (receiversInst is System.Collections.IList receiverList && receiverList.Count > 0)
                    {
                        sb.AppendLine($"\n--- Receiver Fields ({receiverList.Count} receivers) ---");
                        for (int ri = 0; ri < receiverList.Count; ri++)
                        {
                            object? recv = receiverList[ri];
                            if (recv == null) continue;
                            sb.AppendLine($"  [Receiver {ri}: {recv}]");
                            // Dump FootballPlayer own fields
                            object? logicInst = null;
                            foreach (var rf in recv.GetType().GetFields(flags))
                            {
                                try
                                {
                                    object? rv = rf.GetValue(recv);
                                    if (rf.Name == "logic") logicInst = rv;
                                    sb.AppendLine($"    {rf.FieldType.Name} {rf.Name} = {DescribeValue(rv)}");
                                }
                                catch { }
                            }
                            // Dump FootballPlayerLogic fields (holds route/open state)
                            if (logicInst != null)
                            {
                                sb.AppendLine($"    -- Logic Fields ({logicInst.GetType().Name}) --");
                                object? assignmentInst = null;
                                foreach (var lf in logicInst.GetType().GetFields(flags))
                                {
                                    try
                                    {
                                        object? lv = lf.GetValue(logicInst);
                                        if (lf.Name == "Assignment") assignmentInst = lv;
                                        sb.AppendLine($"      {lf.FieldType.Name} {lf.Name} = {DescribeValue(lv)}");
                                    }
                                    catch { }
                                }
                                // Drill into Assignment to get its name fields
                                if (assignmentInst != null)
                                {
                                    sb.AppendLine($"      -- Assignment Fields ({assignmentInst.GetType().Name}) --");
                                    DumpObjectFields(assignmentInst, sb, flags, depth: 2, maxDepth: 2);
                                }
                            }
                        }
                    }
                }

                // Drill into DataManager
                if (dataManagerInst != null)
                {
                    sb.AppendLine($"\n--- DataManager Fields (type: {dataManagerInst.GetType().FullName}) ---");
                    DumpObjectFields(dataManagerInst, sb, flags, depth: 0, maxDepth: 0);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\n--- FootballGameplayMenu dump failed: {ex.Message} ---");
            }
        }

        private static void DumpObjectFields(object inst, System.Text.StringBuilder sb,
            BindingFlags flags, int depth, int maxDepth)
        {
            string indent = new string(' ', (depth + 1) * 2);
            foreach (var field in inst.GetType().GetFields(flags))
            {
                try
                {
                    object? val = field.GetValue(inst);
                    string desc = DescribeValue(val);
                    sb.AppendLine($"{indent}{field.FieldType.Name} {field.Name} = {desc}");

                    // Recurse one level into non-Unity, non-primitive interesting objects
                    if (depth < maxDepth && val != null && !(val is UnityEngine.Object)
                        && !(val is string) && !(val is System.Collections.IList)
                        && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum
                        && field.FieldType.Assembly.GetName().Name == "Assembly-CSharp")
                    {
                        DumpObjectFields(val, sb, flags, depth + 1, maxDepth);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{indent}{field.FieldType.Name} {field.Name} = [ERR: {ex.Message}]");
                }
            }
        }

        private static string DescribeValue(object? val)
        {
            if (val == null) return "null";
            if (val is System.Collections.IList list)
            {
                var items = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Math.Min(list.Count, 12); i++)
                    items.Add(list[i]?.ToString() ?? "null");
                return $"[{list.Count}] {{ {string.Join(", ", items.ToArray())}{(list.Count > 12 ? ", ..." : "")} }}";
            }
            string s = val.ToString() ?? "";
            if (s.Length > 140) s = s.Substring(0, 140) + "...";
            return s;
        }

        private static string GetHierarchyPath(Transform t, int maxDepth)
        {
            var parts = new System.Collections.Generic.List<string>();
            Transform cur = t;
            while (cur != null && parts.Count < maxDepth)
            {
                parts.Insert(0, cur.gameObject.name);
                cur = cur.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private static void InitLoopReflection()
        {
            _loopReflectionDone = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "UIButton")        _loopUiButtonType        = t;
                    if (t.Name == "UIButtonLinker")  _loopUiButtonLinkerType  = t;
                    if (t.Name == "UIButtonStretch") _loopUiButtonStretchType = t;
                }
                break;
            }

            if (_loopUiButtonType != null)
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _loopSelectedField = _loopUiButtonType.GetField("Selected", flags);
                _loopSelectedByControllerField = _loopUiButtonType.GetField("SelectedByController", flags);
                Log.LogInfo($"[Plugin] UIButton.Selected={_loopSelectedField != null} " +
                            $"UIButton.SelectedByController={_loopSelectedByControllerField != null}");
            }
        }

        private static bool IsLoopButtonSelected(MonoBehaviour mb)
        {
            // Strategy 1: SelectedByController (most reliable for gamepad/keyboard nav)
            if (_loopSelectedByControllerField != null)
                try { if (_loopSelectedByControllerField.GetValue(mb) is bool b1 && b1) return true; } catch { }
            // Strategy 2: Selected (general selection state)
            if (_loopSelectedField != null)
                try { if (_loopSelectedField.GetValue(mb) is bool b2 && b2) return true; } catch { }
            // Strategy 3: scale fallback
            Vector3 s = mb.transform.localScale;
            if (s.x > 1.05f || s.y > 1.05f) return true;
            return false;
        }

        private static void PollButtonsLoop(Type type)
        {
            try
            {
                var buttons = UnityEngine.Object.FindObjectsOfType(type);
                if (buttons == null) return;
                foreach (var raw in buttons)
                {
                    var mb = raw as MonoBehaviour;
                    if (mb == null || !mb.gameObject.activeInHierarchy) continue;
                    if (!IsLoopButtonSelected(mb)) continue;

                    string t = MenuReader.GetText(mb.gameObject);
                    // Substitute labels for known unlabeled/poorly-labelled buttons
                    if (string.IsNullOrWhiteSpace(t))
                    {
                        string bn = mb.gameObject.name;
                        if (bn == "ButtonPrevious") t = "Previous";
                        else if (bn == "ButtonNext") t = "Next";
                        else if (bn == "Dummy Button") continue; // silent focus trap
                        else continue;
                    }
                    else if (t == ">") t = "Next";
                    else if (t == "<") t = "Previous";
                    else if (t == "#") t = "Jersey number";
                    if (t == _loopLastSpoken) continue;
                    if (Time.unscaledTime - _lastButtonSpeakTime < BUTTON_SPEAK_MIN_GAP) return;

                    _loopLastSpoken = t;
                    _lastButtonSpeakTime = Time.unscaledTime;
                    try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                        $"[{DateTime.Now:HH:mm:ss}] BTN: {t}\n"); } catch { }
                    SpeechManager.Speak(t + ", button");
                    return;
                }
            }
            catch { }
        }

        private bool _esLoggedNull = false;

        private void PollEventSystem()
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null)
                {
                    if (!_esLoggedNull)
                    {
                        _esLoggedNull = true;
                        Log.LogWarning("[Plugin.EventSystem] EventSystem.current is null.");
                        try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                            $"[{DateTime.Now:HH:mm:ss}] EventSystem NULL\n"); } catch { }
                    }
                    return;
                }

                var sel = es.currentSelectedGameObject;
                if (sel == null) return;

                string text = MenuReader.GetText(sel);
                // Fallback: use the GameObject name if no TMP text found
                if (string.IsNullOrWhiteSpace(text)) text = sel.name;
                if (string.IsNullOrWhiteSpace(text) || text == _lastSpokenButton) return;

                _lastSpokenButton = text;
                Log.LogInfo($"[Plugin.EventSystem] Selected: \"{text}\"");
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss}] ES: {text}\n"); } catch { }
                SpeechManager.Speak(text + ", button");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Plugin.EventSystem] Exception: {ex.Message}");
            }
        }

        private void InitButtonReflection()
        {
            _buttonReflectionDone = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "UIButton")       _uiButtonType       = t;
                    if (t.Name == "UIButtonLinker")  _uiButtonLinkerType = t;
                }
                break;
            }

            if (_uiButtonType == null)
            {
                Log.LogWarning("[Plugin.Update] UIButton type not found.");
            }
            else
            {
                // Find a bool "selected" field
                string[] candidates = { "selected", "isSelected", "_selected", "Selected",
                                        "IsSelected", "highlighted", "isHighlighted", "_highlighted" };
                foreach (string name in candidates)
                {
                    var f = _uiButtonType.GetField(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        _selectedField = f;
                        Log.LogInfo($"[Plugin.Update] Watching UIButton.{name}");
                        break;
                    }
                }

                // Log all UIButton fields for diagnosis
                Log.LogInfo("[Plugin.Update] UIButton fields:");
                foreach (var f in _uiButtonType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    Log.LogInfo($"  {f.FieldType.Name} {f.Name}");
            }

            Log.LogInfo(_uiButtonLinkerType != null
                ? "[Plugin.Update] UIButtonLinker type found."
                : "[Plugin.Update] UIButtonLinker not found.");
        }

        private void PollButtons(Type type)
        {
            UnityEngine.Object[] buttons = FindObjectsOfType(type);
            if (buttons == null || buttons.Length == 0) return;

            foreach (var raw in buttons)
            {
                MonoBehaviour? mb = raw as MonoBehaviour;
                if (mb == null || !mb.gameObject.activeInHierarchy) continue;

                bool selected = IsSelected(mb);
                if (!selected) continue;

                string text = MenuReader.GetText(mb.gameObject);
                if (string.IsNullOrWhiteSpace(text) || text == _lastSpokenButton) continue;

                _lastSpokenButton = text;
                Log.LogInfo($"[Plugin.Update] {type.Name} selected: \"{text}\"");
                SpeechManager.Speak(text + ", button");
                return;
            }
        }

        private bool IsSelected(MonoBehaviour mb)
        {
            if (_selectedField != null)
            {
                try { if (_selectedField.GetValue(mb) is bool b && b) return true; }
                catch { }
            }
            Vector3 s = mb.transform.localScale;
            if (s.x > 1.05f || s.y > 1.05f) return true;

            var img = mb.GetComponent<UnityEngine.UI.Image>();
            if (img != null)
            {
                Color c = img.color;
                if (c.r > 0.9f && c.g > 0.9f && c.b > 0.9f && c.a > 0.5f) return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            // Don't shut down SpeechManager here — the PlayerLoop injection survives
            // scene reloads and continues calling SpeechManager.Speak(). Shutting down
            // the worker thread here would silence all speech after the first scene change.
            // Speech cleanup happens when the process exits (background thread dies automatically).
            _harmony?.UnpatchSelf();
        }

        private IEnumerator DelayedStartupSpeak()
        {
            // Use real time so timeScale=0 loading screens don't block this
            yield return new WaitForSecondsRealtime(4f);
            SpeechManager.Speak("Football Simulator is ready.");
        }

        private IEnumerator AutoDumpAfterDelay(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            Log.LogInfo("[AutoDump] Dumping scene hierarchy...");
            GameDiscovery.DumpSceneNow();
            Log.LogInfo("[AutoDump] Done.");
        }

        private static void DumpMenuClasses()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;

                foreach (var type in asm.GetTypes())
                {
                    string n = type.Name.ToLower();
                    if (!n.Contains("menu") && !n.Contains("ui") && !n.Contains("hud")
                        && !n.Contains("button") && !n.Contains("select") && !n.Contains("nav")
                        && !n.Contains("panel") && !n.Contains("screen") && !n.Contains("dialog"))
                        continue;

                    Log.LogInfo($"[MenuClass] {type.FullName}");
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name.StartsWith("<")) continue;
                        Log.LogInfo($"  .{m.Name}()");
                    }
                }
                break;
            }
        }
    }

    internal static class PluginInfo
    {
        public const string PLUGIN_GUID    = "com.footballaccess.mod";
        public const string PLUGIN_NAME    = "Football Accessibility Mod";
        public const string PLUGIN_VERSION = "2.0.0";
    }
}
