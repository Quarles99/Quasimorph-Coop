# Keeping the ship and star map in sync (investigation)

Written from the decompiled game (`Assembly-CSharp`, current Steam build) on 2026-11-11. Status: built (`Net/NetSession.World.cs`, `Patches/WorldPatches.cs`) but not yet tested between two PCs. The "Risks" section lists what to watch for.

## How the game runs the ship layer

- **One clock.** `SpaceTime` (`Time`, `TimeScale`, `Paused`) is advanced by `SpaceTimeSystem.Update` once per frame. Any screen other than the space HUD pauses it (`Paused = UI.IsAnyShowing(SpaceHudScreen, InputLockerScreen)`), except during travel.
- **Everything else hangs off that clock.** `GameLoop.Tick_Space` runs about 20 systems every frame, in order: orbit rings, the clock, item expiry, item production, cargo, ship projects, merc recovery, missions, news, stations, five faction systems, station restocking, ship travel, shipping, world prices, tutorial hints. Most of them compare `SpaceTime.Time` with a timestamp (`SpaceTickTimers`, per-station and per-mission dates) and roll `UnityEngine.Random` when it passes.
- **Campaign state is 22 "global components"** (`ComponentsLayout`): `Difficulty`, `SpaceTime`, `MagnumProjects`, `ItemsPrices`, `Mercenaries`, `Factions`, `Stations`, `Missions`, `Shippings`, `TravelMetadata`, `MagnumProgression`, `MagnumCargo`, `News`, `RaidMetadata`, `Statistics`, `AchievementRunState`, `StoryTriggers`, `SpaceTickTimers`, `MonsterTransfer`, `CombatLog`, `DebugData`, `UserMods`. The ship-map scene adds `Spaceship`, `SpaceObjects` and `Scenarios` (`slot_N_space.dat`).
- **They load in place.** `SaveToJSON.CreateNode(component)` writes one; `component.LoadJSON(node)` refills the *same* instance (it replaces each `[Save]` field with freshly built objects and calls `IAfterLoad`). The game's own load does exactly this. It is the same trick `ItemSync` uses for storages.
- **Size** (measured on local saves, JSON / zlib): an early campaign is about 1.1 MB / 70 KB, a late one about 5.7 MB / 350 KB. `Stations`, `Missions` and `Shippings` are over 95% of it; everything else is under 15 KB compressed.

## Why the friend's copy drifts today

The friend loads the host's save once (on joining, and each level) and then runs the whole `Tick_Space` on their own: their own clock speed, their own random rolls for missions, news, prices and faction moves. Within minutes their star map shows missions the host doesn't have. Only their merc's gear goes back to the host (`ShipSync`).

## What's built

**Clock.** The host sends `ShipMsg.Clock` every 0.25 s, and right away when speed, pause or the flight timer's running state changes. It carries the time, speed and pause, plus the flight timer while travelling. On the friend's copy a `SpaceTimeSystem.Update` postfix overwrites `Time` and `TimeScale` and sets `DeltaTime` to how far the host's clock moved. Mid-flight it also sets the friend's `TravelTimer` to the host's (extrapolated between messages), and pauses it when the host's is paused. The friend's speed buttons follow the host's.

**No simulation on the friend's copy.** While the friend is on the host's ship (`ShipClient`), a `GameLoop.Tick_Space` prefix runs only `OrbitRingsSystem`, `SpaceTimeSystem` and `TravelSystem.ProcessSpaceshipTravel`. Space scenarios (`SpaceGameMode.InitScenario`, `RunScenarioByStoryId`) and orbit events (`IngameEventSystem.RandomizeOrbitEvent`) don't run there. The star map's Departure button tells the friend that only the host flies.

**Component stream.** When the friend's copy arrives on the ship it sends `ShipMsg.Request(false)`, which tells the host to start (or restart) streaming. Every second the host serializes all components except `Stations`, `Missions` and `Shippings` with `SaveToJSON.CreateNode`, plus one of those three in turn. It sends the ones whose JSON changed as `ShipMsg.World`, with a version number. `TravelMetadata` isn't sent mid-flight. The first send after arrival is everything. A serialize taking over 40 ms is logged (at most every 30 s).

**Loading on the friend's copy.** Received JSON waits, newest per component (older versions are dropped, since a big chunked message can complete after a newer one), until no screen other than the space HUD is open, no drag is in progress and the arsenal isn't being edited. Then it's loaded with `LoadJSON` in `DeserializeGlobalComponents` order, followed by `PresetIdRegistry.NormalizeMissions` when missions came. If the host's ship is at another planet and nobody is flying, the friend's ship is put in orbit there with no arrival event. Then the station panels (`SpaceStationsWindow.Initialize`), the newsline and the questlines are rebuilt.

**Travel.** A `TravelSystem.StartSpaceshipTravel` postfix on the host sends `ShipMsg.Travel` (from, to, Bramfatura counter after the jump). The friend's copy puts its ship at `from` if needed, then calls `StartSpaceshipTravel` itself and copies the counter. The clock keeps the two flights in step.

**Gear.** The friend's merc's gear still goes through the arsenal request/change/result protocol (`ShipSync`). A world update never lands while the arsenal is open.

### Risks to check in the first two-PC test

- **Stale references in the space views.** `LoadJSON` replaces each `Mission`, `Station` and `Shipping` object with a new one. Screens are covered by waiting until they're closed, and the HUD's station panels, newsline and questlines are rebuilt. Anything else on the star map that caches one of those objects (map markers, tooltips) would show old data until it's reopened.
- **`IAfterLoad` side effects.** Check what each component's `OnAfterLoad` does when run on a live scene (it was written for a fresh load).
- **Cost of serializing.** `SaveToJSON` on a 2–3 MB `Missions` or `Stations` every 3 s may hitch the host's frame late in a campaign. Look for the "Serializing the star map" warning in the host's log; if it shows up, compare a cheap fingerprint first (counts and ids), or serialize on a background thread from a snapshot.
- **Scenarios.** Story scenarios on the ship (`SpacemodeTutorial*`, Bramfatura jumps) change `TimeScale` and show screens. They no longer run on the friend's copy, so the friend misses their dialogs. Bramfatura jumps in particular are untested.
- **Merc and cargo reloads.** `Mercenaries` and `MagnumCargo` are reloaded whole on the friend's copy. Gear edits are protected by the arsenal check, but anything else that holds a `Mercenary` across a reload (for example the selected merc in `SpaceshipScreen`) is refreshed only when that screen is reopened.

### Alternatives considered

- **Lockstep the ship layer** like the dungeon (same seed, replay every ship action). It would need every ship screen's buttons routed as ops, as `StationOps` does for station menus, but for trading, crafting, the mission board, travel, recruiting and projects. Weeks of work and fragile.
- **Reload the ship save on the friend** whenever something changes. It reuses the join path and is always correct, but each reload is a loading screen of a few seconds and closes whatever the friend has open. It's usable as a fallback ("Refresh ship" in the friend's Co-op menu) but not as the main sync.
