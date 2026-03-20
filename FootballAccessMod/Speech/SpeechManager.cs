using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx.Logging;

namespace FootballAccessMod.Speech
{
    /// <summary>
    /// Central speech controller.
    /// Primary: NVDA controller client (if nvdaControllerClient64.dll present).
    /// Fallback: Persistent PowerShell process that owns a System.Speech SAPI voice,
    ///           fed text via stdin — works around Mono's COM activation restriction.
    /// </summary>
    public static class SpeechManager
    {
        private static ManualLogSource? _log;

        // PowerShell SAPI bridge
        private static Process? _psProcess;
        private static StreamWriter? _psWriter;
        private static readonly object _psLock = new object();

        private struct SpeechItem { public string text; public bool interrupt; }

        // Background write thread so we never block Unity's main thread
        private static readonly ConcurrentQueue<SpeechItem> _queue
            = new ConcurrentQueue<SpeechItem>();
        private static Thread? _workerThread;
        private static volatile bool _running;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;
            try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                $"[{DateTime.Now:HH:mm:ss.fff}] SPEECH_INIT\n"); } catch { }
            NvdaController.Initialize(log);

            // Always start the PowerShell SAPI bridge as a fallback, even when NVDA is available.
            // If NVDA speakText fails at runtime we can seamlessly switch to SAPI.
            StartPowerShellBridge();

            _running = true;
            _workerThread = new Thread(Worker) { IsBackground = true, Name = "FootballAccess_Speech" };
            _workerThread.Start();
        }

        public static void Shutdown()
        {
            _running = false;
            _workerThread?.Interrupt();
            try { _psProcess?.Kill(); } catch { }
        }

        // ---- Public API ----

        public static void Speak(string text, bool interrupt = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _log?.LogDebug($"[SPEECH] {text}");

            // Auto-restart the worker if it died (e.g. scene reload called OnDestroy)
            if (!_running || _workerThread == null || !_workerThread.IsAlive)
            {
                _running = true;
                _workerThread = new Thread(Worker) { IsBackground = true, Name = "FootballAccess_Speech" };
                _workerThread.Start();
            }

            if (interrupt)
            {
                // Drain the queue before adding the new item
                while (_queue.TryDequeue(out _)) { }
            }
            _queue.Enqueue(new SpeechItem { text = text, interrupt = interrupt });
        }

        public static void SpeakQueued(string text) => Speak(text, interrupt: false);
        public static void Notify(string text) => SpeakQueued(text);

        // ---- Worker thread ----

        private static void Worker()
        {
            try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                $"[{DateTime.Now:HH:mm:ss.fff}] WORKER_STARTED\n"); } catch { }
            while (_running)
            {
                try
                {
                    SpeechItem item;
                    if (_queue.TryDequeue(out item))
                    {
                        SendSpeech(item.text, item.interrupt);
                    }
                    else
                    {
                        Thread.Sleep(30);
                    }
                }
                catch (ThreadInterruptedException)
                {
                    try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                        $"[{DateTime.Now:HH:mm:ss.fff}] WORKER_INTERRUPTED\n"); } catch { }
                    break;
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                        $"[{DateTime.Now:HH:mm:ss.fff}] WORKER_EX: {ex.Message}\n"); } catch { }
                    _log?.LogDebug($"[Speech] Worker: {ex.Message}");
                }
            }
            try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                $"[{DateTime.Now:HH:mm:ss.fff}] WORKER_EXITED running={_running}\n"); } catch { }
        }

        private static void SendSpeech(string text, bool interrupt)
        {
            // Write proof-of-call to file — always, so we can diagnose even if audio is silent
            try
            {
                string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{stamp}] SPEAK: {text}\n");
            }
            catch { }

            // --- NVDA path ---
            if (NvdaController.IsRunning())
            {
                if (interrupt) NvdaController.CancelSpeech();
                bool spoke = NvdaController.SpeakText(text);
                _log?.LogInfo($"[Speech] NVDA speakText result={spoke} text=\"{text.Substring(0, Math.Min(40, text.Length))}\"");
                if (spoke) NvdaController.BrailleMessage(text);
            }
            else
            {
                _log?.LogInfo("[Speech] NVDA not running — SAPI only");
            }

            // --- SAPI path (only when NVDA is not running) ---
            if (!NvdaController.IsRunning())
            {
                lock (_psLock)
                {
                    if (_psWriter == null || _psProcess?.HasExited == true)
                    {
                        _log?.LogWarning("[Speech] PowerShell bridge not running, restarting...");
                        StartPowerShellBridge();
                    }

                    try
                    {
                        if (interrupt)
                            _psWriter?.WriteLine("CANCEL");
                        _psWriter?.WriteLine(text);
                        _psWriter?.Flush();
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning($"[Speech] Write to PowerShell failed: {ex.Message}");
                    }
                }
            }
        }

        // ---- PowerShell bridge setup ----

        private static void StartPowerShellBridge()
        {
            // PowerShell script: reads lines from stdin, speaks via System.Speech SAPI.
            // "CANCEL" cancels current speech. Any other line is spoken (interrupting previous).
            const string script =
                "[Console]::InputEncoding=[System.Text.Encoding]::UTF8;" +
                "Add-Type -AssemblyName System.Speech;" +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                "$s.Rate = 1;" +
                "while($true){" +
                    "$line = [Console]::ReadLine();" +
                    "if($line -eq $null){break};" +
                    "if($line -eq 'CANCEL'){$s.SpeakAsyncCancelAll();continue};" +
                    "$s.SpeakAsyncCancelAll();" +
                    "$s.SpeakAsync($line)" +
                "}";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _psProcess = Process.Start(psi);
                if (_psProcess != null)
                {
                    _psWriter = _psProcess.StandardInput;
                    _log?.LogInfo("[Speech] PowerShell SAPI bridge started.");
                }
                else
                {
                    _log?.LogError("[Speech] Failed to start PowerShell bridge.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[Speech] Could not start PowerShell bridge: {ex.Message}");
            }
        }
    }
}
