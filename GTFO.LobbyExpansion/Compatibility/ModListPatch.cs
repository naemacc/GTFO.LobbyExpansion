using System.Collections.Generic;
using System.Reflection.Emit;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;

namespace GTFO.LobbyExpansion.Compatibility;

public class ModListPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "Localia.ModList";

    public static ModListPatch Instance { get; } = new();

    private Type? _modListManagerType;

    private ModListPatch() { }

    #region Overrides of ModCompatibilityPatch

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

            _modListManagerType = ReflectionUtil.GetRequiredTypeByName("ModList_Manager", assembly);

            ExpandUiModButtons();
            ApplyPatches(harmony);

            L.Debug($"Applied patches in {nameof(ModListPatch)}.");
        }
        catch (Exception ex)
        {
            L.Error($"{nameof(ModListPatch)}.Apply() failed: {ex}");
            throw;
        }
    }

    #endregion

    private void ExpandUiModButtons()
    {
        var field = AccessTools.Field(_modListManagerType, "UI_ModButtons");
        if (field is null)
            throw new Exception($"Unable to find {_modListManagerType?.FullName}.UI_ModButtons.");

        var oldArray = (Array)field.GetValue(null)!;

        if (oldArray.Length >= PluginConfig.MaxPlayers)
            return; // already big enough (e.g. Apply() ran twice)

        var elementType = field.FieldType.GetElementType()!;
        var newArray = Array.CreateInstance(elementType, PluginConfig.MaxPlayers);
        Array.Copy(oldArray, newArray, oldArray.Length);
        field.SetValue(null, newArray);
    }

    private void ApplyPatches(Harmony harmony)
    {
        PatchTranspiler(harmony, _modListManagerType, "GenerateLobbyButtons");
        PatchTranspiler(harmony, _modListManagerType, "checkSlotState");
        PatchTranspiler(harmony, _modListManagerType, "ClearButtonText");
    }

    // Patched manually since we don't have compile-time references to ModList's types.
    private static void PatchTranspiler(Harmony harmony, Type? targetType, string methodName)
    {
        var original = AccessTools.Method(targetType, methodName);
        if (original is null)
            throw new Exception($"Unable to find {targetType?.FullName}::{methodName}().");

        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ModListPatch), nameof(HardcodedFour_Transpiler)));
        harmony.Patch(original, transpiler: transpiler);
    }

    /// Replaces ONLY the `ldc.i4.4` that is part of the loop's condition check.
    /// Matched by requiring it to sit between a load of the loop counter and a comparison/branch opcode.
    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> HardcodedFour_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getLoopBound = AccessTools.Method(typeof(ModListPatch), nameof(GetLoopBound));
        var codes = new List<CodeInstruction>(instructions);
        var matchIndices = new List<int>();

        for (var i = 0; i < codes.Count - 2; i++)
        {
            if (IsLdloc(codes[i].opcode)
                && codes[i + 1].opcode == OpCodes.Ldc_I4_4
                && IsCompareOrBranch(codes[i + 2].opcode))
            {
                matchIndices.Add(i + 1);
            }
        }

        if (matchIndices.Count != 1)
        {
            throw new Exception(
                $"Expected exactly 1 loop-bound pattern (ldloc; ldc.i4.4; compare/branch), found {matchIndices.Count}. " +
                "ModList's compiled IL may have changed - refusing to guess which constant is the loop bound.");
        }

        // Mutate in place rather than replace the object to preserve the instruction's identity,
        // which HarmonyX tracks jump targets by separately from .labels contents.
        var target = codes[matchIndices[0]];
        target.opcode = OpCodes.Call;
        target.operand = getLoopBound;

        return codes;
    }

    private static bool IsLdloc(OpCode opcode)
    {
        return opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1
            || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3
            || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc;
    }

    private static bool IsCompareOrBranch(OpCode opcode)
    {
        return opcode == OpCodes.Clt || opcode == OpCodes.Clt_Un
            || opcode == OpCodes.Cgt || opcode == OpCodes.Cgt_Un
            || opcode == OpCodes.Blt || opcode == OpCodes.Blt_S || opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S
            || opcode == OpCodes.Bge || opcode == OpCodes.Bge_S || opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S
            || opcode == OpCodes.Ble || opcode == OpCodes.Ble_S || opcode == OpCodes.Ble_Un || opcode == OpCodes.Ble_Un_S
            || opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S || opcode == OpCodes.Bgt_Un || opcode == OpCodes.Bgt_Un_S;
    }

    private static int GetLoopBound()
    {
        var field = AccessTools.Field(Instance._modListManagerType, "PlayerLobbyBars");
        var playerLobbyBars = (Array?)field?.GetValue(null);

        return playerLobbyBars is null
            ? PluginConfig.MaxPlayers
            : Math.Min(PluginConfig.MaxPlayers, playerLobbyBars.Length);
    }
}
