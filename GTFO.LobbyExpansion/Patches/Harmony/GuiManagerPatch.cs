using HarmonyLib;

namespace GTFO.LobbyExpansion.Patches.Harmony;

/**
 * Replaces the GuiManagerManualPatch sig-scan patch for
 * `m_playerPings = new SyncedNavMarkerWrapper[4]` inside GuiManager.Setup().
 *
 * That patch searches a ~7500-byte compiled method for the raw byte pattern
 * "BA 04 00 00 00 4C 8B E8" (mov edx, 4; mov r13, rax) and overwrites the
 * immediate 4. That pattern is not guaranteed to be unique inside a function
 * this large - GuiManager::Setup() very plausibly contains other small
 * array/enum allocations of length 4 compiled to the same byte sequence. If
 * FindSignatureInIl2CppMethod matches one of those instead of the one
 * immediately preceding m_playerPings' allocation, the patch "succeeds" (no
 * PatchingException, since a match WAS found) while silently leaving
 * m_playerPings at its vanilla length of 4 - which is consistent with Netstat's
 * MarkerPinger.Update() still throwing ArgumentOutOfRangeException indexing
 * m_playerPings by a 5th/6th player's PlayerSlotIndex().
 *
 * This does the same job without relying on matching raw machine code:
 * resize the array by reference, after the real Setup() has already run.
 * Remove the corresponding Patch entry from GuiManagerManualPatch.SetupPatches()
 * once this is confirmed working, so the two don't fight over the same field.
 */
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
