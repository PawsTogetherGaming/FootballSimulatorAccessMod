using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// In-game accessible settings menu for the Football Accessibility Mod.
    ///
    /// Open / close:  F2 on keyboard, OR hold Back (Select) button for 2 seconds.
    /// Navigate:      D-pad Up / Down  (or joystick button 10/11)
    /// Change value:  D-pad Left / Right (or joystick button 12/13)
    /// Close:         B button  OR  F2 again
    ///
    /// Every focus change and value change is spoken through NVDA/SAPI.
    /// Changes are saved immediately to BepInEx config.
    /// </summary>
    public static class SettingsMenuReader
    {
        // ---- State ----
        public  static bool  IsOpen     { get; private set; } = false;
        private static int   _focus     = 0;           // which setting is selected
        private static float _backHeld  = 0f;          // seconds Back button held
        private const  float BACK_HOLD  = 2f;          // hold time to open via Back button

        // D-pad / button edge-detection
        private static bool  _upWasDown    = false;
        private static bool  _downWasDown  = false;
        private static bool  _leftWasDown  = false;
        private static bool  _rightWasDown = false;
        private static bool  _bWasDown     = false;
        private static bool  _f2WasDown    = false;
        private static bool  _escWasDown   = false;
        private static bool  _backWasDown  = false;

        // Heartbeat — logs button state once per second while menu is open
        private static float _heartbeatTimer = 0f;

        // ---- Setting definitions ----
        // Order shown in the menu — grouped by category.
        private static readonly SettingDef[] Settings = new SettingDef[]
        {
            // ---- In-Game HUD ----
            new SettingDef(
                name:        "Read down and distance automatically",
                description: "Speaks down and distance every time it changes during a game. " +
                             "You can always press D on the keyboard to hear it on demand.",
                get:         () => (ModSettings.ReadDDChanges?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadDDChanges != null) ModSettings.ReadDDChanges.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read quarter changes",
                description: "Announces the start of each new quarter or overtime automatically.",
                get:         () => (ModSettings.ReadQuarterChanges?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadQuarterChanges != null) ModSettings.ReadQuarterChanges.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read score changes",
                description: "Announces the updated score whenever it changes. " +
                             "You can always press S on the keyboard to hear the score on demand.",
                get:         () => (ModSettings.ReadScoreChanges?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadScoreChanges != null) ModSettings.ReadScoreChanges.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            // ---- Play Call Screen ----
            new SettingDef(
                name:        "Read play call tooltips",
                description: "Reads formation nav prompts such as down and distance and select formation " +
                             "when the play call screen opens, and formation name as you browse.",
                get:         () => (ModSettings.ReadTooltips?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadTooltips != null) ModSettings.ReadTooltips.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read selected play",
                description: "Announces the play name when you call it and when the CPU calls their play.",
                get:         () => (ModSettings.ReadSelectedPlay?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadSelectedPlay != null) ModSettings.ReadSelectedPlay.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            // ---- Receivers ----
            new SettingDef(
                name:        "Read routes automatically",
                description: "Reads receiver route assignments at the start of each play automatically. " +
                             "L1 always repeats them regardless of this setting.",
                get:         () => (ModSettings.ReadRoutesAuto?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadRoutesAuto != null) ModSettings.ReadRoutesAuto.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read receiver open and covered changes",
                description: "Announces when a receiver becomes open during the QB drop-back, " +
                             "for example A open or X covered.",
                get:         () => (ModSettings.ReadReceiverChanges?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadReceiverChanges != null) ModSettings.ReadReceiverChanges.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read play art routes",
                description: "Reads the route description from the play art diagram shown at the start of each play.",
                get:         () => (ModSettings.ReadPlayArt?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadPlayArt != null) ModSettings.ReadPlayArt.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            // ---- Defense ----
            new SettingDef(
                name:        "Read defender switch",
                description: "Announces position, name, and field location when you switch which defender you control.",
                get:         () => (ModSettings.ReadDefenderSwitch?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadDefenderSwitch != null) ModSettings.ReadDefenderSwitch.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read defender state changes",
                description: "Announces when your defender's behavior changes, " +
                             "such as blitzing, in coverage, or on it when going for the ball.",
                get:         () => (ModSettings.ReadDefenderState?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadDefenderState != null) ModSettings.ReadDefenderState.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Read ball in air direction",
                description: "Announces where the ball is and how far away while it is in the air, " +
                             "updated every half second.",
                get:         () => (ModSettings.ReadBallInAir?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadBallInAir != null) ModSettings.ReadBallInAir.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Defensive heat seek power",
                description: "Controls how powerfully R1 routes your defender to the ball. " +
                             "0 is off. 1 to 3 chases the current position. " +
                             "4 to 6 predicts where the target is going. " +
                             "7 to 9 adds a speed lead for intercepts. " +
                             "10 is maximum and also auto-activates on every passing play " +
                             "and within 6 metres of the carrier on run plays.",
                get:         () => ModSettings.HeatSeekPower?.Value ?? 5,
                set:         v  => { if (ModSettings.HeatSeekPower != null) ModSettings.HeatSeekPower.Value = v; },
                min: 0, max: 10,
                format:      v => v == 0  ? "Off"
                               : v == 10 ? "Maximum, auto"
                               : v.ToString()),

            // ---- Offense ----
            new SettingDef(
                name:        "Read evasion prompts",
                description: "Suggests an evasion move when a defender closes on the ball carrier, " +
                             "such as left L1 stiff arm or straight B to truck.",
                get:         () => (ModSettings.ReadEvasionPrompts?.Value ?? true) ? 1 : 0,
                set:         v  => { if (ModSettings.ReadEvasionPrompts != null) ModSettings.ReadEvasionPrompts.Value = v == 1; },
                min: 0, max: 1,
                format:      v => v == 1 ? "On" : "Off"),

            new SettingDef(
                name:        "Offensive run assist power",
                description: "Controls how aggressively the run assist guides the ball carrier. " +
                             "0 is off. 1 to 3 gives occasional open lane hints. " +
                             "4 to 6 gives frequent hints. " +
                             "7 to 9 adds cut timing calls such as cut left now. " +
                             "10 is maximum and takes full AI control, " +
                             "automatically steering the carrier into the best open lane.",
                get:         () => ModSettings.OffensiveAssistPower?.Value ?? 5,
                set:         v  => { if (ModSettings.OffensiveAssistPower != null) ModSettings.OffensiveAssistPower.Value = v; },
                min: 0, max: 10,
                format:      v => v == 0  ? "Off"
                               : v == 10 ? "Maximum, auto"
                               : v.ToString()),

            // ---- CPU Difficulty (TEMPORARILY HIDDEN) ----
            // Setting hidden because the underlying patches did not produce a noticeable
            // difference in playtest. Code preserved in Accessibility/DifficultyManager.cs
            // and the ConfigEntry stays bound (so saved values aren't lost) — re-add this
            // SettingDef and re-enable DifficultyPatches/DifficultyManager in Plugin.cs
            // once a more impactful approach lands.
        };

        // =========================================================
        // PollInput — called every frame (before the 120ms gate)
        // =========================================================

        public static void PollInput()
        {
            // ---- F2 toggle ----
            bool f2 = Input.GetKey(KeyCode.F2);
            if (f2 && !_f2WasDown)
            {
                Plugin.Log.LogInfo("[SettingsMenu] F2 pressed — calling Toggle()");
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{System.DateTime.Now:HH:mm:ss.fff}] SETTINGS_F2_PRESSED IsOpen={IsOpen}\n"); } catch { }
                Toggle();
            }
            _f2WasDown = f2;

            // ---- Back button hold-to-open (only when menu is closed) ----
            bool back = Input.GetKey(KeyCode.JoystickButton6);
            if (!IsOpen)
            {
                if (back)
                {
                    _backHeld += Time.unscaledDeltaTime;
                    if (_backHeld >= BACK_HOLD && !_backWasDown)
                    {
                        _backWasDown = true;
                        Plugin.Log.LogInfo("[SettingsMenu] Back hold triggered — calling Open()");
                        try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                            $"[{System.DateTime.Now:HH:mm:ss.fff}] SETTINGS_BACK_HOLD\n"); } catch { }
                        Open();
                    }
                }
                else
                {
                    _backHeld    = 0f;
                    _backWasDown = false;
                }
                return;
            }
            else
            {
                _backHeld = 0f;
            }

            // ---- Menu navigation (only when open) ----
            // KEYBOARD ONLY — deliberately no D-pad / joystick button reads here.
            // The game's menu cursor is moved by controller D-pad; if we also read
            // controller input for the mod menu the two systems fight each other and
            // the game cursor drifts.  Since the menu is opened with F2 (a keyboard
            // key), navigating with keyboard arrow keys is natural and causes zero
            // interference with the game's controller-based navigation.

            // Escape → close
            bool esc = Input.GetKey(KeyCode.Escape);
            if (esc && !_escWasDown)
            {
                Close();
                _escWasDown = esc;
                return;
            }
            _escWasDown = esc;

            // Arrow keys
            bool up    = Input.GetKey(KeyCode.UpArrow);
            bool down  = Input.GetKey(KeyCode.DownArrow);
            bool left  = Input.GetKey(KeyCode.LeftArrow);
            bool right = Input.GetKey(KeyCode.RightArrow);

            // Heartbeat: write key state to log once per second
            _heartbeatTimer += Time.unscaledDeltaTime;
            if (_heartbeatTimer >= 1f)
            {
                _heartbeatTimer = 0f;
                Plugin.Log.LogInfo($"[SettingsMenu] HB focus={_focus} up={up} dn={down} lt={left} rt={right}");
                try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                    $"[{System.DateTime.Now:HH:mm:ss.fff}] SETTINGS_HB focus={_focus} up={up} dn={down} lt={left} rt={right}\n"); } catch { }
            }

            if (up    && !_upWasDown)    { Plugin.Log.LogInfo("[SettingsMenu] Key UP");    MoveFocus(-1); }
            if (down  && !_downWasDown)  { Plugin.Log.LogInfo("[SettingsMenu] Key DOWN");  MoveFocus(+1); }
            if (left  && !_leftWasDown)  { Plugin.Log.LogInfo("[SettingsMenu] Key LEFT");  ChangeValue(-1); }
            if (right && !_rightWasDown) { Plugin.Log.LogInfo("[SettingsMenu] Key RIGHT"); ChangeValue(+1); }

            _upWasDown    = up;
            _downWasDown  = down;
            _leftWasDown  = left;
            _rightWasDown = right;
        }

        // =========================================================
        // Actions
        // =========================================================

        private static void Toggle() { if (IsOpen) Close(); else Open(); }

        private static void Open()
        {
            IsOpen = true;
            _focus = 0;
            _heartbeatTimer = 0f;

            var s = Settings[_focus];
            Plugin.Log.LogInfo($"[SettingsMenu] Open() called. Settings.Length={Settings.Length} firstName={s.Name}");
            try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                $"[{System.DateTime.Now:HH:mm:ss.fff}] SETTINGS_OPEN firstName={s.Name}\n"); } catch { }
            string msg =
                $"Mod settings. {Settings.Length} options. " +
                $"Use up and down arrow keys to navigate, left and right arrow keys to change. Escape to close. " +
                $"Setting 1 of {Settings.Length}: {s.Name}, {s.Format(s.Get())}.";
            SpeechManager.Speak(msg);
            SpeechManager.SpeakQueued(s.Description);
        }

        private static void Close()
        {
            IsOpen = false;
            _backWasDown    = false;
            _backHeld       = 0f;
            _heartbeatTimer = 0f;
            SpeechManager.Speak("Mod settings closed.");
        }

        private static void MoveFocus(int dir)
        {
            _focus = (_focus + dir + Settings.Length) % Settings.Length;
            var s = Settings[_focus];
            Plugin.Log.LogInfo($"[SettingsMenu] MoveFocus dir={dir} newFocus={_focus} name={s.Name}");
            try { System.IO.File.AppendAllText(@"C:\football\speech_log.txt",
                $"[{System.DateTime.Now:HH:mm:ss.fff}] SETTINGS_FOCUS idx={_focus} name={s.Name}\n"); } catch { }
            SpeechManager.Speak($"Setting {_focus + 1} of {Settings.Length}: {s.Name}, {s.Format(s.Get())}.");
            SpeechManager.SpeakQueued(s.Description);
        }

        private static void ChangeValue(int dir)
        {
            var s   = Settings[_focus];
            int cur = s.Get();
            int nxt = Mathf.Clamp(cur + dir, s.Min, s.Max);
            if (nxt == cur)
            {
                SpeechManager.Speak(dir < 0 ? "Already at minimum." : "Already at maximum.");
                return;
            }
            s.Set(nxt);
            SpeechManager.Speak($"{s.Name}: {s.Format(nxt)}.");
        }

        // ---- Describe setting (for "What is this?" — hold A) ----
        // Called externally if we want a long-press describe feature later.
        public static void DescribeCurrent()
        {
            if (!IsOpen) return;
            SpeechManager.Speak(Settings[_focus].Description);
        }

        // =========================================================
        // Setting definition struct
        // =========================================================

        private struct SettingDef
        {
            internal string Name;
            internal string Description;
            internal System.Func<int>    Get;
            internal System.Action<int>  Set;
            internal int Min, Max;
            internal System.Func<int, string> Format;

            internal SettingDef(string name, string description,
                System.Func<int> get, System.Action<int> set,
                int min, int max, System.Func<int, string> format)
            {
                Name = name; Description = description;
                Get = get; Set = set;
                Min = min; Max = max; Format = format;
            }
        }
    }
}
