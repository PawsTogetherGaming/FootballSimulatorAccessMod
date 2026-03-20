using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FootballAccessMod.Utils
{
    /// <summary>
    /// Debug discovery tool - run this to dump:
    ///   1. All classes in Assembly-CSharp.dll with their fields/methods
    ///   2. Active scene's full GameObject hierarchy with components
    ///   3. Current UI state
    ///
    /// Output written to: BepInEx/logs/GameDiscovery.txt
    /// Enable by pressing F12 during gameplay.
    /// This data lets us find the exact class/field names for precise Harmony patches.
    /// </summary>
    public class GameDiscovery : MonoBehaviour
    {
        private static readonly string OutputPath =
            Path.Combine(Paths.BepInExRootPath, "logs", "GameDiscovery.txt");

        private bool _triggered = false;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F12) && !_triggered)
            {
                _triggered = true;
                DumpAll();
                _triggered = false;
                Speech.SpeechManager.Speak("Discovery dump written to log file.");
            }

            // F11 = dump current scene hierarchy only (faster)
            if (Input.GetKeyDown(KeyCode.F11))
            {
                DumpSceneHierarchy();
                Speech.SpeechManager.Speak("Scene hierarchy dumped.");
            }
        }

        private static void DumpAll()
        {
            using var writer = new StreamWriter(OutputPath, append: false, Encoding.UTF8);

            writer.WriteLine("=== Football Simulator - Accessibility Discovery Dump ===");
            writer.WriteLine($"Generated: {DateTime.Now}");
            writer.WriteLine();

            DumpAssemblyClasses(writer);
            DumpSceneHierarchyToWriter(writer);
        }

        private static void DumpAssemblyClasses(TextWriter writer)
        {
            writer.WriteLine("=== ASSEMBLY-CSHARP CLASSES ===");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;

                foreach (var type in asm.GetTypes())
                {
                    // Skip Unity internals and generated types
                    if (type.Namespace?.StartsWith("Unity") == true) continue;
                    if (type.Name.Contains("<") || type.Name.Contains(">")) continue;

                    writer.WriteLine($"\nCLASS: {type.FullName}");

                    // Fields
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        writer.WriteLine($"  FIELD: {field.FieldType.Name} {field.Name}");
                    }

                    // Properties
                    foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    {
                        writer.WriteLine($"  PROP: {prop.PropertyType.Name} {prop.Name}");
                    }

                    // Methods (public only, skip generated)
                    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) continue;
                        if (method.DeclaringType == typeof(object)) continue;
                        writer.WriteLine($"  METHOD: {method.ReturnType.Name} {method.Name}()");
                    }
                }
                break;
            }
            writer.WriteLine("\n=== END CLASSES ===\n");
        }

        public static void DumpSceneNow()
        {
            DumpSceneHierarchy();
        }

        private static void DumpSceneHierarchy()
        {
            using var writer = new StreamWriter(
                Path.Combine(Paths.BepInExRootPath, "logs", "SceneHierarchy.txt"),
                append: false, Encoding.UTF8);
            DumpSceneHierarchyToWriter(writer);
        }

        private static void DumpSceneHierarchyToWriter(TextWriter writer)
        {
            writer.WriteLine("=== SCENE HIERARCHY ===");
            writer.WriteLine($"Active Scene: {SceneManager.GetActiveScene().name}");

            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                DumpTransform(writer, root.transform, 0);
            }
            writer.WriteLine("=== END HIERARCHY ===");
        }

        private static void DumpTransform(TextWriter writer, Transform t, int depth)
        {
            string indent = new string(' ', depth * 2);
            string components = "";

            foreach (var comp in t.GetComponents<Component>())
            {
                if (comp == null) continue;
                components += $"[{comp.GetType().Name}] ";
            }

            // Include text content if available
            string textContent = "";
            var tmp = t.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                textContent = $" TEXT=\"{tmp.text.Trim()}\"";

            writer.WriteLine($"{indent}{t.name} {components}{textContent}");

            if (depth < 8) // Limit depth to prevent huge files
            {
                foreach (Transform child in t)
                    DumpTransform(writer, child, depth + 1);
            }
        }
    }
}
