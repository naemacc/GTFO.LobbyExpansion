// Credit to Dinorush for a large part of the original fix.

using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using Il2CppInterop.Runtime;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace GTFO.LobbyExpansion.Compatibility;

public class ChatterRebornPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "CHTR";

    public static ChatterRebornPatch Instance { get; } = new();

    private Type? _dramaChatterManagerType;
    private Type? _dramaChatterMachineType;
    private Type? _enemyDetectionManagerType;
    private Type? _enemyDramaBehaviorType;
    private Type? _dramaChatterBaseType;

    // EnemyDramaBehavior (per-instance fields)
    private FieldInfo? _m_playerVisiblityField;
    private FieldInfo? _m_detectedPlayers;
    private FieldInfo? _m_destroyedField;
    private FieldInfo? _m_aggressiveField;

    // DramaChatterManager (singleton)
    private PropertyInfo? _dramaChatterManagerCurrentProperty;
    private PropertyInfo? _playerDramaMachinesProperty;
    private FieldInfo? _allowParticipationField;

    // EnemyDetectionManager (singleton)
    private PropertyInfo? _enemyDetectionManagerCurrentProperty;
    private FieldInfo? _m_enemyScoresField;
    private FieldInfo? _m_enemyVisibilitesField; // Typo in ChatterReborn's field name is intentional.
    private FieldInfo? _aggressiveEnemiesField;

    private ChatterRebornPatch() { }

    public override bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public override void Apply(Harmony harmony)
    {
        try
        {
            var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
            var assembly = pluginInstance.GetType().Assembly;

            _dramaChatterManagerType = ReflectionUtil.GetRequiredTypeByName("DramaChatterManager", assembly);
            _dramaChatterMachineType = ReflectionUtil.GetRequiredTypeByName("DramaChatterMachine", assembly);
            _enemyDetectionManagerType = ReflectionUtil.GetRequiredTypeByName("EnemyDetectionManager", assembly);
            _enemyDramaBehaviorType = ReflectionUtil.GetRequiredTypeByName("EnemyDramaBehavior", assembly);
            _dramaChatterBaseType = ReflectionUtil.GetRequiredTypeByName("DRAMA_Chatter_Base", assembly);

            ResolveMembers();
            ApplyPatches(harmony);

            L.Debug($"Applied patches in {nameof(ChatterRebornPatch)}.");
        }
        catch (Exception ex)
        {
            // Loud on purpose: a silent failure here previously meant NONE of these patches
            // were ever registered, with no indication of why in the log.
            L.Error($"{nameof(ChatterRebornPatch)}.Apply() failed: {ex}");
            throw;
        }

        // Belt-and-suspenders: if, for whatever reason, ChatterReborn's Setup() already ran
        // before we got here (Setup() only ever runs once per process, so our postfix would
        // never get a chance to fire), fix the already-allocated arrays immediately too.
        // This is safe to run unconditionally - SetValue doesn't care what the old value was.
        try
        {
            var dramaChatterManager = _dramaChatterManagerCurrentProperty!.GetValue(null);
            if (dramaChatterManager is not null)
                ExpandDramaChatterManager(dramaChatterManager);

            var detectionManager = _enemyDetectionManagerCurrentProperty!.GetValue(null);
            if (detectionManager is not null)
                ExpandEnemyDetectionManager(detectionManager);
        }
        catch (Exception ex)
        {
            L.Error($"{nameof(ChatterRebornPatch)}: immediate array fix-up failed (non-fatal, the Setup() postfixes are still active): {ex}");
        }

        // Catch any EnemyDramaBehavior that already Awake()'d before this patch applied.
        try
        {
            var il2cppType = Il2CppType.From(_enemyDramaBehaviorType);
            var existing = UnityEngine.Object.FindObjectsOfType(il2cppType);
            foreach (var enemy in existing)
            {
                _m_playerVisiblityField!.SetValue(enemy, new float[PluginConfig.MaxPlayers]);
                _m_detectedPlayers!.SetValue(enemy, new bool[PluginConfig.MaxPlayers]);
            }
            if (existing.Length > 0)
                L.Debug($"{nameof(ChatterRebornPatch)}: re-sized arrays on {existing.Length} pre-existing EnemyDramaBehavior instance(s).");
        }
        catch (Exception ex)
        {
            L.Error($"{nameof(ChatterRebornPatch)}: pre-existing EnemyDramaBehavior fix-up failed (non-fatal): {ex}");
        }
    }

    private void ResolveMembers()
    {
        _m_playerVisiblityField = AccessTools.Field(_enemyDramaBehaviorType, "m_playerVisiblity");
        RequireMember(_m_playerVisiblityField, nameof(_m_playerVisiblityField));

        _m_detectedPlayers = AccessTools.Field(_enemyDramaBehaviorType, "m_detectedPlayers");
        RequireMember(_m_detectedPlayers, nameof(_m_detectedPlayers));

        _m_destroyedField = AccessTools.Field(_enemyDramaBehaviorType, "m_destroyed");
        RequireMember(_m_destroyedField, nameof(_m_destroyedField));

        _m_aggressiveField = AccessTools.Field(_enemyDramaBehaviorType, "m_aggressive");
        RequireMember(_m_aggressiveField, nameof(_m_aggressiveField));

        _dramaChatterManagerCurrentProperty = _dramaChatterManagerType!.GetProperty(
            "Current",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        RequireMember(_dramaChatterManagerCurrentProperty, nameof(_dramaChatterManagerCurrentProperty));

        _playerDramaMachinesProperty = _dramaChatterManagerType.GetProperty(
            "PlayerDramaMachines",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        RequireMember(_playerDramaMachinesProperty, nameof(_playerDramaMachinesProperty));

        _allowParticipationField = _dramaChatterManagerType.GetField(
            "_allow_participation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        RequireMember(_allowParticipationField, nameof(_allowParticipationField));

        _enemyDetectionManagerCurrentProperty = _enemyDetectionManagerType!.GetProperty(
            "Current",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        RequireMember(_enemyDetectionManagerCurrentProperty, nameof(_enemyDetectionManagerCurrentProperty));

        _m_enemyScoresField = _enemyDetectionManagerType.GetField(
            "m_enemyScores",
            BindingFlags.Public | BindingFlags.Instance);
        RequireMember(_m_enemyScoresField, nameof(_m_enemyScoresField));

        _m_enemyVisibilitesField = _enemyDetectionManagerType.GetField(
            "m_enemyVisibilites",
            BindingFlags.Public | BindingFlags.Instance);
        RequireMember(_m_enemyVisibilitesField, nameof(_m_enemyVisibilitesField));

        _aggressiveEnemiesField = _enemyDetectionManagerType.GetField(
            "AggressiveEnemies",
            BindingFlags.Public | BindingFlags.Static);
        RequireMember(_aggressiveEnemiesField, nameof(_aggressiveEnemiesField));
    }

    private static void RequireMember(object? member, string memberName)
    {
        if (member is null)
            throw new Exception($"Unable to find {memberName}; ChatterReborn's internals may have changed.");
    }

    protected void ApplyPatches(Harmony harmony)
    {
        PatchMethod(harmony, _dramaChatterManagerType, "Setup", postfix: nameof(DramaChatterManager_Setup_Postfix));
        PatchMethod(harmony, _enemyDetectionManagerType, "Setup", postfix: nameof(EnemyDetectionManager_Setup_Postfix));
        PatchMethod(harmony, _enemyDramaBehaviorType, "Awake", postfix: nameof(EnemyDramaBehavior__Awake__Postfix));
        PatchMethod(harmony, _enemyDramaBehaviorType, "DisableEnemyDramaBehavior", prefix: nameof(DisableEnemyDramaBehavior_Prefix));
        PatchTranspiler(harmony, _dramaChatterBaseType, "OnOrderBackToBioScanPrisoner", nameof(OnOrderBackToBioScanPrisoner_Transpiler));
    }

    // Patched manually since we don't have compile-time references to ChatterReborn's types.
    private static void PatchTranspiler(Harmony harmony, Type? targetType, string methodName, string transpilerName)
    {
        var original = AccessTools.Method(targetType, methodName);
        if (original is null)
            throw new Exception($"Unable to find {targetType?.FullName}::{methodName}().");

        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ChatterRebornPatch), transpilerName));
        harmony.Patch(original, transpiler: transpiler);
    }

    // Patched manually since we don't have compile-time references to ChatterReborn's types.
    private static void PatchMethod(Harmony harmony, Type? targetType, string methodName, string? prefix = null, string? postfix = null)
    {
        var original = AccessTools.Method(targetType, methodName);
        if (original is null)
            throw new Exception($"Unable to find {targetType?.FullName}::{methodName}().");

        var prefixMethod = prefix is null ? null : new HarmonyMethod(AccessTools.Method(typeof(ChatterRebornPatch), prefix));
        var postfixMethod = postfix is null ? null : new HarmonyMethod(AccessTools.Method(typeof(ChatterRebornPatch), postfix));

        harmony.Patch(original, prefix: prefixMethod, postfix: postfixMethod);
    }

    private void ExpandDramaChatterManager(object dramaChatterManagerInstance)
    {
        var expandedChatterMachineArray = Array.CreateInstance(_dramaChatterMachineType!, PluginConfig.MaxPlayers);
        _playerDramaMachinesProperty!.SetValue(dramaChatterManagerInstance, expandedChatterMachineArray);
        _allowParticipationField!.SetValue(dramaChatterManagerInstance, new bool[PluginConfig.MaxPlayers]);
    }

    private void ExpandEnemyDetectionManager(object detectionManagerInstance)
    {
        _m_enemyScoresField!.SetValue(detectionManagerInstance, new float[PluginConfig.MaxPlayers]);
        _m_enemyVisibilitesField!.SetValue(detectionManagerInstance, new int[PluginConfig.MaxPlayers]);
    }

    // Runs immediately after ChatterReborn's own DramaChatterManager.Setup(), whenever that
    // happens to fire - no dependency on StartMainGame.Start ordering.
    [HarmonyWrapSafe]
    private static void DramaChatterManager_Setup_Postfix(object __instance)
    {
        Instance.ExpandDramaChatterManager(__instance);
    }

    // Runs immediately after ChatterReborn's own EnemyDetectionManager.Setup().
    [HarmonyWrapSafe]
    private static void EnemyDetectionManager_Setup_Postfix(object __instance)
    {
        Instance.ExpandEnemyDetectionManager(__instance);
    }

    // Runs immediately after every EnemyDramaBehavior.Awake() - i.e. every enemy spawn.
    [HarmonyWrapSafe]
    private static void EnemyDramaBehavior__Awake__Postfix(object __instance)
    {
        if (Instance._enemyDramaBehaviorType is null)
        {
            L.Error($"Awake postfix can't run; {nameof(_enemyDramaBehaviorType)} is null.");
            return;
        }

        if (!Instance._enemyDramaBehaviorType.IsInstanceOfType(__instance))
        {
            L.Error(
                $"Awake postfix was passed an instance that is NOT an instance of {nameof(_enemyDramaBehaviorType)} type? It was of type {__instance.GetType().FullName}");
            return;
        }

        Instance._m_playerVisiblityField!.SetValue(__instance, new float[PluginConfig.MaxPlayers]);
        Instance._m_detectedPlayers!.SetValue(__instance, new bool[PluginConfig.MaxPlayers]);
    }

    /// Replaces the hardcoded "for (int i = 0; i < 4; i++)" found in
    /// EnemyDramaBehavior.DisableEnemyDramaBehavior() in the decompiled ChatterReborn source.
    /// With arrays now sized to MaxPlayers this no longer throws, but the hardcoded loop would
    /// silently skip cleanup for player slots 4+, leaving stale detection/aggro scores behind.
    [HarmonyWrapSafe]
    private static bool DisableEnemyDramaBehavior_Prefix(object __instance)
    {
        var self = Instance;

        bool alreadyDestroyed = (bool)self._m_destroyedField!.GetValue(__instance)!;
        if (alreadyDestroyed)
            return false; // Skip original; matches its own early-return behavior.

        self._m_destroyedField.SetValue(__instance, true);

        bool isAggressive = (bool)self._m_aggressiveField!.GetValue(__instance)!;
        if (isAggressive)
        {
            int currentAggressive = (int)self._aggressiveEnemiesField!.GetValue(null)!;
            self._aggressiveEnemiesField.SetValue(null, currentAggressive - 1);
            self._m_aggressiveField.SetValue(__instance, false);
        }

        var detectedPlayers = (bool[])self._m_detectedPlayers!.GetValue(__instance)!;
        var detectionManager = self._enemyDetectionManagerCurrentProperty!.GetValue(null);

        if (detectionManager is not null)
        {
            var enemyVisibilites = (int[])self._m_enemyVisibilitesField!.GetValue(detectionManager)!;
            var enemyScores = (float[])self._m_enemyScoresField!.GetValue(detectionManager)!;

            for (int i = 0; i < detectedPlayers.Length; i++)
            {
                if (!detectedPlayers[i])
                    continue;

                if (i < enemyVisibilites.Length)
                    enemyVisibilites[i]--;

                if (i < enemyScores.Length)
                    enemyScores[i] -= 8f;
            }
        }

        return false; // Skip the original's hardcoded-to-4 execution entirely.
    }

    /// DRAMA_Chatter_Base.OnOrderBackToBioScanPrisoner() allocates `bool[] array = new bool[4]`
    /// and indexes it by PlayerAgent.CharacterID (`array[val4.CharacterID] = true`), which throws
    /// IndexOutOfRangeException for any player whose CharacterID is 4 or higher - i.e. any 5th+
    /// player, in this dialogue path specifically (triggered when telling a straggler to get back
    /// to a bioscan in progress). Unlike the other arrays here, this one is a local variable
    /// declared inside the method body rather than a field set up in Setup()/Awake(), so there's
    /// nothing to resize after the fact with a prefix/postfix - the fix has to happen at the IL
    /// level. This transpiler replaces ONLY the `ldc.i4.4` immediately followed by `newarr` (the
    /// exact shape of `new bool[4]`), not every `ldc.i4.4` in the method - a blind opcode-only
    /// replace risks corrupting an unrelated constant elsewhere that happens to also compile to
    /// ldc.i4.4 (an enum argument, for instance), which caused a real regression in ModListPatch
    /// for exactly this reason. Throws if the pattern isn't found exactly once rather than
    /// silently guessing wrong.
    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> OnOrderBackToBioScanPrisoner_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMaxPlayers = AccessTools.Method(typeof(ChatterRebornPatch), nameof(GetMaxPlayersAsInt));
        var codes = new List<CodeInstruction>(instructions);
        var matchIndices = new List<int>();

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4 && codes[i + 1].opcode == OpCodes.Newarr)
                matchIndices.Add(i);
        }

        if (matchIndices.Count != 1)
        {
            throw new Exception(
                $"Expected exactly 1 `ldc.i4.4; newarr` pattern in OnOrderBackToBioScanPrisoner(), found {matchIndices.Count}. " +
                "ChatterReborn's compiled IL may have changed - refusing to guess which constant is the array size.");
        }

        // Mutate the existing CodeInstruction object in place rather than replacing it - even
        // with .labels/.blocks copied across, replacing the object changes its identity, and
        // HarmonyX's bake step can track jump targets by that identity rather than purely by
        // the .labels field contents. Keeping the same object is the textbook-correct pattern
        // specifically to avoid "Bad label content in ILGenerator" when the replaced
        // instruction happens to be a branch target - which is exactly what was happening here
        // (ChatterReborn is a Debug build; the `ldc.i4.4` before `newarr` in
        // OnOrderBackToBioScanPrisoner() turned out to be one).
        var target = codes[matchIndices[0]];
        target.opcode = OpCodes.Call;
        target.operand = getMaxPlayers;

        return codes;
    }

    private static int GetMaxPlayersAsInt() => PluginConfig.MaxPlayers;
}
