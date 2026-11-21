using HarmonyLib;

namespace GTFO.LobbyExpansion.Patches.Harmony;

[HarmonyPatch(typeof(DefaultCharacterLayouts))]
public static class DefaultCharacterLayoutsPatch
{
    [HarmonyPatch(nameof(DefaultCharacterLayouts.GetDefaultVanityItems))]
    [HarmonyPriority(Priority.High)] // Priority high to fix some errors with StrikerMode.
    [HarmonyPrefix]
    public static bool GetDefaultVanityItems__Prefix(ref int characterIndex)
    {
        if (characterIndex > 3)
        {
            characterIndex %= 4;
            L.Verbose($"Clamping {nameof(characterIndex)} to {characterIndex}.");
        }

        return HarmonyControlFlow.Execute;
    }
}
