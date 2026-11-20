using System.Collections.Generic;
using System.Reflection.Emit;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;

namespace GTFO.LobbyExpansion.Compatibility;

/**
 * ModList.ModList_Manager mirrors LocaliaCore's per-slot mod-sync status ("MOD: SYNCED",
 * "MOD: GETTING...", "MOD: UNKNOWN") as little text overlays on each player's lobby bar.
 * It hardcodes this to 4 players in four places:
 *
 *   public static CM_Item[] UI_ModButtons = new CM_Item[4];
 *
 *   GenerateLobbyButtons() { for (int i = 0; i < 4; i++) { UI_ModButtons[i] = ...; ... } }
 *   checkSlotState()       { for (int i = 0; i < 4; i++) { ...UI_ModButtons[i].SetText(...); } }
 *   ClearButtonText()      { for (int i = 0; i < 4; i++) { UI_ModButtons[i].SetText(...); } }
 *
 * GenerateLobbyButtons() never even creates a button object for PlayerLobbyBars[4+], so in an
 * expanded lobby, players beyond the 4th simply never get a mod-sync label at all - not even
 * "MOD: UNKNOWN" - since there's no UI_ModButtons entry there for checkSlotState()/
 * ClearButtonText() to write into. This isn't a crash bug (nothing throws), it's a silent
 * feature gap: confirmed via screenshot showing page 2 of a 6-player lobby with no mod-sync
 * text over any player, while page 1's four players show it correctly.
 *
 * The fix resizes UI_ModButtons and replaces the loop's `4` with
 * Math.Min(PluginConfig.MaxPlayers, PlayerLobbyBars.Length) via transpiler. Confirmed in-game
 * (logged diagnostic, since removed) that PlayerLobbyBars.Length actually equals
 * PluginConfig.MaxPlayers, so this reaches every real player slot. The Math.Min clamp is kept
 * regardless, as cheap insurance against PlayerLobbyBars.Length ever legitimately differing.
 *
 * IMPORTANT: the transpiler only replaces the `ldc.i4.4` that is part of the loop's own
 * condition check (matched by the surrounding ldloc/compare pattern), not every `ldc.i4.4` in
 * the method. GenerateLobbyButtons() also calls
 * PageLoadout.m_guiLayer.AddRectComp(..., GuiAnchor.MidLeft, ...) inside the same loop; if that
 * enum's underlying value also happens to compile to `ldc.i4.4`, a blind opcode-only replace
 * would silently corrupt that argument on every iteration instead of just extending the loop -
 * which is exactly what happened in an earlier version of this patch (confirmed by A/B test:
 * removing this patch restored the ability to click "MOD: SYNCED" to open the mod list popup;
 * re-adding it broke clicking for every player, not just the newly-covered ones - consistent
 * with a per-iteration argument getting corrupted rather than the loop bound itself). If the
 * expected ldloc/ldc.i4.4/compare pattern isn't found exactly once, this throws rather than
 * guessing, since ModList's compiled IL could change between versions.
 */
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

    /// Replaces ONLY the `ldc.i4.4` that is part of the loop's own condition check - matched by
    /// requiring it to sit between a load of the loop counter and a comparison/branch opcode
    /// (the standard shape the C# compiler emits for `for (int i = 0; i < 4; i++)`, including
    /// the extra `clt`/`stloc`/`ldloc`/`brtrue` steps Debug builds add - ModList.dll is a Debug
    /// build per its AssemblyInfo). Any other `ldc.i4.4` in the method (e.g. an enum constant
    /// used as a call argument) is left completely alone. Throws if the pattern isn't found
    /// exactly once rather than silently doing nothing or guessing wrong, since a codegen shape
    /// we don't expect is exactly what caused the original bug this replaces.
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

        // Mutate in place rather than replace the object - preserves the instruction's identity,
        // which HarmonyX's bake step can track jump targets by separately from .labels contents.
        // This happened to work as a straight replacement in testing, but ChatterRebornPatch hit
        // exactly this bug on a different method in the same run ("Bad label content in
        // ILGenerator"), so hardening this one defensively rather than relying on luck.
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
