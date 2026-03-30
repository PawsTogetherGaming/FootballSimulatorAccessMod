using System;
using System.Reflection;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Offensive proximity feedback.
    ///
    /// Left motor ramps up as the closest defender closes on the ball carrier.
    /// When they get within tackle range the carrier hears a directional evasion
    /// prompt: "Left! L1" / "Right! R1" / "Straight! B" / "Near line! X"
    /// matching the game's AI evasion decision tree exactly.
    /// "Incoming!" fires once as a shorter warning before the full prompt.
    /// </summary>
    public static class OffenseProximityReader
    {
        // ---- Reflection refs ----
        private static bool      _reflDone              = false;
        private static object    _matchInst             = null;

        private static FieldInfo _fldPlayState          = null;
        private static FieldInfo _fldPlayerWithBall     = null;
        private static FieldInfo _fldFpLogic            = null;
        private static FieldInfo _fldClosestDefDist     = null;  // float
        private static FieldInfo _fldClosestDefender    = null;  // FootballPlayer
        private static FieldInfo _fldIsHomeDefense      = null;  // bool on FootballMatch
        private static FieldInfo _fldScrimmageLine      = null;  // GameObject

        // ---- State ----
        private static bool   _wasInGame                = false;
        private static bool   _evasionAnnounced         = false;
        private static string _lastEvasionPrompt        = "";

        // Proximity thresholds (metres)
        private const float   PROX_MAX                  = 8f;
        private const float   PROX_MIN                  = 1.5f;
        private const float   EVASION_DIST              = 3f;    // prompt fires here
        private const float   EVASION_RESET_DIST        = 4.5f;  // resets after defender backs off

        // =========================================================
        // Poll
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

            bool ballCarried = playState == "QBHasBall" || playState == "PlayerHasBall";

            if (!ballCarried)
            {
                VibrationManager.SetLeft(0f);
                _evasionAnnounced = false;
                _lastEvasionPrompt = "";
                return;
            }

            // Get carrier and logic
            object carrier = null;
            object logic   = null;
            Component carrierComp = null;
            try
            {
                carrier     = _fldPlayerWithBall?.GetValue(_matchInst);
                carrierComp = carrier as Component;
                if (carrierComp != null && _fldFpLogic != null)
                    logic = _fldFpLogic.GetValue(carrierComp);
            }
            catch { return; }

            if (logic == null) { VibrationManager.SetLeft(0f); return; }

            float closestDist = float.MaxValue;
            try
            {
                closestDist = (float)(_fldClosestDefDist?.GetValue(logic) ?? float.MaxValue);
            }
            catch { }

            if (closestDist == float.MaxValue) { VibrationManager.SetLeft(0f); return; }

            // Ramp left motor
            float motor = Mathf.InverseLerp(PROX_MAX, PROX_MIN, closestDist);
            VibrationManager.SetLeft(motor);

            // Evasion prompt
            if (closestDist <= EVASION_DIST && (ModSettings.ReadEvasionPrompts?.Value ?? true))
            {
                string prompt = BuildEvasionPrompt(carrierComp, logic);
                if (!_evasionAnnounced || prompt != _lastEvasionPrompt)
                {
                    _evasionAnnounced  = true;
                    _lastEvasionPrompt = prompt;
                    SpeechManager.Speak(prompt);
                }
            }
            else if (closestDist > EVASION_RESET_DIST)
            {
                _evasionAnnounced  = false;
                _lastEvasionPrompt = "";
            }
        }

        // ---- Evasion prompt ----
        // Mirrors the game's AI evasion decision tree from FootballPlayerLogic.

        private static string BuildEvasionPrompt(Component carrierComp, object logic)
        {
            // Near goal line → X to dive
            try
            {
                if (_fldScrimmageLine != null && _fldIsHomeDefense != null)
                {
                    var scrGo = _fldScrimmageLine.GetValue(_matchInst) as GameObject;
                    bool isHomeDefense = (bool)(_fldIsHomeDefense.GetValue(_matchInst) ?? false);
                    bool goingNorth    = !isHomeDefense;
                    if (scrGo != null)
                    {
                        float carrierZ  = carrierComp.transform.position.z;
                        float goalLineZ = goingNorth ? 54f : -54f;
                        float distToGoal = Mathf.Abs(goalLineZ - carrierZ);
                        if (distToGoal < 10f)
                            return "Near line! X to dive.";
                    }
                }
            }
            catch { }

            // Get closest defender position
            try
            {
                if (_fldClosestDefender != null)
                {
                    object defPlayer = _fldClosestDefender.GetValue(logic);
                    var defComp      = defPlayer as Component;
                    if (defComp != null)
                    {
                        Vector3 toDefender = defComp.transform.position - carrierComp.transform.position;

                        // Convert to carrier-local space
                        Vector3 local = carrierComp.transform.InverseTransformDirection(toDefender);

                        // Defender from the side: stiff arm
                        if (Mathf.Abs(local.x) > Mathf.Abs(local.z) + 0.1f)
                        {
                            if (local.x > 0)
                                return "Right! R1 stiff arm.";
                            else
                                return "Left! L1 stiff arm.";
                        }

                        // Defender straight ahead: truck
                        return "Straight! B to truck.";
                    }
                }
            }
            catch { }

            // Fallback
            return "Incoming! Evade.";
        }

        // ---- Reset ----

        private static void ResetState()
        {
            _wasInGame         = false;
            _evasionAnnounced  = false;
            _lastEvasionPrompt = "";
            _reflDone          = false;
            _matchInst         = null;
            VibrationManager.SetLeft(0f);
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
                    _fldPlayState      = matchType.GetField("playState",      flags);
                    _fldPlayerWithBall = matchType.GetField("PlayerWithBall", flags);
                    _fldIsHomeDefense  = matchType.GetField("isHomeDefense",  flags);
                    _fldScrimmageLine  = matchType.GetField("ScrimmageLine",  flags);

                    if (_fldPlayerWithBall != null)
                    {
                        var fpType  = _fldPlayerWithBall.FieldType;
                        _fldFpLogic = fpType.GetField("logic", flags);

                        if (_fldFpLogic != null)
                        {
                            var fplType        = _fldFpLogic.FieldType;
                            _fldClosestDefDist = fplType.GetField("ClosestDefenderDistance", flags);
                            _fldClosestDefender= fplType.GetField("ClosestDefender",         flags);
                        }
                    }

                    Plugin.Log.LogInfo(
                        $"[OffenseProximityReader] match={_matchInst != null} " +
                        $"playState={_fldPlayState != null} pwb={_fldPlayerWithBall != null} " +
                        $"fpLogic={_fldFpLogic != null} closestDist={_fldClosestDefDist != null} " +
                        $"closestDef={_fldClosestDefender != null}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[OffenseProximityReader] EnsureRefs: {ex.Message}");
            }

            return _matchInst != null && _fldPlayState != null;
        }
    }
}
