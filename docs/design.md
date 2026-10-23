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
2. **Online, first pass.** Steam P2P via the bundled Steamworks.NET. The host sends the dungeon using the game's own `[Save]` serializer at mission start. The client sends commands (move, shoot, use item). The host runs them and broadcasts each command with the RNG state so the client replays it. On each machine the slot holds *that player's own* merc, and turn gating checks which merc belongs to which peer.
3. **Online, robust.** Desync detection (state hashes) with full resync through the save serializer. Write the client's merc and loot back to the client's own save. Fix the milestone-1 limitations (mid-mission saves, per-merc extraction and death).

## Determinism risks for milestone 2+

- `UnityEngine.Random` is used in about 340 places. `UnityEngine.Random.state` can be read and set, so it can be synced alongside each command.
- `System.Random` instances also exist in `Monster`, `CreatureSystem`, `DungeonBuilder`, `MissionSystem` and `TravelSystem`. These need syncing or a host-only path.
- Any game logic that depends on animation or frame timing will drift. Both peers need the same game build and the same mod list.
