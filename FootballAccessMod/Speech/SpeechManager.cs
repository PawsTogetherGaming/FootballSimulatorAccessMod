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
    /// Primary path: NVDA controller client (if nvdaControllerClient64.dll present and NVDA is running).
    /// Fallback path: Persistent PowerShell process that tries JAWS via COM (FreedomSci.JawsApi);
    ///                if JAWS is not running, falls back to System.Speech SAPI inside the same process.
    ///                The PowerShell bridge works around Mono's COM activation restriction.
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

            // --- PowerShell bridge path (JAWS COM + SAPI fallback, used when NVDA not running) ---
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
                        // Prefix protocol: "I:" interrupts current speech; "Q:" queues after it.
                        // Without this both JAWS and SAPI would clobber a queued line (e.g. the
                        // settings-menu description) with the next interrupting line.
                        string prefix = interrupt ? "I:" : "Q:";
                        _psWriter?.WriteLine(prefix + text);
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

        // The bridge script. Written to disk on first start and invoked with `powershell -File`
        // (not -EncodedCommand). The file-based path is more reliable: easier to debug, no
        // base64/UTF-16 encoding edge cases, and you can run it standalone to verify it works.
        //
        // Engine selection at startup:
        //   1. Try JAWS via COM (FreedomSci.JawsApi). If JAWS is running and Enable() works, use it.
        //   2. Otherwise fall back to System.Speech SAPI.
        // Protocol on stdin:
        //   "CANCEL"     — stop current speech
        //   "I:<text>"   — interrupt and speak <text>
        //   "Q:<text>"   — queue <text> after current speech
        //   "<text>"     — same as I: (legacy)
        private const string BRIDGE_SCRIPT = @"
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
$logPath = 'C:\football\speech_log.txt'
function LogPS($msg) {
    try { Add-Content -Path $logPath -Value (""[$([DateTime]::Now.ToString('HH:mm:ss.fff'))] PS $msg"") -ErrorAction SilentlyContinue } catch {}
}

LogPS 'BRIDGE_STARTING'

# JAWS via COM. New-Object will throw if COM class not registered (= JAWS not installed).
$jaws = $null
try {
    $jaws = New-Object -ComObject FreedomSci.JawsApi
    LogPS 'JAWS_COM_OBJECT_CREATED'
    $enabled = $false
    try { $enabled = [bool]$jaws.Enable($false) } catch { LogPS ('JAWS_ENABLE_THREW: ' + $_.Exception.Message) }
    if (-not $enabled) {
        LogPS 'JAWS_ENABLE_RETURNED_FALSE'
        $jaws = $null
    }
} catch {
    LogPS ('JAWS_COM_FAIL: ' + $_.Exception.Message)
    $jaws = $null
}

# SAPI fallback — always init so we have something if JAWS fails mid-game.
$sapi = $null
try {
    Add-Type -AssemblyName System.Speech -ErrorAction Stop
    $sapi = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $sapi.Rate = 1
    LogPS 'SAPI_READY'
} catch {
    LogPS ('SAPI_INIT_FAIL: ' + $_.Exception.Message)
}

$mode = if ($jaws) { 'JAWS' } else { 'SAPI' }
LogPS (""BRIDGE_ENGINE_SELECTED: $mode"")

# Speak a startup probe so we can hear the bridge came up
try {
    if ($jaws) { $jaws.SayString('Football mod ready.', $true) | Out-Null }
    elseif ($sapi) { $sapi.SpeakAsync('Football mod ready.') | Out-Null }
} catch { LogPS ('STARTUP_PROBE_FAIL: ' + $_.Exception.Message) }

LogPS 'ENTERING_READ_LOOP'
while ($true) {
    $line = $null
    try { $line = [Console]::In.ReadLine() } catch { LogPS ('READ_FAIL: ' + $_.Exception.Message); break }
    if ($line -eq $null) { LogPS 'STDIN_CLOSED'; break }

    if ($line -eq 'CANCEL') {
        if ($jaws) { try { $jaws.StopSpeech() | Out-Null } catch {} }
        elseif ($sapi) { try { $sapi.SpeakAsyncCancelAll() } catch {} }
        continue
    }

    $interrupt = $true
    $text = $line
    if ($line.Length -ge 2) {
        $p = $line.Substring(0, 2)
        if ($p -eq 'I:') { $text = $line.Substring(2); $interrupt = $true }
        elseif ($p -eq 'Q:') { $text = $line.Substring(2); $interrupt = $false }
    }

    if ([string]::IsNullOrWhiteSpace($text)) { continue }

    if ($jaws) {
        try {
            $jaws.SayString($text, $interrupt) | Out-Null
        } catch {
            LogPS ('JAWS_SAY_FAIL: ' + $_.Exception.Message + ' - permanent SAPI fallback')
            $jaws = $null
            if ($sapi) {
                if ($interrupt) { try { $sapi.SpeakAsyncCancelAll() } catch {} }
                try { $sapi.SpeakAsync($text) | Out-Null } catch { LogPS ('SAPI_SPEAK_FAIL: ' + $_.Exception.Message) }
            }
        }
    } elseif ($sapi) {
        if ($interrupt) { try { $sapi.SpeakAsyncCancelAll() } catch {} }
        try { $sapi.SpeakAsync($text) | Out-Null } catch { LogPS ('SAPI_SPEAK_FAIL: ' + $_.Exception.Message) }
    } else {
        LogPS ('NO_ENGINE_DROP: ' + $text)
    }
}
LogPS 'BRIDGE_EXITED'
";

        private static string GetScriptPath()
        {
            try
            {
                string asmDir = System.IO.Path.GetDirectoryName(
                    typeof(SpeechManager).Assembly.Location) ?? @"C:\football";
                return System.IO.Path.Combine(asmDir, "football_speech_bridge.ps1");
            }
            catch
            {
                return @"C:\football\football_speech_bridge.ps1";
            }
        }

        private static void StartPowerShellBridge()
        {
            try
            {
                string scriptPath = GetScriptPath();

                // Always rewrite — keeps the on-disk script in sync with code edits.
                System.IO.File.WriteAllText(scriptPath, BRIDGE_SCRIPT, new System.Text.UTF8Encoding(false));
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss.fff}] BRIDGE_SCRIPT_WRITTEN: {scriptPath}\n"); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
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
                    _log?.LogInfo($"[Speech] PowerShell bridge started (PID {_psProcess.Id}, file={scriptPath}).");
                    try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                        $"[{DateTime.Now:HH:mm:ss.fff}] BRIDGE_PROCESS_STARTED PID={_psProcess.Id}\n"); } catch { }
                }
                else
                {
                    _log?.LogError("[Speech] Failed to start PowerShell bridge.");
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[Speech] Could not start PowerShell bridge: {ex.Message}");
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{DateTime.Now:HH:mm:ss.fff}] BRIDGE_START_EX: {ex.Message}\n"); } catch { }
            }
        }
    }
}
