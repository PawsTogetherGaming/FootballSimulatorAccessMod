using System;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Monitors the Exhibition (TeamSelect_Screen) UI for value changes and
    /// announces them via NVDA. Because this screen uses no UIButton selection
    /// state, we watch TMP text values and speak when they change.
    /// </summary>
    public static class ExhibitionReader
    {
        private static bool _wasActive = false;

        // Cached TMP components
        private static TextMeshProUGUI _homeTeamName;
        private static TextMeshProUGUI _homeTeamMascot;
        private static TextMeshProUGUI _awayTeamName;
        private static TextMeshProUGUI _awayTeamMascot;
        private static TextMeshProUGUI _stadium;
        private static TextMeshProUGUI _weather;
        private static TextMeshProUGUI _timePerQuarter;
        private static TextMeshProUGUI _timeOfDay;

        // Last spoken values
        private static string _lastHomeTeam    = "";
        private static string _lastAwayTeam    = "";
        private static string _lastStadium     = "";
        private static string _lastWeather     = "";
        private static string _lastTimePerQ    = "";
        private static string _lastTimeOfDay   = "";

        public static void Poll()
        {
            var screen = GameObject.Find("TeamSelect_Screen");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                _wasActive = false;
                return;
            }

            if (!_wasActive)
            {
                // Just entered exhibition — cache refs and read full state
                _wasActive = true;
                CacheTMPs(screen);
                SpeakFullState();
                return;
            }

            // Screen was already active — check for value changes
            CheckChanges();
        }

        private static void CacheTMPs(GameObject screen)
        {
            _homeTeamName    = FindTMP(screen, "Home Team Name");
            _homeTeamMascot  = FindTMP(screen, "Home Team Mascot");
            _awayTeamName    = FindTMP(screen, "Away Team Name");
            _awayTeamMascot  = FindTMP(screen, "Away Team Mascot");
            _stadium         = FindTMP(screen, "Stadium");
            _weather         = FindTMP(screen, "Weather");
            _timePerQuarter  = FindTMP(screen, "Time Per Quarter");
            _timeOfDay       = FindTMP(screen, "Time Of Day");

            // Seed last-known values so we don't double-speak on entry
            _lastHomeTeam  = Clean(_homeTeamName);
            _lastAwayTeam  = Clean(_awayTeamName);
            _lastStadium   = Clean(_stadium);
            _lastWeather   = Clean(_weather);
            _lastTimePerQ  = Clean(_timePerQuarter);
            _lastTimeOfDay = Clean(_timeOfDay);
        }

        private static void SpeakFullState()
        {
            string home   = TeamLabel(_homeTeamName, _homeTeamMascot);
            string away   = TeamLabel(_awayTeamName, _awayTeamMascot);
            string stad   = Clean(_stadium);
            string weath  = Clean(_weather);
            string tpq    = Clean(_timePerQuarter);
            string tod    = Clean(_timeOfDay);

            string msg = $"Exhibition match setup. " +
                         $"Home team: {home}. " +
                         $"Away team: {away}. " +
                         $"Stadium: {stad}. Weather: {weath}. {tpq}. Time of day: {tod}. " +
                         "Use left and right bumpers to switch between home and away. " +
                         "Use left and right to cycle teams. " +
                         "Press Y for match options. Press A to confirm and play.";
            SpeechManager.Speak(msg);
        }

        private static void CheckChanges()
        {
            string homeTeam = Clean(_homeTeamName);
            string homeMasc = Clean(_homeTeamMascot);
            string awayTeam = Clean(_awayTeamName);
            string awayMasc = Clean(_awayTeamMascot);
            string stadium  = Clean(_stadium);
            string weather  = Clean(_weather);
            string tpq      = Clean(_timePerQuarter);
            string tod      = Clean(_timeOfDay);

            string fullHome = $"{homeTeam} {homeMasc}".Trim();
            string fullAway = $"{awayTeam} {awayMasc}".Trim();

            if (fullHome != _lastHomeTeam && !string.IsNullOrWhiteSpace(fullHome))
            {
                _lastHomeTeam = fullHome;
                SpeechManager.Speak($"Home team: {fullHome}");
                return;
            }
            if (fullAway != _lastAwayTeam && !string.IsNullOrWhiteSpace(fullAway))
            {
                _lastAwayTeam = fullAway;
                SpeechManager.Speak($"Away team: {fullAway}");
                return;
            }
            if (stadium != _lastStadium && !string.IsNullOrWhiteSpace(stadium))
            {
                _lastStadium = stadium;
                SpeechManager.Speak($"Stadium: {stadium}");
                return;
            }
            if (weather != _lastWeather && !string.IsNullOrWhiteSpace(weather))
            {
                _lastWeather = weather;
                SpeechManager.Speak($"Weather: {weather}");
                return;
            }
            if (tpq != _lastTimePerQ && !string.IsNullOrWhiteSpace(tpq))
            {
                _lastTimePerQ = tpq;
                SpeechManager.Speak(tpq);
                return;
            }
            if (tod != _lastTimeOfDay && !string.IsNullOrWhiteSpace(tod))
            {
                _lastTimeOfDay = tod;
                SpeechManager.Speak($"Time of day: {tod}");
                return;
            }
        }

        // ---- Helpers ----

        private static string TeamLabel(TextMeshProUGUI nameComp, TextMeshProUGUI mascotComp)
        {
            return $"{Clean(nameComp)} {Clean(mascotComp)}".Trim();
        }

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            // Strip TMP rich-text tags and collapse whitespace
            string raw = comp.text ?? "";
            // Remove anything inside < >
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char c in raw)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return System.Text.RegularExpressions.Regex.Replace(
                sb.ToString().Trim(), @"\s+", " ");
        }

        private static TextMeshProUGUI FindTMP(GameObject root, string childName)
        {
            // Breadth-first search for a child with matching name
            foreach (TextMeshProUGUI tmp in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp.gameObject.name == childName)
                    return tmp;
            }
            return null;
        }
    }
}
