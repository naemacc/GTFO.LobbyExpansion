using HarmonyLib;

namespace GTFO.LobbyExpansion.Compatibility;

public interface IModCompatibilityPatch
{
    bool ShouldApply();

    void Apply(Harmony harmony);
}
