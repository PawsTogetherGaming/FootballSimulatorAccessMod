using System;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Handles the TeamSelect_Screen (Exhibition / Practice / Multiplayer team picker).
    /// </summary>
    public static class TeamSelectReader
    {
        // ---- Active flag ----
        private static bool _wasActive = false;

        // ---- Cached current screen ----
        private static GameObject _screen = null;

        // ---- Cached team state ----
        private static string _lastHomeTeam = "";
        private static string _lastHomeMasc = "";
        private static string _lastAwayTeam = "";
        private static string _lastAwayMasc = "";

        // ---- Cached match options state ----
        private static string _lastStadium   = "";
        private static string _lastWeather   = "";
        private static string _lastTimeOfDay = "";
        private static string _lastQuarters  = "";

        // ---- Side focus (ControllerIcon_0 world X) ----
        //   x >= 0 → Home (right side), x < 0 → Away (left side)
        private static string     _lastAnnouncedSide = "";
        private static GameObject _ctrlIcon0         = null;
        private static bool       _ctrlIconSearched  = false;

        // ---- Match Options panel ----
        private static MonoBehaviour _menuMgr          = null;
        private static FieldInfo     _inMatchOptsField  = null;
        private static bool          _menuMgrSearched   = false;
        private static bool          _lastInMatchOpts   = false;
        private static bool          _matchOptsHelpFired = false;
        private static float         _matchOptsSuppressRowUntil = 0f; // suppress row focus after opening
        private static float         _pendingMatchOptsHelpTime  = -1f; // fires nav instructions after "Match options." reads

        // Cached option values — updated every Poll(), read by PollInput()
        private static string _cachedWeather   = "";
        private static string _cachedQuarters  = "";
        private static string _cachedStadium   = "";
        private static string _cachedTimeOfDay = "";

        // Match Options row focus (Next AND Prev buttons per row)
        private static string    _lastMatchOptionsRow = "";
        private static Transform _rowWeather     = null, _rowWeatherPrev    = null;
        private static Transform _rowQuarters    = null, _rowQuartersPrev   = null;
        private static Transform _rowStadium     = null, _rowStadiumPrev    = null;
        private static Transform _rowTimeOfDay   = null, _rowTimeOfDayPrev  = null;
        private static bool      _rowsFound     = false;

        // ---- Team arrow buttons (Home/Away Team Next/Prev) ----
        private static Transform _homeTeamNext   = null;
        private static Transform _homeTeamPrev   = null;
        private static Transform _awayTeamNext   = null;
        private static Transform _awayTeamPrev   = null;
        private static bool      _teamArrowsSearched = false;
        private static string    _lastTeamArrow  = "";

        // UIButton.SelectedByController reflection
        private static Type      _uiButtonType       = null;
        private static FieldInfo _selByCtrlField     = null;
        private static bool      _uiBtnReflDone      = false;

        // ---- Maximum Passing ----
        private static bool       _maxPassSearched = false;
        private static GameObject _maxPassObj      = null;
        private static bool       _lastMaxPassing  = false;

        // =========================================================

        // =========================================================
        // PollInput — every Unity frame, before the 120ms throttle.
        // Uses GetKeyDown so quick Y/B taps are never missed.
        // =========================================================

        public static void PollInput()
        {
            if (!_wasActive) return;

            // Y (JoystickButton3) — open Match Options
            if (Input.GetKeyDown(KeyCode.JoystickButton3))
            {
                Plugin.Log.LogInfo($"[TS] Y pressed. _wasActive={_wasActive} _lastInMatchOpts={_lastInMatchOpts} weather='{_cachedWeather}'");
                if (!_lastInMatchOpts)
                {
                    _lastInMatchOpts     = true;
                    _lastMatchOptionsRow = "";
                    OpenMatchOptions();
                }
                return;
            }

            // B (JoystickButton1) — close Match Options
            if (Input.GetKeyDown(KeyCode.JoystickButton1) && _lastInMatchOpts)
            {
                _lastInMatchOpts     = false;
                _lastMatchOptionsRow = "";
            }
        }

        // =========================================================

        public static void Poll()
        {
            _screen = GameObject.Find("TeamSelect_Screen");
            bool nowActive = _screen != null && _screen.activeInHierarchy;

            if (!nowActive)
            {
                if (_wasActive) Reset();
                return;
            }

            // Read current values and cache for PollInput()
            string homeTeam  = ReadPath(_screen, "Bottom Letterbox/Home Team Name");
            string homeMasc  = ReadPath(_screen, "Bottom Letterbox/Home Team Mascot");
            string awayTeam  = ReadPath(_screen, "Bottom Letterbox/Away Team Name");
            string awayMasc  = ReadPath(_screen, "Bottom Letterbox/Away Team Mascot");
            string stadium   = ReadPath(_screen, "Match Options/Stadium");
            string weather   = ReadPath(_screen, "Match Options/Weather");
            string timeOfDay = ReadPath(_screen, "Match Options/Time Of Day");
            string quarters  = ReadPath(_screen, "Match Options/Time Per Quarter");
            _cachedWeather   = weather;
            _cachedQuarters  = quarters;
            _cachedStadium   = stadium;
            _cachedTimeOfDay = timeOfDay;

            // ---- First entry ----
            if (!_wasActive)
            {
                _wasActive = true;

                _lastHomeTeam  = homeTeam;  _lastHomeMasc  = homeMasc;
                _lastAwayTeam  = awayTeam;  _lastAwayMasc  = awayMasc;
                _lastStadium   = stadium;   _lastWeather   = weather;
                _lastTimeOfDay = timeOfDay; _lastQuarters  = quarters;

                // Pre-seed side focus: find the controller icon now so CheckSideFocus
                // doesn't fire on the very next poll and interrupt the entry message.
                _ctrlIcon0        = GameObject.Find("ControllerIcon_0");
                _ctrlIconSearched = true;
                _lastAnnouncedSide = (_ctrlIcon0 != null && _ctrlIcon0.transform.position.x < 0f)
                    ? "Away" : "Home";

                _lastInMatchOpts     = false;
                _lastMatchOptionsRow = "";
                _lastMaxPassing      = false;
                _matchOptsHelpFired  = false;
                _menuMgrSearched     = false;  _menuMgr         = null;
                _inMatchOptsField    = null;
                _maxPassSearched     = false;  _maxPassObj      = null;
                _uiBtnReflDone       = false;
                _rowsFound           = false;
                _rowWeather = _rowWeatherPrev = null;
                _rowQuarters = _rowQuartersPrev = null;
                _rowStadium = _rowStadiumPrev = null;
                _rowTimeOfDay = _rowTimeOfDayPrev = null;
                _teamArrowsSearched  = false;  _lastTeamArrow   = "";

                var sb = new StringBuilder("Team select.");
                if (!string.IsNullOrWhiteSpace(homeTeam))
                    sb.Append($" Home: {homeTeam} {homeMasc}.".TrimEnd(' ', '.') + ".");
                if (!string.IsNullOrWhiteSpace(awayTeam))
                    sb.Append($" Away: {awayTeam} {awayMasc}.".TrimEnd(' ', '.') + ".");
                sb.Append(" Press left or right trigger to scroll through teams.");
                sb.Append(" Press left or right on the D-pad to switch between home and away side.");
                sb.Append(" Press X to toggle Maximum Passing.");
                sb.Append(" Press Y for match options: weather, time of day, stadium, and quarter length.");
                sb.Append(" Press A to confirm.");
                SpeechManager.Speak(sb.ToString());
                return;
            }

            // ---- Lazy inits ----
            if (!_ctrlIconSearched)
            {
                _ctrlIconSearched = true;
                _ctrlIcon0 = GameObject.Find("ControllerIcon_0");
            }
            if (!_menuMgrSearched)
            {
                _menuMgrSearched = true;
                TryFindMenuMgr();
                if (_menuMgr != null)
                    _lastInMatchOpts = ReadInMatchOpts();
            }
            if (!_uiBtnReflDone)
            {
                _uiBtnReflDone = true;
                TryInitUIButtonReflection();
            }
            if (!_maxPassSearched)
            {
                _maxPassSearched = true;
                _maxPassObj = GameObject.Find("Maximum Passing Home_0")
                           ?? SearchChildByName(_screen, "Maximum Passing Home_0");
                if (_maxPassObj != null)
                    _lastMaxPassing = _maxPassObj.activeInHierarchy;
            }
            if (!_teamArrowsSearched)
            {
                _teamArrowsSearched = true;
                _homeTeamNext = _screen?.transform.Find("Home Team Next");
                _homeTeamPrev = _screen?.transform.Find("Home Team Prev");
                _awayTeamNext = _screen?.transform.Find("Away Team Next");
                _awayTeamPrev = _screen?.transform.Find("Away Team Prev");
            }

            // ---- Side focus ----
            CheckSideFocus(homeTeam, homeMasc, awayTeam, awayMasc);

            // ---- Match Options panel open/close (reflection fallback) ----
            // Primary detection is in PollInput() via GetKeyDown on Y/B.
            // This fallback catches edge cases where the panel opens another way.
            if (!_lastInMatchOpts)
                CheckMatchOptionsPanelFallback(stadium, weather, timeOfDay, quarters);

            // ---- Pending Match Options nav instructions ----
            // Fires ~1.2 s after "Match options." so the name reads fully first.
            if (_pendingMatchOptsHelpTime > 0f && Time.unscaledTime >= _pendingMatchOptsHelpTime)
            {
                _pendingMatchOptsHelpTime = -1f;
                SpeechManager.Speak(
                    "Press up and down to navigate options. " +
                    "Press left and right on the D-pad to select an arrow, " +
                    "then press A to change the option value.");
            }

            // ---- Match Options row focus (while panel is open) ----
            // Suppressed until after the nav instructions have played.
            if (_lastInMatchOpts && Time.unscaledTime >= _matchOptsSuppressRowUntil)
                CheckMatchOptionsRow(weather, quarters, stadium, timeOfDay);

            // ---- Team arrow button focus (main screen, outside Match Options) ----
            if (!_lastInMatchOpts)
                CheckTeamArrowFocus(homeTeam, awayTeam);

            // ---- Value changes (team + options) ----
            CheckValueChanges(homeTeam, homeMasc, awayTeam, awayMasc,
                              stadium, weather, timeOfDay, quarters);

            // ---- Maximum Passing ----
            CheckMaximumPassing();
        }

        // --------------------------------------------------------
        // Side focus
        //   ControllerIcon_0 world-X >= 0 → Home (right side)
        //                            < 0 → Away (left side)
        // --------------------------------------------------------
        private static void CheckSideFocus(
            string homeTeam, string homeMasc,
            string awayTeam, string awayMasc)
        {
            if (_ctrlIcon0 == null) return;

            float x    = _ctrlIcon0.transform.position.x;
            string side = x < 0f ? "Away" : "Home";

            if (side == _lastAnnouncedSide) return;
            _lastAnnouncedSide = side;

            string name = side == "Home" ? homeTeam : awayTeam;
            string masc = side == "Home" ? homeMasc : awayMasc;
            string full = $"{name} {masc}".Trim();

            SpeechManager.Speak(string.IsNullOrWhiteSpace(full)
                ? $"{side}."
                : $"{side}. {full}.");
        }

        // --------------------------------------------------------
        // Team arrow button focus (Home/Away Team Next/Prev)
        // Announces direction when cursor lands on an arrow button.
        // Suppressed if the team value is also changing this poll
        // (CheckValueChanges will speak the new team name instead).
        // --------------------------------------------------------
        private static void CheckTeamArrowFocus(string homeTeam, string awayTeam)
        {
            // If a team name is actively changing, let CheckValueChanges speak
            if (homeTeam != _lastHomeTeam || awayTeam != _lastAwayTeam) return;

            string focused = null;
            if      (IsRowFocused(_homeTeamNext)) focused = "HomeNext";
            else if (IsRowFocused(_homeTeamPrev)) focused = "HomePrev";
            else if (IsRowFocused(_awayTeamNext)) focused = "AwayNext";
            else if (IsRowFocused(_awayTeamPrev)) focused = "AwayPrev";

            if (focused == null || focused == _lastTeamArrow) return;
            _lastTeamArrow = focused;

            switch (focused)
            {
                case "HomeNext": SpeechManager.Speak("Home, next.");     break;
                case "HomePrev": SpeechManager.Speak("Home, previous."); break;
                case "AwayNext": SpeechManager.Speak("Away, next.");     break;
                case "AwayPrev": SpeechManager.Speak("Away, previous."); break;
            }
        }

        // --------------------------------------------------------
        // Match Options panel open / close (reflection / row-activity fallback)
        // Only fires when the panel is currently reported as closed; handles
        // cases where something other than Y opens it (edge case).
        // --------------------------------------------------------
        private static void CheckMatchOptionsPanelFallback(
            string stadium, string weather, string timeOfDay, string quarters)
        {
            bool nowIn = ReadInMatchOpts();
            if (!nowIn || nowIn == _lastInMatchOpts) return;
            _lastInMatchOpts     = nowIn;
            _lastMatchOptionsRow = "";
            OpenMatchOptions();
        }

        // Shared logic for when Match Options has just been opened.
        // Reads current values then appends navigation help so the player
        // hears everything in one uninterrupted announcement.
        private const float MATCH_OPTS_HELP_DELAY = 1.5f; // seconds after initial announcement before nav instructions

        private static void OpenMatchOptions()
        {
            FindMatchOptionsRows();
            _matchOptsHelpFired        = true;
            _pendingMatchOptsHelpTime  = Time.unscaledTime + MATCH_OPTS_HELP_DELAY;
            _matchOptsSuppressRowUntil = Time.unscaledTime + 4f + MATCH_OPTS_HELP_DELAY;

            // Announce screen name + whichever option is currently highlighted
            string focused = GetFocusedOptionName();
            SpeechManager.Speak(string.IsNullOrEmpty(focused)
                ? "Match options."
                : $"Match options. {focused}.");
        }

        // Returns the display name of the currently focused match options row, or "" if none.
        private static string GetFocusedOptionName()
        {
            if (IsRowFocused(_rowWeather)   || IsRowFocused(_rowWeatherPrev))   return "Weather";
            if (IsRowFocused(_rowQuarters)  || IsRowFocused(_rowQuartersPrev))  return "Quarters";
            if (IsRowFocused(_rowStadium)   || IsRowFocused(_rowStadiumPrev))   return "Stadium";
            if (IsRowFocused(_rowTimeOfDay) || IsRowFocused(_rowTimeOfDayPrev)) return "Time of day";
            return "";
        }

        // --------------------------------------------------------
        // Match Options row focus (up/down navigation)
        // Uses UIButton.SelectedByController to detect which row
        // the controller cursor is on.
        // --------------------------------------------------------
        private static void CheckMatchOptionsRow(
            string weather, string quarters, string stadium, string timeOfDay)
        {
            string focused = null;
            if      (IsRowFocused(_rowWeather)   || IsRowFocused(_rowWeatherPrev))   focused = "Weather";
            else if (IsRowFocused(_rowQuarters)  || IsRowFocused(_rowQuartersPrev))  focused = "Quarters";
            else if (IsRowFocused(_rowStadium)   || IsRowFocused(_rowStadiumPrev))   focused = "Stadium";
            else if (IsRowFocused(_rowTimeOfDay) || IsRowFocused(_rowTimeOfDayPrev)) focused = "TimeOfDay";

            if (focused == null || focused == _lastMatchOptionsRow) return;
            _lastMatchOptionsRow = focused;

            switch (focused)
            {
                case "Weather":   SpeechManager.Speak($"Weather: {weather}."); break;
                case "Quarters":  SpeechManager.Speak($"{quarters}."); break;
                case "Stadium":   SpeechManager.Speak($"Stadium: {stadium}."); break;
                case "TimeOfDay": SpeechManager.Speak($"Time of day: {timeOfDay}."); break;
            }
        }

        private static bool IsRowFocused(Transform t)
        {
            if (t == null || _uiButtonType == null) return false;
            var mb = t.GetComponent(_uiButtonType) as MonoBehaviour;
            if (mb == null) return false;

            // Strategy 1: SelectedByController
            if (_selByCtrlField != null)
            {
                try { if ((bool)_selByCtrlField.GetValue(mb)) return true; } catch { }
            }
            // Strategy 2: scale
            return t.localScale.x > 1.05f;
        }

        private static void FindMatchOptionsRows()
        {
            if (_rowsFound) return;
            _rowsFound    = true;
            _rowWeather      = _screen?.transform.Find("Match Options/Weather Next");
            _rowWeatherPrev  = _screen?.transform.Find("Match Options/Weather Prev");
            _rowQuarters     = _screen?.transform.Find("Match Options/Time Per Quarter Next");
            _rowQuartersPrev = _screen?.transform.Find("Match Options/Time Per Quarter Prev");
            _rowStadium      = _screen?.transform.Find("Match Options/Stadium Next");
            _rowStadiumPrev  = _screen?.transform.Find("Match Options/Stadium Prev");
            _rowTimeOfDay    = _screen?.transform.Find("Match Options/Time Of Day Next");
            _rowTimeOfDayPrev= _screen?.transform.Find("Match Options/Time Of Day Prev");
        }

        // --------------------------------------------------------
        // Value changes
        // --------------------------------------------------------
        private static void CheckValueChanges(
            string homeTeam, string homeMasc,
            string awayTeam, string awayMasc,
            string stadium, string weather, string timeOfDay, string quarters)
        {
            if (!string.IsNullOrWhiteSpace(homeTeam) && homeTeam != _lastHomeTeam)
            {
                _lastHomeTeam = homeTeam; _lastHomeMasc = homeMasc;
                SpeechManager.Speak($"Home: {homeTeam} {homeMasc}.".TrimEnd(' ', '.') + ".");
                return;
            }
            if (!string.IsNullOrWhiteSpace(awayTeam) && awayTeam != _lastAwayTeam)
            {
                _lastAwayTeam = awayTeam; _lastAwayMasc = awayMasc;
                SpeechManager.Speak($"Away: {awayTeam} {awayMasc}.".TrimEnd(' ', '.') + ".");
                return;
            }
            if (!string.IsNullOrWhiteSpace(stadium) && stadium != _lastStadium)
            {
                _lastStadium = stadium;
                SpeechManager.Speak($"Stadium: {stadium}.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(weather) && weather != _lastWeather)
            {
                _lastWeather = weather;
                SpeechManager.Speak($"Weather: {weather}.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(timeOfDay) && timeOfDay != _lastTimeOfDay)
            {
                _lastTimeOfDay = timeOfDay;
                SpeechManager.Speak($"Time of day: {timeOfDay}.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(quarters) && quarters != _lastQuarters)
            {
                _lastQuarters = quarters;
                SpeechManager.Speak(quarters + ".");
                return;
            }
        }

        // --------------------------------------------------------
        // Maximum Passing
        // --------------------------------------------------------
        private static void CheckMaximumPassing()
        {
            if (_maxPassObj == null) return;
            bool isOn = _maxPassObj.activeInHierarchy;
            if (isOn == _lastMaxPassing) return;
            _lastMaxPassing = isOn;
            SpeechManager.Speak(isOn
                ? "Maximum passing on. Manual pass aiming with the left stick."
                : "Maximum passing off.");
        }

        // --------------------------------------------------------
        // Reflection helpers
        // --------------------------------------------------------
        private static readonly BindingFlags _bf =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static void TryFindMenuMgr()
        {
            try
            {
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var fi = mb.GetType().GetField("inMatchOptions", _bf);
                    if (fi != null && fi.FieldType == typeof(bool))
                    {
                        _menuMgr         = mb;
                        _inMatchOptsField = fi;
                        return;
                    }
                }
            }
            catch { }
        }

        private static bool ReadInMatchOpts()
        {
            // Primary: reflection on inMatchOptions bool field
            if (_menuMgr != null && _inMatchOptsField != null)
            {
                try { return (bool)_inMatchOptsField.GetValue(_menuMgr); }
                catch { }
            }

            // Fallback: check whether the Match Options row buttons exist and are active.
            // FindMatchOptionsRows() must have been called first to populate them.
            // We search eagerly here if needed.
            if (!_rowsFound) FindMatchOptionsRows();
            if (_rowWeather != null && _rowWeather.gameObject.activeInHierarchy) return true;
            if (_rowStadium != null && _rowStadium.gameObject.activeInHierarchy) return true;
            if (_rowQuarters != null && _rowQuarters.gameObject.activeInHierarchy) return true;
            if (_rowTimeOfDay != null && _rowTimeOfDay.gameObject.activeInHierarchy) return true;
            return false;
        }

        private static void TryInitUIButtonReflection()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Assembly-CSharp") continue;
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name != "UIButton") continue;
                        _uiButtonType   = t;
                        _selByCtrlField = t.GetField("SelectedByController", _bf);
                        return;
                    }
                }
            }
            catch { }
        }

        // --------------------------------------------------------
        // Reset / helpers
        // --------------------------------------------------------
        private static void Reset()
        {
            _wasActive           = false;
            _screen              = null;
            _lastHomeTeam        = "";  _lastHomeMasc  = "";
            _lastAwayTeam        = "";  _lastAwayMasc  = "";
            _lastStadium         = "";  _lastWeather   = "";
            _lastTimeOfDay       = "";  _lastQuarters  = "";
            _lastAnnouncedSide   = "";
            _lastInMatchOpts     = false;
            _lastMatchOptionsRow = "";
            _lastMaxPassing      = false;
            _matchOptsHelpFired        = false;
            _matchOptsSuppressRowUntil = 0f;
            _pendingMatchOptsHelpTime  = -1f;
            _cachedWeather = _cachedQuarters = _cachedStadium = _cachedTimeOfDay = "";
            _ctrlIconSearched    = false;  _ctrlIcon0       = null;
            _menuMgrSearched     = false;  _menuMgr         = null;
            _inMatchOptsField    = null;
            _maxPassSearched     = false;  _maxPassObj      = null;
            _rowsFound           = false;
            _rowWeather = _rowWeatherPrev = null;
            _rowQuarters = _rowQuartersPrev = null;
            _rowStadium = _rowStadiumPrev = null;
            _rowTimeOfDay = _rowTimeOfDayPrev = null;
            _teamArrowsSearched  = false;
            _homeTeamNext = _homeTeamPrev = _awayTeamNext = _awayTeamPrev = null;
            _lastTeamArrow       = "";
        }

        private static string ReadPath(GameObject root, string path)
        {
            Transform t = root.transform.Find(path);
            if (t == null) return "";
            var tmp = t.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return "";
            return Clean(tmp.text);
        }

        private static GameObject SearchChildByName(GameObject root, string childName)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject.name == childName)
                    return child.gameObject;
            }
            return null;
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            bool inTag = false;
            foreach (char ch in text)
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
