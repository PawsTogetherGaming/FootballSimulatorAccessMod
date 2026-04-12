using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Defensive accessibility — four core features:
    ///
    /// 1. Player switch: "[Position] [LastName]. QB/Ball [stick direction].
    ///    [lateral], [depth]." — tells the player how to orient and where they are.
    ///
    /// 2. Ball-in-air: compact directional cues every 400 ms in stick terms.
    ///    Right motor buzzes when directly under the ball (XZ dist &lt; 4 m).
    ///
    /// 3. Tackle proximity: left motor ramps up as controlled defender closes
    ///    on the ball carrier — stronger = closer = time to dive/tackle.
    ///
    /// 4. Heat-seek (hold R1): while held, switches controlled player to AI
    ///    control so the game routes them to the catch point / ball carrier.
    ///    Release returns manual control. R1 = JoystickButton5.
    /// </summary>
    public static class DefenseReader
    {
        // ---- Reflection refs ----
        private static bool      _reflDone              = false;
        private static object    _matchInst             = null;

        // FootballMatch fields
        private static FieldInfo _fldPlayState          = null;
        private static FieldInfo _fldPlayType           = null;
        private static FieldInfo _fldDc                 = null;
        private static FieldInfo _fldDefensivePlayers   = null;
        private static FieldInfo _fldBall               = null;
        private static FieldInfo _fldQb                 = null;
        private static FieldInfo _fldIsHomeDefense      = null;
        private static FieldInfo _fldScrimmageLine      = null;
        private static FieldInfo _fldPlayerWithBall     = null;

        // DefensiveCoordinator → GameController → SelectedPlayer
        private static FieldInfo _fldDcGc               = null;
        private static FieldInfo _fldGcSelectedPlayer   = null;

        // FootballPlayer → FootballPlayerLogic
        private static FieldInfo _fldFpLogic            = null;

        // FootballPlayerLogic fields
        private static FieldInfo _fldBehaviorState      = null;
        private static FieldInfo _fldPlayerData         = null;

        // FootballPlayerData fields
        private static FieldInfo _fldLastName           = null;

        // Heat-seek: FootballPlayerLogic → character → controlState
        private static FieldInfo _fldCharacter              = null;  // logic.character
        private static FieldInfo _fldControlState           = null;  // character.controlState
        private static MethodInfo _methSetBehaviorState     = null;  // logic.SetBehaviorState()

        // Heat-seek movement: allowPlayerControl, AssignmentTargets, AssignmentPathComplete
        private static FieldInfo _fldAllowPlayerControl     = null;  // logic.allowPlayerControl (bool)
        private static FieldInfo _fldAssignmentTargets      = null;  // logic.AssignmentTargets (List<Vector3>)
        private static FieldInfo _fldAssignmentPathComplete = null;  // logic.AssignmentPathComplete (bool)

        // Ball hawk: instant reaction to thrown ball
        private static FieldInfo _fldThrownBallReactCounter = null;  // logic.thrownBallReactCounter (int)

        // Human-side guard: gc.isAI — true means the defense is CPU-controlled
        private static FieldInfo _fldGcIsAI                 = null;  // GameController.isAI (bool)

        // Cached enum values (resolved once)
        private static object    _enumAIInControl       = null;
        private static object    _enumPlayerInControl   = null;
        private static object    _enumRunPursuit        = null;
        private static object    _enumTryingToCatch     = null;

        // ---- State ----
        private static bool   _wasInGame                   = false;
        private static string _lastPlayState               = "";
        private static object _lastSelectedPlayerRef       = null;
        private static string _lastControlledBehaviorState = "";
        private static bool   _ballInAirAnnounced          = false;
        private static bool   _defenderOnItAnnounced       = false;

        // Defender-switch debounce — queues the announcement and only fires it
        // after the selected player has been stable for SWITCH_SETTLE seconds.
        // This prevents NVDA stutter when the user rapidly cycles through players.
        private static string _pendingPlayerAnnouncement   = "";
        private static float  _pendingPlayerTime           = -1f;
        private const  float  SWITCH_SETTLE               = 0.3f;

        // Ball direction updates
        private static float  _lastDirTime                 = -10f;
        private const  float  BALL_DIR_INTERVAL            = 0.4f;

        // L3 on-demand readout
        private static bool   _l3WasDown                   = false;

        // R1 heat-seek
        private static bool   _r1WasActive                 = false;

        // Vibration: tackle proximity
        private const float   TACKLE_DIST_MAX              = 10f;  // m — start ramping
        private const float   TACKLE_DIST_MIN              = 1f;   // m — full rumble
        private const float   UNDER_BALL_DIST              = 4f;   // m XZ — right motor on

        // =========================================================
        // Poll — called at the 120 ms throttled rate.
        // =========================================================

        public static void Poll()
        {
            var matchUI = GameObject.Find("Football Match UI");
            bool inGame = matchUI != null && matchUI.activeInHierarchy;

            if (!inGame)
            {
                if (_wasInGame) ResetState();
                return;
            }

            if (!EnsureRefs()) return;
            _wasInGame = true;

            string playState = "";
            try { playState = _fldPlayState?.GetValue(_matchInst)?.ToString() ?? ""; }
            catch { return; }

            // ---- Ball-in-air tracking ----
            bool readBallInAir = ModSettings.ReadBallInAir?.Value ?? true;
            if (playState == "BallInAir")
            {
                if (!_ballInAirAnnounced)
                {
                    _ballInAirAnnounced    = true;
                    _defenderOnItAnnounced = false;
                    if (readBallInAir)
                    {
                        string dir = GetBallDirection(_lastSelectedPlayerRef as Component);
                        SpeechManager.Speak(string.IsNullOrEmpty(dir)
                            ? "Ball in the air!"
                            : $"Ball! {dir}.");
                    }
                    _lastDirTime = Time.unscaledTime;
                }
                else if (readBallInAir && !_defenderOnItAnnounced &&
                         Time.unscaledTime - _lastDirTime >= BALL_DIR_INTERVAL)
                {
                    string dir = GetBallDirection(_lastSelectedPlayerRef as Component);
                    if (!string.IsNullOrEmpty(dir))
                        SpeechManager.Speak(dir + ".");
                    _lastDirTime = Time.unscaledTime;
                }
                if (!_defenderOnItAnnounced)
                    PollDefenderStates();
            }
            else if (_lastPlayState == "BallInAir" || _lastPlayState == "BallTipped")
            {
                _ballInAirAnnounced    = false;
                _defenderOnItAnnounced = false;
                VibrationManager.SetRight(0f);
            }
            _lastPlayState = playState;

            // ---- Active play checks ----
            bool activePlay = playState == "PreSnap"      || playState == "BallSnapped" ||
                              playState == "QBHasBall"    || playState == "PlayerHasBall" ||
                              playState == "BallInAir"    || playState == "BallTipped";

            // Heat-seek must NOT run during PreSnap — R1 (right bumper) is used to cycle
            // formations in the play call screen, and firing "Heat seek on." there interrupts
            // the play name announcement. Only activate heat-seek once the ball is in motion.
            bool ballInMotion = playState == "BallSnapped"  || playState == "QBHasBall" ||
                                playState == "PlayerHasBall" || playState == "BallInAir" ||
                                playState == "BallTipped";

            if (activePlay)
            {
                if (ModSettings.ReadDefenderSwitch?.Value ?? true)
                    CheckSelectedDefender(playState);
                if (ModSettings.ReadDefenderState?.Value ?? true)
                    CheckControlledBehaviorState();
                UpdateProximityVibration(playState);
            }
            if (ballInMotion)
            {
                HandleHeatSeek(playState);
            }
            else if (_r1WasActive)
            {
                // Ball returned to dead/pre-snap while R1 was held — release cleanly
                _r1WasActive = false;
                RestorePlayerControl();
            }
            else
            {
                // Only wipe the tracked player when NO active play is running.
                // During PreSnap (activePlay=true, ballInMotion=false) we must keep
                // _lastSelectedPlayerRef so the debounce timer can fire without being
                // reset every 120 ms by this block.
                if (!activePlay)
                {
                    _lastSelectedPlayerRef       = null;
                    _lastControlledBehaviorState = "";
                }
                VibrationManager.Stop();

                // Release heat-seek if play ended while R1 was held
                if (_r1WasActive)
                {
                    _r1WasActive = false;
                    RestorePlayerControl();
                }
            }

            // Fire pending player-switch announcement once selection has settled
            if (!string.IsNullOrEmpty(_pendingPlayerAnnouncement)
                && Time.unscaledTime >= _pendingPlayerTime)
            {
                if (ModSettings.ReadDefenderSwitch?.Value ?? true)
                    SpeechManager.Speak(_pendingPlayerAnnouncement);
                _pendingPlayerAnnouncement = "";
                _pendingPlayerTime         = -1f;
            }

            // L3 on-demand position readout
            bool l3 = Input.GetKey(KeyCode.JoystickButton8);
            if (l3 && !_l3WasDown && _lastSelectedPlayerRef != null)
                AnnouncePositionOnDemand();
            _l3WasDown = l3;
        }

        // ---- Player switch ----

        private static void CheckSelectedDefender(string playState)
        {
            if (_fldDc == null || _fldDcGc == null || _fldGcSelectedPlayer == null) return;

            object selectedPlayer = null;
            try
            {
                object dc = _fldDc.GetValue(_matchInst);
                if (dc == null) return;
                object gc = _fldDcGc.GetValue(dc);
                if (gc == null) return;
                selectedPlayer = _fldGcSelectedPlayer.GetValue(gc);
            }
            catch { return; }

            if (ReferenceEquals(selectedPlayer, _lastSelectedPlayerRef)) return;
            _lastSelectedPlayerRef       = selectedPlayer;
            _lastControlledBehaviorState = "";

            if (selectedPlayer == null) return;

            var comp = selectedPlayer as Component;

            string position = "";
            string lastName = "";
            string currentBehavior = "";

            try
            {
                var unityObj = selectedPlayer as UnityEngine.Object;
                if (unityObj != null) position = unityObj.name;
            }
            catch { }

            try
            {
                if (_fldFpLogic != null && comp != null)
                {
                    object logic = _fldFpLogic.GetValue(comp);
                    if (logic != null)
                    {
                        if (_fldPlayerData != null && _fldLastName != null)
                        {
                            object data = _fldPlayerData.GetValue(logic);
                            if (data != null)
                                lastName = (_fldLastName.GetValue(data) as string) ?? "";
                        }
                        if (_fldBehaviorState != null)
                            currentBehavior = _fldBehaviorState.GetValue(logic)?.ToString() ?? "";
                    }
                }
            }
            catch { }

            _lastControlledBehaviorState = currentBehavior;

            string label    = FormatPosition(position);
            string name     = string.IsNullOrEmpty(lastName) ? label : $"{label} {lastName}";
            string target   = GetTargetDirection(comp, playState);
            string fieldPos = GetFieldPosition(comp);

            var sb = new System.Text.StringBuilder(name).Append('.');
            if (!string.IsNullOrEmpty(target))   sb.Append(' ').Append(target).Append('.');
            if (!string.IsNullOrEmpty(fieldPos)) sb.Append(' ').Append(fieldPos).Append('.');

            // Queue the announcement — fire only after player selection settles
            _pendingPlayerAnnouncement = sb.ToString();
            _pendingPlayerTime         = Time.unscaledTime + SWITCH_SETTLE;
        }

        // ---- Behavior state change ----

        private static void CheckControlledBehaviorState()
        {
            if (_lastSelectedPlayerRef == null || _fldFpLogic == null || _fldBehaviorState == null)
                return;

            string state = "";
            try
            {
                var comp   = _lastSelectedPlayerRef as Component;
                object logic = comp != null ? _fldFpLogic.GetValue(comp) : null;
                if (logic != null)
                    state = _fldBehaviorState.GetValue(logic)?.ToString() ?? "";
            }
            catch { return; }

            if (state == _lastControlledBehaviorState) return;
            _lastControlledBehaviorState = state;

            string msg = FormatBehaviorState(state);
            if (!string.IsNullOrEmpty(msg))
                SpeechManager.Speak(msg);

            if (state.Contains("TryingToCatch") || state.Contains("TryingToKnock"))
                _defenderOnItAnnounced = true;
        }

        // ---- Tackle proximity vibration ----

        private static void UpdateProximityVibration(string playState)
        {
            var playerComp = _lastSelectedPlayerRef as Component;
            if (playerComp == null)
            {
                VibrationManager.SetLeft(0f);
                VibrationManager.SetRight(0f);
                return;
            }

            // Left motor: proximity to ball carrier
            float leftMotor = 0f;
            try
            {
                object carrier = _fldPlayerWithBall?.GetValue(_matchInst);
                var carrierComp = carrier as Component;
                if (carrierComp != null)
                {
                    float dist = Vector3.Distance(
                        playerComp.transform.position,
                        carrierComp.transform.position);
                    // Ramp: 0 at TACKLE_DIST_MAX, 1 at TACKLE_DIST_MIN
                    leftMotor = Mathf.InverseLerp(TACKLE_DIST_MAX, TACKLE_DIST_MIN, dist);
                }
            }
            catch { }

            // Right motor: directly under ball while it's in the air
            float rightMotor = 0f;
            if (playState == "BallInAir" || playState == "BallTipped")
            {
                try
                {
                    object ball    = _fldBall?.GetValue(_matchInst);
                    var ballComp   = ball as Component;
                    if (ballComp != null)
                    {
                        Vector3 playerXZ = new Vector3(
                            playerComp.transform.position.x, 0f,
                            playerComp.transform.position.z);
                        Vector3 ballXZ = new Vector3(
                            ballComp.transform.position.x, 0f,
                            ballComp.transform.position.z);
                        float xzDist = Vector3.Distance(playerXZ, ballXZ);
                        // Full buzz under the ball, fades out at UNDER_BALL_DIST
                        rightMotor = Mathf.InverseLerp(UNDER_BALL_DIST, 0f, xzDist);
                    }
                }
                catch { }
            }

            VibrationManager.SetLeft(leftMotor);
            VibrationManager.SetRight(rightMotor);
        }

        // ---- R1 heat-seek ----
        // While R1 is held: switch controlled defender to AI mode so the game
        // routes them toward the catch point / ball carrier automatically.
        // Release: restore manual control.
        // Behaviour scales with ModSettings.HeatSeekPower (0 = disabled, 10 = maximum).

        private static void HandleHeatSeek(string playState)
        {
            int power = ModSettings.HeatSeekPower?.Value ?? 5;
            if (power == 0) return;  // disabled

            // Guard: only apply heat-seek when the defense is human-controlled.
            // dc.gc.isAI == true means the CPU is controlling the defense this possession.
            // Fail closed: if we can't verify, don't run heat-seek.
            {
                bool humanOnDefense = false;
                if (_fldGcIsAI != null && _fldDc != null && _fldDcGc != null)
                {
                    try
                    {
                        object dc = _fldDc.GetValue(_matchInst);
                        if (dc != null)
                        {
                            object gc = _fldDcGc.GetValue(dc);
                            if (gc != null)
                                humanOnDefense = !(bool)(_fldGcIsAI.GetValue(gc) ?? true);
                        }
                    }
                    catch { }
                }
                if (!humanOnDefense)
                {
                    if (_r1WasActive) { _r1WasActive = false; RestorePlayerControl(); }
                    return;
                }
            }

            // Guard: during kick/punt/FG plays the "defense" is actually the return team.
            // Heat-seeking toward the ball or carrier would lock the returner in AI pursuit,
            // preventing the player from running the ball back.
            {
                string pt = "";
                try { pt = _fldPlayType?.GetValue(_matchInst)?.ToString() ?? ""; } catch { }
                if (pt == "Kickoff" || pt == "Punt" || pt == "FieldGoal")
                {
                    if (_r1WasActive)
                    {
                        _r1WasActive = false;
                        RestorePlayerControl();
                    }
                    return;
                }
            }

            bool r1 = Input.GetKey(KeyCode.JoystickButton5);

            // Power 10 auto-activations (no R1 required):
            //   • Ball just went in the air (passing play) — activate immediately
            //   • Ball carrier is within 6 m on a run play
            if (power >= 10 && !r1)
            {
                // Passing play: any BallInAir / BallTipped state auto-activates
                if (playState == "BallInAir" || playState == "BallTipped")
                {
                    r1 = true;
                }
                else
                {
                    // Run play: proximity trigger
                    try
                    {
                        var selfComp       = _lastSelectedPlayerRef as Component;
                        object carrier     = _fldPlayerWithBall?.GetValue(_matchInst);
                        var    carrierComp = carrier as Component;
                        if (selfComp != null && carrierComp != null)
                        {
                            float dist = Vector3.Distance(selfComp.transform.position,
                                                          carrierComp.transform.position);
                            if (dist <= 6f) r1 = true;
                        }
                    }
                    catch { }
                }
            }

            if (r1 && !_r1WasActive)
            {
                // Activate heat-seek
                _r1WasActive = true;
                SpeechManager.Speak("Heat seek on.");
                ApplyHeatSeek(playState);
            }
            else if (r1 && _r1WasActive)
            {
                // Keep re-applying each poll so AI state doesn't revert
                ApplyHeatSeek(playState);
            }
            else if (!r1 && _r1WasActive)
            {
                // Release: restore manual control
                _r1WasActive = false;
                RestorePlayerControl();
                SpeechManager.Speak("Control restored.");
            }
        }

        private static void ApplyHeatSeek(string playState)
        {
            if (_lastSelectedPlayerRef == null) return;
            if (_fldFpLogic == null || _fldCharacter == null || _fldControlState == null) return;
            if (_enumAIInControl == null) return;

            try
            {
                var comp   = _lastSelectedPlayerRef as Component;
                object logic = comp != null ? _fldFpLogic.GetValue(comp) : null;
                if (logic == null) return;

                object charInst = _fldCharacter.GetValue(logic);
                if (charInst == null) return;

                // Block the game from returning player control when the left stick is moved.
                _fldAllowPlayerControl?.SetValue(logic, false);

                // Switch to AI control
                _fldControlState.SetValue(charInst, _enumAIInControl);

                // Clear AssignmentTargets and mark path complete so ProcessAssignment()
                // falls through to the behavior-specific AI code (RunPursuit, catch pursuit, etc.)
                // instead of running MoveTowardVector which overrides the game's smart pursuit.
                if (_fldAssignmentTargets != null && _fldAssignmentPathComplete != null)
                {
                    var targets = _fldAssignmentTargets.GetValue(logic)
                                  as System.Collections.Generic.List<Vector3>;
                    targets?.Clear();
                    _fldAssignmentPathComplete.SetValue(logic, true);
                }

                if (playState == "BallInAir" || playState == "BallTipped")
                {
                    // Ball hawk: set thrownBallReactCounter=0 so the game's own
                    // DelayedProcessBallThrown fires immediately. This sets up
                    // RunningTowardBallLandingSpot → TryingToCatchBallInAir,
                    // which runs the defender to the catch point and attempts the INT.
                    _fldThrownBallReactCounter?.SetValue(logic, 0);
                }
                else
                {
                    // Run play: set RunPursuit so the game's AI pursues the ball carrier.
                    if (_enumRunPursuit != null && _methSetBehaviorState != null)
                        _methSetBehaviorState.Invoke(logic, new[] { _enumRunPursuit });
                    else if (_enumRunPursuit != null && _fldBehaviorState != null)
                        _fldBehaviorState.SetValue(logic, _enumRunPursuit);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DefenseReader] ApplyHeatSeek: {ex.Message}");
            }
        }

        // Returns the world-space position the heat-seeking defender should move toward.
        // At power 1–3: current target position (follow).
        // At power 4–9: predicted intercept point using target velocity.
        // Speed boost applied to AssignmentTargets at power 7+ via a lead distance.
        private static Vector3 GetHeatSeekTarget(Component selfComp, string playState)
        {
            int power = ModSettings.HeatSeekPower?.Value ?? 5;
            try
            {
                Vector3 targetPos = Vector3.zero;
                Vector3 targetVel = Vector3.zero;
                bool    gotTarget = false;

                if (playState == "BallInAir" || playState == "BallTipped")
                {
                    object ball    = _fldBall?.GetValue(_matchInst);
                    var    ballComp = ball as Component;
                    if (ballComp != null)
                    {
                        targetPos = ballComp.transform.position;
                        targetVel = GetVelocity(ballComp);
                        gotTarget = true;
                    }
                }
                else
                {
                    object carrier    = _fldPlayerWithBall?.GetValue(_matchInst);
                    var    carrierComp = carrier as Component;
                    if (carrierComp != null)
                    {
                        targetPos = carrierComp.transform.position;
                        targetVel = GetVelocity(carrierComp);
                        gotTarget = true;
                    }
                }

                if (!gotTarget) return selfComp != null ? selfComp.transform.position : Vector3.zero;

                // Power 1–3: just chase current position
                if (power <= 3) return targetPos;

                // Power 4+: predict intercept — lead the target by velocity × lookahead time
                // Lookahead scales from 0.4s (power 4) to 1.2s (power 10)
                float lookahead = Mathf.Lerp(0.4f, 1.2f, (power - 4) / 6f);
                Vector3 predicted = targetPos + targetVel * lookahead;

                // Power 7+: push the target further ahead to simulate a speed boost effect
                if (power >= 7 && selfComp != null)
                {
                    float extraLead = Mathf.Lerp(0f, 2f, (power - 7) / 3f);
                    Vector3 dir = (predicted - selfComp.transform.position).normalized;
                    predicted += dir * extraLead;
                }

                return predicted;
            }
            catch { }
            return selfComp != null ? selfComp.transform.position : Vector3.zero;
        }

        private static void RestorePlayerControl()
        {
            if (_lastSelectedPlayerRef == null) return;
            if (_fldFpLogic == null || _fldCharacter == null || _fldControlState == null) return;
            if (_enumPlayerInControl == null) return;

            try
            {
                var comp   = _lastSelectedPlayerRef as Component;
                object logic = comp != null ? _fldFpLogic.GetValue(comp) : null;
                if (logic == null) return;

                object charInst = _fldCharacter.GetValue(logic);
                if (charInst == null) return;

                // Re-enable player control and restore the control state
                _fldAllowPlayerControl?.SetValue(logic, true);
                _fldControlState.SetValue(charInst, _enumPlayerInControl);

                // Stop AI movement path so the character doesn't keep running on its own
                _fldAssignmentPathComplete?.SetValue(logic, true);
                var targets = _fldAssignmentTargets?.GetValue(logic)
                              as System.Collections.Generic.List<Vector3>;
                targets?.Clear();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DefenseReader] RestorePlayerControl: {ex.Message}");
            }
        }

        // ---- Scan all defenders for ball pursuit (AI teammates) ----

        private static void PollDefenderStates()
        {
            if (_fldDefensivePlayers == null || _fldFpLogic == null || _fldBehaviorState == null)
                return;

            IEnumerable defenders = null;
            try { defenders = _fldDefensivePlayers.GetValue(_matchInst) as IEnumerable; }
            catch { return; }
            if (defenders == null) return;

            foreach (object player in defenders)
            {
                if (player == null) continue;
                if (ReferenceEquals(player, _lastSelectedPlayerRef)) continue;
                try
                {
                    var comp   = player as Component;
                    object logic = comp != null ? _fldFpLogic.GetValue(comp) : null;
                    if (logic == null) continue;
                    string state = _fldBehaviorState.GetValue(logic)?.ToString() ?? "";
                    if (state.Contains("TryingToCatch") || state.Contains("TryingToKnock"))
                    {
                        _defenderOnItAnnounced = true;
                        SpeechManager.Speak("Defender on it!");
                        return;
                    }
                }
                catch { }
            }
        }

        // ---- Direction helpers ----

        private static string GetTargetDirection(Component playerComp, string playState)
        {
            if (playerComp == null) return "";
            try
            {
                Vector3 targetPos;
                string  label;

                if (playState == "PreSnap" || playState == "BallSnapped")
                {
                    object qb = _fldQb?.GetValue(_matchInst);
                    var qbComp = qb as Component;
                    if (qbComp == null) return "";
                    targetPos = qbComp.transform.position;
                    label     = "QB";
                }
                else
                {
                    object ball = _fldBall?.GetValue(_matchInst);
                    var ballComp = ball as Component;
                    if (ballComp == null) return "";
                    targetPos = ballComp.transform.position;
                    label     = "Ball";
                }

                string stick = StickDirection(playerComp, targetPos);
                return string.IsNullOrEmpty(stick) ? "" : $"{label} {stick}";
            }
            catch { return ""; }
        }

        private static string GetBallDirection(Component playerComp)
        {
            if (playerComp == null || _fldBall == null) return "";
            try
            {
                object ball    = _fldBall.GetValue(_matchInst);
                var ballComp   = ball as Component;
                if (ballComp == null) return "";

                int meters = Mathf.RoundToInt(
                    Vector3.Distance(playerComp.transform.position, ballComp.transform.position));
                if (meters <= 2) return "On you";

                string stick = StickDirection(playerComp, ballComp.transform.position);
                return string.IsNullOrEmpty(stick)
                    ? $"{meters}m"
                    : $"{stick}, {meters}m";
            }
            catch { return ""; }
        }

        private static string StickDirection(Component playerComp, Vector3 targetPos)
        {
            Vector3 toBall = targetPos - playerComp.transform.position;
            if (toBall.sqrMagnitude < 0.01f) return "";
            Vector3 local = playerComp.transform.InverseTransformDirection(toBall.normalized);

            string fwd = local.z >=  0.35f ? "up"
                       : local.z <= -0.35f ? "down"
                       : "";
            string lat = local.x >=  0.35f ? "right"
                       : local.x <= -0.35f ? "left"
                       : "";

            if (!string.IsNullOrEmpty(fwd) && !string.IsNullOrEmpty(lat))
                return $"{fwd}-{lat}";
            return string.IsNullOrEmpty(fwd) ? lat : fwd;
        }

        // ---- Field position ----

        private static string GetFieldPosition(Component playerComp)
        {
            if (playerComp == null || _fldScrimmageLine == null || _fldIsHomeDefense == null)
                return "";
            try
            {
                var scrimmageGo = _fldScrimmageLine.GetValue(_matchInst) as GameObject;
                if (scrimmageGo == null) return "";

                bool isHomeDefense = (bool)(_fldIsHomeDefense.GetValue(_matchInst) ?? false);
                bool goingNorth    = !isHomeDefense;

                Vector3 pos        = playerComp.transform.position;
                float   scrimmageZ = scrimmageGo.transform.position.z;

                float depthM = goingNorth ? pos.z - scrimmageZ : scrimmageZ - pos.z;
                int   depthYards = Mathf.RoundToInt(depthM * 1.0936f);

                string depth = depthYards <= 0 ? "at the line"
                             : depthYards == 1 ? "1 yard back"
                             : $"{depthYards} yards back";

                float  x       = pos.x;
                string lateral = x < -16f ? "wide left"
                               : x <  -6f ? "left hash"
                               : x >  16f ? "wide right"
                               : x >   6f ? "right hash"
                               : "middle";

                return $"{lateral}, {depth}";
            }
            catch { return ""; }
        }

        // ---- L3 on-demand readout ----

        private static void AnnouncePositionOnDemand()
        {
            var comp = _lastSelectedPlayerRef as Component;
            string pos = GetFieldPosition(comp);
            string dir = (_lastPlayState == "BallInAir")
                ? GetBallDirection(comp)
                : GetTargetDirection(comp, _lastPlayState);

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(pos)) sb.Append(pos);
            if (!string.IsNullOrEmpty(dir))
            {
                if (sb.Length > 0) sb.Append(". ");
                sb.Append(dir);
            }
            if (sb.Length > 0) SpeechManager.Speak(sb.Append('.').ToString());
        }

        // ---- Labels ----

        private static string FormatBehaviorState(string state)
        {
            if (string.IsNullOrEmpty(state)) return "";
            switch (state)
            {
                case "PassCoverage":                 return "In coverage";
                case "RunPursuit":                   return "In pursuit";
                case "Blitzing":                     return "Blitzing";
                case "RushingPasser":                return "Rushing passer";
                case "TryingToCatchBallInAir":       return "On it!";
                case "TryingToKnockBallAway":        return "Going for deflection!";
                case "RunningTowardBallLandingSpot": return "Running to ball";
                case "TryingToPickUpFumble":
                case "TryingToPickupGroundBall":     return "Loose ball!";
                default:                             return "";
            }
        }

        private static string FormatPosition(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Defender";
            switch (raw.ToUpperInvariant())
            {
                case "CB1":                              return "Cornerback 1";
                case "CB2":                              return "Cornerback 2";
                case "MLB":                              return "Middle linebacker";
                case "OLB": case "OLB1": case "OLB2":   return "Outside linebacker";
                case "ILB": case "ILB1": case "ILB2":   return "Inside linebacker";
                case "SS":                               return "Strong safety";
                case "FS":                               return "Free safety";
                case "DL1": case "DL2": case "DL3": case "DL4": return "Defensive lineman";
                case "DE1": case "DE2":                  return "Defensive end";
                case "DT": case "NT":                    return "Defensive tackle";
                default:                                 return raw;
            }
        }

        // ---- Velocity helper (avoids direct Rigidbody reference which is in a separate module) ----

        private static Vector3 GetVelocity(Component comp)
        {
            if (comp == null) return Vector3.zero;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                // Try the character rigidbody field first (ProtocadeCharacter.rigidbody)
                foreach (var f in comp.GetType().GetFields(flags))
                {
                    if (f.Name != "rigidbody" && f.Name != "_rigidbody") continue;
                    var rb = f.GetValue(comp);
                    if (rb == null) continue;
                    var velProp = rb.GetType().GetProperty("velocity",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (velProp != null) return (Vector3)velProp.GetValue(rb, null);
                }
                // Fallback: look for any component with a velocity property on the same GO
                foreach (var c in comp.gameObject.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var velProp = c.GetType().GetProperty("velocity",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (velProp != null && velProp.PropertyType == typeof(Vector3))
                        return (Vector3)velProp.GetValue(c, null);
                }
            }
            catch { }
            return Vector3.zero;
        }

        // ---- Reset ----

        private static void ResetState()
        {
            if (_r1WasActive) RestorePlayerControl();
            _wasInGame                   = false;
            _lastPlayState               = "";
            _lastSelectedPlayerRef       = null;
            _lastControlledBehaviorState = "";
            _ballInAirAnnounced          = false;
            _defenderOnItAnnounced       = false;
            _lastDirTime                 = -10f;
            _l3WasDown                   = false;
            _r1WasActive                 = false;
            _pendingPlayerAnnouncement   = "";
            _pendingPlayerTime           = -1f;
            _reflDone                    = false;
            _matchInst                   = null;
            VibrationManager.Stop();
        }

        // ---- Reflection bootstrap ----

        private static bool EnsureRefs()
        {
            if (_reflDone) return _matchInst != null && _fldPlayState != null;
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

                    var matchType = _matchInst.GetType();
                    _fldPlayState        = matchType.GetField("playState",        flags);
                    _fldPlayType         = matchType.GetField("playType",         flags);
                    _fldDc               = matchType.GetField("dc",               flags);
                    _fldDefensivePlayers = matchType.GetField("defensivePlayers",  flags);
                    _fldBall             = matchType.GetField("Ball",              flags);
                    _fldQb               = matchType.GetField("qb",               flags);
                    _fldIsHomeDefense    = matchType.GetField("isHomeDefense",     flags);
                    _fldScrimmageLine    = matchType.GetField("ScrimmageLine",     flags);
                    _fldPlayerWithBall   = matchType.GetField("PlayerWithBall",    flags);

                    // Navigate dc → gc → SelectedPlayer → logic → character type chain
                    if (_fldDc != null)
                    {
                        var dcType = _fldDc.FieldType;
                        _fldDcGc = dcType.GetField("gc", flags);

                        if (_fldDcGc != null)
                        {
                            var gcType = _fldDcGc.FieldType;
                            _fldGcSelectedPlayer = gcType.GetField("SelectedPlayer", flags);
                            _fldGcIsAI           = gcType.GetField("isAI",           flags);

                            if (_fldGcSelectedPlayer != null)
                            {
                                var fpType = _fldGcSelectedPlayer.FieldType;
                                _fldFpLogic = fpType.GetField("logic", flags);

                                if (_fldFpLogic != null)
                                {
                                    var fplType = _fldFpLogic.FieldType; // FootballPlayerLogic
                                    _fldBehaviorState = fplType.GetField("behaviorState",     flags);
                                    _fldPlayerData    = fplType.GetField("footballPlayerData", flags);
                                    _fldCharacter     = fplType.GetField("character",          flags);

                                    // SetBehaviorState method
                                    _methSetBehaviorState = fplType.GetMethod("SetBehaviorState",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                                    // Heat-seek movement fields
                                    _fldAllowPlayerControl     = fplType.GetField("allowPlayerControl",    flags);
                                    _fldAssignmentTargets      = fplType.GetField("AssignmentTargets",     flags);
                                    _fldAssignmentPathComplete = fplType.GetField("AssignmentPathComplete", flags);
                                    _fldThrownBallReactCounter = fplType.GetField("thrownBallReactCounter", flags);

                                    if (_fldPlayerData != null)
                                    {
                                        var fpdType = _fldPlayerData.FieldType;
                                        _fldLastName = fpdType.GetField("LastName", flags);
                                    }

                                    // Navigate character → controlState (may be on a base class)
                                    if (_fldCharacter != null)
                                    {
                                        Type charType = _fldCharacter.FieldType;
                                        Type t = charType;
                                        while (t != null && _fldControlState == null)
                                        {
                                            _fldControlState = t.GetField("controlState",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            t = t.BaseType;
                                        }

                                        // Cache enum values for ControlState and BehaviorState
                                        if (_fldControlState != null)
                                        {
                                            var csType = _fldControlState.FieldType;
                                            try { _enumAIInControl    = Enum.Parse(csType, "AIInControl");    } catch { }
                                            try { _enumPlayerInControl= Enum.Parse(csType, "PlayerInControl");} catch { }
                                        }
                                        if (_fldBehaviorState != null)
                                        {
                                            var bsType = _fldBehaviorState.FieldType;
                                            try { _enumRunPursuit   = Enum.Parse(bsType, "RunPursuit");              } catch { }
                                            try { _enumTryingToCatch= Enum.Parse(bsType, "TryingToCatchBallInAir");  } catch { }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    Plugin.Log.LogInfo(
                        $"[DefenseReader] match={_matchInst != null} playState={_fldPlayState != null} " +
                        $"dc={_fldDc != null} dcGc={_fldDcGc != null} " +
                        $"selectedPlayer={_fldGcSelectedPlayer != null} fpLogic={_fldFpLogic != null} " +
                        $"behaviorState={_fldBehaviorState != null} lastName={_fldLastName != null} " +
                        $"ball={_fldBall != null} qb={_fldQb != null} pwb={_fldPlayerWithBall != null} " +
                        $"character={_fldCharacter != null} controlState={_fldControlState != null} " +
                        $"AIInControl={_enumAIInControl != null} PlayerInControl={_enumPlayerInControl != null} " +
                        $"RunPursuit={_enumRunPursuit != null} TryingToCatch={_enumTryingToCatch != null} " +
                        $"allowPlayerControl={_fldAllowPlayerControl != null} " +
                        $"assignmentTargets={_fldAssignmentTargets != null} " +
                        $"assignmentPathComplete={_fldAssignmentPathComplete != null}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DefenseReader] EnsureRefs: {ex.Message}");
            }

            return _matchInst != null && _fldPlayState != null;
        }
    }
}
