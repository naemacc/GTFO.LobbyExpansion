using ChainedPuzzles;
using HarmonyLib;
using Player;
using System.Collections.Generic;

namespace GTFO.LobbyExpansion.Patches.Harmony;

#pragma warning disable S101

[HarmonyPatch(typeof(CP_Bioscan_Core))]
public static class CP_Bioscan_CorePatch
{
    [HarmonyPatch(nameof(CP_Bioscan_Core.Setup))]
    [HarmonyPostfix]
    public static void Setup__Postfix(CP_Bioscan_Core __instance)
    {
        var hud = __instance.m_HUDComp?.TryCast<CP_Bioscan_Hud>();

        if (hud != null)
            CP_Bioscan_HudPatch.HudToCoreMap[hud.GetInstanceID()] = __instance;
    }

    [HarmonyPatch("OnSyncStateChange")]
    [HarmonyPostfix]
    public static void OnSyncStateChange__Postfix(
        CP_Bioscan_Core __instance,
        eBioscanStatus status,
        List<PlayerAgent>? playersInScan,
        int playersMax)
    {
        // Get counts for logging/HUD fix
        var syncCount = __instance.m_sync.GetCurrentState().playersInScan;
        var listCount = playersInScan?.Count ?? 0;

        if (status != eBioscanStatus.Scanning)
            return;

        // Fix travel scan movement for >4 players
        UpdateMovableScanMovement(__instance, playersMax);

        // HUD fix: only needed when sync count exceeds list count
        if (syncCount <= listCount)
            return;

        __instance.m_hud.SetPlayerData(
            __instance.m_puzzleIndex,
            syncCount,
            playersMax,
            IsLocalPlayerInScan(__instance, playersInScan),
            __instance.m_playerScanner.ScanPlayersRequired,
            __instance.m_playerScanner.ReduceWhenNoPlayer);
    }

    private static void UpdateMovableScanMovement(CP_Bioscan_Core core, int playersMax)
    {
        if (!core.IsMovable)
            return;

        var movingComp = core.m_movingComp;

        if (movingComp == null || !movingComp.OnlyMoveWhenScannig)
            return;

        // Calculate true player count by checking positions ourselves
        // Don't trust sync state - it may be stale due to movement feedback loop
        var actualCount = CountPlayersInScan(core);

        var requirement = core.m_playerScanner.ScanPlayersRequired;
        var requireAll = requirement.RequireAllPlayers();
        var requireSolo = requirement.RequireSoloPlayer();
        var noRequirement = requirement == PlayerRequirement.None;

        var shouldMove = noRequirement ||
                         (requireAll && actualCount == playersMax) ||
                         (requireSolo && actualCount == 1);

        if (shouldMove)
            movingComp.ResumeMovement();
        else
            movingComp.PauseMovement();
    }

    private static bool IsLocalPlayerInScan(
        CP_Bioscan_Core core,
        List<PlayerAgent>? playersInScan)
    {
        if (core.enabled && playersInScan != null)
            for (var i = 0; i < playersInScan.Count; i++)
                if (playersInScan[i] != null && playersInScan[i].IsLocallyOwned)
                    return true;

        if (!PlayerManager.TryGetLocalPlayerAgent(out var localPlayer) || !localPlayer.Alive)
            return false;

        var scanner = core.m_PlayerScannerComp?.TryCast<CP_PlayerScanner>();

        if (scanner == null)
            return false;

        var radius = scanner.Radius;
        var distSqr = (localPlayer.Position - core.transform.position).sqrMagnitude;
        return distSqr < radius * radius;
    }

    private static int CountPlayersInScan(CP_Bioscan_Core core)
    {
        var scanner = core.m_PlayerScannerComp?.TryCast<CP_PlayerScanner>();

        if (scanner == null)
            return 0;

        var radiusSqr = scanner.Radius * scanner.Radius;
        var scanPos = core.transform.position;
        var count = 0;
        var players = PlayerManager.PlayerAgentsInLevel;

        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];

            if (player != null && player.Alive)
            {
                var distSqr = (player.Position - scanPos).sqrMagnitude;

                if (distSqr < radiusSqr)
                    count++;
            }
        }

        return count;
    }
}
