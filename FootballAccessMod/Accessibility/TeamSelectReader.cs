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

        public static void Poll()
        {
            _screen = GameObject.Find("TeamSelect_Screen");
            bool nowActive = _screen != null && _screen.activeInHierarchy;

            if (!nowActive)
            {
                if (_wasActive) Reset();
                return;
            }

            // Read current values
            string homeTeam  = ReadPath(_screen, "Bottom Letterbox/Home Team Name");
            string homeMasc  = ReadPath(_screen, "Bottom Letterbox/Home Team Mascot");
            string awayTeam  = ReadPath(_screen, "Bottom Letterbox/Away Team Name");
            string awayMasc  = ReadPath(_screen, "Bottom Letterbox/Away Team Mascot");
            string stadium   = ReadPath(_screen, "Match Options/Stadium");
            string weather   = ReadPath(_screen, "Match Options/Weather");
            string timeOfDay = ReadPath(_screen, "Match Options/Time Of Day");
            string quarters  = ReadPath(_screen, "Match Options/Time Per Quarter");

            // ---- First entry ----
            if (!_wasActive)
            {
                _wasActive = true;

                _lastHomeTeam  = homeTeam;  _lastHomeMasc  = homeMasc;
                _lastAwayTeam  = awayTeam;  _lastAwayMasc  = awayMasc;
                _lastStadium   = stadium;   _lastWeather   = weather;
                _lastTimeOfDay = timeOfDay; _lastQuarters  = quarters;

                _lastAnnouncedSide   = "";
                _lastInMatchOpts     = false;
                _lastMatchOptionsRow = "";
                _lastMaxPassing      = false;
                _ctrlIconSearched    = false;  _ctrlIcon0       = null;
                _menuMgrSearched     = false;  _menuMgr         = null;
                _inMatchOptsField    = null;
                _maxPassSearched     = false;  _maxPassObj      = null;
                _uiBtnReflDone       = false;
                _rowsFound           = false;
                _teamArrowsSearched  = false;  _lastTeamArrow   = "";

                var sb = new StringBuilder("Team select.");
                if (!string.IsNullOrWhiteSpace(homeTeam))
                    sb.Append($" Home: {homeTeam} {homeMasc}.".TrimEnd(' ', '.') + ".");
                if (!string.IsNullOrWhiteSpace(awayTeam))
                    sb.Append($" Away: {awayTeam} {awayMasc}.".TrimEnd(' ', '.') + ".");
                sb.Append(" Press left or right to switch sides.");
                sb.Append(" Press Y for match options. Press A to confirm.");
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

            // ---- Match Options panel open/close ----
            CheckMatchOptionsPanel(stadium, weather, timeOfDay, quarters);

            // ---- Match Options row focus (while panel is open) ----
            if (_lastInMatchOpts)
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
        // Match Options panel open / close
        // --------------------------------------------------------
        private static void CheckMatchOptionsPanel(
            string stadium, string weather, string timeOfDay, string quarters)
        {
            bool nowIn = ReadInMatchOpts();
            if (nowIn == _lastInMatchOpts) return;
            _lastInMatchOpts     = nowIn;
            _lastMatchOptionsRow = "";

            if (!nowIn) return;   // closed — nothing to say

            // Just opened: find row buttons and read full state
            FindMatchOptionsRows();

            var sb = new StringBuilder("Match options.");
            if (!string.IsNullOrWhiteSpace(weather))   sb.Append($" Weather: {weather}.");
            if (!string.IsNullOrWhiteSpace(quarters))  sb.Append($" {quarters}.");
            if (!string.IsNullOrWhiteSpace(stadium))   sb.Append($" Stadium: {stadium}.");
            if (!string.IsNullOrWhiteSpace(timeOfDay)) sb.Append($" Time of day: {timeOfDay}.");
            sb.Append(" Use up and down to navigate rows, left and right to change.");
            SpeechManager.Speak(sb.ToString());
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
            if (_menuMgr == null || _inMatchOptsField == null) return false;
            try { return (bool)_inMatchOptsField.GetValue(_menuMgr); }
            catch { return false; }
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
