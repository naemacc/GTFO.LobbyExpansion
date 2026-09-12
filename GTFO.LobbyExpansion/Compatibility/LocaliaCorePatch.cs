using System.Collections;
using System.Reflection;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;

namespace GTFO.LobbyExpansion.Compatibility;

public class LocaliaCorePatch : ModCompatibilityPatch
{
    public const string PluginGuid = "Localia.LocaliaCore";

    public static LocaliaCorePatch Instance { get; } = new();

    private static readonly string[] ArrayFieldNames =
    {
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
    };

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

    // Logs members.
    private MethodInfo? _logsErrorMethod;
    private MethodInfo? _logsVerboseMethod;

    private LocaliaCorePatch() { }

    #region Overrides of ModCompatibilityPatch

    public override bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public override void Apply(Harmony harmony)
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

    #endregion

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
            var field = AccessTools.Field(_networkManagerType, fieldName);

            if (field is null)
                throw new Exception($"Unable to find Network_Manager.{fieldName}.");

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
            _clearSlotMethod!.Invoke(null, new object[] { i });

        L.Debug($"{nameof(LocaliaCorePatch)}: expanded {ArrayFieldNames.Length} Network_Manager arrays to {PluginConfig.MaxPlayers}.");
    }

    private void ApplyPatches(Harmony harmony)
    {
        PatchMethod(harmony, _networkManagerType, "outLobbyClear", nameof(Network_Manager__outLobbyClear__Prefix));
        PatchMethod(harmony, _moniterType, "FixedUpdate", nameof(LocaliaCore_Moniter__FixedUpdate__Prefix));
        PatchMethod(harmony, _apiType, "is_Valid_Human", nameof(API__is_Valid_Human__Prefix));
        PatchMethod(harmony, _apiType, "has_got_modlist", nameof(API__has_got_modlist__Prefix));
        PatchMethod(harmony, _apiType, "has_Everyone_Install_Plugin", nameof(API__has_Everyone_Install_Plugin__Prefix));

        L.Debug($"Applied patches in {nameof(LocaliaCorePatch)}.");
    }

    private static void PatchMethod(Harmony harmony, Type? targetType, string methodName, string prefixName)
    {
        var original = AccessTools.Method(targetType, methodName);

        if (original is null)
            throw new Exception($"Unable to find {targetType?.FullName}::{methodName}().");

        var prefix = AccessTools.Method(typeof(LocaliaCorePatch), prefixName);
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
    }

    #region Manual patches (reflection-only targets, no compile-time reference to LocaliaCore)

    [HarmonyWrapSafe]
    private static bool Network_Manager__outLobbyClear__Prefix()
    {
        var self = Instance;

        var random = new Random();
        self._myChalNumField!.SetValue(null, random.Next(10000, 99999));

        for (var i = 0; i < PluginConfig.MaxPlayers; i++)
            self._clearSlotMethod!.Invoke(null, new object[] { i });

        ((IDictionary)self._heartBeatBufferField!.GetValue(null)!).Clear();

        return false; // skip the original's hardcoded-to-4 loop entirely
    }

    [HarmonyWrapSafe]
    private static bool LocaliaCore_Moniter__FixedUpdate__Prefix()
    {
        var self = Instance;

        try
        {
            var boardcastAvaliable = (int)self._boardcastAvaliableField!.GetValue(null)!;

            if (boardcastAvaliable > 0
                && Clock.Time > (float)self._nextBoardcastAvaliableTimeField!.GetValue(null)!
                && (bool)self._sendA2sInfoMethod!.Invoke(null, new object?[] { null })!)
            {
                boardcastAvaliable--;
                self._boardcastAvaliableField.SetValue(null, boardcastAvaliable);
                self._nextBoardcastAvaliableTimeField.SetValue(null, Clock.Time + 2f * (5 - boardcastAvaliable) + 1f);
            }

            var humanCount = 0;
            var availableCount = 0;
            var completedCount = 0;
            var canSend = false;

            if (Clock.Time > (float)self._nextHeartbeatTimeField!.GetValue(null)!)
            {
                self._nextHeartbeatTimeField.SetValue(null, Clock.Time + 0.5f);
                canSend = true;
            }

            var slotLookup = (ulong[])self._slotLookupField!.GetValue(null)!;
            var slotSNet = (SNet_Player[])self._slotSNetField!.GetValue(null)!;
            var slotAvaliable = (ulong[])self._slotAvaliableField!.GetValue(null)!;
            var gotModList = (bool[])self._gotModListField!.GetValue(null)!;

            for (var i = 0; i < PluginConfig.MaxPlayers; i++)
            {
                if (slotLookup[i] == 0uL || slotSNet[i] is null || slotSNet[i].IsBot)
                    continue;

                humanCount++;

                if (slotAvaliable[i] == 0uL)
                    continue;

                availableCount++;

                if (gotModList[i])
                    completedCount++;
                else
                    self._checkModListGettingMethod!.Invoke(null, new object[] { i });

                self._checkHeartbeatMethod!.Invoke(null, new object[] { i, canSend });
            }

            var playerCount = (int)self._playerCountField!.GetValue(null)!;
            var availCountField = (int)self._availableCountField!.GetValue(null)!;
            var completedCountField = (int)self._completedCountField!.GetValue(null)!;
            var changed = false;

            if (playerCount != humanCount)
            {
                changed = true;
                self._onHumanCountChangedMethod!.Invoke(null, new object[] { playerCount, humanCount });
            }

            if (availCountField != availableCount)
            {
                changed = true;
                self._onAvailableHumanCountChangedMethod!.Invoke(null, new object[] { availCountField, availableCount });
            }

            if (completedCountField != completedCount)
            {
                changed = true;
                // Matches the original's own (likely unintentional) use of availableCount here
                // rather than completedCount, preserved for behavioral parity.
                self._onModListHumanCountChangedMethod!.Invoke(null, new object[] { availCountField, completedCount });
            }

            if (changed)
            {
                var available = (int)self._availableCountField!.GetValue(null)!;
                var player = (int)self._playerCountField!.GetValue(null)!;
                var completed = (int)self._completedCountField!.GetValue(null)!;
                self._logsVerboseMethod!.Invoke(null, new object[] { $"human available: {available}/{player}, credited: {completed}" });
            }
        }
        catch (Exception ex)
        {
            self._logsErrorMethod!.Invoke(null, new object[] { ex.Message });
        }

        return false; // skip the original's hardcoded-to-4 loop entirely
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
    private static bool API__has_Everyone_Install_Plugin__Prefix(string PLUGIN_GUID, string minVer, string maxVer, ref bool __result)
    {
        var self = Instance;

        for (var i = 0; i < PluginConfig.MaxPlayers; i++)
        {
            var isValidHuman = (bool)self._isValidHumanMethod!.Invoke(null, new object[] { i })!;

            if (!isValidHuman)
                continue;

            var hasPlugin = (bool)self._hasInstallPluginMethod!.Invoke(null, new object?[] { i, PLUGIN_GUID, null, null })!;

            if (!hasPlugin)
            {
                __result = false;
                return false;
            }
        }

        __result = true;
        return false;
    }

    #endregion
}
