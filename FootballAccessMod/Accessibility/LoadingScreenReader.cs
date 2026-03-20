using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Announces loading-screen tips and the Early Access notice.
    /// </summary>
    public static class LoadingScreenReader
    {
        private static string _lastScene = "";

        public static void Poll()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (!scene.StartsWith("LoadingScreen_")) { _lastScene = ""; return; }
            if (scene == _lastScene) return;
            _lastScene = scene;

            if (scene == "LoadingScreen_EarlyAcessInfoPage")
            {
                SpeechManager.Speak("Early Access notice. Press A to accept.");
                return;
            }

            // Read tip text if present
            string tip = TryGetTip();
            if (!string.IsNullOrWhiteSpace(tip))
                SpeechManager.Speak(tip);
        }

        private static string TryGetTip()
        {
            // Football/season loading screens
            var tipGo = GameObject.Find("Loading Tips");
            if (tipGo != null)
            {
                var tmp = tipGo.GetComponent<TextMeshProUGUI>();
                if (tmp != null) { string s = Clean(tmp); if (!string.IsNullOrWhiteSpace(s)) return s; }
            }

            // Main-menu loading screen ("C_Foreground/Text (TMP)")
            var fg = GameObject.Find("C_Foreground");
            if (fg != null)
            {
                foreach (var tmp in fg.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    string s = Clean(tmp);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }

            return "";
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
    }
}
