using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Franchise mode accessibility: drafting, trading, standings, roster management.
    /// Reads the game's CSV data files to supplement screen reading.
    /// </summary>
    public static class FranchiseReader
    {
        // Cache of player data from roster.csv for announcement enrichment
        private static Dictionary<string, PlayerData> _rosterCache = new();
        private static bool _dataLoaded = false;

        private static readonly string RosterPath =
            "C:/Program Files (x86)/Steam/steamapps/common/Football Simulator/" +
            "Football Simulator_Data/StreamingAssets/Mods~/Default/roster.csv";

        public static void Initialize()
        {
            LoadRosterData();
        }

        // ---- Roster CSV Loading ----

        private static void LoadRosterData()
        {
            _rosterCache.Clear();
            if (!File.Exists(RosterPath))
            {
                Plugin.Log.LogWarning("[FranchiseReader] roster.csv not found.");
                return;
            }

            try
            {
                using var reader = new StreamReader(RosterPath);
                string? header = reader.ReadLine(); // Skip header
                while (!reader.EndOfStream)
                {
                    string? line = reader.ReadLine();
                    if (line == null) continue;
                    var p = PlayerData.FromCsvLine(line);
                    if (p != null) _rosterCache[p.FullName] = p;
                }
                Plugin.Log.LogInfo($"[FranchiseReader] Loaded {_rosterCache.Count} players from roster.");
                _dataLoaded = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[FranchiseReader] Failed to load roster: {ex.Message}");
            }
        }

        // ---- Announcement Helpers ----

        /// <summary>Announce a draft pick with full details.</summary>
        public static void AnnounceDraftPick(string playerName, string position, int overall,
            string team, int round, int pick)
        {
            string announcement = $"Round {round}, pick {pick}. {team} selects {playerName}. " +
                                  $"{position}, overall rating {overall}.";

            if (_rosterCache.TryGetValue(playerName, out var data))
            {
                announcement += $" Age {data.Age}. " + GetTopStats(data, position);
            }

            SpeechManager.Speak(announcement);
        }

        /// <summary>Announce a trade offer or completed trade.</summary>
        public static void AnnounceTrade(string givingTeam, string receivingTeam,
            string givingAssets, string receivingAssets, bool accepted)
        {
            string status = accepted ? "Trade accepted" : "Trade offer";
            SpeechManager.Speak(
                $"{status}. {givingTeam} gives: {givingAssets}. " +
                $"{receivingTeam} gives: {receivingAssets}.");
        }

        /// <summary>Announce a player card (e.g., in roster management).</summary>
        public static void AnnouncePlayerCard(string playerName, string position,
            int overall, string team)
        {
            string announcement = $"{playerName}. {position}. Overall {overall}. {team}.";

            if (_rosterCache.TryGetValue(playerName, out var data))
                announcement += " " + GetTopStats(data, position);

            SpeechManager.Speak(announcement);
        }

        /// <summary>Read standings row.</summary>
        public static void AnnounceStandingsRow(string team, int wins, int losses,
            int rank, string division)
        {
            SpeechManager.Speak(
                $"{division} rank {rank}. {team}. {wins} wins, {losses} losses.");
        }

        private static string GetTopStats(PlayerData p, string position)
        {
            return position switch
            {
                "QB" => $"Throw power {p.ThrowPower}, accuracy short {p.ThrowAccShort}, mid {p.ThrowAccMid}, deep {p.ThrowAccDeep}.",
                "WR" or "TE" => $"Speed {p.Speed}, catching {p.Catching}, route running short {p.RouteRunShort}.",
                "RB" => $"Speed {p.Speed}, agility {p.Agility}, carrying {p.Carrying}.",
                "CB" or "S" => $"Speed {p.Speed}, man coverage {p.ManCoverage}, zone coverage {p.ZoneCoverage}.",
                "LB" => $"Speed {p.Speed}, tackle {p.Tackle}, zone coverage {p.ZoneCoverage}.",
                "DL" or "DE" or "DT" => $"Strength {p.Strength}, block shed {p.BlockShedding}, power moves {p.PowerMoves}.",
                "OL" or "OT" or "OG" or "C" => $"Strength {p.Strength}, pass block {p.PassBlock}, run block {p.RunBlock}.",
                _ => $"Speed {p.Speed}, strength {p.Strength}."
            };
        }

        // ---- Harmony Patches ----
        // Generic patch: detect when franchise-related panels become active.
        // NOTE: [HarmonyPatch] attribute removed — patching GameObject.SetActive fires on every
        // object in the engine and is too dangerous. SceneScanner handles screen detection via
        // text change monitoring instead. This class is kept for manual invocation only.
        private static class GameObject_SetActive_Patch
        {
            static void Postfix(GameObject __instance, bool value)
            {
                if (!value || __instance == null) return;

                string name = __instance.name.ToLower();

                if (name.Contains("draft") && (name.Contains("panel") || name.Contains("screen")))
                {
                    SpeechManager.Speak("Draft screen. Use up and down to navigate draft picks.");
                }
                else if (name.Contains("trade") && (name.Contains("panel") || name.Contains("screen")))
                {
                    SpeechManager.Speak("Trade screen. Browse players and press A to initiate a trade.");
                }
                else if (name.Contains("roster") && (name.Contains("panel") || name.Contains("screen")))
                {
                    SpeechManager.Speak("Roster screen.");
                }
                else if (name.Contains("standing") && (name.Contains("panel") || name.Contains("screen")))
                {
                    SpeechManager.Speak("Standings screen.");
                }
                else if (name.Contains("schedule") && (name.Contains("panel") || name.Contains("screen")))
                {
                    SpeechManager.Speak("Schedule screen.");
                }
                else if (name.Contains("franchise") && (name.Contains("menu") || name.Contains("hub")))
                {
                    SpeechManager.Speak("Franchise menu. Draft, trade, roster, schedule.");
                }
            }
        }
    }

    // ---- Data Model for CSV roster ----

    internal class PlayerData
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string FullName => $"{FirstName} {LastName}";
        public string Team { get; set; } = "";
        public int Overall { get; set; }
        public string Position { get; set; } = "";
        public int Age { get; set; }
        public int Speed { get; set; }
        public int Agility { get; set; }
        public int Strength { get; set; }
        public int ThrowPower { get; set; }
        public int Catching { get; set; }
        public int ManCoverage { get; set; }
        public int ZoneCoverage { get; set; }
        public int Tackle { get; set; }
        public int Carrying { get; set; }
        public int ThrowAccShort { get; set; }
        public int ThrowAccMid { get; set; }
        public int ThrowAccDeep { get; set; }
        public int RouteRunShort { get; set; }
        public int BlockShedding { get; set; }
        public int PowerMoves { get; set; }
        public int PassBlock { get; set; }
        public int RunBlock { get; set; }

        // Columns (0-indexed) from the roster.csv header:
        // 0:firstName,1:lastName,2:name,3:team,4:team_id,5:overall_rating,
        // 6:skin_color,7:hair_color,8:jersey_num,9:position,10:height,11:weight,12:age,
        // 13:agility,14:speed,15:strength,16:throwPower,17:catching,18:pursuit,
        // 19:manCoverage,20:jumping,21:kickPower,22:acceleration,23:carrying,
        // 24:kickAccuracy,25:runBlock,26:passBlock,27:tackle,28:breakTackle,
        // 29:durability,30:stamina,31:trucking,32:stiffArm,33:spinMove,34:jukeMove,
        // 35:breakSack,36:leadBlock,37:impactBlocking,38:powerMoves,39:finesseMoves,
        // 40:blockShedding,41:playRecognition,42:zoneCoverage,43:spectacularCatch,
        // 44:catchInTraffic,45:shortRouteRunning,46:mediumRouteRunning,47:deepRouteRunning,
        // 48:hitPower,49:throwAccShort,50:throwAccMid,51:throwAccDeep,52:throwOnRun

        public static PlayerData? FromCsvLine(string line)
        {
            var cols = line.Split(',');
            if (cols.Length < 53) return null;
            if (!int.TryParse(cols[5], out int overall)) return null;

            return new PlayerData
            {
                FirstName    = cols[0].Trim(),
                LastName     = cols[1].Trim(),
                Team         = cols[3].Trim(),
                Overall      = overall,
                Position     = cols[9].Trim(),
                Age          = Parse(cols[12]),
                Agility      = Parse(cols[13]),
                Speed        = Parse(cols[14]),
                Strength     = Parse(cols[15]),
                ThrowPower   = Parse(cols[16]),
                Catching     = Parse(cols[17]),
                ManCoverage  = Parse(cols[19]),
                Carrying     = Parse(cols[23]),
                RunBlock     = Parse(cols[25]),
                PassBlock    = Parse(cols[26]),
                Tackle       = Parse(cols[27]),
                PowerMoves   = Parse(cols[38]),
                BlockShedding = Parse(cols[40]),
                ZoneCoverage = Parse(cols[42]),
                RouteRunShort = Parse(cols[45]),
                ThrowAccShort = Parse(cols[49]),
                ThrowAccMid  = Parse(cols[50]),
                ThrowAccDeep = Parse(cols[51]),
            };
        }

        private static int Parse(string s) =>
            int.TryParse(s?.Trim(), out int v) ? v : 0;
    }
}
