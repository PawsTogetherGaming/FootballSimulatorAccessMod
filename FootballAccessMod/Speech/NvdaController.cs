using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;

namespace FootballAccessMod.Speech
{
    /// <summary>
    /// NVDA controller client wrapper.
    /// Searches for any x64 nvdaControllerClient DLL regardless of filename,
    /// loads it manually, and resolves function pointers via GetProcAddress
    /// so the DllImport name doesn't need to match the file on disk.
    /// </summary>
    internal static class NvdaController
    {
        private static ManualLogSource? _log;
        private static bool _available = false;
        private static bool _initialized = false;

        // Function pointer delegates
        private delegate int TestIfRunningDelegate();
        private delegate int SpeakTextDelegate([MarshalAs(UnmanagedType.LPWStr)] string text);
        private delegate int CancelSpeechDelegate();
        private delegate int BrailleMessageDelegate([MarshalAs(UnmanagedType.LPWStr)] string text);

        private static TestIfRunningDelegate?  _testIfRunning;
        private static SpeakTextDelegate?      _speakText;
        private static CancelSpeechDelegate?   _cancelSpeech;
        private static BrailleMessageDelegate? _brailleMessage;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        internal static void Initialize(ManualLogSource log)
        {
            if (_initialized) return;
            _initialized = true;
            _log = log;

            string? dllPath = FindDll();
            if (dllPath == null)
            {
                _log?.LogWarning(
                    "[NVDA] nvdaControllerClient DLL not found in BepInEx/plugins/ or game root. " +
                    "Falling back to SAPI TTS.");
                return;
            }

            IntPtr module = LoadLibraryW(dllPath);
            if (module == IntPtr.Zero)
            {
                _log?.LogWarning($"[NVDA] LoadLibrary failed for {dllPath} (Win32 error {Marshal.GetLastWin32Error()})");
                return;
            }

            _log?.LogInfo($"[NVDA] Loaded {Path.GetFileName(dllPath)}");

            // Resolve function pointers by name
            _testIfRunning  = GetDelegate<TestIfRunningDelegate>(module,  "nvdaController_testIfRunning");
            _speakText      = GetDelegate<SpeakTextDelegate>(module,      "nvdaController_speakText");
            _cancelSpeech   = GetDelegate<CancelSpeechDelegate>(module,   "nvdaController_cancelSpeech");
            _brailleMessage = GetDelegate<BrailleMessageDelegate>(module, "nvdaController_brailleMessage");

            if (_testIfRunning == null || _speakText == null)
            {
                _log?.LogWarning("[NVDA] Could not resolve required exports. Is this really an nvdaControllerClient DLL?");
                return;
            }

            try
            {
                int result = _testIfRunning();
                _available = (result == 0);
                _log?.LogInfo(_available
                    ? "[NVDA] NVDA is running — will use NVDA for speech."
                    : "[NVDA] DLL loaded but NVDA is not currently running. Will retry on each call.");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[NVDA] testIfRunning threw: {ex.Message}");
            }
        }

        internal static bool IsRunning()
        {
            if (_testIfRunning == null) return false;
            try { return _testIfRunning() == 0; }
            catch { return false; }
        }

        internal static bool SpeakText(string text)
        {
            if (_speakText == null) return false;
            try { return _speakText(text) == 0; }
            catch (Exception ex) { _log?.LogDebug($"[NVDA] speak: {ex.Message}"); return false; }
        }

        internal static bool CancelSpeech()
        {
            if (_cancelSpeech == null) return false;
            try { return _cancelSpeech() == 0; }
            catch { return false; }
        }

        internal static bool BrailleMessage(string text)
        {
            if (_brailleMessage == null) return false;
            try { return _brailleMessage(text) == 0; }
            catch { return false; }
        }

        // ---- Helpers ----

        private static string? FindDll()
        {
            string pluginsDir = Path.Combine(Paths.BepInExRootPath, "plugins");
            string gameRoot   = Path.GetDirectoryName(Paths.ExecutablePath) ?? "";

            // Accept any filename containing "nvdaControllerClient" that is x64
            string[] candidates = {
                "nvdaControllerClient64.dll",
                "nvdaControllerClient_x64.dll",
                "nvdaControllerClient.dll"       // some releases use this name for x64
            };

            foreach (string name in candidates)
            {
                foreach (string dir in new[] { pluginsDir, gameRoot })
                {
                    string path = Path.Combine(dir, name);
                    if (File.Exists(path))
                    {
                        _log?.LogInfo($"[NVDA] Found candidate: {path}");
                        return path;
                    }
                }
            }
            return null;
        }

        private static T? GetDelegate<T>(IntPtr module, string name) where T : Delegate
        {
            IntPtr ptr = GetProcAddress(module, name);
            if (ptr == IntPtr.Zero)
            {
                _log?.LogWarning($"[NVDA] Export not found: {name}");
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }
    }
}
