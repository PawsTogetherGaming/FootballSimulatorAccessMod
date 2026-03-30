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

        // FootballPlayerLogic → character → controlState
        private static FieldInfo _fldCharacter            = null;
        private static FieldInfo _fldControlState         = null;

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

            // Engage AI control
            try
            {
                _fldAllowPlayerControl?.SetValue(logic, false);
                _fldControlState?.SetValue(charInst, _enumAIInControl);

                if (_methSetBehaviorState != null && _enumRunningWithBall != null)
                    _methSetBehaviorState.Invoke(logic, new[] { _enumRunningWithBall });
                else if (_fldBehaviorState != null && _enumRunningWithBall != null)
                    _fldBehaviorState.SetValue(logic, _enumRunningWithBall);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[OffensiveAssistReader] ApplyAssist control: {ex.Message}");
                return;
            }

            // Build target position in the best open lane
            Vector3 targetPos = BuildLaneTarget(carrier, bestDir);

            // Write AssignmentTargets so the AI movement loop runs
            try
            {
                var targets = _fldAssignmentTargets?.GetValue(logic) as List<Vector3>;
                if (targets != null)
                {
                    targets.Clear();
                    targets.Add(targetPos);
                }
                _fldAssignmentPathComplete?.SetValue(logic, false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[OffensiveAssistReader] ApplyAssist targets: {ex.Message}");
            }

            if (!_assistActive)
            {
                _assistActive = true;
                SpeechManager.Speak("Run assist on.");
            }
        }

        // Calculates a world-space waypoint in the chosen lane, LANE_LOOKAHEAD metres ahead.
        private static Vector3 BuildLaneTarget(Component carrier, string bestDir)
        {
            try
            {
                bool isHomeDefense = false;
                try { isHomeDefense = (bool)(_fldIsHomeDefense?.GetValue(_matchInst) ?? false); }
                catch { }

                // "Ahead" in world space depends on which direction the offence is advancing.
                // isHomeDefense=true means the home team is defending their own end, so the
                // offence (away) is going north (+Z). We invert for the team on offence.
                float forwardZ = isHomeDefense ? 1f : -1f;

                Vector3 pos = carrier.transform.position;

                // Lateral offset: aim for centre of zone
                float lateralX = bestDir == "left"  ? -LANE_OFFSET_SIDE
                               : bestDir == "right" ?  LANE_OFFSET_SIDE
                               : pos.x;             // "straight" — keep current X

                return new Vector3(lateralX, pos.y, pos.z + forwardZ * LANE_LOOKAHEAD);
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
            if (power <= 3)
            {
                if (string.IsNullOrEmpty(bestDir)) return "";
                int bestCount  = bestDir == "left" ? freeLeft : (bestDir == "right" ? freeRight : freeMiddle);
                int worstCount = Math.Max(freeLeft, Math.Max(freeMiddle, freeRight));
                if (worstCount - bestCount < 2) return "";
                return $"Open {bestDir}.";
            }
            if (power <= 6)
            {
                return string.IsNullOrEmpty(bestDir) ? "" : $"Open {bestDir}.";
            }
            // Power 7–9
            if (string.IsNullOrEmpty(bestDir)) return "";
            string facing = GetCarrierFacingZone(carrier);
            return facing == bestDir
                ? $"Stay {bestDir} — lane open."
                : $"Cut {bestDir}!";
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
                        $"RunningWithBall={_enumRunningWithBall != null}");
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
