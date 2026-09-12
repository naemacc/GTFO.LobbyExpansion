using System.Collections.Generic;
using ChainedPuzzles;
using HarmonyLib;
using Player;

namespace GTFO.LobbyExpansion.Patches.Harmony;

[HarmonyPatch(typeof(CP_BasicMovable), nameof(CP_BasicMovable.PauseMovement))]
[HarmonyPriority(Priority.Last)]
public static class ECC_CompatibilityPatch
{
    [HarmonyPrefix]
    public static bool PauseMovement__Prefix(CP_BasicMovable __instance)
    {
        var totalAlive = CountAlivePlayersInLevel();
        if (totalAlive <= 4)
            return HarmonyControlFlow.Execute; // let vanilla PauseMovement run normally

        var bioscanCore = __instance.GetComponent<CP_Bioscan_Core>();
        if (bioscanCore == null)
            return HarmonyControlFlow.Execute;

        if (!__instance.OnlyMoveWhenScannig)
            return HarmonyControlFlow.Execute;

        // Recompute
        var actualCount = CountPlayersInScan(bioscanCore);

        var requireAll    = bioscanCore.m_playerScanner.ScanPlayersRequired.RequireAllPlayers();
        var requireSolo   = bioscanCore.m_playerScanner.ScanPlayersRequired.RequireSoloPlayer();
        var noRequirement = bioscanCore.m_playerScanner.ScanPlayersRequired == PlayerRequirement.None;

        var shouldMove = noRequirement ||
                         (requireAll  && actualCount >= totalAlive) ||
                         (requireSolo && actualCount == 1);

        if (shouldMove)
        {
            // all players are in scan logs
            L.Verbose($"[ECC Comp] Suppressing PauseMovement={bioscanCore.m_puzzleIndex}" + $" | actualInScan={actualCount} | totalAlive={totalAlive}");
            return HarmonyControlFlow.DontExecute;
        }

        return HarmonyControlFlow.Execute; //players just not in scan
    }

    private static int CountPlayersInScan(CP_Bioscan_Core core)
    {
        var scanner = core.m_PlayerScannerComp?.TryCast<CP_PlayerScanner>();
        if (scanner == null)
            return 0;

        var radiusSqr = scanner.Radius * scanner.Radius;
        var scanPos   = core.transform.position;
        var count     = 0;
        var players   = PlayerManager.PlayerAgentsInLevel;

        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p != null && p.Alive && (p.Position - scanPos).sqrMagnitude < radiusSqr)
                count++;
        }

        return count;
    }

    private static int CountAlivePlayersInLevel()
    {
        var players = PlayerManager.PlayerAgentsInLevel;
        var count   = 0;
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p != null && p.Alive)
                count++;
        }
        return count;
    }
}
