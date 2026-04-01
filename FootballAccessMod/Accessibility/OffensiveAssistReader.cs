using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Offensive run-assist — guides the ball carrier through traffic.
    ///
    /// Power levels (ModSettings.OffensiveAssistPower):
    ///   0     = disabled
    ///   1–3   = audio hints only: announces open lane direction when a clear gap exists
    ///   4–6   = frequent hints + blocker calls
    ///   7–9   = cut-timing calls ("Cut left now!")
    ///  10     = FULL TAKEOVER — sets AIInControl, steers AssignmentTargets into the best
    ///           open lane every poll, right-motor vibration tracks lane quality.
    ///           Returns control to the player as soon as power drops below 10 or the
    ///           play ends.
    ///
    /// Lane detection: scans all defensive players, classifies which lateral zone has the
    /// fewest unblocked defenders within threat range. A defender is "unblocked" if their
    /// logic.Blocker field is null.
    /// </summary>
    public static class OffensiveAssistReader
    {
        // ---- Reflection refs ----
        private static bool      _reflDone                = false;
        private static object    _matchInst               = null;

        private static FieldInfo _fldPlayState            = null;
        private static FieldInfo _fldPlayerWithBall       = null;
        private static FieldInfo _fldDefensivePlayers     = null;
        private static FieldInfo _fldIsHomeDefense        = null;

        // FootballPlayer → logic
        private static FieldInfo _fldFpLogic              = null;

        // FootballPlayerLogic fields
        private static FieldInfo _fldBlocker              = null;
        private static FieldInfo _fldAllowPlayerControl   = null;
        private static FieldInfo _fldAssignmentTargets    = null;
        private static FieldInfo _fldAssignmentPathComplete = null;
        private static FieldInfo _fldBehaviorState        = null;
        private static MethodInfo _methSetBehaviorState   = null;
        private static FieldInfo _fldClosestDefenderDist  = null;
        private static FieldInfo _fldClosestDefender      = null;
        private static FieldInfo _fldLastTimeTouched      = null;

        // FootballPlayerLogic → character → controlState
        private static FieldInfo _fldCharacter            = null;
        private static FieldInfo _fldControlState         = null;

        // character → characterActions → evasion methods
        private static FieldInfo  _fldCharacterActions    = null;
        private static MethodInfo _methSpeedBoost         = null;
        private static MethodInfo _methStiffArm           = null;
        private static MethodInfo _methPowerRush          = null;
        private static MethodInfo _methDive               = null;

        // character → animator (for cooldown checks)
        private static FieldInfo  _fldAnimator            = null;

        // Human-side guard: oc.gc.isAI — true means CPU controls offense this possession
        private static FieldInfo _fldOc                   = null;   // FootballMatch.oc
        private static FieldInfo _fldOcGc                 = null;   // OffensiveCoordinator.gc
        private static FieldInfo _fldGcIsAI               = null;   // GameController.isAI

        // Enum values
        private static object    _enumAIInControl         = null;
        private static object    _enumPlayerInControl     = null;
        private static object    _enumRunningWithBall     = null;

        // ---- State ----
        private static bool   _wasInGame                  = false;
        private static bool   _assistActive               = false;  // power-10 takeover engaged
        private static string _lastHint                   = "";
        private static float  _lastHintTime               = -10f;
        private static bool   _laneOpenVibrated           = false;
        private static float  _lastBoostTime              = -10f;
        private static float  _lastEvasionTime            = -10f;

        // Cooldown range (seconds)
        private const float HINT_COOLDOWN_MAX             = 3.5f;  // power 1
        private const float HINT_COOLDOWN_MIN             = 0.8f;  // power 9

        // Lateral zones (world X)
        private const float ZONE_HASH                     = 6f;

        // Threat scan radius
        private const float THREAT_RANGE                  = 12f;

        // Target point: metres ahead of carrier in best lane
        private const float LANE_LOOKAHEAD                = 10f;
        // Lateral offset to aim for the centre of each zone
        private const float LANE_OFFSET_SIDE              = 10f;

        // =========================================================
        // Poll — called at the 120 ms throttled rate.
        // =========================================================

        public static void Poll()
        {
            int power = ModSettings.OffensiveAssistPower?.Value ?? 5;

            if (power == 0)
            {
                if (_assistActive) ReleaseControl();
                VibrationManager.SetRight(0f);
                return;
            }

            var matchUI = GameObject.Find("Football Match UI");
            bool inGame = matchUI != null && matchUI.activeInHierarchy;

            if (!inGame)
            {
                if (_wasInGame) ResetState();
                return;
            }

            if (!EnsureRefs()) return;
            _wasInGame = true;

            // Guard: only apply offensive assist when the offense is human-controlled.
            // oc.gc.isAI == true means the CPU is controlling the offense this possession.
            // Fail closed: if we can't verify the human is on offense, don't run assists.
            {
                bool humanOnOffense = false;
                if (_fldOc != null && _fldOcGc != null && _fldGcIsAI != null)
                {
                    try
                    {
                        object oc = _fldOc.GetValue(_matchInst);
                        if (oc != null)
                        {
                            object gc = _fldOcGc.GetValue(oc);
                            if (gc != null)
                                humanOnOffense = !(bool)(_fldGcIsAI.GetValue(gc) ?? true);
                        }
                    }
                    catch { }
                }
                if (!humanOnOffense)
                {
                    if (_assistActive) ReleaseControl();
                    return;
                }
            }

            string playState = "";
            try { playState = _fldPlayState?.GetValue(_matchInst)?.ToString() ?? ""; }
            catch { return; }

            // Only active while someone is running with the ball
            if (playState != "PlayerHasBall")
            {
                if (_assistActive) ReleaseControl();
                _lastHint         = "";
                _laneOpenVibrated = false;
                VibrationManager.SetRight(0f);
                return;
            }

            Component carrier = null;
            try { carrier = (_fldPlayerWithBall?.GetValue(_matchInst)) as Component; }
            catch { }
            if (carrier == null) { if (_assistActive) ReleaseControl(); return; }

            // ---- Scan defenders: count unblocked per lateral zone ----
            int freeLeft   = 0;
            int freeMiddle = 0;
            int freeRight  = 0;

            IEnumerable defenders = null;
            try { defenders = _fldDefensivePlayers?.GetValue(_matchInst) as IEnumerable; }
            catch { }

            if (defenders != null)
            {
                Vector3 carrierPos = carrier.transform.position;
                foreach (object def in defenders)
                {
                    if (def == null) continue;
                    try
                    {
                        var defComp = def as Component;
                        if (defComp == null) continue;

                        float xzDist = Vector2.Distance(
                            new Vector2(carrierPos.x, carrierPos.z),
                            new Vector2(defComp.transform.position.x, defComp.transform.position.z));
                        if (xzDist > THREAT_RANGE) continue;

                        object logic   = _fldFpLogic?.GetValue(defComp);
                        if (logic == null) continue;
                        object blocker = _fldBlocker?.GetValue(logic);
                        if (blocker != null) continue;  // blocked — not a threat

                        float defX = defComp.transform.position.x;
                        if      (defX < -ZONE_HASH) freeLeft++;
                        else if (defX >  ZONE_HASH) freeRight++;
                        else                         freeMiddle++;
                    }
                    catch { }
                }
            }

            // Best lane
            string bestDir = PickBestDir(freeLeft, freeMiddle, freeRight);
            bool   laneOpen = !string.IsNullOrEmpty(bestDir);

            // ---- Power 10: FULL TAKEOVER ----
            if (power >= 10)
            {
                ApplyAssist(carrier, bestDir, freeLeft, freeMiddle, freeRight, laneOpen);
                return;
            }

            // ---- Power 1–9: release takeover if it was active ----
            if (_assistActive) ReleaseControl();

            // ---- Audio hints (power 1–9) ----
            string hint = PickLaneHint(freeLeft, freeMiddle, freeRight, carrier, power, bestDir);

            // Vibration (power 7–9: one-shot buzz when lane opens)
            if (power >= 7)
            {
                if (laneOpen && !_laneOpenVibrated)
                {
                    VibrationManager.SetRight(0.5f);
                    _laneOpenVibrated = true;
                }
                else if (!laneOpen)
                {
                    VibrationManager.SetRight(0f);
                    _laneOpenVibrated = false;
                }
            }

            if (string.IsNullOrEmpty(hint)) { _lastHint = ""; return; }

            float cooldown     = Mathf.Lerp(HINT_COOLDOWN_MAX, HINT_COOLDOWN_MIN, (power - 1) / 8f);
            bool  newHint      = hint != _lastHint;
            bool  cooldownDone = (Time.unscaledTime - _lastHintTime) >= cooldown;

            if (newHint || cooldownDone)
            {
                _lastHint     = hint;
                _lastHintTime = Time.unscaledTime;
                SpeechManager.Speak(hint);
            }
        }

        // =========================================================
        // Power 10: apply AI takeover — steers carrier into best lane.
        // =========================================================

        private static void ApplyAssist(Component carrier, string bestDir,
                                        int freeLeft, int freeMiddle, int freeRight,
                                        bool laneOpen)
        {
            // ---- Vibration: tracks lane quality continuously ----
            int   bestCount    = Math.Min(freeLeft, Math.Min(freeMiddle, freeRight));
            float vibStrength  = laneOpen ? Mathf.Lerp(0.8f, 0.3f, bestCount / 3f) : 0f;
            VibrationManager.SetRight(vibStrength);

            // Get carrier logic object
            object logic = null;
            try { logic = _fldFpLogic?.GetValue(carrier); } catch { }
            if (logic == null) { if (_assistActive) ReleaseControl(); return; }

            object charInst = null;
            try { charInst = _fldCharacter?.GetValue(logic); } catch { }
            if (charInst == null) { if (_assistActive) ReleaseControl(); return; }

            // Engage AI control — let the game's own AIRunWithBall() handle movement.
            // We set AssignmentPathComplete=true and clear AssignmentTargets so that
            // ProcessAssignment() skips the manual target-following code and falls
            // through to AIRunWithBall(), which:
            //   - uses GetTowardEndzone() for the correct direction
            //   - evades defenders automatically
            //   - performs stiff arms and power rushes
            //   - has a safety check that prevents running backward
            try
            {
                _fldAllowPlayerControl?.SetValue(logic, false);
                _fldControlState?.SetValue(charInst, _enumAIInControl);

                if (_methSetBehaviorState != null && _enumRunningWithBall != null)
                    _methSetBehaviorState.Invoke(logic, new[] { _enumRunningWithBall });
                else if (_fldBehaviorState != null && _enumRunningWithBall != null)
                    _fldBehaviorState.SetValue(logic, _enumRunningWithBall);

                // Clear targets and mark path complete so the game routes into
                // AIRunWithBall() instead of MoveTowardVector().
                _fldAssignmentPathComplete?.SetValue(logic, true);
                var targets = _fldAssignmentTargets?.GetValue(logic) as List<Vector3>;
                targets?.Clear();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[OffensiveAssistReader] ApplyAssist control: {ex.Message}");
                return;
            }

            if (!_assistActive)
            {
                _assistActive = true;
                SpeechManager.Speak("Run assist on.");
            }

            // ---- Active play-making: speed boost + evasion moves ----
            TriggerActions(logic, charInst, carrier);
        }

        // Triggers speed boost when the field is open, and evasion moves
        // (stiff arm, power rush, dive) when defenders are close.
        private static void TriggerActions(object logic, object charInst, Component carrier)
        {
            // Read closest defender distance from logic
            float defDist = 1000f;
            try { defDist = (float)(_fldClosestDefenderDist?.GetValue(logic) ?? 1000f); } catch { }

            // Get characterActions object for triggering moves
            object actions = null;
            try { actions = _fldCharacterActions?.GetValue(charInst); } catch { }
            if (actions == null) return;

            // ---- Speed boost when no defenders nearby ----
            if (defDist > 8f && (Time.unscaledTime - _lastBoostTime) > 2f)
            {
                try
                {
                    _methSpeedBoost?.Invoke(actions, null);
                    _lastBoostTime = Time.unscaledTime;
                }
                catch { }
            }

            // ---- Evasion moves when a defender is closing in ----
            if (defDist > 4f) return;   // only act when defender is within 4m
            if ((Time.unscaledTime - _lastEvasionTime) < 0.5f) return;  // cooldown

            // Check animator bools to avoid triggering during an active move.
            // Use reflection to call GetBool since UnityEngine.AnimationModule may not be referenced.
            object animator = null;
            try { animator = _fldAnimator?.GetValue(charInst); } catch { }
            if (animator != null)
            {
                try
                {
                    var getBoolMethod = animator.GetType().GetMethod("GetBool",
                        new[] { typeof(string) });
                    if (getBoolMethod != null)
                    {
                        bool inStiff = (bool)(getBoolMethod.Invoke(animator, new object[] { "InStiffArm" }) ?? false);
                        bool inRush  = (bool)(getBoolMethod.Invoke(animator, new object[] { "InPowerRush" }) ?? false);
                        if (inStiff || inRush) return;
                    }
                }
                catch { }
            }

            // Check last-touched cooldown (mirrors game's logic)
            float lastTouched = 0f;
            try { lastTouched = (float)(_fldLastTimeTouched?.GetValue(logic) ?? 0f); } catch { }

            // Determine defender direction relative to carrier
            Component defComp = null;
            try
            {
                object defObj = _fldClosestDefender?.GetValue(logic);
                defComp = defObj as Component;
            }
            catch { }

            if (defComp != null)
            {
                Vector3 toDefender = defComp.transform.position - carrier.transform.position;

                // Defender coming from the side → stiff arm toward them
                if (Mathf.Abs(toDefender.x) > Mathf.Abs(toDefender.z + 0.1f)
                    && (Time.time - lastTouched) > 1f)
                {
                    try
                    {
                        bool isLeft = toDefender.x < 0f;
                        _methStiffArm?.Invoke(actions, new object[] { isLeft });
                        _lastEvasionTime = Time.unscaledTime;
                        return;
                    }
                    catch { }
                }

                // Defender ahead → power rush / truck
                if ((Time.time - lastTouched) > 0.5f)
                {
                    try
                    {
                        _methPowerRush?.Invoke(actions, null);
                        _lastEvasionTime = Time.unscaledTime;
                        return;
                    }
                    catch { }
                }
            }
        }

        // Calculates a world-space waypoint in the chosen lane, LANE_LOOKAHEAD metres ahead.
        // Uses isHomeDefense to determine downfield Z direction — this is reliable regardless
        // of which way the carrier's model is facing (spin moves, cuts, handoff animations
        // can all cause transform.forward to point toward the wrong end zone).
        private static Vector3 BuildLaneTarget(Component carrier, string bestDir)
        {
            try
            {
                Vector3 pos = carrier.transform.position;

                // isHomeDefense=true  → away is on offense → going south (−Z)
                // isHomeDefense=false → home is on offense → going north (+Z)
                bool ihd = false;
                try { ihd = (bool)(_fldIsHomeDefense?.GetValue(_matchInst) ?? false); } catch { }
                float downfieldZ = ihd ? -1f : 1f;

                float lateralX = bestDir == "left"  ? -LANE_OFFSET_SIDE
                               : bestDir == "right" ?  LANE_OFFSET_SIDE
                               : pos.x;   // "straight" — keep current X

                return new Vector3(lateralX, pos.y, pos.z + downfieldZ * LANE_LOOKAHEAD);
            }
            catch
            {
                return carrier.transform.position;
            }
        }

        // =========================================================
        // Release control back to the player.
        // =========================================================

        private static void ReleaseControl()
        {
            _assistActive     = false;
            _laneOpenVibrated = false;
            VibrationManager.SetRight(0f);

            Component carrier = null;
            try { carrier = (_fldPlayerWithBall?.GetValue(_matchInst)) as Component; }
            catch { }
            if (carrier == null) return;

            try
            {
                object logic    = _fldFpLogic?.GetValue(carrier);
                if (logic == null) return;
                object charInst = _fldCharacter?.GetValue(logic);
                if (charInst == null) return;

                _fldAllowPlayerControl?.SetValue(logic, true);
                _fldControlState?.SetValue(charInst, _enumPlayerInControl);
                _fldAssignmentPathComplete?.SetValue(logic, true);
                var targets = _fldAssignmentTargets?.GetValue(logic) as List<Vector3>;
                targets?.Clear();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[OffensiveAssistReader] ReleaseControl: {ex.Message}");
            }
        }

        // =========================================================
        // Lane helpers
        // =========================================================

        private static string PickBestDir(int freeLeft, int freeMiddle, int freeRight)
        {
            int min = Math.Min(freeLeft, Math.Min(freeMiddle, freeRight));
            if      (freeLeft   == min && freeLeft   < freeRight  && freeLeft   < freeMiddle) return "left";
            else if (freeRight  == min && freeRight  < freeLeft   && freeRight  < freeMiddle) return "right";
            else if (freeMiddle == min && freeMiddle < freeLeft   && freeMiddle < freeRight)  return "straight";
            return "";
        }

        private static string PickLaneHint(int freeLeft, int freeMiddle, int freeRight,
                                           Component carrier, int power, string bestDir)
        {
            if (string.IsNullOrEmpty(bestDir)) return "";

            // Convert world-space zone name to carrier-relative direction.
            // Zones are classified by world X: "left"=world −X, "right"=world +X.
            // If the carrier faces south (fwd.z < 0), world −X is actually their right.
            string spokenDir = WorldZoneToCarrierDir(carrier, bestDir);

            if (power <= 3)
            {
                int bestCount  = bestDir == "left" ? freeLeft : (bestDir == "right" ? freeRight : freeMiddle);
                int worstCount = Math.Max(freeLeft, Math.Max(freeMiddle, freeRight));
                if (worstCount - bestCount < 2) return "";
                return $"Open {spokenDir}.";
            }
            if (power <= 6)
            {
                return $"Open {spokenDir}.";
            }
            // Power 7–9
            string facing = GetCarrierFacingZone(carrier);
            return facing == spokenDir
                ? $"Stay {spokenDir} — lane open."
                : $"Cut {spokenDir}!";
        }

        // Converts a world-X zone name ("left"=−X, "right"=+X, "straight") into
        // a carrier-relative direction so audio cues match what the player sees.
        private static string WorldZoneToCarrierDir(Component carrier, string worldZone)
        {
            if (worldZone == "straight") return "straight";
            try
            {
                // If carrier faces south (fwd.z < 0), world left/right are swapped.
                float fwdZ = carrier.transform.forward.z;
                bool flipped = fwdZ < 0f;
                if (!flipped) return worldZone;
                return worldZone == "left" ? "right" : "left";
            }
            catch { return worldZone; }
        }

        private static string GetCarrierFacingZone(Component carrier)
        {
            try
            {
                Vector3 vel = GetVelocity(carrier);
                if (vel.sqrMagnitude < 0.1f) return "straight";
                if      (vel.x < -1.5f) return "left";
                else if (vel.x >  1.5f) return "right";
                else                    return "straight";
            }
            catch { return "straight"; }
        }

        // Velocity helper — avoids direct Rigidbody reference (separate physics module).
        private static Vector3 GetVelocity(Component comp)
        {
            if (comp == null) return Vector3.zero;
            try
            {
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

        // =========================================================
        // Reset
        // =========================================================

        private static void ResetState()
        {
            if (_assistActive) ReleaseControl();
            _wasInGame        = false;
            _assistActive     = false;
            _lastHint         = "";
            _lastHintTime     = -10f;
            _laneOpenVibrated = false;
            _reflDone         = false;
            _matchInst        = null;
            VibrationManager.SetRight(0f);
        }

        // =========================================================
        // Reflection bootstrap
        // =========================================================

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
                    _fldPlayerWithBall   = matchType.GetField("PlayerWithBall",   flags);
                    _fldDefensivePlayers = matchType.GetField("defensivePlayers",  flags);
                    _fldIsHomeDefense    = matchType.GetField("isHomeDefense",     flags);

                    // Cache oc → gc → isAI for human-side guard
                    _fldOc = matchType.GetField("oc", flags);
                    if (_fldOc != null)
                    {
                        var ocType = _fldOc.FieldType;
                        _fldOcGc = ocType.GetField("gc", flags);
                        if (_fldOcGc != null)
                            _fldGcIsAI = _fldOcGc.FieldType.GetField("isAI", flags);
                    }

                    // FootballPlayer → logic → all needed fields
                    if (_fldPlayerWithBall != null)
                    {
                        var fpType  = _fldPlayerWithBall.FieldType;
                        _fldFpLogic = fpType.GetField("logic", flags);

                        if (_fldFpLogic != null)
                        {
                            var fplType = _fldFpLogic.FieldType;  // FootballPlayerLogic

                            _fldBlocker               = fplType.GetField("Blocker",             flags);
                            _fldAllowPlayerControl    = fplType.GetField("allowPlayerControl",   flags);
                            _fldAssignmentTargets     = fplType.GetField("AssignmentTargets",    flags);
                            _fldAssignmentPathComplete= fplType.GetField("AssignmentPathComplete", flags);
                            _fldBehaviorState         = fplType.GetField("behaviorState",        flags);
                            _fldCharacter             = fplType.GetField("character",            flags);
                            _fldClosestDefenderDist   = fplType.GetField("ClosestDefenderDistance", flags);
                            _fldClosestDefender       = fplType.GetField("ClosestDefender",      flags);
                            _fldLastTimeTouched       = fplType.GetField("LastTimeTouchedByDefender", flags);

                            _methSetBehaviorState = fplType.GetMethod("SetBehaviorState",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                            // Enum values for BehaviorState
                            if (_fldBehaviorState != null)
                            {
                                var bsType = _fldBehaviorState.FieldType;
                                try { _enumRunningWithBall = Enum.Parse(bsType, "RunningWithBall"); } catch { }
                            }

                            // character → controlState enum values
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
                                if (_fldControlState != null)
                                {
                                    var csType = _fldControlState.FieldType;
                                    try { _enumAIInControl    = Enum.Parse(csType, "AIInControl");     } catch { }
                                    try { _enumPlayerInControl= Enum.Parse(csType, "PlayerInControl"); } catch { }
                                }

                                // character → animator
                                _fldAnimator = charType.GetField("animator", flags);
                                // Walk up base types if needed
                                t = charType;
                                while (t != null && _fldAnimator == null)
                                {
                                    _fldAnimator = t.GetField("animator", flags);
                                    t = t.BaseType;
                                }

                                // character → characterActions → evasion methods
                                _fldCharacterActions = charType.GetField("characterActions", flags);
                                if (_fldCharacterActions == null)
                                {
                                    // Walk base types
                                    t = charType.BaseType;
                                    while (t != null && _fldCharacterActions == null)
                                    {
                                        _fldCharacterActions = t.GetField("characterActions", flags);
                                        t = t.BaseType;
                                    }
                                }
                                if (_fldCharacterActions != null)
                                {
                                    var caType = _fldCharacterActions.FieldType;
                                    _methSpeedBoost = caType.GetMethod("SpeedBoost", flags);
                                    _methStiffArm   = caType.GetMethod("StiffArm",   flags);
                                    _methPowerRush  = caType.GetMethod("PowerRush",  flags);
                                    _methDive       = caType.GetMethod("Dive",       flags, null, Type.EmptyTypes, null);
                                    // Dive has overloads — get the parameterless one
                                    if (_methDive == null)
                                        _methDive = caType.GetMethod("Dive", flags);
                                }
                            }
                        }
                    }

                    Plugin.Log.LogInfo(
                        $"[OffensiveAssistReader] match={_matchInst != null} " +
                        $"playState={_fldPlayState != null} pwb={_fldPlayerWithBall != null} " +
                        $"fpLogic={_fldFpLogic != null} blocker={_fldBlocker != null} " +
                        $"defPlayers={_fldDefensivePlayers != null} " +
                        $"allowPlayerControl={_fldAllowPlayerControl != null} " +
                        $"assignTargets={_fldAssignmentTargets != null} " +
                        $"character={_fldCharacter != null} controlState={_fldControlState != null} " +
                        $"AIInControl={_enumAIInControl != null} PlayerInControl={_enumPlayerInControl != null} " +
                        $"RunningWithBall={_enumRunningWithBall != null} " +
                        $"charActions={_fldCharacterActions != null} speedBoost={_methSpeedBoost != null} " +
                        $"stiffArm={_methStiffArm != null} powerRush={_methPowerRush != null} " +
                        $"closestDefDist={_fldClosestDefenderDist != null} animator={_fldAnimator != null}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[OffensiveAssistReader] EnsureRefs: {ex.Message}");
            }

            return _matchInst != null && _fldPlayState != null;
        }
    }
}
