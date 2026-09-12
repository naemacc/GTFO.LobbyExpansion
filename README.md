# GTFO LobbyExpansion (Personalized Fork)

> **⚠️ Notice:** The original repository for this mod appears to be dead/abandoned. This is **not** an official continuation or a new maintainer's revival, but rather a personalized fork created to keep the mod working smoothly alongside other popular community mods. 

### Branch Information
* **`personalized` (Default):** Contains all the custom compatibility patches, bug fixes, and additions.
* **`master`:** The original state of the upstream repository, preserved just in case it is ever needed for reference.

---

## 🛠️ What's New in This Fork?

This fork introduces several new Harmony patches to support lobbies with 5+ players across various community mods, fixing hardcoded limits and preventing crashes. 

### 🐛 Fixes
* **ChatterReborn Compatibility** *(Credit to Dinorush for a large part of the original fix)*
  * **Dynamic array sizing:** Replaces hardcoded loops (`for (int i = 0; i < 4; i++)`) to process all player slots correctly, preventing the mod from leaving stale detection/aggro scores behind for players 5+.
  * **Transpiler Fix:** Resolves an `IndexOutOfRangeException` when ordering a 5th+ player back to a bioscan by dynamically swapping `ldc.i4.4` to call `GetMaxPlayersAsInt()`.
  * **Error Handling & Fallbacks:** Exceptions during patch application now throw loudly instead of failing silently. Added late-initialization array fix-ups in case ChatterReborn's `Setup()` or `Awake()` runs before the patch applies.

### ✨ Additions
* **ExtraObjectiveSetup / ECPC Compatibility** *(Salvaged from upstream PR #3)*
  * Added a Harmony patch to fix the ECPC T scan. Because ECPC sometimes kills the coroutine, this patch safely suppresses the call. The implementation was adapted directly from the core to ensure stability.
* **LocaliaCore Compatibility**
  * Expanded `Network_Manager` static arrays (previously hardcoded to length 4) to support `MaxPlayers`, preventing crashes and corrupted slot-index bookkeeping.
  * Patched hardcoded loop bounds (0..3) in `LocaliaCore_Moniter.FixedUpdate()` and `Network_Manager.outLobbyClear()`.
  * Patched bounds checks in API methods (`is_Valid_Human`, `has_got_modlist`, `has_Everyone_Install_Plugin`) to ensure public modlist synchronization works correctly for expanded lobbies.
* **ModList Compatibility**
  * Expanded the `UI_ModButtons` array initialization to support `MaxPlayers`.
  * Added safe IL transpilers to replace hardcoded loop bounds in `GenerateLobbyButtons`, `checkSlotState`, and `ClearButtonText` with a dynamic cap. This ensures players 5+ properly receive their mod-sync UI labels on the lobby screen.
* **PlayerSync Compatibility**
  * Added a null check guard for StaminaSync packets. Drops packets safely if the sender is null (e.g., when a packet is in-flight for a player mid-leave/rejoin), mirroring existing behavior in AmmoSync and preventing native-to-managed trampoline crashes.
* **ZombifiedInitiative Compatibility**
  * **Orphaned Component Guard:** Prevents a continuous `NullReferenceException` loop in `ZombieComp.Update()` if a bot's `PlayerAgent` is destroyed by another mod's player removal bug.
  * **Sentry Mode Networking:** Fixed a feature gap where the "sentry mode" bot command wasn't networked when issued by a non-host client. Snapshots the `allowedmove` flag and forwards the toggle to the host using an unused `ZINetInfo.FUNC` ID (6), resetting `DescBase.Status` so bots can successfully un-stick and resume movement later.

---

## ⚙️ How to Build & Install

I am not providing pre-compiled releases for this fork. If you want to use these patches, you will need to build the mod yourself from source.

1. Clone this repository and make sure you are on the `personalized` branch.
2. Open the project in Visual Studio or your preferred C# IDE.
3. Ensure your project references point to your GTFO installation's managed assemblies (standard GTFO BepInEx modding setup).
4. Build the project.
5. Take the compiled `.dll` from the output folder and drop it into your `GTFO/BepInEx/plugins` folder.
6. Make sure the mods you are patching (ChatterReborn, LocaliaCore, etc.) are up to date.

## 🤝 Credits
* The original LobbyExpansion author(s).
* **Dinorush** for the foundational ChatterReborn fix.
* The original author of the salvaged ECPC patch PR.
