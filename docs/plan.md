# Quasimorph Co-op: feature plan (handoff)

Context for Claude Code: this plan was written from the mod's source (commit 06419aa) and docs/design.md, **without** the decompiled game. Items marked **[check in game code]** depend on game internals; verify them against the decompiled `Assembly-CSharp` before implementing. Goal: close feature gaps against pa3k's "Multiplayer" Workshop mod (id 3803123951). LAN/direct IP is out of scope. Rough time estimates assume focused work.

## Phase 0: Test between two real PCs (1–2 sessions)
Every phase below adds network messages, and the online code has never run between two machines. Before starting, play one full mission with a friend: a floor change, gear changes on the ship, and a forced resync. Fix whatever breaks.

## Phase 1: Solo and dropping the partner (~1 day)
- **Bug to fix first:** `SavePatches` (Postfix, `MoveNextLocation`) carries `FinishSnapshot` into `PartyPersistence.Pending` and ignores the Partner setting. `SpawnPending` brings the old partner back, the party has 2 members, so `Partner.AutoAdd` never runs. Changing the partner (or setting "none") mid-mission does nothing until the mission ends, even though the menu says "joins from the next level on".
  - Fix: when building `Pending`, keep only `Partner.Get(state)`. A dropped partner goes back to the ship at the floor exit, the same as extracting.
- **Hold partner (new menu option):** a held partner automatically skips each action point (`SkipTurn` right after `MakeActive` in `TurnPatches`). It stays on the map and enemies can still hit it, so it can't be used as a free extract. Online this must be a synced action at an action-point boundary: add a new `DirectAction`.
- **Friend disconnects (`OnPeerLost` / `Stop`):** today this silently becomes hotseat. Instead, ask the host to choose "Hold partner" (default) or "Control partner".

## Phase 2: Healing the partner and handing over items (2–3 days)
pa3k added healing a teammate on Sep 30, so this is a real gap.
- **Treat partner:** when the mercs are adjacent, open the health screen on the partner, using your own medkits and splints. Wound clicks are already replayed through `InventoryOps`; add a target creature id to them (`CommandCodec` already has `TCreature`).
  - **[check in game code]** Can the health screen show a creature other than `Creatures.Player`?
- **Give item:** reuse the corpse/container loot screen with the partner's inventory as the target. `ItemSync` already sends any watched storage that changes.
  - **[check in game code]** How does the loot screen pick its target?
  - Confirm the partner's inventory is in the set of storages your merc can reach.

## Phase 3: Turn modes (4–7 days; biggest risk to lockstep)
The host picks the mode per save, like Enemies (`EnemyScaling`): applies from the next level, locked while online.

| Mode | Behaviour | How |
|---|---|---|
| Alternating (current) | Action points come up in the order the game queues them | No change |
| Party first | Both mercs spend their action points before any enemy acts | Reorder the queue each round so the party's entries come before the monsters'. **[check in game code]** how `TurnController` builds a round and whether enemy entries are mixed between the player's |
| Shared pool | Whichever player acts first takes the next action point | Host decides who got there first and sends a new `Claim(apSeq, mercId)` message. Both games wait for it, then `MakeActive` that merc. A friend's claim costs one round trip |
| Free until combat | Shared pool while no enemy is spotted, then party first | Switch only at a round boundary; "spotted" is part of the simulation, so both games agree |

Decisions:
- Shared pool: cap each merc at its own max action points (recommended for balance), or allow any split like pa3k?
- Add the turn mode to the state hash (recommended: yes).

Supporting work:
- Add a `turnmode=` option to `LockstepTest`; run the bot under each mode at different frame rates.
- HUD line: "Your turn" / "Partner's turn" / "Free: act to claim", plus the partner's action points (requested by pa3k's users).

## Phase 4: Giving the friend a say on the ship
Build in tiers on the existing ShipSync pattern (snapshot, version, refuse on conflict).
- **4a. Mission vote and Ready (~2 days):** new `ShipMsg` kinds `Suggest(missionId)` and `Ready`. Host sees the friend's suggested mission. Start waits until the friend is Ready; host can override (reuse the `AllowMissionStart` warning).
- **4b. Friend picks their own merc (~half a day):** today only the host cycles the partner. Friend sends a request; host applies it and gets a notice.
- **4c. Requests the host approves (~2–4 days per kind of action):** friend builds a purchase, sale or craft on their ship copy and sends it like a gear change; host gets a confirm prompt; the game's own methods apply it. Start with trading.
  - **[check in game code]** trade and crafting methods, and whether they're safe to call off-screen.
- **Skip a fully live shared ship.** Recycling, cloning and healing run on timers in the host's state, so it would need lockstep on the ship too. docs/design.md already rules this out.

## Suggested order
0 → 1 → 2 → 4a + 4b → 3 → 4c. Phases 1, 2 and 4a/4b are small and ship quickly. Turn modes is the most likely to break lockstep, so it goes in once the basics are proven between two PCs.
