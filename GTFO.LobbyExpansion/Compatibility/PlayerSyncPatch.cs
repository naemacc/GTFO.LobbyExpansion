using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;

namespace GTFO.LobbyExpansion.Compatibility;

public class PlayerSyncPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "io.takina.gtfo.PlayerSync";

    public static PlayerSyncPatch Instance { get; } = new();

    private Type? _staminaSyncType;

    private PlayerSyncPatch() { }

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

    #endregion

    [HarmonyWrapSafe]
    private static bool StaminaSync__HandlePacket__Prefix(byte[] data, SNet_Player? sender)
    {
        if (sender is not null)
            return true; 

        L.Warning($"{nameof(PlayerSyncPatch)}: dropped a StaminaSync packet with an unresolved sender (likely a leave/rejoin race).");
        return false; 
    }
}
