# GTFO LobbyExpansion (Fork)

> [!NOTE]
> The original repository for this mod appears to be abandoned. This fork is a personalized compatibility build for current GTFO community mods; it is not an official continuation or a managed revival of the upstream project.

## Branch Information

- **`personalized` (default):** The actively maintained branch with current compatibility patches, bug fixes, and configuration support.
- **`Build-14562266`:** Maintains compatibility with GTFO build `14562266`, before the later game security/update changes.
- **`master`:** The original upstream repository state, preserved for reference.

Release and build configuration can output branch-specific DLLs under the build output directory. Use the artifact for your GTFO build branch, not the DLL from the wrong build line.

## Features

LobbyExpansion expands the lobby and in-game player support beyond the vanilla four-slot assumptions. The default configured capacity is **8 players**, and custom counts must be at least **4**.

### Lobby and UI

- Expands lobby, loadout, expedition, elevator, map, compass, player-card, coverage, voice, and network slot handling for additional players.
- Adds player pagination and dynamic lobby/list layouts where game or mod UI code assumes only four slots.
- Applies manual and Harmony patches for both elevated lobby displays and runtime player/session systems.
- Keeps extra-slot bot permissions, bot names, and bioscan labels configurable.
- Includes fixes for elevator/cage/ride seating, stale player state, player cards, and 5+ player coverage or scanner behavior.

### Forge Compatibility

- **Forge Core:** Expands static and instance player-slot arrays, patches hardcoded four-player loops, and guards known parser-crash paths.
- **Forge Replay:** Expands replay rosters and player arrays to `MaxPlayers`, including player cards, statistics, ratings, team tables, analysis, reports, director behavior, first-person/spectate presentation, snapshot/body state, and optional player-scope methods.
- Replaces hardcoded replay bounds and stack/array allocations with configured player capacity while preserving separate enemy entity-ID ranges.
- Supports replay card pagination and initializes newly exposed player-stat/series/radar structures instead of leaving null or undersized elements (Switching pass the 4th card will switch cards page).

### Community Mod Compatibility

| Mod | Compatibility provided |
|---|---|
| ChatterReborn | Resizes per-player and enemy detection arrays, updates 4-player loops, supports late initialization, and prevents stale 5+ player aggro/detection state. _(Dinorush provided a large part of the original fix.)_ |
| OptionalNetworkAPI | Clears stale bot dictionary entries before player-add handling, preventing rejoin loops and `ArgumentException` failures. |
| LocaliaCore | Expands `Network_Manager` arrays and loops, fixes slot bounds and public mod-sync checks, and updates player monitor behavior. |
| PacksHelper | Resizes PacksHelper's static health, gear, tool, and information arrays for the configured lobby size. |
| ModList | Expands mod-button storage and updates exact loop bounds so 5+ players receive lobby mod-sync labels. |
| ConflictModList | Expands the lobby-seat array in `LobbyNetworkHud` when the third-party mod's seat HUD is smaller than the configured lobby. |
| PingConsumables | Suppresses only recurring PingConsumables housekeeping log events while preserving useful initialization logging. |
| PlayerSync | Guards null stamina-sync senders to prevent native-to-managed crashes for players leaving or rejoining. |
| ZombifiedInitiative | Guards orphaned zombie components and forwards non-host sentry-mode commands to the host using the mod's networking protocol. |
| ExtraObjectiveSetup / ECPC | Includes the salvaged ECPC T-scan coroutine guard for the `ECC_CompatibilityPatch`. |

Compatibility patches are optional and activate only when the matching plugin GUID is loaded. Missing or incompatible optional mods do not prevent the core LobbyExpansion patches from loading.

## Configuration

Configuration is loaded from BepInEx's configured `ConfigPath`.

### Player count

Create or edit:

```text
BepInEx/config/LobbyExpansionConfig.json
```

```json
{
  "MaxPlayers": 8
}
```

- `MaxPlayers` defaults to `8`.
- Values below `4` fall back to the default and are logged as invalid.
- The custom player count is used by core patches and supported compatibility patches.

### User data

Lobby slots, bot names, and bioscan labels are stored separately at:

```text
BepInEx/config/LobbyExpansionConfig_UserData.json
```

The file is created automatically with defaults. Its supported fields are:

```json
{
  "SlotPermissions": {},
  "CustomExtraBotNames": [],
  "BioscanLetters": ["A", "B", "C", "D", "E", "F", "G", "H"]
}
```

- `SlotPermissions` saves permissions for extra lobby slots.
- `CustomExtraBotNames` replaces default names for slots 5 and onward.
- `BioscanLetters` supplies custom labels for bioscan interfaces.

Missing user-data fields are restored to safe defaults at startup. The configured bioscan-letter list is extended as needed for extra slots.

## How to Install

Pre-compiled releases are available on the [Releases page](https://github.com/naemacc/GTFO.LobbyExpansion/releases/latest).

1. Install GTFO BepInEx.
2. Install any optional community mods you want this mod to patch.
3. Download the compressed `.dll` for the matching GTFO branch/build and extract it.
4. Place the DLL in `BepInEx/plugins` (or in the mod-manager profile's plugin folder).
5. Launch the game. Optional compatibility patches are selected from the plugins currently loaded at startup.

## Building

The project is a `.NET 6` IL2CPP/BepInEx mod targeting `net6.0-windows` and `x64`.

Set `GameFolder` to the GTFO game directory in `GameFolder.props`. A build can then:

- copy the output automatically to `BepInEx/plugins` when `CopyToPluginFolder` is enabled;
- append the current Git branch to the build/target output path;
- produce Debug or Release builds for the repository's branch-specific setup.

## Credits

- The original LobbyExpansion authors.
- **Dinorush** for the foundational ChatterReborn compatibility work.
- Community mod authors and maintainers whose plugins are detected and patched by this fork.
- The original authors of salvaged compatibility patches preserved in this repository.
