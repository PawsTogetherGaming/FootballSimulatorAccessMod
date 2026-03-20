using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Monitors the Load Season screen. Announces save slot info when the screen
    /// opens and whenever the user cycles to a different slot (LB/RB buttons).
    /// </summary>
    public static class LoadSeasonReader
    {
        private static bool _wasActive = false;

        private static TextMeshProUGUI _saveSlotText;
        private static TextMeshProUGUI _teamName;
        private static TextMeshProUGUI _teamMascot;
        private static TextMeshProUGUI _recordNums;
        private static TextMeshProUGUI _seasonDate;

        private static string _lastSlot = "";

        public static void Poll()
        {
            var screen = GameObject.Find("LoadSeason_Screen");
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
            _saveSlotText = FindTMP(screen, "Save Slot Text");
            _teamName     = FindTMP(screen, "Season Team Name");
            _teamMascot   = FindTMP(screen, "Season Team Mascot");
            _recordNums   = FindTMP(screen, "Season Record Nums");
            _seasonDate   = FindTMP(screen, "Season Team Date");

            _lastSlot = Clean(_saveSlotText);
        }

        private static void SpeakFullState()
        {
            string slot   = Clean(_saveSlotText);
            string team   = $"{Clean(_teamName)} {Clean(_teamMascot)}".Trim();
            string record = Clean(_recordNums);
            string date   = Clean(_seasonDate);

            string msg = "Load Season.";
            if (!string.IsNullOrWhiteSpace(slot))   msg += $" {slot}.";
            if (!string.IsNullOrWhiteSpace(team))   msg += $" {team}.";
            if (!string.IsNullOrWhiteSpace(record)) msg += $" Record: {record}.";
            if (!string.IsNullOrWhiteSpace(date))   msg += $" {date}.";
            msg += " Left and right bumper to change slot. A to load.";

            SpeechManager.Speak(msg);
        }

        private static void CheckChanges()
        {
            string slot = Clean(_saveSlotText);
            if (slot != _lastSlot && !string.IsNullOrWhiteSpace(slot))
            {
                _lastSlot = slot;
                // Re-announce everything for the new slot
                SpeakFullState();
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
