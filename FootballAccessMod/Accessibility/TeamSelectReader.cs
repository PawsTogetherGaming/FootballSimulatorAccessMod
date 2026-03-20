using System.Text;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Handles the TeamSelect_Screen (Exhibition / Practice / Multiplayer team picker).
    /// Announces the screen on entry with both teams and match options.
    /// Polls for team/option changes and speaks them as they change.
    /// The arrow buttons (Home Team Next/Prev, Away Team Next/Prev, option
    /// Next/Prev) all have empty UIButton labels, so changes are detected by
    /// watching the text values rather than button selection.
    /// </summary>
    public static class TeamSelectReader
    {
        private static bool   _wasActive      = false;
        private static string _lastHomeTeam   = "";
        private static string _lastAwayTeam   = "";
        private static string _lastStadium    = "";
        private static string _lastWeather    = "";
        private static string _lastTimeOfDay  = "";
        private static string _lastQuarters   = "";

        public static void Poll()
        {
            var screen = GameObject.Find("TeamSelect_Screen");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                if (_wasActive) Reset();
                return;
            }

            string homeTeam  = ReadPath(screen, "Bottom Letterbox/Home Team Name");
            string homeMasc  = ReadPath(screen, "Bottom Letterbox/Home Team Mascot");
            string awayTeam  = ReadPath(screen, "Bottom Letterbox/Away Team Name");
            string awayMasc  = ReadPath(screen, "Bottom Letterbox/Away Team Mascot");
            string stadium   = ReadPath(screen, "Match Options/Stadium");
            string weather   = ReadPath(screen, "Match Options/Weather");
            string timeOfDay = ReadPath(screen, "Match Options/Time Of Day");
            string quarters  = ReadPath(screen, "Match Options/Time Per Quarter");

            if (!_wasActive)
            {
                _wasActive = true;
                _lastHomeTeam  = homeTeam;
                _lastAwayTeam  = awayTeam;
                _lastStadium   = stadium;
                _lastWeather   = weather;
                _lastTimeOfDay = timeOfDay;
                _lastQuarters  = quarters;

                var sb = new StringBuilder();
                sb.Append("Team select.");
                if (!string.IsNullOrWhiteSpace(homeTeam)) sb.Append($" Home: {homeTeam} {homeMasc}.");
                if (!string.IsNullOrWhiteSpace(awayTeam)) sb.Append($" Away: {awayTeam} {awayMasc}.");
                if (!string.IsNullOrWhiteSpace(stadium))  sb.Append($" Stadium: {stadium}.");
                if (!string.IsNullOrWhiteSpace(weather))  sb.Append($" Weather: {weather}.");
                if (!string.IsNullOrWhiteSpace(timeOfDay))sb.Append($" Time of day: {timeOfDay}.");
                if (!string.IsNullOrWhiteSpace(quarters)) sb.Append($" {quarters}.");
                SpeechManager.Speak(sb.ToString());
                return;
            }

            // Poll for changes
            if (!string.IsNullOrWhiteSpace(homeTeam) && homeTeam != _lastHomeTeam)
            {
                _lastHomeTeam = homeTeam;
                string full = string.IsNullOrWhiteSpace(homeMasc) ? homeTeam : $"{homeTeam} {homeMasc}";
                SpeechManager.Speak($"Home: {full}.");
            }
            if (!string.IsNullOrWhiteSpace(awayTeam) && awayTeam != _lastAwayTeam)
            {
                _lastAwayTeam = awayTeam;
                string full = string.IsNullOrWhiteSpace(awayMasc) ? awayTeam : $"{awayTeam} {awayMasc}";
                SpeechManager.Speak($"Away: {full}.");
            }
            if (!string.IsNullOrWhiteSpace(stadium) && stadium != _lastStadium)
            {
                _lastStadium = stadium;
                SpeechManager.Speak($"Stadium: {stadium}.");
            }
            if (!string.IsNullOrWhiteSpace(weather) && weather != _lastWeather)
            {
                _lastWeather = weather;
                SpeechManager.Speak($"Weather: {weather}.");
            }
            if (!string.IsNullOrWhiteSpace(timeOfDay) && timeOfDay != _lastTimeOfDay)
            {
                _lastTimeOfDay = timeOfDay;
                SpeechManager.Speak($"Time of day: {timeOfDay}.");
            }
            if (!string.IsNullOrWhiteSpace(quarters) && quarters != _lastQuarters)
            {
                _lastQuarters = quarters;
                SpeechManager.Speak(quarters + ".");
            }
        }

        private static void Reset()
        {
            _wasActive     = false;
            _lastHomeTeam  = "";
            _lastAwayTeam  = "";
            _lastStadium   = "";
            _lastWeather   = "";
            _lastTimeOfDay = "";
            _lastQuarters  = "";
        }

        private static string ReadPath(GameObject root, string path)
        {
            Transform t = root.transform.Find(path);
            if (t == null) return "";
            var tmp = t.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return "";
            return Clean(tmp.text);
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
