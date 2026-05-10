using BepInEx.Configuration;

namespace FootballAccessMod
{
    /// <summary>
    /// Central settings for the Football Simulator Accessibility Mod.
    /// All values are persisted to BepInEx/config/FootballAccessMod.cfg.
    /// Use the in-game settings menu (F2 or hold Back 2 seconds) to change them.
    /// </summary>
    public static class ModSettings
    {
        // ---- In-game HUD auto-announcements ----
        public static ConfigEntry<bool> ReadDDChanges;       // down & distance on change
        public static ConfigEntry<bool> ReadQuarterChanges;  // quarter change
        public static ConfigEntry<bool> ReadScoreChanges;    // score change

        // ---- Play Call Screen ----
        public static ConfigEntry<bool> ReadTooltips;        // "Select formation", DD on open, etc.
        public static ConfigEntry<bool> ReadSelectedPlay;    // play name when called / CPU play

        // ---- Receivers / Passing ----
        public static ConfigEntry<bool> ReadRoutesAuto;      // auto-announce routes at PreSnap
        public static ConfigEntry<bool> ReadReceiverChanges; // "A open!" during QB drop-back
        public static ConfigEntry<bool> ReadPlayArt;         // play art route descriptions

        // ---- Defense ----
        public static ConfigEntry<bool> ReadDefenderSwitch;  // player-switch announcement
        public static ConfigEntry<bool> ReadDefenderState;   // behavior state changes
        public static ConfigEntry<bool> ReadBallInAir;       // ball-in-air direction updates
        public static ConfigEntry<int>  HeatSeekPower;       // 0=off … 10=auto/max

        // ---- Offense ----
        public static ConfigEntry<bool> ReadEvasionPrompts;  // "Left! L1 stiff arm." etc.
        public static ConfigEntry<int>  OffensiveAssistPower; // 0=off … 10=full takeover

        // ---- CPU Difficulty ----
        // 0 = normal game balance, higher = easier CPU defense.
        // Persists across exhibition, season, and any mid-season match.
        public static ConfigEntry<int>  CPUDefenseDifficulty;

        public static void Init(ConfigFile config)
        {
            const string HUD        = "In-Game HUD";
            const string PLAYCALL   = "Play Call";
            const string RECEIVERS  = "Receivers";
            const string DEFENSE    = "Defense";
            const string OFFENSE    = "Offense";
            const string DIFFICULTY = "CPU Difficulty";

            // HUD auto-changes
            ReadDDChanges = config.Bind(HUD, "ReadDDChanges", true,
                "Automatically announce down and distance when it changes.");
            ReadQuarterChanges = config.Bind(HUD, "ReadQuarterChanges", true,
                "Automatically announce when the quarter changes.");
            ReadScoreChanges = config.Bind(HUD, "ReadScoreChanges", true,
                "Automatically announce when the score changes.");

            // Play call
            ReadTooltips = config.Bind(PLAYCALL, "ReadTooltips", true,
                "Read formation-nav tooltips (down/distance on open, 'Select formation', etc.).");
            ReadSelectedPlay = config.Bind(PLAYCALL, "ReadSelectedPlay", true,
                "Announce the play name when you call it and when the CPU calls theirs.");

            // Receivers
            ReadRoutesAuto = config.Bind(RECEIVERS, "ReadRoutesAuto", true,
                "Auto-announce receiver routes at PreSnap. L1 always repeats them regardless.");
            ReadReceiverChanges = config.Bind(RECEIVERS, "ReadReceiverChanges", true,
                "Announce when a receiver becomes open during the QB drop-back (e.g. 'A open!').");
            ReadPlayArt = config.Bind(RECEIVERS, "ReadPlayArt", true,
                "Read route descriptions from the play art diagram at PreSnap.");

            // Defense
            ReadDefenderSwitch = config.Bind(DEFENSE, "ReadDefenderSwitch", true,
                "Announce position, name, and field location when you switch defenders.");
            ReadDefenderState = config.Bind(DEFENSE, "ReadDefenderState", true,
                "Announce defender behavior state changes (Blitzing, In coverage, On it!, etc.).");
            ReadBallInAir = config.Bind(DEFENSE, "ReadBallInAir", true,
                "Announce ball direction and distance while it is in the air.");
            HeatSeekPower = config.Bind(DEFENSE, "HeatSeekPower", 5,
                new ConfigDescription(
                    "R1 heat-seek power. 0=off, 1-3=chase target, 4-6=intercept prediction, " +
                    "7-9=intercept+lead, 10=max (auto on every pass and within 6m on runs).",
                    new AcceptableValueRange<int>(0, 10)));

            // Offense
            ReadEvasionPrompts = config.Bind(OFFENSE, "ReadEvasionPrompts", true,
                "Announce evasion suggestions when a defender closes on the ball carrier " +
                "(e.g. 'Left! L1 stiff arm.').");
            OffensiveAssistPower = config.Bind(OFFENSE, "OffensiveAssistPower", 5,
                new ConfigDescription(
                    "Run assist power. 0=off, 1-3=occasional hints, 4-6=frequent hints, " +
                    "7-9=cut timing calls, 10=full AI takeover (steers carrier into open lane).",
                    new AcceptableValueRange<int>(0, 10)));

            // CPU Difficulty (both sides — defense AND offense)
            CPUDefenseDifficulty = config.Bind(DIFFICULTY, "CPUDefenseDifficulty", 0,
                new ConfigDescription(
                    "How much weaker the CPU plays, on both defense and offense. " +
                    "0=normal game balance. " +
                    "On defense: reduces dive-tackle distance, narrows defender attack angle, slows " +
                    "defender pursuit and grab tackles, drops most CPU interception attempts. " +
                    "On offense: slows CPU players (so plays develop later), perturbs CPU QB throw " +
                    "accuracy, and boosts your AI defenders' chance to pick off CPU passes. " +
                    "10 makes the CPU largely passive. Applies in exhibition and season modes, " +
                    "including mid-season matches.",
                    new AcceptableValueRange<int>(0, 10)));
        }
    }
}
