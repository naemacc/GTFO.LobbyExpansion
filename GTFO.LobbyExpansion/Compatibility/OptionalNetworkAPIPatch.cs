using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;
using System.Collections;
using System.Reflection;

namespace GTFO.LobbyExpansion.Compatibility;

/**
 * OptionalNetworkAPI's ModManager tracks every player/bot in two static Dictionary<ulong,
 * PlayerInfo> fields (_players, _bots), keyed by SNet_Player.Lookup. OnAddPlayer() uses plain
 * Dictionary.Add() for both:
 *
 *   internal static void OnAddPlayer(SNet_Player player)
 *   {
 *       if (_players.ContainsKey(player.Lookup)) return;
 *       ...
 *       _players.Add(player.Lookup, playerInfo);
 *       ...
 *       else if (playerInfo.IsBot)
 *       {
 *           _bots.Add(playerInfo.Lookup, playerInfo);   // <- throws if this Lookup is already present
 *           ...
 *       }
 *   }
 *
 * The only place ModManager ever removes an entry is OnRemovePlayer(), hooked on
 * SNet_SessionHub.OnLeftLobby - a real network departure event. Bots don't have their own
 * connection to disconnect, so if a bot's removal is ever interrupted upstream before that event
 * fires (exactly the class of bug behind the ZombifiedInitiativePatch orphaned-ZombieComp fix
 * elsewhere in this project - e.g. another mod's player-removal patch throwing partway through
 * in a 5+ player lobby), OptionalNetworkAPI never finds out and keeps a stale _bots/_players
 * entry forever. If a later bot is ever assigned the same Lookup, _bots.Add() throws:
 *
 *   [Error: HarmonyX] Error while running static void OptionalNetworking.Patches.NetworkPatches::
 *   Post_FoundMaster(). Error: System.ArgumentException: An item with the same key has already
 *   been added. Key: 1
 *      at OptionalNetworking.Managers.ModManager.OnAddPlayer(SNet_Player player)
 *
 * Confirmed this is still present in OptionalNetworkAPI 1.3.1's own decompiled source - the
 * 1.3.1 changelog's "Fixed MasterInfo not updating correctly" entry is a different, unrelated
 * bug and doesn't touch this code path.
 *
 * Rather than try to chase down exactly which upstream mod/scenario leaves the stale entry
 * (LocaliaCorePatch already closes the one documented cause of this general class of problem,
 * but there could be others not yet identified), this guards the symptom directly: a prefix on
 * OnAddPlayer() clears any pre-existing _players/_bots entry for this Lookup before the original
 * runs, but ONLY when the incoming player is a bot (SNet_Player.IsBot, checked independently of
 * OptionalNetworkAPI's own PlayerInfo.IsBot, which doesn't exist yet at this point). Real human
 * players are left completely untouched, since SNet_SessionHub-driven removal is reliable for
 * them and the original method's ContainsKey early-return is a legitimate idempotency guard we
 * don't want to defeat for a normal re-announce of an already-connected player.
 */
public class OptionalNetworkAPIPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "Dinorush.OptionalNetworkAPI";

    public static OptionalNetworkAPIPatch Instance { get; } = new();

    private FieldInfo? _playersField;
    private FieldInfo? _botsField;

    private OptionalNetworkAPIPatch() { }

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
