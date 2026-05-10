using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FootballAccessMod.Accessibility
{
    /// <summary>
    /// Implements CPUDefenseDifficulty across the whole game. Three layers of effect:
    ///
    /// 1. PhysicsGameplaySettings tweaks (this MonoBehaviour, every frame): scales four
    ///    tackle/blocking coefficients on the shared PGS asset. Cheap, broad, persistent
    ///    across exhibition / season / mid-season matches.
    ///
    /// 2. AttemptCatchBall postfix (DifficultyPatches): drops a fraction of CPU-defender
    ///    interception attempts. Targets the user's "I keep getting picked off" complaint.
    ///
    /// 3. ActualSpeed postfix (DifficultyPatches): scales down the move speed of CPU
    ///    defenders only — kick-coverage tacklers, pursuit defenders, deep safeties.
    ///    Player-controlled defenders are unaffected.
    ///
    /// Layers 2 and 3 only apply to defenders on the team OPPOSITE the user. We track
    /// "user's team" by watching FootballPlayerLogic.Selected — any time a human-driven
    /// player is selected, we cache that team reference. If the user is on defense, we
    /// won't accidentally nerf their own AI teammates.
    /// </summary>
    public class DifficultyManager : MonoBehaviour
    {
        // ---- PGS scaling fields (layer 1) ----
        // Index order is load-bearing — kept in sync with the multiplier array in ApplyLevel.
        private static readonly string[] FieldNames =
        {
            "dive_AITackleDistance_coeff",          // 0: AI dive-tackle range
            "dive_AITackle_AttackAngleLimit",       // 1: AI dive-tackle angle window
            "sameDirectionTimeForTackle",           // 2: how long carrier must hold direction before AI commits
            "runBlockingPushForce_coeff",           // 3: how hard run-blockers push (both teams)
            "grabTackle_muscleGrabDistance",        // 4: defender hand-grab reach (both teams)
            "grabTackle_handGrabVelocity_coeff",    // 5: defender hand-close speed (both teams)
            "AI_CoverageAgility_coeff",             // 6: AI defender coverage agility (AI only)
            "characterRotationSpeedAI_coeff",       // 7: AI rotation speed (AI only)
            "Pass_CoverageAccelerationPenalty_coeff", // 8: receiver accel penalty in coverage (closer to 1 = less penalty)
        };

        private UnityEngine.Object? _pgs;
        private FieldInfo[]? _fields;
        private float[] _originals = new float[FieldNames.Length];
        private bool _hasOriginals;
        private int _lastApplied = int.MinValue;

        private float _findRetryTimer = 0f;
        private const float FIND_RETRY_SECONDS = 1.0f;

        // ---- User-team tracking (layers 2+3) ----
        // Set by polling FootballPlayerLogic.Selected; consumed by DifficultyPatches.
        // Object reference, compared by reference equality to footballPlayerData.footballTeam.
        internal static object? UserTeamRef = null;
        private float _teamPollTimer = 0f;
        private const float TEAM_POLL_SECONDS = 0.5f;

        // ---- Special-teams gate (layer 3 cap) ----
        // True when the current play is Kickoff / Punt / FieldGoal. Used to throttle the
        // ActualSpeed nerf back to 30% on those plays — going harder breaks punter/cover
        // pathing (the v2.2.0-first-cut punt-freeze bug).
        internal static volatile bool IsSpecialTeams = false;
        private Type? _matchType;
        private System.Reflection.PropertyInfo? _matchInstanceProp;
        private FieldInfo? _stOffenseFi;
        private FieldInfo? _stDefenseFi;
        private float _matchPollTimer = 0f;
        private const float MATCH_POLL_SECONDS = 0.25f;

        private void Update()
        {
            // ---- Layer 1: PGS field scaling ----
            if (_pgs == null)
            {
                _findRetryTimer -= Time.unscaledDeltaTime;
                if (_findRetryTimer > 0f)
                {
                    PollUserTeam();
                    return;
                }
                _findRetryTimer = FIND_RETRY_SECONDS;

                if (TryFindPGS())
                {
                    CacheOriginals();
                    _lastApplied = int.MinValue;
                }
            }

            int level = ModSettings.CPUDefenseDifficulty?.Value ?? 0;
            if (_pgs != null && level != _lastApplied)
            {
                ApplyLevel(level);
                _lastApplied = level;
            }

            PollUserTeam();
            PollSpecialTeams();
        }

        // -----------------------------------------------------------------
        // PGS scaling (layer 1)
        // -----------------------------------------------------------------

        // Resolves a type by trying namespaced and bare variants. The game ships under
        // namespace `Football` (file-scoped, C# 10), but BepInEx/Harmony's TypeByName
        // sometimes resolves bare names too — we try both to be safe.
        internal static Type? ResolveType(params string[] names)
        {
            foreach (var n in names)
            {
                Type? t = AccessTools.TypeByName(n);
                if (t != null) return t;
            }
            return null;
        }

        private bool TryFindPGS()
        {
            try
            {
                Type? pgsType = ResolveType("Football.PhysicsGameplaySettings", "PhysicsGameplaySettings");
                if (pgsType == null) return false;

                var instances = Resources.FindObjectsOfTypeAll(pgsType);
                if (instances == null || instances.Length == 0) return false;

                _pgs = (UnityEngine.Object)instances[0];

                _fields = new FieldInfo[FieldNames.Length];
                for (int i = 0; i < FieldNames.Length; i++)
                {
                    _fields[i] = pgsType.GetField(FieldNames[i],
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                    if (_fields[i] == null)
                    {
                        Plugin.Log.LogWarning($"[Difficulty] PGS field not found: {FieldNames[i]}");
                        _pgs = null;
                        return false;
                    }
                }

                Plugin.Log.LogInfo($"[Difficulty] Found PhysicsGameplaySettings instance ({_pgs.name}), {_fields.Length} fields bound.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Difficulty] TryFindPGS: {ex.Message}");
                return false;
            }
        }

        private void CacheOriginals()
        {
            if (_pgs == null || _fields == null) return;
            try
            {
                for (int i = 0; i < _fields.Length; i++)
                    _originals[i] = (float)_fields[i].GetValue(_pgs);
                _hasOriginals = true;

                Plugin.Log.LogInfo(
                    $"[Difficulty] Originals: " +
                    $"diveDist={_originals[0]:F4}, angle={_originals[1]:F2}, " +
                    $"sameDirTime={_originals[2]:F4}, runBlockPush={_originals[3]:F3}, " +
                    $"grabDist={_originals[4]:F3}, handVel={_originals[5]:F2}, " +
                    $"AICovAgi={_originals[6]:F5}, AIRot={_originals[7]:F3}, " +
                    $"covAccelPen={_originals[8]:F4}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Difficulty] CacheOriginals: {ex.Message}");
            }
        }

        private void ApplyLevel(int level)
        {
            if (_pgs == null || _fields == null || !_hasOriginals) return;

            level = Mathf.Clamp(level, 0, 10);
            float t = level / 10f;

            // Multipliers, indexed to match FieldNames order. At level 0 every entry is 1.0
            // (full restore). At level 10 the values below take effect.
            float[] mul =
            {
                1f - (t * 0.85f),  // 0: dive distance       → 0.15x at L10 (was 0.30)
                1f - (t * 0.75f),  // 1: dive angle          → 0.25x at L10 (was 0.40)
                1f + (t * 3.00f),  // 2: same-direction time → 4.00x at L10 (was 2.50)
                1f + (t * 1.50f),  // 3: run-block push      → 2.50x at L10 (was 1.50)
                1f - (t * 0.60f),  // 4: grab reach          → 0.40x at L10 (NEW)
                1f - (t * 0.60f),  // 5: hand-grab speed     → 0.40x at L10 (NEW)
                1f - (t * 0.60f),  // 6: AI coverage agility → 0.40x at L10 (NEW, AI-only field)
                1f - (t * 0.50f),  // 7: AI rotation speed   → 0.50x at L10 (NEW, AI-only field)
                1f + (t * 0.025f), // 8: receiver coverage accel penalty → 1.025x at L10 (NEW; original 0.975 → ~0.999)
            };

            try
            {
                for (int i = 0; i < _fields.Length; i++)
                    _fields[i].SetValue(_pgs, _originals[i] * mul[i]);

                if (level == 0)
                    Plugin.Log.LogInfo("[Difficulty] Restored to normal game balance (level 0).");
                else
                    Plugin.Log.LogInfo(
                        $"[Difficulty] Applied level {level}: " +
                        $"diveDist x{mul[0]:F2}, angle x{mul[1]:F2}, sameDir x{mul[2]:F2}, " +
                        $"blockPush x{mul[3]:F2}, grabDist x{mul[4]:F2}, handVel x{mul[5]:F2}, " +
                        $"AICovAgi x{mul[6]:F2}, AIRot x{mul[7]:F2}, covAccelPen x{mul[8]:F3}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Difficulty] ApplyLevel: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------
        // User-team polling (consumed by DifficultyPatches.IsOpposingDefender)
        // -----------------------------------------------------------------

        private void PollUserTeam()
        {
            _teamPollTimer -= Time.unscaledDeltaTime;
            if (_teamPollTimer > 0f) return;
            _teamPollTimer = TEAM_POLL_SECONDS;

            try
            {
                Type? logicType = ResolveType("Football.FootballPlayerLogic", "FootballPlayerLogic");
                if (logicType == null) return;

                var all = Resources.FindObjectsOfTypeAll(logicType);
                if (all == null) return;

                FieldInfo? selFi = AccessTools.Field(logicType, "Selected");
                FieldInfo? dataFi = AccessTools.Field(logicType, "footballPlayerData");
                if (selFi == null || dataFi == null) return;

                FieldInfo? teamFi = null;
                foreach (var obj in all)
                {
                    bool sel = (bool)selFi.GetValue(obj);
                    if (!sel) continue;
                    object data = dataFi.GetValue(obj);
                    if (data == null) continue;
                    if (teamFi == null) teamFi = AccessTools.Field(data.GetType(), "footballTeam");
                    if (teamFi == null) return;
                    object team = teamFi.GetValue(data);
                    if (team != null && !ReferenceEquals(team, UserTeamRef))
                    {
                        UserTeamRef = team;
                        Plugin.Log.LogInfo("[Difficulty] User team updated.");
                    }
                    return; // first selected player is enough
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Difficulty] PollUserTeam: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------
        // Special-teams polling (consumed by DifficultyPatches.ActualSpeed_Postfix)
        // -----------------------------------------------------------------

        private void PollSpecialTeams()
        {
            _matchPollTimer -= Time.unscaledDeltaTime;
            if (_matchPollTimer > 0f) return;
            _matchPollTimer = MATCH_POLL_SECONDS;

            try
            {
                if (_matchType == null)
                {
                    _matchType = ResolveType("Football.FootballMatch", "FootballMatch");
                    if (_matchType == null) return;
                    _matchInstanceProp = _matchType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    _stOffenseFi = AccessTools.Field(_matchType, "specialTeamsOffense");
                    _stDefenseFi = AccessTools.Field(_matchType, "specialTeamsDefense");
                }

                if (_matchInstanceProp == null || _stOffenseFi == null || _stDefenseFi == null) return;

                object inst = _matchInstanceProp.GetValue(null);
                if (inst == null) { IsSpecialTeams = false; return; }

                bool off = (bool)_stOffenseFi.GetValue(inst);
                bool def = (bool)_stDefenseFi.GetValue(inst);
                IsSpecialTeams = off || def;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[Difficulty] PollSpecialTeams: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void OnDestroy()
        {
            if (_pgs != null && _fields != null && _hasOriginals)
            {
                try
                {
                    for (int i = 0; i < _fields.Length; i++)
                        _fields[i].SetValue(_pgs, _originals[i]);
                    Plugin.Log.LogInfo("[Difficulty] OnDestroy: restored PGS originals.");
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Harmony patches that nerf CPU defenders in proportion to CPUDefenseDifficulty.
    /// Applied once at startup from Plugin.Awake. Patches no-op when difficulty=0.
    /// </summary>
    public static class DifficultyPatches
    {
        private static FieldInfo? _playerTypeField;
        private static FieldInfo? _playerField;
        private static FieldInfo? _gcField;
        private static FieldInfo? _isAIField;
        private static FieldInfo? _dataField;
        private static FieldInfo? _teamField;
        private static FieldInfo? _ballRigidBodyField; // Rigidbody on FootballPlayerLogic, used by ThrowBall_Postfix
        private static int _defensivePlayerEnumValue = 2; // FootballPlayerLogic.PlayerType.DefensivePlayer

        private static bool _initialized = false;

        public static void ApplyPatches(Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Type? logicType = DifficultyManager.ResolveType("Football.FootballPlayerLogic", "FootballPlayerLogic");
                if (logicType == null)
                {
                    Plugin.Log.LogWarning("[DifficultyPatches] FootballPlayerLogic type not found — patches skipped.");
                    return;
                }
                Plugin.Log.LogInfo($"[DifficultyPatches] Resolved logic type: {logicType.FullName}");

                _playerTypeField = AccessTools.Field(logicType, "playerType");
                _playerField     = AccessTools.Field(logicType, "player");
                _dataField       = AccessTools.Field(logicType, "footballPlayerData");
                _ballRigidBodyField = AccessTools.Field(logicType, "ballRigidBody");

                Type? enumType = AccessTools.Inner(logicType, "PlayerType");
                if (enumType != null && enumType.IsEnum)
                {
                    try { _defensivePlayerEnumValue = (int)Enum.Parse(enumType, "DefensivePlayer"); }
                    catch { /* keep default */ }
                }

                Type? playerType = DifficultyManager.ResolveType("Football.FootballPlayer", "FootballPlayer");
                if (playerType != null)
                    _gcField = AccessTools.Field(playerType, "gameController");

                if (_gcField != null)
                    _isAIField = AccessTools.Field(_gcField.FieldType, "isAI");

                if (_dataField != null)
                    _teamField = AccessTools.Field(_dataField.FieldType, "footballTeam");

                Plugin.Log.LogInfo(
                    $"[DifficultyPatches] Field bind: playerType={_playerTypeField!=null}, " +
                    $"player={_playerField!=null}, gameController={_gcField!=null}, " +
                    $"isAI={_isAIField!=null}, footballPlayerData={_dataField!=null}, " +
                    $"footballTeam={_teamField!=null}, ballRigidBody={_ballRigidBodyField!=null}");

                TryPatch(harmony, logicType, "AttemptCatchBall", nameof(AttemptCatchBall_Postfix));
                TryPatch(harmony, logicType, "ActualSpeed",      nameof(ActualSpeed_Postfix));
                TryPatch(harmony, logicType, "ThrowBall",        nameof(ThrowBall_Postfix));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DifficultyPatches] ApplyPatches: {ex.Message}");
            }
        }

        private static void TryPatch(Harmony harmony, Type type, string methodName, string postfixName)
        {
            try
            {
                MethodInfo? method = AccessTools.Method(type, methodName);
                if (method == null)
                {
                    Plugin.Log.LogWarning($"[DifficultyPatches] Method {type.Name}:{methodName} not found.");
                    return;
                }
                MethodInfo? postfix = typeof(DifficultyPatches).GetMethod(
                    postfixName, BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                {
                    Plugin.Log.LogWarning($"[DifficultyPatches] Postfix {postfixName} not found.");
                    return;
                }
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                Plugin.Log.LogInfo($"[DifficultyPatches] Patched {type.Name}:{methodName}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DifficultyPatches] Patch {methodName} failed: {ex.Message}");
            }
        }

        // Classification helpers ------------------------------------------------
        //
        // Three flavors:
        //   IsOpposingAI(instance, out level)        — any CPU player on the other team
        //   IsOpposingAIDefender(instance, out level) — same, plus playerType==DefensivePlayer
        //   IsUserTeamAIDefender(instance, out level) — CPU-controlled AI on the USER's team
        //                                                that happens to be a defender (used to
        //                                                boost INT chance when CPU is throwing).
        //
        // All require UserTeamRef to be known — better to no-op than nerf the wrong side
        // (the v2.2.0 first cut hit this when the user was on defense at high difficulty).

        private static bool TryGetTeamAndAI(object instance, out object? team, out bool isAI)
        {
            team = null;
            isAI = false;
            if (instance == null) return false;

            try
            {
                if (_playerField != null && _gcField != null && _isAIField != null)
                {
                    object player = _playerField.GetValue(instance);
                    if (player == null) return false;
                    object gc = _gcField.GetValue(player);
                    if (gc == null) return false;
                    isAI = (bool)_isAIField.GetValue(gc);
                }
                if (_dataField == null || _teamField == null) return false;
                object data = _dataField.GetValue(instance);
                if (data == null) return false;
                team = _teamField.GetValue(data);
                return team != null;
            }
            catch { return false; }
        }

        private static bool IsOpposingAI(object instance, out int level)
        {
            level = ModSettings.CPUDefenseDifficulty?.Value ?? 0;
            if (level <= 0) return false;
            if (DifficultyManager.UserTeamRef == null) return false;
            if (!TryGetTeamAndAI(instance, out var team, out bool isAI)) return false;
            if (!isAI) return false;
            if (ReferenceEquals(team, DifficultyManager.UserTeamRef)) return false;
            return true;
        }

        private static bool IsOpposingAIDefender(object instance, out int level)
        {
            if (!IsOpposingAI(instance, out level)) return false;
            if (_playerTypeField == null) return false;
            try
            {
                int pt = Convert.ToInt32(_playerTypeField.GetValue(instance));
                return pt == _defensivePlayerEnumValue;
            }
            catch { return false; }
        }

        private static bool IsUserTeamAIDefender(object instance, out int level)
        {
            level = ModSettings.CPUDefenseDifficulty?.Value ?? 0;
            if (level <= 0) return false;
            if (DifficultyManager.UserTeamRef == null) return false;
            if (!TryGetTeamAndAI(instance, out var team, out bool isAI)) return false;
            if (!isAI) return false;
            if (!ReferenceEquals(team, DifficultyManager.UserTeamRef)) return false;
            if (_playerTypeField == null) return false;
            try
            {
                int pt = Convert.ToInt32(_playerTypeField.GetValue(instance));
                return pt == _defensivePlayerEnumValue;
            }
            catch { return false; }
        }

        // Catch outcome — works in both directions:
        //  - CPU defender catches ball thrown by user QB → drop up to 95% of those at L10
        //    (was 80%; bumped because L10 still let too many INTs through).
        //  - User-team AI defender attempts a catch and the engine said "no" → flip to
        //    yes with up to 50% chance at L10. Net effect: more CPU passes get picked off
        //    by your AI teammates on defense.
        private static void AttemptCatchBall_Postfix(object __instance, ref bool __result)
        {
            // Successful catch by an opposing CPU defender — try to drop it.
            if (__result)
            {
                if (IsOpposingAIDefender(__instance, out int dropLvl))
                {
                    float dropChance = Mathf.Clamp01(dropLvl / 10f * 0.95f);
                    if (UnityEngine.Random.value < dropChance)
                        __result = false;
                    return;
                }
            }
            // Missed catch by a user-team AI defender — chance to flip to a pick.
            else
            {
                if (IsUserTeamAIDefender(__instance, out int boostLvl))
                {
                    float boostChance = Mathf.Clamp01(boostLvl / 10f * 0.50f);
                    if (UnityEngine.Random.value < boostChance)
                        __result = true;
                }
            }
        }

        // Scales movement speed of every opposing AI player (defense AND offense).
        // Originally only nerfed defenders; widened to cover CPU offense so plays develop
        // slower and the user's defense can actually make stops on long runs / scrambles.
        // Cap is play-state-aware:
        //   - Regular plays: up to 60% reduction (CPU at 40% speed at L10).
        //   - Special teams (Kickoff/Punt/FieldGoal): held to 30% reduction — anything
        //     stronger broke the punt/kickoff cover AI in v2.2.0 first cut (CPU froze,
        //     play hung). The looser cap is fine for regular plays where AI behavior
        //     has richer fallbacks.
        private static void ActualSpeed_Postfix(object __instance, ref float __result)
        {
            if (!IsOpposingAI(__instance, out int level)) return;

            float maxReduction = DifficultyManager.IsSpecialTeams ? 0.30f : 0.60f;
            float scale = 1f - (level / 10f * maxReduction);
            __result *= scale;
        }

        // Perturbs the ball's velocity right after a CPU QB releases it, so CPU passes
        // drift off-target. Greater scatter = more incompletions and more INT chances
        // for the user defense. Yaw is the dominant perturbation (left/right miss);
        // pitch is half-amplitude to avoid sailing the ball into orbit or into the dirt.
        private static void ThrowBall_Postfix(object __instance)
        {
            if (!IsOpposingAI(__instance, out int level)) return;
            if (_ballRigidBodyField == null) return;

            try
            {
                if (_ballRigidBodyField.GetValue(__instance) is Rigidbody rb)
                {
                    float maxDeg = level / 10f * 18f; // up to ±18° at L10
                    float yaw   = UnityEngine.Random.Range(-maxDeg, maxDeg);
                    float pitch = UnityEngine.Random.Range(-maxDeg * 0.5f, maxDeg * 0.5f);
                    Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
                    rb.velocity = rot * rb.velocity;
                }
            }
            catch { /* leave velocity alone on any reflection failure */ }
        }
    }
}
