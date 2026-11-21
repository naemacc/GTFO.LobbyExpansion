using CellMenu;
using GTFO.LobbyExpansion.Util;

namespace GTFO.LobbyExpansion.Patches.Manual;

#pragma warning disable S101

public class CM_PageLoadoutManualPatch : ManualPatch
{
    protected override Type TargetType { get; } = typeof(CM_PageLoadout);

    protected override void SetupPatches()
    {
        Patches.AddRange([
            new Patch
            {
                Method = nameof(CM_PageLoadout.Setup),
                Description = $"Adjust CM_PageLoadout Setup m_playerLobbyBars size from 4 -> {PluginConfig.MaxPlayers}.",
                Pattern = "BA 04 00 00 00 E8 ?? ?? ?? ?? 48 8B D0 48 89 87 08 02 00 00",
                Offset = 1,
                Bytes = [PluginConfig.MaxPlayers],
            }
        ]);
    }
}
