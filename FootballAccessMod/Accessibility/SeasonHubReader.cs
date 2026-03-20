using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Handles the "New Season Mode" scene (season hub).
    /// Announces which screen is active on entry, and reads out standings
    /// in full (since that screen has no UIButtons to navigate).
    /// TeamStats and PlayerStats navigation is covered by the global UIButton poller.
    /// </summary>
    public static class SeasonHubReader
    {
        private static bool _wasInHub = false;
        private static string _lastScreen = "";
        private static string _lastConference = "";
        private static string _lastPlayerTeam = "";
        private static string _lastDepthChartTeam = "";
        private static string _lastDepthChartPos = "";

        public static void Poll()
        {
            bool inHub = SceneManager.GetActiveScene().name == "New Season Mode";

            if (!inHub)
            {
                if (_wasInHub)
                {
                    _wasInHub = false;
                    _lastScreen = "";
                    _lastConference = "";
                    _lastPlayerTeam = "";
                    _lastDepthChartTeam = "";
                    _lastDepthChartPos = "";
                }
                return;
            }

            if (!_wasInHub)
            {
                _wasInHub = true;
                SpeechManager.Speak("Season hub.");
            }

            CheckTeamStats();
            CheckPlayerStats();
            CheckStandings();
            CheckSchedule();
            CheckGameplan();
            CheckStats();
            CheckDepthChart();
        }

        // ---- Team Stats ----

        private static void CheckTeamStats()
        {
            var screen = GameObject.Find("TeamStats_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            if (_lastScreen == "TeamStats_Screen") return;
            _lastScreen = "TeamStats_Screen";

            SpeechManager.Speak(
                "Team stats screen. " +
                "Up and down to select a team. " +
                "Left and right to change stat column.");
        }

        // ---- Player Stats ----

        private static void CheckPlayerStats()
        {
            var screen = GameObject.Find("PlayerStats_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            if (_lastScreen != "PlayerStats_Screen")
            {
                _lastScreen = "PlayerStats_Screen";
                var teamTmp = FindTMP(screen, "Team Name Text");
                string team = Clean(teamTmp);
                _lastPlayerTeam = team;
                SpeechManager.Speak(
                    $"Player stats screen. {team}. " +
                    "Up and down to select a player. " +
                    "Left and right to change stat column.");
                return;
            }

            // Announce team change (e.g. if position filter changes label)
            var teamTmpPoll = FindTMP(screen, "Team Name Text");
            string teamNow = Clean(teamTmpPoll);
            if (!string.IsNullOrWhiteSpace(teamNow) && teamNow != _lastPlayerTeam)
            {
                _lastPlayerTeam = teamNow;
                SpeechManager.Speak(teamNow);
            }
        }

        // ---- Standings ----

        private static void CheckStandings()
        {
            var screen = GameObject.Find("Standings_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            var confTmp = FindTMP(screen, "Conference-Text");
            string conf = Clean(confTmp);

            if (_lastScreen != "Standings_Screen")
            {
                _lastScreen = "Standings_Screen";
                _lastConference = conf;
                SpeakStandings(screen, conf);
                return;
            }

            // Detect conference toggle (ParadeButton2 "Toggle Conference")
            if (!string.IsNullOrWhiteSpace(conf) && conf != _lastConference)
            {
                _lastConference = conf;
                SpeakStandings(screen, conf);
            }
        }

        private static void SpeakStandings(GameObject screen, string conference)
        {
            var sb = new StringBuilder();
            sb.Append($"{conference} conference standings. ");

            foreach (string div in new[] { "East", "West", "North", "South" })
            {
                Transform divObj = screen.transform.Find($"Conference/{div}");
                if (divObj == null) continue;

                sb.Append($"{div}: ");
                // Buttons are named "Button", "Button (1)", "Button (2)", "Button (3)"
                int rank = 1;
                foreach (Transform child in divObj)
                {
                    if (!child.gameObject.name.StartsWith("Button")) continue;

                    string team = CleanTransform(child, "TeamName Text");
                    string rec  = CleanTransform(child, "Record Text");

                    if (!string.IsNullOrWhiteSpace(team))
                        sb.Append($"{rank}. {team} {rec}. ");
                    rank++;
                }
            }

            SpeechManager.Speak(sb.ToString());
        }

        // ---- Schedule ----

        private static void CheckSchedule()
        {
            var screen = GameObject.Find("Schedule_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            if (_lastScreen == "Schedule_Screen") return;
            _lastScreen = "Schedule_Screen";

            var teamTmp = FindTMP(screen, "CurrentTeam");
            string team = Clean(teamTmp);
            string record = CleanTransform(screen.transform, "Record-Text/HomeTeamRecord");

            string msg = "Schedule screen.";
            if (!string.IsNullOrWhiteSpace(team))   msg += $" {team}.";
            if (!string.IsNullOrWhiteSpace(record)) msg += $" Record: {record}.";
            msg += " Up and down to browse weeks. Left and right bumper to change team.";
            SpeechManager.Speak(msg);
        }

        // ---- Gameplan ----

        private static void CheckGameplan()
        {
            var screen = GameObject.Find("Gameplan_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            if (_lastScreen == "Gameplan_Screen") return;
            _lastScreen = "Gameplan_Screen";

            SpeechManager.Speak("Game plan screen. Depth chart or playbook.");
        }

        // ---- Stats ----

        private static void CheckStats()
        {
            var screen = GameObject.Find("Stats_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            if (_lastScreen == "Stats_Screen") return;
            _lastScreen = "Stats_Screen";

            SpeechManager.Speak("Stats screen. Team stats or player stats.");
        }

        // ---- Depth Chart ----

        private static void CheckDepthChart()
        {
            var screen = GameObject.Find("DepthChart_Screen");
            if (screen == null || !screen.activeInHierarchy) return;

            var teamTmp = FindTMP(screen, "Team Name Text");
            string team = Clean(teamTmp);

            Transform posTransform = screen.transform.Find("MainBar/Position/Text (TMP)");
            var posTmp = posTransform != null ? posTransform.GetComponent<TextMeshProUGUI>() : null;
            string pos = Clean(posTmp);

            if (_lastScreen != "DepthChart_Screen")
            {
                _lastScreen = "DepthChart_Screen";
                _lastDepthChartTeam = team;
                _lastDepthChartPos = pos;

                string msg = "Depth chart screen.";
                if (!string.IsNullOrWhiteSpace(team)) msg += $" {team}.";
                if (!string.IsNullOrWhiteSpace(pos))  msg += $" Position: {pos}.";
                msg += " Up and down to select player. Left and right to change stat column.";
                SpeechManager.Speak(msg);
                return;
            }

            if (!string.IsNullOrWhiteSpace(team) && team != _lastDepthChartTeam)
            {
                _lastDepthChartTeam = team;
                SpeechManager.Speak(team);
            }
            if (!string.IsNullOrWhiteSpace(pos) && pos != _lastDepthChartPos)
            {
                _lastDepthChartPos = pos;
                SpeechManager.Speak($"Position: {pos}");
            }
        }

        // ---- Helpers ----

        private static TextMeshProUGUI FindTMP(GameObject root, string name)
        {
            foreach (TextMeshProUGUI t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t.gameObject.name == name) return t;
            return null;
        }

        private static string CleanTransform(Transform parent, string childName)
        {
            Transform t = parent.Find(childName);
            if (t == null) return "";
            var tmp = t.GetComponent<TextMeshProUGUI>();
            return tmp == null ? "" : Clean(tmp);
        }

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            var sb = new StringBuilder();
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
    }
}
