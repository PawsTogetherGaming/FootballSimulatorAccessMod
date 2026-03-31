using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// In-game accessibility: polls TMP scoreboard text for down/distance,
    /// score, quarter changes, play-call screen state, and play names via Reflection.
    /// Called from Plugin's PlayerLoop (AccessibilityLoop) every ~120ms.
    /// </summary>
    public static class GameplayReader
    {
        // --- In-game state tracking ---
        private static bool _wasInGame = false;

        private static string _lastDownDist  = "";
        private static string _lastQuarter   = "";
        private static string _lastHomeScore = "";
        private static string _lastAwayScore = "";

        // Grace period: for the first N polls after game entry, re-seed all change
        // detectors without announcing — TMP values may not be ready on frame 0.
        private static int  _gracePolls          = 0;
        private static bool _postGraceAnnounced  = false; // fires one-time state readout after grace

        // --- Play-call screen state ---
        private static bool   _playCallOpen  = false;
        private static string _lastFormation = "";

        // --- Cached TMP refs (populated once on entry) ---
        private static TextMeshProUGUI _tmpDownDist;
        private static TextMeshProUGUI _tmpQuarter;
        private static TextMeshProUGUI _tmpHomeScore;
        private static TextMeshProUGUI _tmpAwayScore;
        private static TextMeshProUGUI _tmpHomeName;
        private static TextMeshProUGUI _tmpAwayName;

        // --- OffenseBox / DefenseBox refs (found on first play-call open) ---
        private static TextMeshProUGUI _tmpOffenseBoxTitle;
        private static TextMeshProUGUI _tmpFormationName;
        private static TextMeshProUGUI _tmpDefenseBoxTitle;
        private static TextMeshProUGUI _tmpDefFormationName;

        // GameObjects cached so we can check activeInHierarchy
        private static GameObject _offenseBox = null;
        private static GameObject _defenseBox = null;

        // --- Playbook / play-name tracking via Reflection ---
        private static bool        _playbookRefsDone    = false;
        private static object      _playbookInst        = null; // Football.Playbook MonoBehaviour

        // Offense
        private static FieldInfo   _fldDepth            = null; // Playbook.OffensivePlaybookDepth
        private static FieldInfo   _fldPage             = null; // Playbook.OffensivePlaybookPage
        private static FieldInfo   _fldPlayList         = null; // Playbook.offensivePlayList
        private static FieldInfo   _fldOffButtons       = null; // Playbook.offensiveButtons (List<GO>)
        private static string      _lastPlayName        = "";
        private static int         _lastPlayPage        = -1;
        private static int         _lastPlayCol         = -1;

        // Defense
        private static FieldInfo   _fldDefDepth         = null; // Playbook.DefensivePlaybookDepth
        private static FieldInfo   _fldDefPage          = null; // Playbook.DefensivePlaybookPage
        private static FieldInfo   _fldDefPlayList      = null; // Playbook.defensivePlayList
        private static FieldInfo   _fldDefButtons       = null; // Playbook.DefensiveButtons (List<GO>)
        private static string      _lastDefPlayName     = "";
        private static int         _lastDefPlayPage     = -1;
        private static int         _lastDefPlayCol      = -1;
        private static bool        _defPlayCallOpen     = false;
        private static string      _lastDefFormation    = "";

        // Depth transition tracking (detect 1→0 to re-announce after a play runs)
        private static int         _lastOffDepth        = 0;
        private static int         _lastDefDepth        = 0;

        // Button press tracking for play confirmation (X=col0, A=col1, Y=col2)
        private static bool        _xWasDown            = false;
        private static bool        _aWasDown            = false;
        private static bool        _yWasDown            = false;

        // One-time diagnostic: log play item type so we know if ToString() gives real names
        private static bool        _playItemLogged      = false;

        // Confirmed play tracking via oc.CurrentPlay / dc.CurrentPlay (100% accurate)
        private static object      _ocInst              = null;
        private static object      _dcInst              = null;
        private static FieldInfo   _fldOcCurrentPlay    = null;
        private static FieldInfo   _fldDcCurrentPlay    = null;
        private static string      _lastCalledPlay      = "";
        private static string      _lastCalledDefPlay   = "";

        // Audibles tracking
        private static object      _audiblesInst        = null;
        private static FieldInfo   _fldAudiblesVisible  = null;
        private static bool        _audiblesWasOpen     = false;

        // Status readout (Select/Back button = JoystickButton6)
        private static object      _matchInst           = null;
        private static FieldInfo   _fldTimeLeft         = null;  // FootballMatch.TimeLeftInSeconds
        private static FieldInfo   _fldHomeTO           = null;  // FootballMatch.HomeTimeOutCounter
        private static FieldInfo   _fldAwayTO           = null;  // FootballMatch.AwayTimeOutCounter
        private static bool        _statusBtnWasDown    = false;
        private static bool        _tKeyWasDown         = false;
        private static bool        _sKeyWasDown         = false;
        private static bool        _dKeyWasDown         = false;

        // LB/RB bumper press tracking — announce formation name on each press
        private static bool        _lbWasDown           = false;
        private static bool        _rbWasDown           = false;

        // =========================================================
        // PollInput — every Unity frame, before the 120ms throttle.
        // Announces the selected play the instant X/A/Y is pressed
        // at play-selection depth, so the 120ms Poll() never misses it.
        // =========================================================

        public static void PollInput()
        {
            if (!_wasInGame) return;
            if (!_playCallOpen && !_defPlayCallOpen) return;
            if (!_playbookRefsDone || _playbookInst == null) return;

            // Determine which side is open and what depth we're at
            bool isDefense = _defPlayCallOpen && !_playCallOpen;
            int depth = 0;
            int page  = 0;
            try
            {
                var fldD = isDefense ? _fldDefDepth : _fldDepth;
                var fldP = isDefense ? _fldDefPage  : _fldPage;
                depth = (int)(fldD?.GetValue(_playbookInst) ?? 0);
                page  = (int)(fldP?.GetValue(_playbookInst) ?? 0);
            }
            catch { return; }

            if (depth != 1) return;   // only at play-selection depth

            // X = col 0, A = col 1, Y = col 2
            int col = -1;
            if (Input.GetKeyDown(KeyCode.JoystickButton2)) col = 0; // X
            else if (Input.GetKeyDown(KeyCode.JoystickButton0)) col = 1; // A
            else if (Input.GetKeyDown(KeyCode.JoystickButton3)) col = 2; // Y
            if (col < 0) return;

            if (!(ModSettings.ReadSelectedPlay?.Value ?? true)) return;

            var fldList = isDefense ? _fldDefPlayList : _fldPlayList;
            string playName = GetPlayAt(fldList, page, col);
            if (string.IsNullOrWhiteSpace(playName)) return;

            string side = isDefense ? "defense" : "offense";
            SpeechManager.Speak(BuildPlayAnnouncement(playName, side));
            Plugin.Log.LogInfo($"[GameplayReader] PollInput: play selected '{playName}' col={col} side={side}");

            // Suppress the 120ms Poll() from re-announcing the same play
            if (isDefense) _lastCalledDefPlay = playName;
            else           _lastCalledPlay    = playName;
        }

        // =========================================================

        public static void Poll()
        {
            // Gameplay is active when GameplayMenu_Canvas is present and active,
            // OR when "Football Match UI" exists (covers practice mode pre-snap
            // where GameplayMenu_Canvas is inactive but the play-call box is open).
            var canvas  = GameObject.Find("GameplayMenu_Canvas");
            var matchUI = GameObject.Find("Football Match UI");
            bool inGame = (canvas  != null && canvas.activeInHierarchy)
                       || (matchUI != null && matchUI.activeInHierarchy);

            if (!inGame)
            {
                if (_wasInGame) ResetState();
                return;
            }

            if (!_wasInGame)
            {
                _wasInGame = true;
                CacheRefs();
                AnnounceEntry();
                return;
            }

            // Grace period: re-seed without announcing so stale values don't fire
            if (_gracePolls > 0)
            {
                _gracePolls--;
                _lastDownDist  = Clean(_tmpDownDist);
                _lastQuarter   = Clean(_tmpQuarter);
                _lastHomeScore = Clean(_tmpHomeScore);
                _lastAwayScore = Clean(_tmpAwayScore);
                return;
            }

            // One-time post-grace quarter readout — DD is not announced here,
            // it will fire naturally via CheckDownDistance when it first appears after kickoff.
            if (!_postGraceAnnounced)
            {
                _postGraceAnnounced = true;
                string q = FormatQuarter(_lastQuarter);
                if (!string.IsNullOrWhiteSpace(q))
                    SpeechManager.Speak(q);
            }

            CheckDownDistance();
            CheckQuarter();
            CheckScore();
            CheckPlayCall();
            // Announce play names only while the play call screen is open (navigation/confirmation).
            // Suppress after the screen closes so we don't announce plays mid-execution.
            if (_playCallOpen || _defPlayCallOpen)
                CheckCurrentPlay();
            CheckAudibles();

            // LB / RB — announce current formation name on each press while either
            // play-call box is visible (covers both depth 0 formation nav AND depth 1
            // play selection, and audibles).
            bool anyBoxVisible = (_offenseBox != null && _offenseBox.activeInHierarchy)
                              || (_defenseBox != null && _defenseBox.activeInHierarchy)
                              || _audiblesWasOpen;
            if (anyBoxVisible)
            {
                bool lb = Input.GetKey(KeyCode.JoystickButton4);
                bool rb = Input.GetKey(KeyCode.JoystickButton5);
                if ((lb && !_lbWasDown) || (rb && !_rbWasDown))
                {
                    if (_audiblesWasOpen)
                        AnnounceAudiblesNow();    // re-read current audible plays
                    else
                        AnnounceCurrentFormation();
                }
                _lbWasDown = lb;
                _rbWasDown = rb;
            }

            // Right stick click / R3 (JoystickButton9) — full status readout
            bool statusBtn = Input.GetKey(KeyCode.JoystickButton9);
            if (statusBtn && !_statusBtnWasDown) ReadOutStatus();
            _statusBtnWasDown = statusBtn;

            // Keyboard shortcuts (keyboard not used during gameplay)
            bool tKey = Input.GetKey(KeyCode.A);
            if (tKey && !_tKeyWasDown) AnnounceTimeRemaining();
            _tKeyWasDown = tKey;

            bool sKey = Input.GetKey(KeyCode.S);
            if (sKey && !_sKeyWasDown) AnnounceScore();
            _sKeyWasDown = sKey;

            bool dKey = Input.GetKey(KeyCode.D);
            if (dKey && !_dKeyWasDown && !string.IsNullOrWhiteSpace(_lastDownDist))
                SpeechManager.Speak(FormatDownDist(_lastDownDist));
            _dKeyWasDown = dKey;
        }

        private static void ResetState()
        {
            _wasInGame        = false;
            _playCallOpen     = false;
            _defPlayCallOpen  = false;
            _lastFormation    = "";
            _lastDefFormation = "";
            _gracePolls          = 0;
            _postGraceAnnounced  = false;
            _lastPlayName     = "";
            _lastPlayPage     = -1;
            _lastPlayCol      = -1;
            _lastDefPlayName  = "";
            _lastDefPlayPage  = -1;
            _lastDefPlayCol   = -1;
            _lastOffDepth     = 0;
            _lastDefDepth     = 0;
            _xWasDown         = false;
            _aWasDown         = false;
            _yWasDown         = false;
            _playItemLogged   = false;
            _ocInst           = null;
            _dcInst           = null;
            _fldOcCurrentPlay = null;
            _fldDcCurrentPlay = null;
            _lastCalledPlay   = "";
            _lastCalledDefPlay= "";
            _audiblesInst       = null;
            _fldAudiblesVisible = null;
            _audiblesWasOpen    = false;
            _matchInst          = null;
            _statusBtnWasDown   = false;
            _tKeyWasDown        = false;
            _sKeyWasDown        = false;
            _dKeyWasDown        = false;
            _lbWasDown          = false;
            _rbWasDown          = false;
            _playbookRefsDone = false;
            _playbookInst     = null;
            _tmpDownDist      = _tmpQuarter   = null;
            _tmpHomeScore     = _tmpAwayScore = null;
            _tmpHomeName      = _tmpAwayName  = null;
            _tmpOffenseBoxTitle  = _tmpFormationName    = null;
            _tmpDefenseBoxTitle  = _tmpDefFormationName = null;
            _offenseBox          = null;
            _defenseBox          = null;
        }

        private static void CacheRefs()
        {
            _tmpDownDist  = FindTMPByName("DownAndDistance");
            _tmpQuarter   = FindTMPByName("Quarter");
            _tmpHomeScore = FindTMPByName("HomeTeamScore");
            _tmpAwayScore = FindTMPByName("AwayTeamScore");
            _tmpHomeName  = FindTMPByName("HomeTeamName");
            _tmpAwayName  = FindTMPByName("AwayTeamName");

            // Seed initial values
            _lastDownDist  = Clean(_tmpDownDist);
            _lastQuarter   = Clean(_tmpQuarter);
            _lastHomeScore = Clean(_tmpHomeScore);
            _lastAwayScore = Clean(_tmpAwayScore);

            // Allow ~1.8 seconds for TMP values to fully load before change-detecting
            _gracePolls = 15;

            Plugin.Log.LogInfo($"[GameplayReader] Entered game. DD={_lastDownDist} Q={_lastQuarter}");
        }

        private static void AnnounceEntry()
        {
            // Only announce teams here — quarter and DD are announced after the grace
            // period confirms TMP has settled to real values (avoids stale quarter readout).
            string home = Clean(_tmpHomeName);
            string away = Clean(_tmpAwayName);
            SpeechManager.Speak($"{home} vs {away}.");
        }

        // ---- Change detectors ----

        private static void CheckDownDistance()
        {
            string dd = Clean(_tmpDownDist);
            if (string.IsNullOrWhiteSpace(dd) || dd == _lastDownDist) return;
            _lastDownDist = dd;
            if (ModSettings.ReadDDChanges?.Value ?? true)
                SpeechManager.Speak(FormatDownDist(dd));
        }

        private static void CheckQuarter()
        {
            string q = Clean(_tmpQuarter);
            if (string.IsNullOrWhiteSpace(q) || q == _lastQuarter) return;
            bool hadValue = !string.IsNullOrWhiteSpace(_lastQuarter);
            _lastQuarter = q;
            if (hadValue && (ModSettings.ReadQuarterChanges?.Value ?? true))
                SpeechManager.Speak(FormatQuarter(q));
        }

        private static void CheckScore()
        {
            string home = Clean(_tmpHomeScore);
            string away = Clean(_tmpAwayScore);
            if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away)) return;
            if (home == _lastHomeScore && away == _lastAwayScore) return;

            _lastHomeScore = home;
            _lastAwayScore = away;

            if (ModSettings.ReadScoreChanges?.Value ?? true)
            {
                string homeName = Clean(_tmpHomeName);
                string awayName = Clean(_tmpAwayName);
                SpeechManager.Speak($"Score: {homeName} {home}, {awayName} {away}");
            }
        }

        private static void CheckPlayCall()
        {
            // One-time ref cache for OffenseBox + DefenseBox
            if (_offenseBox == null)
            {
                _offenseBox = GameObject.Find("OffenseBox");
                if (_offenseBox != null)
                {
                    _tmpOffenseBoxTitle = FindTMPInChildren(_offenseBox, "Title");
                    _tmpFormationName   = FindTMPInChildren(_offenseBox, "FormationName");
                }
            }
            if (_defenseBox == null)
            {
                _defenseBox = GameObject.Find("DefenseBox");
                if (_defenseBox != null)
                {
                    _tmpDefenseBoxTitle  = FindTMPInChildren(_defenseBox, "Title");
                    _tmpDefFormationName = FindTMPInChildren(_defenseBox, "FormationName");
                }
            }

            // Only read title text when the box is actually visible —
            // TMP components retain their last value when inactive, which causes
            // false positives mid-play.
            string offTitle = (_offenseBox != null && _offenseBox.activeInHierarchy)
                ? Clean(_tmpOffenseBoxTitle) : "";
            string defTitle = (_defenseBox != null && _defenseBox.activeInHierarchy)
                ? Clean(_tmpDefenseBoxTitle) : "";

            // Lazily cache Playbook + OC + DC Reflection refs
            if (!_playbookRefsDone) CachePlaybookRefs();

            bool offWasOpen = _playCallOpen;
            CheckOneSide(
                title:         offTitle,
                playKeyword:   "Offense Play",
                formKeyword:   "Offense Formation",
                formationTmp:  _tmpFormationName,
                fldDepth:      _fldDepth,
                fldPage:       _fldPage,
                fldPlayList:   _fldPlayList,
                fldButtons:    _fldOffButtons,
                isOpen:        ref _playCallOpen,
                lastFormation: ref _lastFormation,
                lastPlayName:  ref _lastPlayName,
                lastPlayPage:  ref _lastPlayPage,
                lastPlayCol:   ref _lastPlayCol,
                lastDepth:     ref _lastOffDepth,
                sideLabel:     "offense");

            // If offense just opened this poll (kickoff scenario), skip defense
            // to avoid double-announcing. Defense will catch up next poll.
            bool offJustOpened = !offWasOpen && _playCallOpen;
            if (offJustOpened) return;

            CheckOneSide(
                title:         defTitle,
                playKeyword:   "Defense Play",
                formKeyword:   "Defense Formation",
                formationTmp:  _tmpDefFormationName,
                fldDepth:      _fldDefDepth,
                fldPage:       _fldDefPage,
                fldPlayList:   _fldDefPlayList,
                fldButtons:    _fldDefButtons,
                isOpen:        ref _defPlayCallOpen,
                lastFormation: ref _lastDefFormation,
                lastPlayName:  ref _lastDefPlayName,
                lastPlayPage:  ref _lastDefPlayPage,
                lastPlayCol:   ref _lastDefPlayCol,
                lastDepth:     ref _lastDefDepth,
                sideLabel:     "defense");
        }

        private static void CheckOneSide(
            string title, string playKeyword, string formKeyword,
            TextMeshProUGUI formationTmp,
            FieldInfo fldDepth, FieldInfo fldPage, FieldInfo fldPlayList, FieldInfo fldButtons,
            ref bool isOpen, ref string lastFormation,
            ref string lastPlayName, ref int lastPlayPage, ref int lastPlayCol,
            ref int lastDepth,
            string sideLabel)
        {
            // Match any "Play" title that isn't the formation title.
            // No longer requiring sideLabel so kickoff/special-teams titles also match.
            bool open = !string.IsNullOrWhiteSpace(title)
                     && !string.Equals(title, formKeyword, StringComparison.OrdinalIgnoreCase)
                     && title.IndexOf("Play", StringComparison.OrdinalIgnoreCase) >= 0;

            if (open)
                Plugin.Log.LogInfo($"[GameplayReader] {sideLabel} play screen open. Title=\"{title}\"");

            if (!open)
            {
                isOpen       = false;
                lastPlayName = "";
                lastPlayPage = -1;
                lastPlayCol  = -1;
                lastDepth    = 0;
                return;
            }

            int depth = 0, page = 0;
            if (_playbookInst != null)
            {
                try { depth = (int)(fldDepth?.GetValue(_playbookInst) ?? 0); } catch { }
                try { page  = (int)(fldPage?.GetValue(_playbookInst)  ?? 0); } catch { }
            }

            // Detect selected column (0/1/2) from button scale
            int col = GetSelectedColumn(fldButtons);

            if (!isOpen)
            {
                isOpen    = true;
                lastDepth = depth;
                // Seed button states immediately so any held button from menu nav doesn't misfire
                SeedButtonStates();
                string formation = Clean(formationTmp);
                lastFormation = formation;
                string dd = FormatDownDist(_lastDownDist);

                if (depth == 1)
                {
                    // Already in play selection — announce all plays on this page
                    lastPlayPage = page;
                    lastPlayCol  = col;
                    string pageDesc = GetPageNamesLabeled(fldPlayList, page);
                    lastPlayName = GetPlayAt(fldPlayList, page, col);
                    bool readTooltips = ModSettings.ReadTooltips?.Value ?? true;
                    if (readTooltips)
                        SpeechManager.Speak(string.IsNullOrWhiteSpace(pageDesc)
                            ? $"{dd}. {formation}. {lastPlayName}."
                            : $"{dd}. {formation}. {pageDesc}.");
                }
                else
                {
                    // Formation nav — keep position at -1 so first depth=1 poll always announces
                    lastPlayPage = -1;
                    lastPlayCol  = -1;
                    lastPlayName = "";
                    bool readTooltips = ModSettings.ReadTooltips?.Value ?? true;
                    if (readTooltips)
                        SpeechManager.Speak($"{dd}. Select formation. {formation}.");
                }
                return;
            }

            // Already open: formation nav (depth 0)
            if (depth == 0)
            {
                bool returnedFromPlay = (lastDepth == 1);
                // Save before clearing — needed to announce which play was just selected
                string savedPlayName = lastPlayName;
                int    savedPlayPage = lastPlayPage;
                lastPlayPage = -1;
                lastPlayCol  = -1;
                lastPlayName = "";
                lastDepth    = 0;

                string formation = Clean(formationTmp);
                if (returnedFromPlay)
                {
                    // Depth 1→0 is the play confirmation moment. Try to announce the play name.
                    string announced = "";

                    // Try 1: button detection — button may still be held on this poll
                    int confirmCol = ConsumePlayButtonPress();
                    if (confirmCol >= 0 && savedPlayPage >= 0)
                    {
                        string pn = GetPlayAt(fldPlayList, savedPlayPage, confirmCol);
                        if (!string.IsNullOrWhiteSpace(pn)) announced = pn;
                    }

                    // Try 2: last highlighted play from column detection
                    if (string.IsNullOrWhiteSpace(announced) && !string.IsNullOrWhiteSpace(savedPlayName))
                        announced = savedPlayName;

                    // Try 3: read oc/dc.CurrentPlay right now — it may be set at selection time
                    if (string.IsNullOrWhiteSpace(announced))
                    {
                        try
                        {
                            string ocPlay = sideLabel == "offense"
                                ? (_fldOcCurrentPlay?.GetValue(_ocInst)?.ToString() ?? "")
                                : (_fldDcCurrentPlay?.GetValue(_dcInst)?.ToString() ?? "");
                            if (!string.IsNullOrWhiteSpace(ocPlay)) announced = ocPlay;
                        }
                        catch { }
                    }

                    Plugin.Log.LogInfo($"[GameplayReader] returnedFromPlay ({sideLabel}): savedPlay=\"{savedPlayName}\" confirmCol={confirmCol} announced=\"{announced}\"");

                    bool readSelectedPlay = ModSettings.ReadSelectedPlay?.Value ?? true;
                    if (!string.IsNullOrWhiteSpace(announced) && readSelectedPlay)
                    {
                        SpeechManager.Speak(BuildPlayAnnouncement(announced, sideLabel));
                        // Suppress CheckCurrentPlay from re-announcing the same play at snap time
                        if (sideLabel == "offense") _lastCalledPlay    = announced;
                        else                         _lastCalledDefPlay = announced;
                    }

                    // Re-announce formation + DD so the user knows they're back at formation nav
                    lastFormation = formation;
                    string dd = FormatDownDist(_lastDownDist);
                    bool readTooltips2 = ModSettings.ReadTooltips?.Value ?? true;
                    if (readTooltips2)
                        SpeechManager.Speak($"{dd}. Select formation. {formation}.");
                    // Seed so stale button state from the play press doesn't misfire next time
                    SeedButtonStates();
                    return;
                }
                if (!string.IsNullOrWhiteSpace(formation) && formation != lastFormation)
                {
                    lastFormation = formation;
                    bool readTooltips3 = ModSettings.ReadTooltips?.Value ?? true;
                    if (readTooltips3)
                        SpeechManager.Speak($"Formation: {formation}");
                }
                return;
            }

            // depth 1: play selection — announce play when page or column changes
            lastDepth = 1;
            bool pageChanged = page != lastPlayPage;
            // Only track column changes when we can reliably detect the column (col != -1)
            bool colChanged  = col >= 0 && col != lastPlayCol && lastPlayPage == page;

            if (pageChanged || colChanged)
            {
                lastPlayPage = page;
                if (col >= 0) lastPlayCol = col;

                if (pageChanged)
                {
                    int total = GetTotalPages(fldPlayList);
                    string pageDesc = GetPageNamesLabeled(fldPlayList, page);
                    string pageLabel = total > 1 ? $"Page {page + 1} of {total}. " : "";
                    SpeechManager.Speak(string.IsNullOrWhiteSpace(pageDesc)
                        ? $"Page {page + 1}"
                        : $"{pageLabel}{pageDesc}.");
                }
                else if (colChanged)
                {
                    // Column reliably detected — announce just the highlighted play name
                    string currentPlay = GetPlayAt(fldPlayList, page, col);
                    lastPlayName = currentPlay;
                    SpeechManager.Speak(currentPlay);
                }
            }

            // Keep button state current so SeedButtonStates works correctly on next open.
            // Do NOT announce here — CheckCurrentPlay() handles confirmation via oc/dc.CurrentPlay.
            ConsumePlayButtonPress();
        }

        // ---- Confirmed play detection ----

        private static void CheckCurrentPlay()
        {
            bool readSelectedPlay = ModSettings.ReadSelectedPlay?.Value ?? true;
            if (!readSelectedPlay) return;

            if (_fldOcCurrentPlay != null && _ocInst != null)
            {
                string name = CleanPlayName(_fldOcCurrentPlay.GetValue(_ocInst)?.ToString() ?? "");
                if (!string.IsNullOrWhiteSpace(name) && name != _lastCalledPlay)
                {
                    _lastCalledPlay = name;
                    SpeechManager.Speak(BuildPlayAnnouncement(name, "offense"));
                }
            }
            if (_fldDcCurrentPlay != null && _dcInst != null)
            {
                string name = CleanPlayName(_fldDcCurrentPlay.GetValue(_dcInst)?.ToString() ?? "");
                if (!string.IsNullOrWhiteSpace(name) && name != _lastCalledDefPlay)
                {
                    _lastCalledDefPlay = name;
                    SpeechManager.Speak(BuildPlayAnnouncement(name, "defense"));
                }
            }
        }

        // ---- Audibles ----

        private static void CheckAudibles()
        {
            if (!_playbookRefsDone) CachePlaybookRefs();
            if (_audiblesInst == null || _fldAudiblesVisible == null) return;

            bool visible = false;
            try { visible = (bool)(_fldAudiblesVisible.GetValue(_audiblesInst) ?? false); }
            catch { return; }

            bool justOpened = visible && !_audiblesWasOpen;
            _audiblesWasOpen = visible;

            if (!justOpened) return;

            // offensivePlayList is populated with the formation's plays during audibles.
            // Page 0 → indices 0/1/2, Page 1 → indices 3/4/5, etc.
            // X button = index 0 (left), A button = index 1 (middle), B button = index 2 (right).
            int page = 0;
            try { page = (int)(_fldPage?.GetValue(_playbookInst) ?? 0); } catch { }

            string x = GetPlayAt(_fldPlayList, page, 0);
            string a = GetPlayAt(_fldPlayList, page, 1);
            string b = GetPlayAt(_fldPlayList, page, 2);

            var sb = new System.Text.StringBuilder("Audibles.");
            if (!string.IsNullOrWhiteSpace(x)) sb.Append($" X: {x}.");
            if (!string.IsNullOrWhiteSpace(a)) sb.Append($" A: {a}.");
            if (!string.IsNullOrWhiteSpace(b)) sb.Append($" B: {b}.");

            SpeechManager.Speak(sb.ToString().Trim());
        }

        private static void ReadOutStatus()
        {
            var sb = new System.Text.StringBuilder();

            // Quarter
            string q = FormatQuarter(_lastQuarter);
            if (!string.IsNullOrWhiteSpace(q)) sb.Append(q);

            // Time remaining (MM:SS from TimeLeftInSeconds)
            try
            {
                if (_fldTimeLeft != null && _matchInst != null)
                {
                    int secs = (int)(_fldTimeLeft.GetValue(_matchInst) ?? 0);
                    int m = secs / 60;
                    int s = secs % 60;
                    sb.Append($". {m}:{s:00} remaining");
                }
            }
            catch { }

            // Score
            string home = Clean(_tmpHomeName);
            string away = Clean(_tmpAwayName);
            string hs   = Clean(_tmpHomeScore);
            string as_  = Clean(_tmpAwayScore);
            if (!string.IsNullOrWhiteSpace(hs) && !string.IsNullOrWhiteSpace(as_))
                sb.Append($". {home} {hs}, {away} {as_}");

            // Down and distance
            string dd = FormatDownDist(_lastDownDist);
            if (!string.IsNullOrWhiteSpace(dd)) sb.Append(". " + dd);

            // Timeouts remaining (3 per half minus counter)
            try
            {
                if (_fldHomeTO != null && _fldAwayTO != null && _matchInst != null)
                {
                    int homeUsed = (int)(_fldHomeTO.GetValue(_matchInst) ?? 0);
                    int awayUsed = (int)(_fldAwayTO.GetValue(_matchInst) ?? 0);
                    int homeLeft = Math.Max(0, 3 - homeUsed);
                    int awayLeft = Math.Max(0, 3 - awayUsed);
                    sb.Append($". {home} {homeLeft}, {away} {awayLeft} timeouts");
                }
            }
            catch { }

            string text = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                SpeechManager.Speak(text);
        }

        // Re-reads the current audible plays on demand (called on LB/RB press during audibles).
        private static void AnnounceAudiblesNow()
        {
            if (_playbookInst == null) return;
            int page = 0;
            try { page = (int)(_fldPage?.GetValue(_playbookInst) ?? 0); } catch { }

            string x = GetPlayAt(_fldPlayList, page, 0);
            string a = GetPlayAt(_fldPlayList, page, 1);
            string b = GetPlayAt(_fldPlayList, page, 2);

            var sb = new System.Text.StringBuilder("Audibles.");
            if (!string.IsNullOrWhiteSpace(x)) sb.Append($" X: {x}.");
            if (!string.IsNullOrWhiteSpace(a)) sb.Append($" A: {a}.");
            if (!string.IsNullOrWhiteSpace(b)) sb.Append($" B: {b}.");
            SpeechManager.Speak(sb.ToString().Trim());
        }

        private static void AnnounceCurrentFormation()
        {
            // Announce whichever play-call box is currently open
            if (_playCallOpen)
            {
                string f = Clean(_tmpFormationName);
                if (!string.IsNullOrWhiteSpace(f))
                { SpeechManager.Speak(f); return; }
            }
            if (_defPlayCallOpen)
            {
                string f = Clean(_tmpDefFormationName);
                if (!string.IsNullOrWhiteSpace(f))
                { SpeechManager.Speak(f); return; }
            }
        }

        private static void AnnounceTimeRemaining()
        {
            try
            {
                if (_fldTimeLeft != null && _matchInst != null)
                {
                    int secs = (int)(_fldTimeLeft.GetValue(_matchInst) ?? 0);
                    int m = secs / 60;
                    int s = secs % 60;
                    SpeechManager.Speak($"{m}:{s:00} remaining");
                    return;
                }
            }
            catch { }
            // Fallback: nothing useful to say
        }

        private static void AnnounceScore()
        {
            string home = Clean(_tmpHomeName);
            string away = Clean(_tmpAwayName);
            string hs   = Clean(_tmpHomeScore);
            string as_  = Clean(_tmpAwayScore);
            if (!string.IsNullOrWhiteSpace(hs) && !string.IsNullOrWhiteSpace(as_))
                SpeechManager.Speak($"{home} {hs}, {away} {as_}");
        }

        // Strips the class-name suffix Unity appends to ToString() on play objects.
        // e.g. "FB Trap (Football.FootballPlay)" → "FB Trap"
        private static string CleanPlayName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            int paren = raw.IndexOf(" (");
            if (paren > 0) raw = raw.Substring(0, paren);
            return raw.Trim();
        }

        // ---- Playbook Reflection ----

        private static void CachePlaybookRefs()
        {
            _playbookRefsDone = true;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Assembly-CSharp") continue;

                    var pbType = asm.GetType("Football.Playbook");

                    if (pbType != null)
                    {
                        var arr = UnityEngine.Object.FindObjectsOfType(pbType);
                        if (arr != null && arr.Length > 0) _playbookInst = arr[0];
                    }

                    if (pbType != null && _playbookInst != null)
                    {
                        _fldDepth       = pbType.GetField("OffensivePlaybookDepth", flags);
                        _fldPage        = pbType.GetField("OffensivePlaybookPage",  flags);
                        _fldPlayList    = pbType.GetField("offensivePlayList",       flags);
                        _fldOffButtons  = pbType.GetField("offensiveButtons",        flags);
                        _fldDefDepth    = pbType.GetField("DefensivePlaybookDepth",  flags);
                        _fldDefPage     = pbType.GetField("DefensivePlaybookPage",   flags);
                        _fldDefPlayList = pbType.GetField("defensivePlayList",        flags);
                        _fldDefButtons  = pbType.GetField("DefensiveButtons",         flags);
                    }

                    // Navigate FootballGameplayMenu → footballMatch → oc / dc
                    var fgmType = asm.GetType("FootballGameplayMenu");
                    if (fgmType != null)
                    {
                        var fgmArr = UnityEngine.Object.FindObjectsOfType(fgmType);
                        if (fgmArr != null && fgmArr.Length > 0)
                        {
                            var fgmInst = fgmArr[0];
                            var fldMatch = fgmType.GetField("footballMatch", flags);
                            if (fldMatch != null)
                            {
                                var matchInst = fldMatch.GetValue(fgmInst);
                                if (matchInst != null)
                                {
                                    _matchInst = matchInst;
                                    var mt = matchInst.GetType();
                                    var fldOc  = mt.GetField("oc",       flags);
                                    var fldDc  = mt.GetField("dc",       flags);
                                    var fldAud = mt.GetField("audibles", flags);
                                    _fldTimeLeft = mt.GetField("TimeLeftInSeconds",   flags);
                                    _fldHomeTO   = mt.GetField("HomeTimeOutCounter",  flags);
                                    _fldAwayTO   = mt.GetField("AwayTimeOutCounter",  flags);
                                    if (fldOc  != null) _ocInst       = fldOc.GetValue(matchInst);
                                    if (fldDc  != null) _dcInst       = fldDc.GetValue(matchInst);
                                    if (fldAud != null)
                                    {
                                        _audiblesInst = fldAud.GetValue(matchInst);
                                        if (_audiblesInst != null)
                                            _fldAudiblesVisible = _audiblesInst.GetType()
                                                .GetField("offensiveAudiblesVisible", flags);
                                    }
                                }
                            }
                        }
                    }

                    if (_ocInst != null)
                    {
                        var ocType = _ocInst.GetType();
                        _fldOcCurrentPlay  = ocType.GetField("CurrentPlay", flags);
                        // Seed so we don't announce the previous game's last play on entry
                        _lastCalledPlay = CleanPlayName(_fldOcCurrentPlay?.GetValue(_ocInst)?.ToString() ?? "");
                    }
                    if (_dcInst != null)
                    {
                        var dcType = _dcInst.GetType();
                        _fldDcCurrentPlay  = dcType.GetField("CurrentPlay", flags);
                        _lastCalledDefPlay = CleanPlayName(_fldDcCurrentPlay?.GetValue(_dcInst)?.ToString() ?? "");
                    }

                    Plugin.Log.LogInfo(
                        $"[GameplayReader] PB={_playbookInst != null} " +
                        $"OPlayList={_fldPlayList != null} OButtons={_fldOffButtons != null} " +
                        $"DPlayList={_fldDefPlayList != null} DButtons={_fldDefButtons != null} " +
                        $"OC={_ocInst != null} DC={_dcInst != null}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[GameplayReader] CachePlaybookRefs failed: {ex.Message}");
            }
        }

        // Returns 0/1/2 for the selected play column, or -1 if indeterminate.
        // Checks MonoBehaviour boolean "selected/focus" fields first, then falls back to scale.
        private static int GetSelectedColumn(FieldInfo fldButtons)
        {
            if (_playbookInst == null || fldButtons == null) return -1;
            try
            {
                var list = fldButtons.GetValue(_playbookInst) as System.Collections.IList;
                if (list == null || list.Count < 3) return -1;
                var bflags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Pass 1: boolean "selected/focus/current" fields on MonoBehaviours
                for (int i = 0; i < 3; i++)
                {
                    var go = list[i] as GameObject;
                    if (go == null) continue;
                    foreach (var mb in go.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        foreach (var f in mb.GetType().GetFields(bflags))
                        {
                            if (f.FieldType != typeof(bool)) continue;
                            string n = f.Name.ToLower();
                            if (!n.Contains("select") && !n.Contains("focus") && !n.Contains("current")) continue;
                            try { if ((bool)f.GetValue(mb)) return i; } catch { }
                        }
                    }
                }

                // Pass 2: scale fallback — only return a result if one button is meaningfully larger
                float bestScale = -1f;
                int bestIdx = -1;
                float secondBest = -1f;
                for (int i = 0; i < 3; i++)
                {
                    var go = list[i] as GameObject;
                    if (go == null) continue;
                    float s = go.transform.localScale.x;
                    if (s > bestScale) { secondBest = bestScale; bestScale = s; bestIdx = i; }
                    else if (s > secondBest) { secondBest = s; }
                }
                // Only trust scale if the winner is at least 5% larger than the runner-up
                return (bestScale > secondBest * 1.05f) ? bestIdx : -1;
            }
            catch { return -1; }
        }

        private static string GetPlayAt(FieldInfo fldPlayList, int page, int col)
        {
            if (_playbookInst == null || fldPlayList == null) return "";
            try
            {
                var list = fldPlayList.GetValue(_playbookInst) as System.Collections.IList;
                if (list == null) return "";
                int idx = page * 3 + col;
                if (idx < 0 || idx >= list.Count) return "";
                return ExtractPlayName(list[idx]);
            }
            catch { return ""; }
        }

        // Try common name fields via reflection before falling back to ToString().
        // Logs the item type once so we can verify what the list actually contains.
        private static string ExtractPlayName(object item)
        {
            if (item == null) return "";
            if (item is string s) return s;

            var t = item.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var fieldName in new[] { "playName", "PlayName", "name", "Name", "title", "Title", "displayName", "DisplayName" })
            {
                var f = t.GetField(fieldName, flags);
                if (f != null && f.FieldType == typeof(string))
                {
                    try
                    {
                        var val = f.GetValue(item) as string;
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                    catch { }
                }
            }

            // Log item type + all string fields once for diagnostics
            if (!_playItemLogged)
            {
                _playItemLogged = true;
                var sb = new System.Text.StringBuilder($"[PlayItem type={t.FullName}] Fields: ");
                foreach (var f in t.GetFields(flags))
                    if (f.FieldType == typeof(string))
                        try { sb.Append($"{f.Name}={f.GetValue(item)}; "); } catch { }
                Plugin.Log.LogInfo($"[GameplayReader] {sb}");
            }

            return CleanPlayName(item.ToString() ?? "");
        }

        // Position labels for play columns (left, middle, right on screen)
        private static readonly string[] ColLabels = { "1st", "2nd", "3rd" };

        private static string GetPageNamesLabeled(FieldInfo fldPlayList, int page)
        {
            if (_playbookInst == null || fldPlayList == null) return "";
            try
            {
                var list = fldPlayList.GetValue(_playbookInst) as System.Collections.IList;
                if (list == null) return "";
                int start = page * 3;
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 3; i++)
                {
                    int idx = start + i;
                    if (idx >= list.Count) break;
                    string n = ExtractPlayName(list[idx]);
                    if (!string.IsNullOrWhiteSpace(n))
                        parts.Add($"{ColLabels[i]}: {n}");
                }
                return string.Join(", ", parts.ToArray());
            }
            catch { return ""; }
        }

        private static int GetTotalPages(FieldInfo fldPlayList)
        {
            if (_playbookInst == null || fldPlayList == null) return 1;
            try
            {
                var list = fldPlayList.GetValue(_playbookInst) as System.Collections.IList;
                if (list == null || list.Count == 0) return 1;
                return (list.Count + 2) / 3;
            }
            catch { return 1; }
        }

        // ---- Play announcement helpers ----

        // Builds "[play name], [type] play." e.g. "PA Crossers, passing play."
        // For defense just returns the play name (it's always a defense play).
        private static string BuildPlayAnnouncement(string name, string side)
        {
            string type = InferPlayType(name, side);
            return string.IsNullOrEmpty(type) ? $"{name}." : $"{name}, {type} play.";
        }

        private static string InferPlayType(string name, string side)
        {
            if (side == "defense") return "";
            string up = (name ?? "").ToUpper();
            if (up.StartsWith("PA ") || up.StartsWith("PA_") || up.Contains("PASS") ||
                up.Contains("CROSS") || up.Contains("SLANT") || up.Contains("POST") ||
                up.Contains("MESH") || up.Contains("FLOOD") || up.Contains("HITCH") ||
                up.Contains("CURL") || up.Contains("SEAM") || up.Contains("CORNER") ||
                up.Contains("STICK") || up.Contains("SCREEN") || up.Contains("QUICK") ||
                up.Contains("SPRINT") || up.Contains("ROUTE"))
                return "passing";
            if (up.StartsWith("FB ") || up.StartsWith("HB ") || up.StartsWith("QB ") ||
                up.Contains("DIVE") || up.Contains("SWEEP") || up.Contains("POWER") ||
                up.Contains("COUNTER") || up.Contains("TOSS") || up.Contains("DRAW") ||
                up.Contains("BLAST") || up.Contains("OPTION") || up.Contains("LEAD") ||
                up.Contains("TRAP") || up.Contains("WEDGE") || up.Contains("ISO") ||
                up.Contains("SNEAK"))
                return "running";
            return "";
        }

        // ---- Button press helpers ----

        // Snapshot current joystick state so a button already held doesn't misfire.
        private static void SeedButtonStates()
        {
            _xWasDown = Input.GetKey(KeyCode.JoystickButton2);
            _aWasDown = Input.GetKey(KeyCode.JoystickButton0);
            _yWasDown = Input.GetKey(KeyCode.JoystickButton3);
        }

        // Returns 0/1/2 if X/A/Y was newly pressed this poll, -1 otherwise.
        // X → col 0 (1st play), A → col 1 (2nd play), Y → col 2 (3rd play)
        private static int ConsumePlayButtonPress()
        {
            bool xNow = Input.GetKey(KeyCode.JoystickButton2);
            bool aNow = Input.GetKey(KeyCode.JoystickButton0);
            bool yNow = Input.GetKey(KeyCode.JoystickButton3);

            int result = -1;
            if      (xNow && !_xWasDown) result = 0;
            else if (aNow && !_aWasDown) result = 1;
            else if (yNow && !_yWasDown) result = 2;

            _xWasDown = xNow;
            _aWasDown = aNow;
            _yWasDown = yNow;
            return result;
        }

        // ---- Helpers ----

        private static string FormatDownDist(string raw)
        {
            return raw.Replace("&", "and");
        }

        private static string FormatQuarter(string raw)
        {
            // "3RD QUARTER" → "3rd quarter", "OVERTIME" → "overtime", etc.
            return raw.ToLower();
        }

        private static string Clean(TextMeshProUGUI comp)
        {
            if (comp == null) return "";
            string raw = comp.text ?? "";
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (char c in raw)
            {
                if (c == '<') { inTag = true;  continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return System.Text.RegularExpressions.Regex.Replace(
                sb.ToString().Trim(), @"\s+", " ");
        }

        private static TextMeshProUGUI FindTMPByName(string name)
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
                if (t.gameObject.name == name) return t;
            return null;
        }

        private static TextMeshProUGUI FindTMPInChildren(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t.gameObject.name == name) return t;
            return null;
        }
    }
}
