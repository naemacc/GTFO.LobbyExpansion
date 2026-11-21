using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace GTFO.LobbyExpansion.Compatibility;

/**
 * LocaliaCore.Network_Manager stores all per-player networking state (slot lookups, mod-list
 * sync, heartbeat/ping tracking) in static arrays hardcoded to length 4:
 *
 *   public static ulong[] slot_lookup = new ulong[4];
 *   public static SNet_Player[] slot_SNet = new SNet_Player[4];
 *   ... (13 more, all `new X[4]`)
 *
 * indexed throughout by `player.PlayerSlotIndex()`. In a 5+ player lobby this throws
 * IndexOutOfRangeException from Patch_AddToPlayersInGame, Patch_RemoveFromPlayersInGame,
 * Patch_RemovePlayerFromSession, and Patch_IncomingMapInteraction - and, worse, can leave
 * SNet_Player <-> slot-index bookkeeping in other mods (e.g. ChatterReborn's CharacterID-indexed
 * dialogue arrays) reading/writing the wrong slot, since those all ultimately depend on the same
 * underlying player-slot assignment machinery this corrupts.
 *
 * On top of the array sizes, a handful of methods have their own hardcoded `4` loop bound /
 * range check, which would silently continue to ignore players 5+ even once the arrays above
 * are big enough to not crash:
 *   - LocaliaCore_Moniter.FixedUpdate() loops slots 0..3 to track ping/heartbeat/mod-list state.
 *   - Network_Manager.outLobbyClear() loops slots 0..3 to reset state between lobbies (if left
 *     unfixed, slots 4+ would carry stale data across lobby transitions).
 *   - API.is_Valid_Human / API.has_got_modlist bound-check `slot < 4`.
 *   - API.has_Everyone_Install_Plugin loops slots 0..3.
 * These aren't crash bugs on their own, but API.has_Everyone_Install_Core /
 * has_Everyone_Got_Modlist (and anything another mod builds on top of LocaliaCore's public API)
 * would be silently wrong for lobbies bigger than 4, so they're fixed here too rather than left
 * as a "doesn't crash, good enough" gap.
 */
public class LocaliaCorePatch : IModCompatibilityPatch
{
    public const string PluginGuid = "Localia.LocaliaCore";

    public static LocaliaCorePatch Instance { get; } = new();

    private static readonly string[] ArrayFieldNames =
    [
        "slot_lookup",
        "slot_SNet",
        "slot_avaliable",
        "slot_coreVer",
        "gotModList",
        "gettingModList",
        "last_try_get_time",
        "try_times",
        "a2s_back_times",
        "slot_ChalNum",
        "modListBufferLength",
        "lastHeartBeatTime",
        "slot_ping",
        "modListBuffer",
        "modList",
    ];

    private Type? _networkManagerType;
    private Type? _apiType;
    private Type? _moniterType;

    // Network_Manager members.
    private FieldInfo? _slotLookupField;
    private FieldInfo? _slotSNetField;
    private FieldInfo? _slotAvaliableField;
    private FieldInfo? _gotModListField;
    private FieldInfo? _myChalNumField;
    private FieldInfo? _heartBeatBufferField;
    private MethodInfo? _clearSlotMethod;
    private MethodInfo? _sendA2sInfoMethod;

    // LocaliaCore_Moniter members.
    private FieldInfo? _playerCountField;
    private FieldInfo? _availableCountField;
    private FieldInfo? _completedCountField;
    private FieldInfo? _boardcastAvaliableField;
    private FieldInfo? _nextBoardcastAvaliableTimeField;
    private FieldInfo? _nextHeartbeatTimeField;
    private MethodInfo? _checkModListGettingMethod;
    private MethodInfo? _checkHeartbeatMethod;

    // API members.
    private MethodInfo? _onHumanCountChangedMethod;
    private MethodInfo? _onAvailableHumanCountChangedMethod;
    private MethodInfo? _onModListHumanCountChangedMethod;
    private MethodInfo? _isValidHumanMethod;
    private MethodInfo? _hasInstallPluginMethod;

    // Logs (internal class, but reflection doesn't care about that).
    private MethodInfo? _logsErrorMethod;
    private MethodInfo? _logsVerboseMethod;

    private LocaliaCorePatch() { }

    public bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        _networkManagerType = ReflectionUtil.GetRequiredTypeByName("Network_Manager", assembly);
        _apiType = ReflectionUtil.GetRequiredTypeByName("API", assembly);
        _moniterType = ReflectionUtil.GetRequiredTypeByName("LocaliaCore_Moniter", assembly);
        var logsType = ReflectionUtil.GetRequiredTypeByName("Logs", assembly);

        ResolveMembers(logsType);
        ExpandArrays();
        ApplyPatches(harmony);
    }

    private void ResolveMembers(Type logsType)
    {
        _slotLookupField = RequireField(_networkManagerType, "slot_lookup");
        _slotSNetField = RequireField(_networkManagerType, "slot_SNet");
        _slotAvaliableField = RequireField(_networkManagerType, "slot_avaliable");
        _gotModListField = RequireField(_networkManagerType, "gotModList");
        _myChalNumField = RequireField(_networkManagerType, "myChalNum");
        _heartBeatBufferField = RequireField(_networkManagerType, "heartBeatBuffer");
        _clearSlotMethod = RequireMethod(_networkManagerType, "clearSlot", typeof(int));
        _sendA2sInfoMethod = RequireMethod(_networkManagerType, "sendA2s_Info", typeof(SNet_Player));

        _playerCountField = RequireField(_moniterType, "player_Count");
        _availableCountField = RequireField(_moniterType, "available_Count");
        _completedCountField = RequireField(_moniterType, "completed_Count");
        _boardcastAvaliableField = RequireField(_moniterType, "boardcastAvaliable");
        _nextBoardcastAvaliableTimeField = RequireField(_moniterType, "next_boardcastAvaliable_time");
        _nextHeartbeatTimeField = RequireField(_moniterType, "next_heartbeat_time");
        _checkModListGettingMethod = RequireMethod(_moniterType, "checkModListGetting", typeof(int));
        _checkHeartbeatMethod = RequireMethod(_moniterType, "checkHeartbeat", typeof(int), typeof(bool));

        _onHumanCountChangedMethod = RequireMethod(_apiType, "onHumanCountChanged", typeof(int), typeof(int));
        _onAvailableHumanCountChangedMethod = RequireMethod(_apiType, "onAvailableHumanCountChanged", typeof(int), typeof(int));
        _onModListHumanCountChangedMethod = RequireMethod(_apiType, "onModListHumanCountChanged", typeof(int), typeof(int));
        _isValidHumanMethod = RequireMethod(_apiType, "is_Valid_Human", typeof(int));
        _hasInstallPluginMethod = RequireMethod(_apiType, "has_Install_Plugin", typeof(int), typeof(string), typeof(string), typeof(string));

        _logsErrorMethod = RequireMethod(logsType, "Error", typeof(object));
        _logsVerboseMethod = RequireMethod(logsType, "Verbose", typeof(object));
    }

    private static FieldInfo RequireField(Type? type, string fieldName)
    {
        var field = AccessTools.Field(type, fieldName);
        L.Assert(field is not null, $"Unable to find {type?.Name}.{fieldName}.");
        return field!;
    }

    private static MethodInfo RequireMethod(Type? type, string methodName, params Type[] parameterTypes)
    {
        var method = AccessTools.Method(type, methodName, parameterTypes);
        L.Assert(method is not null, $"Unable to find {type?.Name}::{methodName}({string.Join(", ", (object[])parameterTypes)}).");
        return method!;
    }

    private void ExpandArrays()
    {
        foreach (var fieldName in ArrayFieldNames)
        {
            var field = AccessTools.Field(_networkManagerType, fieldName)
                ?? throw new MissingFieldException($"Unable to find Network_Manager.{fieldName}.");

            var oldArray = (Array)field.GetValue(null)!;

            if (oldArray.Length >= PluginConfig.MaxPlayers)
                continue; // already big enough (e.g. Apply() ran twice), don't stomp existing data.

            var elementType = field.FieldType.GetElementType()!;
            var newArray = Array.CreateInstance(elementType, PluginConfig.MaxPlayers);
            Array.Copy(oldArray, newArray, oldArray.Length);
            field.SetValue(null, newArray);
        }

        // Slots beyond the original 4 need their reference-type entries (Dictionary/List)
        // initialized the same way clearSlot() does for the vanilla slots, otherwise the first
        // access to them (e.g. modListBuffer[slot][index] = ...) throws a NullReferenceException.
        for (var i = 4; i < PluginConfig.MaxPlayers; i++)
            _clearSlotMethod!.Invoke(null, [i]);

        L.Debug($"{nameof(LocaliaCorePatch)}: expanded {ArrayFieldNames.Length} Network_Manager arrays to {PluginConfig.MaxPlayers}.");
    }

    private void ApplyPatches(Harmony harmony)
    {
        PatchMethod(harmony, _networkManagerType, "outLobbyClear", nameof(Network_Manager__outLobbyClear__Prefix));
        PatchMethod(harmony, _moniterType, "FixedUpdate", nameof(LocaliaCore_Moniter__FixedUpdate__Prefix));
        PatchMethod(harmony, _apiType, "is_Valid_Human", nameof(API__is_Valid_Human__Prefix));
        PatchMethod(harmony, _apiType, "has_got_modlist", nameof(API__has_got_modlist__Prefix));
        PatchMethod(harmony, _apiType, "has_Everyone_Install_Plugin", nameof(API__has_Everyone_Install_Plugin__Prefix));

        var removePlayerPatch = ReflectionUtil.GetRequiredTypeByName("Patch_RemovePlayerFromSession", _networkManagerType!.Assembly);
        var removePlayerPrefix = AccessTools.Method(removePlayerPatch, "Prefix")
            ?? throw new MissingMethodException($"Unable to find {removePlayerPatch.FullName}::Prefix().");
        harmony.Patch(removePlayerPrefix, transpiler: new HarmonyMethod(AccessTools.Method(typeof(LocaliaCorePatch), nameof(SafeSlotLookup_Transpiler))));

        L.Debug($"Applied patches in {nameof(LocaliaCorePatch)}.");
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> SafeSlotLookup_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var safeLookup = AccessTools.Method(typeof(LocaliaCorePatch), nameof(SafeSlotLookup))!;
        var replacements = 0;

        foreach (var instruction in codes)
        {
            if (instruction.opcode != OpCodes.Ldelem_I8)
                continue;

            instruction.opcode = OpCodes.Call;
            instruction.operand = safeLookup;
            replacements++;
        }

        if (replacements != 1)
            throw new InvalidOperationException($"Expected one slot lookup in LocaliaCore's remove-player prefix, found {replacements}.");

        return codes;
    }

    private static ulong SafeSlotLookup(ulong[] slots, int slot)
    {
        if ((uint)slot < (uint)slots.Length)
            return slots[slot];

        L.Warning($"LocaliaCore attempted to clear out-of-range player slot {slot}; skipping only its slot lookup.");
        return 0;
    }

    private static void PatchMethod(Harmony harmony, Type? targetType, string methodName, string prefixName)
    {
        var original = AccessTools.Method(targetType, methodName) ?? throw new MissingMethodException($"Unable to find {targetType?.FullName}::{methodName}().");
        var prefix = AccessTools.Method(typeof(LocaliaCorePatch), prefixName);
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
    }

    [HarmonyWrapSafe]
    private static bool Network_Manager__outLobbyClear__Prefix()
    {
        var self = Instance;

        self._myChalNumField!.SetValue(null, RandomNumberGenerator.GetInt32(10000, 100000));

        for (var i = 0; i < PluginConfig.MaxPlayers; i++)
            self._clearSlotMethod!.Invoke(null, [i]);

        ((IDictionary)self._heartBeatBufferField!.GetValue(null)!).Clear();

        return false; // skip the original's hardcoded-to-4 loop entirely
    }

    [HarmonyWrapSafe]
    private static bool LocaliaCore_Moniter__FixedUpdate__Prefix()
    {
        var self = Instance;

        try
        {
            self.TryBroadcastA2sInfo();

            var canSend = false;

            if (Clock.Time > (float)self._nextHeartbeatTimeField!.GetValue(null)!)
            {
                self._nextHeartbeatTimeField.SetValue(null, Clock.Time + 0.5f);
                canSend = true;
            }

            var (humanCount, availableCount, completedCount) = self.CountSlotStatus(canSend);
            self.ApplyCountChanges(humanCount, availableCount, completedCount);
        }
        catch (Exception ex)
        {
            self._logsErrorMethod!.Invoke(null, [ex.Message]);
        }

        return false; // skip the original's hardcoded-to-4 loop entirely
    }

    private void TryBroadcastA2sInfo()
    {
        var boardcastAvaliable = (int)_boardcastAvaliableField!.GetValue(null)!;

        if (boardcastAvaliable > 0
            && Clock.Time > (float)_nextBoardcastAvaliableTimeField!.GetValue(null)!
            && (bool)_sendA2sInfoMethod!.Invoke(null, [null])!)
        {
            boardcastAvaliable--;
            _boardcastAvaliableField.SetValue(null, boardcastAvaliable);
            _nextBoardcastAvaliableTimeField.SetValue(null,
                Clock.Time + 2f * (5 - boardcastAvaliable) + 1f);
        }
    }

    private (int HumanCount, int AvailableCount, int CompletedCount) CountSlotStatus(bool canSend)
    {
        var humanCount = 0;
        var availableCount = 0;
        var completedCount = 0;

        var slotLookup = (ulong[])_slotLookupField!.GetValue(null)!;
        var slotSNet = (SNet_Player[])_slotSNetField!.GetValue(null)!;
        var slotAvaliable = (ulong[])_slotAvaliableField!.GetValue(null)!;
        var gotModList = (bool[])_gotModListField!.GetValue(null)!;

        for (var i = 0; i < PluginConfig.MaxPlayers; i++)
        {
            if (slotLookup[i] == 0uL || slotSNet[i] is null || slotSNet[i].IsBot)
                continue;

            humanCount++;

            if (slotAvaliable[i] == 0uL)
                continue;

            availableCount++;

            if (gotModList[i])
            {
                completedCount++;
            }
            else
            {
                _checkModListGettingMethod!.Invoke(null, [i]);
            }

            _checkHeartbeatMethod!.Invoke(null, [i, canSend]);
        }

        return (humanCount, availableCount, completedCount);
    }

    private void ApplyCountChanges(int humanCount, int availableCount, int completedCount)
    {
        var playerCount = (int)_playerCountField!.GetValue(null)!;
        var availCountField = (int)_availableCountField!.GetValue(null)!;
        var completedCountField = (int)_completedCountField!.GetValue(null)!;
        var changed = false;

        if (playerCount != humanCount)
        {
            changed = true;
            _onHumanCountChangedMethod!.Invoke(null, [playerCount, humanCount]);
        }

        if (availCountField != availableCount)
        {
            changed = true;
            _onAvailableHumanCountChangedMethod!.Invoke(null,
                [availCountField, availableCount]);
        }

        if (completedCountField != completedCount)
        {
            changed = true;
            // Matches the original's own (likely unintentional) use of availableCount here
            // rather than completedCount, preserved for behavioral parity.
            _onModListHumanCountChangedMethod!.Invoke(null,
                [availCountField, completedCount]);
        }

        if (
           changed
           && _availableCountField?.GetValue(null) is int available
           && _playerCountField?.GetValue(null) is int player
           && _completedCountField?.GetValue(null) is int completed
        )
        {
            _logsVerboseMethod?.Invoke(null, [$"human available: {available}/{player}, credited: {completed}"]);
        }
    }

    [HarmonyWrapSafe]
    private static bool API__is_Valid_Human__Prefix(int slot, ref bool __result)
    {
        var self = Instance;

        if (slot < 0 || slot >= PluginConfig.MaxPlayers)
        {
            __result = false;
            return false;
        }

        var slotLookup = (ulong[])self._slotLookupField!.GetValue(null)!;
        var slotSNet = (SNet_Player[])self._slotSNetField!.GetValue(null)!;

        __result = slotLookup[slot] != 0uL && slotSNet[slot] is not null && !slotSNet[slot].IsBot;

        return false;
    }

    [HarmonyWrapSafe]
    private static bool API__has_got_modlist__Prefix(int slot, ref bool __result)
    {
        var self = Instance;

        if (slot < 0 || slot >= PluginConfig.MaxPlayers)
        {
            __result = false;
            return false;
        }

        var gotModList = (bool[])self._gotModListField!.GetValue(null)!;
        __result = gotModList[slot];

        return false;
    }

    [HarmonyWrapSafe]
    private static bool API__has_Everyone_Install_Plugin__Prefix(string PLUGIN_GUID, ref bool __result)
    {
        var self = Instance;

        for (var i = 0; i < PluginConfig.MaxPlayers; i++)
        {
            var isValidHuman = (bool)self._isValidHumanMethod!.Invoke(null, [i])!;

            if (!isValidHuman)
                continue;

            var hasPlugin = (bool)self._hasInstallPluginMethod!.Invoke(null, [i, PLUGIN_GUID, null, null])!;

            if (!hasPlugin)
            {
                __result = false;
                return false;
            }
        }

        __result = true;
        return false;
    }

}
