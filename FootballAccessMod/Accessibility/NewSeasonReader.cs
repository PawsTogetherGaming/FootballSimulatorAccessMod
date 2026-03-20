using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Monitors the New Season screen for value changes (team, settings)
    /// and announces them. Works the same way as ExhibitionReader.
    /// </summary>
    public static class NewSeasonReader
    {
        private static bool _wasActive = false;

        private static TextMeshProUGUI _teamName;
        private static TextMeshProUGUI _teamMascot;
        private static TextMeshProUGUI _timePerQuarter;

        private static string _lastTeam        = "";
        private static string _lastTimePerQ    = "";

        public static void Poll()
        {
            var screen = GameObject.Find("NewSeason_Screen");
            bool nowActive = screen != null && screen.activeInHierarchy;

            if (!nowActive)
            {
                _wasActive = false;
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                CacheTMPs(screen);
                SpeakFullState();
                return;
            }

            CheckChanges();
        }

        private static void CacheTMPs(GameObject screen)
        {
            _teamName      = FindTMP(screen, "Season Team Name");
            _teamMascot    = FindTMP(screen, "Season Team Mascot");
            _timePerQuarter = FindTMP(screen, "Time Per Quarter");

            _lastTeam    = $"{Clean(_teamName)} {Clean(_teamMascot)}".Trim();
            _lastTimePerQ = Clean(_timePerQuarter);
        }

        private static void SpeakFullState()
        {
            string team = $"{Clean(_teamName)} {Clean(_teamMascot)}".Trim();
            string tpq  = Clean(_timePerQuarter);

            SpeechManager.Speak(
                $"New Season setup. " +
                $"Season team: {team}. " +
                $"{tpq}. " +
                "Use left and right to change team. Create to start.");
        }

        private static void CheckChanges()
        {
            string team = $"{Clean(_teamName)} {Clean(_teamMascot)}".Trim();
            string tpq  = Clean(_timePerQuarter);

            if (team != _lastTeam && !string.IsNullOrWhiteSpace(team))
            {
                _lastTeam = team;
                SpeechManager.Speak($"Season team: {team}");
                return;
            }
            if (tpq != _lastTimePerQ && !string.IsNullOrWhiteSpace(tpq))
            {
                _lastTimePerQ = tpq;
                SpeechManager.Speak(tpq);
            }
        }

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char ch in (comp.text ?? ""))
            {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            return System.Text.RegularExpressions.Regex.Replace(
                sb.ToString().Trim(), @"\s+", " ");
        }

        private static TextMeshProUGUI FindTMP(GameObject root, string name)
        {
            foreach (TextMeshProUGUI t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t.gameObject.name == name) return t;
            return null;
        }
    }
}
