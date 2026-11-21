using BepInEx.Unity.IL2CPP.Utils.Collections;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using UnityEngine;

namespace GTFO.LobbyExpansion.Patches.Harmony;

[HarmonyPatch(typeof(ElevatorSeat))]
public static class ElevatorSeatPatch
{
    private static int _frame;

    [HarmonyPatch(nameof(ElevatorSeat.SkipPreReleaseSequence))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void SkipPreReleaseSequence__Postfix(CellSoundPlayer sound)
    {
        sound.Stop();

        var currentFrame = Time.frameCount;

        if (_frame != currentFrame)
        {
            // Play sound *once*, a frame later - should be unnoticeable
            CoroutineManager.StartCoroutine(CoroutineHelpers.NextFrame(() =>
            {
                sound.Post(AK.EVENTS.PLAY_15_ELEVATOR_RELEASE, isGlobal: true);
            }).WrapToIl2Cpp());
        }

        _frame = currentFrame;
    }
}

[HarmonyPatch(typeof(ElevatorSeat._PreReleaseSequence_d__33))]
public static class ElevatorSeatPatchTwo
{
    // Pointer of the local player's seat, or IntPtr.Zero until we've seen it.
    //
    // This used to be an `ElevatorSeat _localSeat = null!` and the postfix dereferenced
    // `_localSeat.Pointer` unconditionally. With more seats than vanilla, a non-local seat's
    // iterator can run MoveNext() before the local seat's has run, so _localSeat was still null
    // and the postfix threw a NullReferenceException (logged 4x at elevator start). A non-local
    // seat has nothing to do in the second branch anyway, so skipping until the local seat is
    // known is the same behavior without the exception. Storing the pointer value instead of the
    // wrapper also avoids holding a stale wrapper across levels.
    private static IntPtr _localSeatPointer = IntPtr.Zero;

    [HarmonyPatch(nameof(ElevatorSeat._PreReleaseSequence_d__33.MoveNext))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void ElevatorSeat___PreReleaseSequence_d__33__MoveNext__Postfix(ElevatorSeat._PreReleaseSequence_d__33 __instance)
    {
        var seat = __instance.__4__this;

        if (seat == null)
            return;

        if (__instance.isLocal)
        {
            _localSeatPointer = seat.Pointer;
        }

        if (_localSeatPointer != IntPtr.Zero && seat.Pointer == _localSeatPointer)
        {
            __instance.isLocal = true;
        }
    }
}
