using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;

namespace GTFO.LobbyExpansion.Compatibility;

/**
 * PlayerSync's StaminaSync.HandlePacket() does not guard against a null sender:
 *
 *   private static void HandlePacket(byte[] data, SNet_Player? sender)
 *   {
 *       StaminaInfo value = StaminaInfo.Deserialize(data);
 *       StaminaInfos[sender.Lookup] = value;   // <- NRE if sender is null
 *   }
 *
 * The sender comes from PlayerSync.Network.Net.OnReceive() via
 * NetHelper.GetPlayerByID(messagerID), which returns null whenever the sending
 * player can't currently be found in SNet.LobbyPlayers - most commonly when a
 * stamina packet is already in flight for a player who is mid-leave/rejoin.
 * AmmoSync.HandlePacket() already null-checks its sender the same way; this
 * patch makes StaminaSync do the same via a Harmony prefix, since we don't
 * have a compile-time reference to PlayerSync's internal StaminaSync type.
 *
 * Observed as, roughly:
 *   [Error :Il2CppInterop] During invoking native->managed trampoline
 *   Exception: System.NullReferenceException: Object reference not set to an instance of an object.
 *      at PlayerSync.Sync.Stamina.StaminaSync.HandlePacket(Byte[] data, SNet_Player sender)
 *      at PlayerSync.Network.Net.OnReceive(Il2CppStructArray`1 bytes, UInt32 size, UInt64 messagerID)
 */
public class PlayerSyncPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "io.takina.gtfo.PlayerSync";

    public static PlayerSyncPatch Instance { get; } = new();

    private Type? _staminaSyncType;

    private PlayerSyncPatch() { }

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

            _staminaSyncType = ReflectionUtil.GetRequiredTypeByName("StaminaSync", assembly);

            var original = AccessTools.Method(
                _staminaSyncType,
                "HandlePacket",
                new[] { typeof(byte[]), typeof(SNet_Player) });

            if (original is null)
                throw new Exception($"Unable to find {_staminaSyncType.FullName}::HandlePacket(byte[], SNet_Player).");

            var prefix = AccessTools.Method(typeof(PlayerSyncPatch), nameof(StaminaSync__HandlePacket__Prefix));
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));

            L.Debug($"Applied patches in {nameof(PlayerSyncPatch)}.");
        }
        catch (Exception ex)
        {
            L.Error($"{nameof(PlayerSyncPatch)}.Apply() failed: {ex}");
            throw;
        }
    }

    // Skips the original entirely when sender couldn't be resolved, instead of letting it
    // NullReferenceException on sender.Lookup. Mirrors AmmoSync's own (correct) null-check.
    [HarmonyWrapSafe]
    private static bool StaminaSync__HandlePacket__Prefix(byte[] data, SNet_Player? sender)
    {
        if (sender is not null)
            return true; // let the original run as normal

        L.Warning($"{nameof(PlayerSyncPatch)}: dropped a StaminaSync packet with an unresolved sender (likely a leave/rejoin race).");
        return false; // skip the original, avoid the NRE
    }
}
