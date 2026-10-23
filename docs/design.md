# Design notes

Findings from the game code (build 25651594, Mono, Harmony 2.3.3 and Steamworks.NET bundled). Class and method names are from `Assembly-CSharp`, namespace `MGSC`.

## Mod loading

- Mods are folders with a `modmanifest.json` and a DLL, loaded by `UserModSystem`. Public static methods marked `[Hook(ModHookType.X)]` get called with an `IModContext` (whose `State` is the game's DI container).
- Workshop mods load from Steam. Local mods load from `<persistentDataPath>/LocalUserPresets/<mod>/`. A code-only mod passes the room-preset namespace filter as long as it has no `GameData/BinaryPresetsMap.json`.
- `TypeCache` scans mod assemblies, so mods can add `[ConsoleCommand]` classes. The console opens with the backquote key when `Data.Global.Console` is true.

## The one-player assumption

- `Creatures` has a single `[Save] Player Player` field plus `List<Creature> Monsters`. `creatures.Player` is read 958 times in 228 files.
- `Player.cs` itself barely uses the slot, and `CreatureFactory.CreatePlayer(mercenary, profile, position)` builds a complete merc. That's why a second `Player` instance works.
- `TurnController` is a plain queue of `ITurnable`s. `CreatureSystem.SpawnPlayer` adds the player once per action point. `TurnSystem.CanProcessPlayerTurn` requires `CurrentProcessable == creatures.Player`.
- `DungeonGameMode.Update` stops processing when the slot merc is dead, and subscribes only to the primary's `PlayerDied`.
- AI target selection (`AIVision.GetVisibleEnemies`) uses `Creatures.GetCreature(x, y)` and an alliance check, so anything those lookups return gets targeted.
- Three HUD panels cache the player (`HudCurrentWeaponPanel`, `HudFiremodePanel`, `ActiveImplantPanel`). `DungeonHudScreen.RefreshAllHud` re-initializes them.

## Milestones

1. **Local hotseat (current).** Swap the active merc into the slot. Patch lookups, death, saves and `FinishGame`.
2. **Online, first pass ("networked hotseat").** Steam P2P via the bundled Steamworks.NET 20.1.0 (`SteamNetworkingMessages` and `SteamMatchmaking` lobbies).
   - **Session start.** The host saves. Both peers then load *that same save*, the host included, because `TurnController` isn't saved and a live queue won't match a freshly loaded one. The client receives the slot's files (session, header, space, `dung_*`, `_decals`, coop sidecar; about 1.3 MB, sent in chunks of 512 KB or less) and loads them into a spare slot. The host sends the gameplay RNG state once both are in.
   - **Lockstep.** Both machines simulate. A merc's owner sends its actions: `PlayerCommandQueue` commands, plus the direct calls that bypass the queue (skip turn, facing, movement speed, reload, swap weapon, fire mode). The other machine replays them on the same action point. An action-point hash comparison detects desyncs.
   - **The slot holds the active merc on both machines.** AI, curse regen and invisibility read `creatures.Player`, so it must be identical on both. Local input is blocked while the other player's merc is active.
   - **The friend plays a merc from the host's roster, in a copy of the host's world.** Nothing goes back to the friend's own save yet.
   - **Open gap.** Inventory, looting and healing happen in UI screens outside the command queue.
3. **Online, robust.** Desync detection (state hashes) with full resync through the save serializer. Write the client's merc and loot back to the client's own save. Fix the milestone-1 limitations (mid-mission saves, per-merc extraction and death).

## Determinism risks for milestone 2+

- `UnityEngine.Random` is used in about 340 places. `UnityEngine.Random.state` can be read and set, so it can be synced alongside each command.
- There is no `System.Random` in game code (an earlier grep matched `ItemDropSystem.Randomize`).
- Visual and audio code (portraits, gibs, projectile views, sounds) draws from the same global `UnityEngine.Random` a frame-rate-dependent number of times. `Lockstep/GameplayRng` gives the simulation entry points (`TurnController.Tick`, `GameLoop.Tick_Dungeon`, `PlayerInteractionSystem.ProcessCmd`) their own saved state.
- `Lockstep/LockstepTest` is an offline harness. It loads a test slot, spawns fixed enemies, drives both mercs with a scripted bot, and logs a state hash per action point. Comparing runs at different frame rates shows whether lockstep holds.
- Any game logic that depends on animation or frame timing will drift. Both peers need the same game build and the same mod list.
