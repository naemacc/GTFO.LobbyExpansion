using HarmonyLib;

namespace GTFO.LobbyExpansion.Patches.Harmony;

[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.Setup))]
internal static class GuiManagerPatch
{
    [HarmonyPostfix]
    private static void Postfix(GuiManager __instance)
    {
        var current = __instance.m_playerPings;

        if (current is not null && current.Length >= PluginConfig.MaxPlayers)
            return; // already big enough (e.g. Setup() ran again after we already expanded it)

        var expanded = new SyncedNavMarkerWrapper[PluginConfig.MaxPlayers];

        if (current is not null)
            Array.Copy(current, expanded, Math.Min(current.Length, expanded.Length));

        __instance.m_playerPings = expanded;

        L.Debug($"{nameof(GuiManagerPatch)}: expanded GuiManager.m_playerPings to {PluginConfig.MaxPlayers}.");
    }
}
