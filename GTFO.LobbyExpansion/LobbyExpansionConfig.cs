using SNetwork;
using System.Collections.Generic;

namespace GTFO.LobbyExpansion;

public class LobbyExpansionPlayerCount
{
    public byte MaxPlayers { get; set; } = PluginConfig.DEFAULT_MAX_PLAYERS;
}

public class LobbyExpansionConfig
{
    public Dictionary<int, SNet_PlayerSlotManager.SlotPermission>? SlotPermissions { get; set; } = [];
    public List<string>? CustomExtraBotNames { get; set; } = [];
    public List<string>? BioscanLetters { get; set; } = PluginConfig.DEFAULT_BIOSCAN_LETTERS;
}
