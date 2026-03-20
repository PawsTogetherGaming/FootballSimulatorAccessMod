using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Announces receiver status when the QB drops back to pass.
    /// - At QBHasBall entry: announces open receivers by button name ("A. X.") using
    ///   QB logic ATarget/BTarget/XTarget/YTarget/L1Target. Falls back to position names
    ///   if QB targets are not yet populated.
    /// - During the play: re-announces any receiver that transitions from covered to open.
    /// - At PreSnap: announces route assignments by position name. L1 repeats them.
    /// - Audible accepted: re-announces routes for the new play.
    /// </summary>
    public static class ReceiverReader
    {
        private const float OPEN_THRESHOLD = 3.5f;

        // Button labels matched to their QB target field index
        private static readonly string[] BUTTON_LABELS = { "A", "B", "X", "Y", "L1" };

        // Reflection refs — fetched lazily on first in-game poll
        private static bool        _reflDone          = false;
        private static object      _matchInst         = null;
        private static FieldInfo   _fldPlayState      = null;
        private static FieldInfo   _fldPlayType       = null;
        private static FieldInfo   _fldReceivers      = null;
        private static FieldInfo   _fldLogic          = null;  // FootballPlayer.logic
        private static FieldInfo   _fldClosestDist    = null;  // FootballPlayerLogic.ClosestDefenderDistance
        private static FieldInfo   _fldShowAssignment = null;  // FootballPlayerLogic.ShowAssignment
        private static FieldInfo   _fldAssignment     = null;  // FootballPlayerLogic.Assignment
        private static FieldInfo   _fldOC             = null;  // FootballMatch.oc
        private static FieldInfo   _fldOCCurrentPlay  = null;  // OffensiveCoordinator.CurrentPlay
        private static FieldInfo   _fldQB             = null;  // FootballMatch.qb
        private static FieldInfo   _fldIsBot          = null;  // FootballPlayer.isBot
        private static FieldInfo[] _fldButtonTargets  = null;  // [A,B,X,Y,L1] on FootballPlayerLogic

        // State tracking
        private static bool   _wasInGame              = false;
        private static string _lastState              = "";
        private static bool   _showAssignmentWasTrue  = false;
        private static bool   _routesAnnouncedThisPlay = false;
        private static string _lastCurrentPlay        = "";
        private static bool   _l1WasDown              = false;

        // Per-button open state for covered→open detection during the play
        private static readonly bool[] _buttonWasOpen = new bool[5];

        public static void Poll()
        {
            var matchUI = GameObject.Find("Football Match UI");
            bool inGame = matchUI != null && matchUI.activeInHierarchy;

            if (!inGame)
            {
                if (_wasInGame) ResetState();
                return;
            }

            if (!_wasInGame)
            {
                _wasInGame = true;
                _reflDone  = false;
                _matchInst = null;
            }

            if (!EnsureRefs()) return;

            string playState = "";
            string playType  = "";
            try
            {
                playState = _fldPlayState?.GetValue(_matchInst)?.ToString() ?? "";
                playType  = _fldPlayType?.GetValue(_matchInst)?.ToString()  ?? "";
            }
            catch { return; }

            // Reset per-play flags when a new huddle starts
            if (playState == "InHuddle" && _lastState != "InHuddle")
            {
                _routesAnnouncedThisPlay = false;
                _lastCurrentPlay         = "";
                _l1WasDown               = false;
                ClearButtonOpenState();
            }

            bool justEntered = playState == "QBHasBall"
                            && playType  == "Pass"
                            && _lastState != "QBHasBall";

            bool justPreSnap = playState == "PreSnap" && _lastState != "PreSnap";

            _lastState = playState;

            if (justEntered)
                AnnounceReceivers();
            else if (playState == "QBHasBall" && playType == "Pass")
                PollOpenChanges();  // announce any receiver that just became open

            if (justPreSnap && !_routesAnnouncedThisPlay) AnnounceRoutes();

            // L1 repeat and audible re-announce during PreSnap
            if (playState == "PreSnap")
            {
                bool l1Now = Input.GetKey(KeyCode.JoystickButton4);
                if (l1Now && !_l1WasDown) AnnounceRoutes();
                _l1WasDown = l1Now;

                string currentPlay = GetCurrentPlayName();
                if (!string.IsNullOrEmpty(currentPlay) && currentPlay != _lastCurrentPlay)
                {
                    _lastCurrentPlay         = currentPlay;
                    _routesAnnouncedThisPlay = false;
                    AnnounceRoutes();
                }
            }
            else
            {
                _l1WasDown = false;
            }

            CheckPlayArt();
        }

        // ---- State management ----

        private static void ResetState()
        {
            _wasInGame               = false;
            _lastState               = "";
            _showAssignmentWasTrue   = false;
            _routesAnnouncedThisPlay = false;
            _lastCurrentPlay         = "";
            _l1WasDown               = false;
            ClearButtonOpenState();
        }

        private static void ClearButtonOpenState()
        {
            for (int i = 0; i < _buttonWasOpen.Length; i++)
                _buttonWasOpen[i] = false;
        }

        // ---- Receiver announcement (QB drop-back) ----

        private static void AnnounceReceivers()
        {
            try
            {
                object qbLogic = GetQBLogic();

                if (qbLogic != null && _fldButtonTargets != null)
                {
                    // Button-based: only announce open receivers by button name
                    var sb = new StringBuilder();
                    for (int i = 0; i < BUTTON_LABELS.Length; i++)
                    {
                        if (_fldButtonTargets[i] == null) continue;
                        object target = _fldButtonTargets[i].GetValue(qbLogic);
                        if (target == null) continue;

                        float dist = GetClosestDefenderDist(target);
                        bool  open = dist > OPEN_THRESHOLD;
                        _buttonWasOpen[i] = open;

                        if (open)
                        {
                            if (sb.Length > 0) sb.Append(". ");
                            sb.Append(BUTTON_LABELS[i]);
                        }
                    }

                    if (sb.Length > 0)
                        SpeechManager.Speak(sb.Append(".").ToString());
                    // All covered → say nothing; player will hear covered→open updates as play develops
                    return;
                }

                // Fallback: QB targets not populated yet — use position names, open only
                var list = _fldReceivers?.GetValue(_matchInst) as IList;
                if (list == null || list.Count == 0) return;

                var sbFallback = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    object recv = list[i];
                    if (recv == null) continue;
                    float dist = GetClosestDefenderDist(recv);
                    if (dist > OPEN_THRESHOLD)
                    {
                        if (sbFallback.Length > 0) sbFallback.Append(". ");
                        sbFallback.Append(GetPositionName(recv));
                    }
                }
                if (sbFallback.Length > 0)
                    SpeechManager.Speak(sbFallback.Append(".").ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ReceiverReader] AnnounceReceivers: {ex.Message}");
            }
        }

        // Called every poll tick while QB has the ball — announces any receiver
        // that just transitioned from covered to open.
        private static void PollOpenChanges()
        {
            if (_fldButtonTargets == null) return;
            object qbLogic = GetQBLogic();
            if (qbLogic == null) return;

            try
            {
                for (int i = 0; i < BUTTON_LABELS.Length; i++)
                {
                    if (_fldButtonTargets[i] == null) continue;
                    object target = _fldButtonTargets[i].GetValue(qbLogic);
                    if (target == null) continue;

                    float dist = GetClosestDefenderDist(target);
                    bool  open = dist > OPEN_THRESHOLD;

                    if (open && !_buttonWasOpen[i])
                        SpeechManager.Speak(BUTTON_LABELS[i] + ".");

                    _buttonWasOpen[i] = open;
                }
            }
            catch { }
        }

        // ---- Route announcement (pre-snap) ----

        private static void AnnounceRoutes()
        {
            try
            {
                var list = _fldReceivers?.GetValue(_matchInst) as IList;
                if (list == null || list.Count == 0) return;

                var sb = new StringBuilder("Routes.");
                bool anyRoute = false;
                for (int i = 0; i < list.Count; i++)
                {
                    object recv = list[i];
                    if (recv == null) continue;
                    object logic = _fldLogic?.GetValue(recv);
                    if (logic == null) continue;

                    string pos   = GetPositionName(recv);
                    string route = "";
                    try
                    {
                        object assignment = _fldAssignment?.GetValue(logic);
                        if (assignment != null) route = CleanName(assignment.ToString());
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(route))
                    {
                        sb.Append($" {pos}: {route}.");
                        anyRoute = true;
                    }
                }

                if (anyRoute)
                {
                    _routesAnnouncedThisPlay = true;
                    SpeechManager.Speak(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ReceiverReader] AnnounceRoutes: {ex.Message}");
            }
        }

        // ShowAssignment fallback — fires when L1 is held and play art renders visually
        private static void CheckPlayArt()
        {
            try
            {
                var list = _fldReceivers?.GetValue(_matchInst) as IList;
                if (list == null || list.Count == 0) return;

                bool anyShowing = false;
                for (int i = 0; i < list.Count; i++)
                {
                    object recv = list[i];
                    if (recv == null) continue;
                    object logic = _fldLogic?.GetValue(recv);
                    if (logic == null) continue;
                    try
                    {
                        bool showing = (bool)(_fldShowAssignment?.GetValue(logic) ?? false);
                        if (showing) { anyShowing = true; break; }
                    }
                    catch { }
                }

                bool justOpened = anyShowing && !_showAssignmentWasTrue;
                _showAssignmentWasTrue = anyShowing;
                if (!justOpened) return;

                var sb = new StringBuilder("Routes.");
                for (int i = 0; i < list.Count; i++)
                {
                    object recv = list[i];
                    if (recv == null) continue;
                    object logic = _fldLogic?.GetValue(recv);
                    if (logic == null) continue;

                    string pos   = GetPositionName(recv);
                    string route = "";
                    try
                    {
                        object assignment = _fldAssignment?.GetValue(logic);
                        if (assignment != null) route = CleanName(assignment.ToString());
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(route))
                        sb.Append($" {pos}: {route}.");
                }

                if (sb.Length > "Routes.".Length)
                {
                    _routesAnnouncedThisPlay = true;
                    SpeechManager.Speak(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ReceiverReader] CheckPlayArt: {ex.Message}");
            }
        }

        // ---- Helpers ----

        private static object GetQBLogic()
        {
            try
            {
                object qb = _fldQB?.GetValue(_matchInst);
                if (qb == null) return null;
                return _fldLogic?.GetValue(qb);
            }
            catch { return null; }
        }

        private static string GetCurrentPlayName()
        {
            try
            {
                object oc = _fldOC?.GetValue(_matchInst);
                if (oc == null) return "";
                return _fldOCCurrentPlay?.GetValue(oc)?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static string GetPositionName(object recv)
        {
            string s = recv?.ToString() ?? "";
            int paren = s.IndexOf(" (");
            return paren > 0 ? s.Substring(0, paren) : s;
        }

        private static float GetClosestDefenderDist(object recv)
        {
            try
            {
                object logic = _fldLogic?.GetValue(recv);
                if (logic == null) return 0f;
                object val = _fldClosestDist?.GetValue(logic);
                if (val is float f)  return f;
                if (val is double d) return (float)d;
                return 0f;
            }
            catch { return 0f; }
        }

        // Returns true when the human player is controlling the offense.
        // The QB's isBot field is false when the human player's team is on offense.
        private static bool IsPlayerOnOffense()
        {
            try
            {
                object qb = _fldQB?.GetValue(_matchInst);
                if (qb == null) return true; // can't determine — assume offense to avoid silencing
                object isBot = _fldIsBot?.GetValue(qb);
                if (isBot is bool b) return !b;
                return true;
            }
            catch { return true; }
        }

        private static string CleanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            int paren = raw.IndexOf(" (");
            if (paren > 0) raw = raw.Substring(0, paren);
            return raw.Trim();
        }

        // ---- Reflection bootstrap ----

        private static bool EnsureRefs()
        {
            if (_reflDone) return _matchInst != null
                               && _fldPlayState  != null
                               && _fldReceivers  != null
                               && _fldLogic      != null
                               && _fldClosestDist != null;
            _reflDone = true;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Assembly-CSharp") continue;

                    var fgmType = asm.GetType("FootballGameplayMenu");
                    if (fgmType == null) break;
                    var fgmArr = UnityEngine.Object.FindObjectsOfType(fgmType);
                    if (fgmArr == null || fgmArr.Length == 0) break;

                    var fldMatch = fgmType.GetField("footballMatch", flags);
                    if (fldMatch == null) break;
                    _matchInst = fldMatch.GetValue(fgmArr[0]);
                    if (_matchInst == null) break;

                    var mt = _matchInst.GetType();
                    _fldPlayState = mt.GetField("playState", flags);
                    _fldPlayType  = mt.GetField("playType",  flags);
                    _fldReceivers = mt.GetField("receivers", flags);
                    _fldQB        = mt.GetField("qb",        flags);
                    _fldOC        = mt.GetField("oc",        flags);

                    var fpType = asm.GetType("FootballPlayer");
                    if (fpType != null)
                    {
                        _fldLogic = fpType.GetField("logic", flags);
                        _fldIsBot = fpType.GetField("isBot", flags);
                    }

                    var fplType = asm.GetType("Football.FootballPlayerLogic");
                    if (fplType != null)
                    {
                        _fldClosestDist    = fplType.GetField("ClosestDefenderDistance", flags);
                        _fldShowAssignment = fplType.GetField("ShowAssignment",          flags);
                        _fldAssignment     = fplType.GetField("Assignment",              flags);

                        _fldButtonTargets = new FieldInfo[5];
                        _fldButtonTargets[0] = fplType.GetField("ATarget",  flags);
                        _fldButtonTargets[1] = fplType.GetField("BTarget",  flags);
                        _fldButtonTargets[2] = fplType.GetField("XTarget",  flags);
                        _fldButtonTargets[3] = fplType.GetField("YTarget",  flags);
                        _fldButtonTargets[4] = fplType.GetField("L1Target", flags);
                    }

                    var ocType = asm.GetType("Football.OffensiveCoordinator");
                    if (ocType != null)
                        _fldOCCurrentPlay = ocType.GetField("CurrentPlay", flags);

                    bool btnTargetsOk = _fldButtonTargets != null
                        && _fldButtonTargets[0] != null && _fldButtonTargets[4] != null;
                    Plugin.Log.LogInfo(
                        $"[ReceiverReader] match={_matchInst != null} " +
                        $"ps={_fldPlayState != null} recv={_fldReceivers != null} " +
                        $"logic={_fldLogic != null} dist={_fldClosestDist != null} " +
                        $"showAssign={_fldShowAssignment != null} assign={_fldAssignment != null} " +
                        $"btnTargets={btnTargetsOk}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ReceiverReader] EnsureRefs: {ex.Message}");
            }

            return _matchInst != null && _fldPlayState != null && _fldReceivers != null
                && _fldLogic != null && _fldClosestDist != null;
        }
    }
}
