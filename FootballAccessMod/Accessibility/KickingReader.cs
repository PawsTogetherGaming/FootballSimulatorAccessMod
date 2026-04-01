using System;
using System.Reflection;
using UnityEngine;
using FootballAccessMod.Speech;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Guides the player through each phase of a kicking play (field goal, PAT, punt, kickoff).
    ///
    /// Phase 1 — Aim: Announces ideal aim correction toward the uprights at entry.
    ///   Then polls the stick and reports deviation from the ideal target:
    ///   "On target." / "Go left 5." / "Go right 3."
    /// Phase 2 — Power: Announces power % every 5% on ascent; shouts "MAX!" at 96%+.
    ///   MAX is always optimal — gives max distance and erraticAngle=0.
    /// Phase 3 — Ready: "Power locked. Press A."
    /// Post-kick: "Good!" or "Missed." read from playOutcome.
    /// </summary>
    public static class KickingReader
    {
        private enum KickPhase { None, Aim, Power, Ready }

        // ---- Reflection refs ----
        private static bool         _reflDone                = false;
        private static object       _matchInst               = null;
        private static FieldInfo    _fldPlayType             = null;
        private static FieldInfo    _fldPlayState            = null;
        private static FieldInfo    _fldMetaPlayState        = null;
        private static FieldInfo    _fldBall                 = null;
        private static FieldInfo    _fldKickPowerIsAnimating = null;
        private static FieldInfo    _fldKickingPowerSlider   = null;
        private static PropertyInfo _propSliderValue         = null;
        private static FieldInfo    _fldQB                   = null;
        private static FieldInfo    _fldLogic                = null;
        private static FieldInfo    _fldLockKickingPower     = null;
        private static FieldInfo    _fldWaitForKicking       = null;
        private static FieldInfo    _fldPlayOutcome          = null;
        private static FieldInfo    _fldIsHomeDefense        = null;

        // Human-side guard: oc.gc.isAI — true means CPU is on offense (kicking)
        private static FieldInfo    _fldOc                   = null;
        private static FieldInfo    _fldOcGc                 = null;
        private static FieldInfo    _fldGcIsAI               = null;

        // Cached ball as a Component so we can read its transform.position directly
        private static Component    _ballComponent           = null;

        // KickArrow transform — we read localEulerAngles.y to get the current aim angle.
        // SetKickArrowRotation sets: KickArrow.localRotation = Euler(pitch, stickX*45, 0)
        // so localEulerAngles.y is always the actual yaw in the same degree space as _idealAimDeg.
        private static FieldInfo    _fldKickArrow            = null;
        private static Transform    _kickArrowTransform      = null;

        // ---- State ----
        private static bool      _wasInGame       = false;
        private static KickPhase _lastPhase       = KickPhase.None;

        // Aim phase
        private static float _idealAimDeg    = 0f;   // target degrees: neg=left, pos=right
        private static float _lastAimDeg     = float.MaxValue;
        private static float _lastAimAnnounce = 0f;
        private static bool  _wasOnTarget    = false;

        // Power phase
        private static int   _lastPowerBucket  = -1;
        private static float _prevSliderValue  = 0f;

        // Post-kick outcome
        private static string _lastPlayOutcome = "";

        private const float AIM_INTERVAL       = 0.25f;
        private const float AIM_DEG_THRESHOLD  = 2f;     // degrees change to re-announce
        private const float ON_TARGET_MARGIN   = 2f;     // degrees within ideal = "On target"

        // ---- Public poll entry point ----

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

            string playType, playState, metaPlayState;
            try
            {
                playType      = _fldPlayType?.GetValue(_matchInst)?.ToString()      ?? "";
                playState     = _fldPlayState?.GetValue(_matchInst)?.ToString()     ?? "";
                metaPlayState = _fldMetaPlayState?.GetValue(_matchInst)?.ToString() ?? "";
            }
            catch { return; }

            bool isKickPlay = playType == "FieldGoal" || playType == "Punt" || playType == "Kickoff";

            // Guard: only run kicking guidance when the human is on offense (kicking).
            // oc.gc.isAI == true means CPU controls offense this possession — the human
            // is receiving/defending and shouldn't hear aim/power callouts.
            bool cpuIsKicking = false;
            if (isKickPlay && _fldOc != null && _fldOcGc != null && _fldGcIsAI != null)
            {
                try
                {
                    object oc = _fldOc.GetValue(_matchInst);
                    if (oc != null)
                    {
                        object gc = _fldOcGc.GetValue(oc);
                        if (gc != null)
                            cpuIsKicking = (bool)(_fldGcIsAI.GetValue(gc) ?? false);
                    }
                }
                catch { }
            }

            // Always watch for post-kick outcome during or just after a kick play
            // (but only when the human kicked — not when CPU kicked)
            if (!cpuIsKicking && (isKickPlay || _lastPhase != KickPhase.None))
                PollOutcome();

            if (!isKickPlay || cpuIsKicking || playState == "InHuddle")
            {
                if (_lastPhase != KickPhase.None) _lastPhase = KickPhase.None;
                return;
            }

            object ballInst = null;
            try { ballInst = _fldBall?.GetValue(_matchInst); } catch { }
            if (ballInst == null) return;

            bool kickPowerIsAnimating = false;
            bool lockKickingPower     = false;
            bool waitForKicking       = false;
            try { kickPowerIsAnimating = (bool)(_fldKickPowerIsAnimating?.GetValue(ballInst) ?? false); } catch { }

            object qbLogic = GetQBLogic();
            if (qbLogic != null)
            {
                try { lockKickingPower = (bool)(_fldLockKickingPower?.GetValue(qbLogic) ?? false); } catch { }
                try { waitForKicking   = (bool)(_fldWaitForKicking?.GetValue(qbLogic)   ?? false); } catch { }
            }

            KickPhase phase;
            if (waitForKicking && !kickPowerIsAnimating)
                phase = KickPhase.Ready;
            else if (kickPowerIsAnimating)
                phase = KickPhase.Power;
            else
                phase = KickPhase.Aim;

            if (phase != _lastPhase)
                OnPhaseEnter(phase, playType, metaPlayState, ballInst);
            _lastPhase = phase;

            if (phase == KickPhase.Aim)
                PollAim(playType);
            else if (phase == KickPhase.Power)
                PollPower(ballInst);
        }

        // ---- Phase transition handler ----

        private static void OnPhaseEnter(KickPhase phase, string playType, string metaPlayState, object ballInst)
        {
            if (phase == KickPhase.Aim)
            {
                // Compute the ideal aim target for this kick
                _idealAimDeg = (playType == "FieldGoal")
                    ? ComputeIdealAimDeg(ballInst)
                    : 0f;  // straight for punt/kickoff

                string kickName;
                if (playType == "FieldGoal" && metaPlayState == "PointAfter")
                    kickName = "Point after.";
                else if (playType == "FieldGoal")
                    kickName = "Field goal.";
                else if (playType == "Punt")
                    kickName = "Punt.";
                else
                    kickName = "Kickoff.";

                string targetMsg = AimTargetDescription(_idealAimDeg);
                SpeechManager.Speak(kickName + " " + targetMsg);

                _lastAimDeg      = float.MaxValue;
                _lastAimAnnounce = 0f;
                _wasOnTarget     = false;
                _lastPlayOutcome = GetPlayOutcome();
            }
            else if (phase == KickPhase.Power)
            {
                SpeechManager.Speak("Power. Hold aim, press A at MAX.");
                _lastPowerBucket = -1;
                _prevSliderValue = 0f;
            }
            else if (phase == KickPhase.Ready)
            {
                SpeechManager.Speak("Power locked. Press A.");
            }
        }

        // ---- Aim phase polling ----

        // Reports how far current aim deviates from the ideal target.
        // "On target." = within margin.  "Go left N." / "Go right N." = correction needed.
        private static void PollAim(string playType)
        {
            // Read the actual aim from the KickArrow's local Y Euler angle.
            // SetKickArrowRotation writes: KickArrow.localRotation = Euler(pitch, stickX*45, 0),
            // so localEulerAngles.y gives the current yaw with no axis-name guessing.
            float currentDeg = GetKickArrowYaw();

            // How far is current aim from the ideal? Positive = need to go right.
            float needed = _idealAimDeg - currentDeg;
            bool  onTarget = Mathf.Abs(needed) <= ON_TARGET_MARGIN;

            float now = Time.unscaledTime;

            // Announce immediately when they first land on target
            if (onTarget && !_wasOnTarget)
            {
                _wasOnTarget     = true;
                _lastAimDeg      = currentDeg;
                _lastAimAnnounce = now;
                SpeechManager.Speak("On target.");
                return;
            }
            _wasOnTarget = onTarget;

            // Throttle other announcements by time and magnitude of change
            if (Mathf.Abs(currentDeg - _lastAimDeg) < AIM_DEG_THRESHOLD) return;
            if (now - _lastAimAnnounce < AIM_INTERVAL) return;

            _lastAimDeg      = currentDeg;
            _lastAimAnnounce = now;

            int n = Mathf.RoundToInt(needed);
            if (n < 0)
                SpeechManager.Speak($"Go left {-n}.");
            else if (n > 0)
                SpeechManager.Speak($"Go right {n}.");
            // If n==0 but not onTarget (shouldn't happen), stay quiet
        }

        // Returns the KickArrow's current local yaw in degrees (-45 to +45).
        // Negative = aimed left, positive = aimed right.
        private static float GetKickArrowYaw()
        {
            // Resolve the transform on first call (or after a reset)
            if (_kickArrowTransform == null)
            {
                try
                {
                    object ballInst = _fldBall?.GetValue(_matchInst);
                    if (ballInst != null && _fldKickArrow != null)
                    {
                        var go = _fldKickArrow.GetValue(ballInst) as GameObject;
                        if (go != null) _kickArrowTransform = go.transform;
                    }
                }
                catch { }
            }
            if (_kickArrowTransform == null) return 0f;

            // localEulerAngles.y is in [0, 360) — normalize to (-180, 180]
            float y = _kickArrowTransform.localEulerAngles.y;
            if (y > 180f) y -= 360f;
            return y;
        }

        // ---- Power phase polling ----

        private static void PollPower(object ballInst)
        {
            float value = 0f;
            try
            {
                object slider = _fldKickingPowerSlider?.GetValue(ballInst);
                if (slider != null && _propSliderValue != null)
                    value = (float)_propSliderValue.GetValue(slider, null);
            }
            catch { return; }

            // Detect bar clearly reversing direction — reset bucket so next
            // ascent is announced fresh.  Use a wider threshold (0.08 ≈ 8%) to
            // avoid false resets from float jitter near the top of the bar, and
            // never reset once MAX has been announced (bucket == 100).
            if (_lastPowerBucket < 100 && value < _prevSliderValue - 0.08f)
                _lastPowerBucket = -1;
            _prevSliderValue = value;

            if (value >= 0.96f)
            {
                if (_lastPowerBucket < 96)
                {
                    _lastPowerBucket = 100;
                    SpeechManager.Speak("MAX!");
                }
                return;
            }

            int pct    = Mathf.RoundToInt(value * 100f);
            int bucket = (pct / 5) * 5;
            if (bucket > _lastPowerBucket && bucket > 0)
            {
                _lastPowerBucket = bucket;
                SpeechManager.Speak($"{bucket}.");
            }
        }

        // ---- Post-kick outcome ----

        private static void PollOutcome()
        {
            string outcome = GetPlayOutcome();
            if (outcome == _lastPlayOutcome) return;
            _lastPlayOutcome = outcome;

            if (outcome == "FieldGoalMade")
                SpeechManager.Speak("Good!");
            else if (outcome == "FieldGoalMissed")
                SpeechManager.Speak("Missed.");
        }

        private static string GetPlayOutcome()
        {
            try { return _fldPlayOutcome?.GetValue(_matchInst)?.ToString() ?? ""; }
            catch { return ""; }
        }

        // ---- Ideal aim computation ----

        // Returns target aim degrees for a field goal: negative = aim left, positive = aim right.
        // 0 = aim straight (ball is on centre of field).
        // Mirrors SetKickArrowRotationAI: target = (0, y, ±54) based on isHomeDefense.
        private static float ComputeIdealAimDeg(object ballInst)
        {
            try
            {
                // Use the ball's actual world position (same source the AI uses)
                if (_ballComponent == null) return 0f;
                Vector3 ballPos = _ballComponent.transform.position;

                // Determine target end zone using isHomeDefense, same as IsGoingNorth()
                // IsGoingNorth() = true when offense is going toward Z=+54
                // The kicker is always on offense, so:
                //   isHomeDefense == false → home is offense → going north → Z=+54
                //   isHomeDefense == true  → away is offense → going south → Z=-54
                bool isHomeDefense = false;
                try { isHomeDefense = (bool)(_fldIsHomeDefense?.GetValue(_matchInst) ?? false); } catch { }
                bool goingNorth = !isHomeDefense;
                float targetZ = goingNorth ? 54f : -54f;

                Vector3 aimDir = (new Vector3(0f, ballPos.y, targetZ) - ballPos).normalized;

                // asin of the x-component gives the yaw angle needed to centre on the uprights
                return Mathf.Asin(Mathf.Clamp(aimDir.x, -1f, 1f)) * Mathf.Rad2Deg;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[KickingReader] ComputeIdealAimDeg: {ex.Message}");
                return 0f;
            }
        }

        // Builds the entry-phase aim message from ideal degrees.
        private static string AimTargetDescription(float idealDeg)
        {
            int d = Mathf.RoundToInt(idealDeg);
            if (Mathf.Abs(d) <= 1) return "Aim straight.";
            if (d < 0) return $"Aim left {-d} degrees.";
            return $"Aim right {d} degrees.";
        }

        // ---- Helpers ----

        private static object GetQBLogic()
        {
            try
            {
                object qb = _fldQB?.GetValue(_matchInst);
                return qb == null ? null : _fldLogic?.GetValue(qb);
            }
            catch { return null; }
        }

        private static void ResetState()
        {
            _wasInGame           = false;
            _lastPhase           = KickPhase.None;
            _lastPowerBucket     = -1;
            _prevSliderValue     = 0f;
            _idealAimDeg         = 0f;
            _lastAimDeg          = float.MaxValue;
            _wasOnTarget         = false;
            _lastPlayOutcome     = "";
            _ballComponent       = null;
            _kickArrowTransform  = null;
        }

        // ---- Reflection bootstrap ----

        private static bool EnsureRefs()
        {
            if (_reflDone) return _matchInst != null && _fldPlayType != null && _fldBall != null;
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
                    _fldPlayType      = mt.GetField("playType",      flags);
                    _fldPlayState     = mt.GetField("playState",     flags);
                    _fldMetaPlayState = mt.GetField("metaPlayState", flags);
                    _fldBall          = mt.GetField("Ball",          flags);
                    _fldQB            = mt.GetField("qb",            flags);
                    _fldPlayOutcome   = mt.GetField("playOutcome",   flags);
                    _fldIsHomeDefense = mt.GetField("isHomeDefense", flags);

                    // oc → gc → isAI for human-side guard
                    _fldOc = mt.GetField("oc", flags);
                    if (_fldOc != null)
                    {
                        var ocType = _fldOc.FieldType;
                        _fldOcGc = ocType.GetField("gc", flags);
                        if (_fldOcGc != null)
                            _fldGcIsAI = _fldOcGc.FieldType.GetField("isAI", flags);
                    }

                    object ballInst = null;
                    try { ballInst = _fldBall?.GetValue(_matchInst); } catch { }
                    if (ballInst != null)
                    {
                        // Cache ball as Component so we can read transform.position directly
                        _ballComponent = ballInst as Component;

                        var bt = ballInst.GetType();
                        _fldKickPowerIsAnimating = bt.GetField("kickPowerIsAnimating", flags);
                        _fldKickingPowerSlider   = bt.GetField("kickingPowerSlider",   flags);
                        _fldKickArrow            = bt.GetField("KickArrow",            flags);

                        object sliderInst = null;
                        try { sliderInst = _fldKickingPowerSlider?.GetValue(ballInst); } catch { }
                        if (sliderInst != null)
                            _propSliderValue = sliderInst.GetType().GetProperty("value",
                                BindingFlags.Instance | BindingFlags.Public);
                    }

                    var fpType = asm.GetType("FootballPlayer");
                    if (fpType != null)
                        _fldLogic = fpType.GetField("logic", flags);

                    var fplType = asm.GetType("Football.FootballPlayerLogic");
                    if (fplType != null)
                    {
                        _fldLockKickingPower = fplType.GetField("lockKickingPowerMeter",   flags);
                        _fldWaitForKicking   = fplType.GetField("waitForKickingAnimation", flags);
                    }

                    Plugin.Log.LogInfo(
                        $"[KickingReader] match={_matchInst != null} ball={_fldBall != null} " +
                        $"ballComp={_ballComponent != null} animating={_fldKickPowerIsAnimating != null} " +
                        $"slider={_fldKickingPowerSlider != null} sliderVal={_propSliderValue != null} " +
                        $"homeDefense={_fldIsHomeDefense != null} lock={_fldLockKickingPower != null} " +
                        $"wait={_fldWaitForKicking != null} outcome={_fldPlayOutcome != null}");
                    break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[KickingReader] EnsureRefs: {ex.Message}");
            }

            return _matchInst != null && _fldPlayType != null && _fldBall != null;
        }
    }
}
