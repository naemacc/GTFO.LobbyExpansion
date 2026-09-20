using System.Collections;
using System.Reflection;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;

namespace GTFO.LobbyExpansion.Compatibility;

public class OptionalNetworkAPIPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "Dinorush.OptionalNetworkAPI";

    public static OptionalNetworkAPIPatch Instance { get; } = new();

    private FieldInfo? _playersField;
    private FieldInfo? _botsField;

    private OptionalNetworkAPIPatch() { }

    #region Overrides of ModCompatibilityPatch

    public override bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public override void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        var modManagerType = ReflectionUtil.GetRequiredTypeByName("ModManager", assembly);

        _playersField = AccessTools.Field(modManagerType, "_players");
        if (_playersField is null)
            throw new Exception($"Unable to find {modManagerType.FullName}._players.");

        _botsField = AccessTools.Field(modManagerType, "_bots");
        if (_botsField is null)
            throw new Exception($"Unable to find {modManagerType.FullName}._bots.");

        var onAddPlayer = AccessTools.Method(modManagerType, "OnAddPlayer");
        if (onAddPlayer is null)
            throw new Exception($"Unable to find {modManagerType.FullName}.OnAddPlayer(SNet_Player).");

        var prefix = AccessTools.Method(typeof(OptionalNetworkAPIPatch), nameof(OnAddPlayer_Prefix));
        harmony.Patch(onAddPlayer, prefix: new HarmonyMethod(prefix));

        L.Debug($"Applied patches in {nameof(OptionalNetworkAPIPatch)}.");
    }

    #endregion

    [HarmonyWrapSafe]
    private static void OnAddPlayer_Prefix(SNet_Player player)
    {
        if (!player.IsBot)
            return; // real players go through SNet_SessionHub.OnLeftLobby reliably - leave them alone

        var lookup = player.Lookup;

        ClearStaleEntry(Instance._playersField, lookup);
        ClearStaleEntry(Instance._botsField, lookup);
    }

    private static void ClearStaleEntry(FieldInfo? field, ulong lookup)
    {
        if (field?.GetValue(null) is not IDictionary dict)
            return;

        if (!dict.Contains(lookup))
            return;

        L.Warning(
            $"{nameof(OptionalNetworkAPIPatch)}: clearing a stale bot entry (lookup {lookup}) from OptionalNetworkAPI's " +
            "tracking before re-adding it - likely left behind by an interrupted bot removal elsewhere.");
        dict.Remove(lookup);
    }
}

