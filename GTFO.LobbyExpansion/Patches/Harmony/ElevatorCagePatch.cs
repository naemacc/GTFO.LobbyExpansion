using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;

namespace GTFO.LobbyExpansion.Patches.Harmony;

[HarmonyPatch(typeof(ElevatorCage))]
public static class ElevatorCagePatch
{
    // Why do the empty return true prefixes exist? Catching exceptions to not cause infinite drop-in bug.

    [HarmonyPatch(nameof(ElevatorCage.SkipPreReleaseSequence))]
    [HarmonyPrefix]
    [HarmonyWrapSafe]
    public static bool SkipPreReleaseSequence__Prefix(ElevatorCage __instance, ref int playerId)
    {
        L.LogExecutingMethod($"{nameof(playerId)}: {playerId}");

        if (__instance.m_seatsFromShaft[playerId] == null)
        {
            L.Warning($"playerId {playerId} has a null seat. Skipping call.");
            return HarmonyControlFlow.DontExecute;
        }

        return HarmonyControlFlow.Execute;
    }

    [HarmonyPatch(nameof(ElevatorCage.RegisterSpawnPoints))]
    [HarmonyPrefix]
    [HarmonyWrapSafe]
    public static bool RegisterSpawnPoints__Prefix(ElevatorCage __instance)
    {
        L.LogExecutingMethod();
        __instance.m_spawnPoints = new Il2CppReferenceArray<PlayerSpawnpoint>(PluginConfig.MaxPlayers);

        for (var i = 0; i < PluginConfig.MaxPlayers; i++)
        {
            var seat = i % 4;
            __instance.m_spawnPoints[i] = PlayerManager.RegisterSpawnpoint(
                PlayerspawnpointType.StartInElevator,
                i,
                __instance.m_seatsFromShaft[seat]);
        }

        return HarmonyControlFlow.DontExecute;
    }

    [HarmonyPatch(nameof(ElevatorCage.PlaySeatOpenStraps))]
    [HarmonyPrefix]
    [HarmonyWrapSafe]
    public static bool PlaySeatOpenStraps__Prefix(int playerId)
    {
        if (playerId > 3)
        {
            L.Verbose("Skipping PlaySeatOpenStraps for playerId > 3.");
            return HarmonyControlFlow.DontExecute;
        }

        return HarmonyControlFlow.Execute;
    }
}
