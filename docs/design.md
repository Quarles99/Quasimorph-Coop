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

1. **Local hotseat.** Swap the active merc into the slot. Patch lookups, death, saves and `FinishGame`.
2. **Online, first pass ("networked hotseat").** Steam P2P via the bundled Steamworks.NET 20.1.0 (`SteamNetworkingMessages` and `SteamMatchmaking` lobbies).
   - **Session start.** The host saves. Both peers then load *that same save*, the host included, because `TurnController` isn't saved and a live queue won't match a freshly loaded one. The client receives the slot's files (session, header, space, `dung_*`, `_decals`, coop sidecar; about 1.3 MB, sent in chunks of 512 KB or less) and loads them into a spare slot. The host sends the gameplay RNG state once both are in.
   - **Lockstep.** Both machines simulate. A merc's owner sends its actions: `PlayerCommandQueue` commands, plus the direct calls that bypass the queue (skip turn, facing, movement speed, reload, swap weapon, fire mode). The other machine replays them on the same action point. An action-point hash comparison detects desyncs.
   - **The slot holds the active merc on both machines.** AI, curse regen and invisibility read `creatures.Player`, so it must be identical on both. Local input is blocked while the other player's merc is active.
   - **The friend plays a merc from the host's roster, in a copy of the host's world.** Ownership is by merc (the host's chosen partner is the friend's), not party order.
3. **Online, inventory and recovery.**
   - **Inventory screens.** They move items through a lot of UI code (drag and drop removes the item from every storage until it's dropped), so `Net/ItemSync` doesn't replay that code. The acting machine records the reachable storages (party inventories, floor, corpses and containers within 2 cells) when the action point starts and sends any storage whose save-format JSON changed. The other machine loads it into its existing `ItemStorage` objects (`LoadJSON` keeps every reference valid), then redoes what the bypassed storage events would have done (weight, resists, 3D view, HUD).
   - **Actions with effects beyond items** are replayed as calls inside the gameplay RNG scope on both machines (`Net/InventoryOps`): consumables, devices, exp gainers, fire removal, disassembly, spilling, repairs, `Player.StartReload`, `EffectsController.PropagateAction`, `PlayerInteractionSystem.EndPlayerTurn`, and wound clicks on the health screen. Item arguments are (storage address, index), or a serialized copy for an item that is in no storage (being dragged). Storage changes are always sent before the next recorded input, so order is kept.
   - **Turn end after inventory use.** The game ends that action point in `DungeonGameMode.Update`, outside the simulation, when the inventory closes. The owner sends that as `TurnEnd`; the other machine blocks its own copy of that check and waits for it.
   - **Read-only screens.** While the other player's merc is in the slot, `TurnSystem.CanProcessPlayerTurn` reports false to UI code whenever a screen other than the HUD is open.
   - **Desync recovery.** A hash mismatch (or an action that fails to apply) makes the host re-send its level once a merc is waiting for input; both reload it.
   - **Shared vision.** Each inactive party merc gets its own line-of-sight pass into the same `MapController` listener right after the slot merc's, so fog and "seen by player" cover the whole party. It affects AI, so both machines must run it, and they do.
   - **Deaths.** A dead deployed merc hands `MercenaryState.InRaid` and party slot 0 to a survivor. Fallen party mercs are listed in `slot_N_coop_fallen.dat` and resolved like `MissionSystem.MercDied` after `ProcessFinishedDungeonData`.
   - **Station menus and the shuttle.** Opening one is a world command both machines replay; the buttons inside are UI code, so `Net/StationOps` sends each button's effect as an op (autodoc HP and wounds, repair and recharge stations, battery recharge, workbench crafting, shuttle mission exit, abort) and replays it through the station's own methods. On the watching machine the menu the replayed command opened is closed again at once (the interaction itself still runs: scanners update what they reveal, workbenches may roll their recipe). Station charge is part of the state hash. Near a shuttle, `ItemSync` also watches the ship cargo tabs and the shuttle cargo. Mission exit and abort flush the outbox before the level is torn down.
   - **Per-merc AI reads.** `AiVisionCalculator.FindTargetsInSight` drops invisible mercs individually instead of the first Player when the slot merc is invisible. `Creature` code that reads `_creatures.Player.Mercenary` (curse data, resists, look angle) uses the creature's own mercenary when the creature is a merc.
   - **The ship (friend's merc only).** `Net/ShipSync`: the friend's copy of the ship asks the host for a snapshot of the storages the arsenal touches (the merc's containers and inventory fields, the ship cargo tabs, fridge, recycling bin, shuttle cargo) when the friend opens their merc's arsenal, and sends the storages it changed when it closes. The host applies them only if each of those storages still serializes exactly as it was sent (and the snapshot version is current), then sends a fresh snapshot either way. No merging: conflicts are refused and the friend redoes the change. Changes arriving after the host left the ship are refused. Only the host starts missions and recycles. A friend who joins while the host is on the ship is sent the ship save (the host doesn't reload).
4. **Feature parity with the Multiplayer Workshop mod (current; see docs/plan.md).**
   - **Changing or dropping the partner mid-mission.** On a floor change only the current partner is carried over. Any other party merc goes back to the ship the same way as an extraction, and `Partner.AutoAdd` brings in the new pick.
   - **Hold partner.** A held party merc stays on the map but passes each action point. `SkipTurn` runs right after `ProcessActionPoint` on both machines and is never sent. Online, only the host toggles it, as a `DirectAction.Hold` recorded on the host's merc's action point so both machines apply it at the same point. The flag is saved in the party sidecar (` held` after the position), so it survives floors, reloads and resyncs, and it is part of the state hash. When the session ends mid-level, the host's game holds the friend's merc instead of handing it to the host. A friend joining again releases it. The last living merc is never held.
5. **Later.** Writing the friend's results back to their own campaign (not planned: it's the host's campaign). A fully shared ship (missions, trading, crafting) would need its own sync and isn't planned.

## Determinism risks for milestone 2+

- `UnityEngine.Random` is used in about 340 places. `UnityEngine.Random.state` can be read and set, so it can be synced alongside each command.
- There is no `System.Random` in game code (an earlier grep matched `ItemDropSystem.Randomize`).
- Visual and audio code (portraits, gibs, projectile views, sounds) draws from the same global `UnityEngine.Random` a frame-rate-dependent number of times. `Lockstep/GameplayRng` gives the simulation entry points (`TurnController.Tick`, `GameLoop.Tick_Dungeon`, `PlayerInteractionSystem.ProcessCmd`) their own saved state.
- `Lockstep/LockstepTest` is an offline harness. It loads a test slot, spawns fixed enemies, drives both mercs with a scripted bot, and logs a state hash per action point. Comparing runs at different frame rates shows whether lockstep holds.
- Some gameplay runs in coroutines the simulation starts and Unity resumes later (explosions roll damage, stun and fire after a visual delay; shooting and throwing continue after animations). `Lockstep/SimCoroutines` wraps any coroutine started inside the simulation scope so each step of it runs in that scope too.
- Projectiles advance by `Time.deltaTime`, so a shot took a frame-rate-dependent number of steps, and some rolls depend on per-step state. Inside `Tick_Dungeon` projectiles use a fixed step through the game's own `BallisticSystem.IsOverridenDeltaTime`.
- `Lockstep/SimCoroutines` gives each coroutine started inside the simulation a private RNG stream seeded from the simulation state, so interleaving with other frame work can't change its rolls.
- The harness's `stn=N` (station buttons through the real window handlers), `exitat=N` (shuttle exit) and `abortat=N` options, and `selftest=ship` (the host's side of a gear change on one machine), cover the milestone 3 additions.
- The harness's `rngtrace=from-to` logs every simulation scope (and per-frame dungeon system) that drew random numbers in that action point range, to find where two runs part.
- Any game logic that depends on animation or frame timing will drift. Both peers need the same game build and the same mod list.
