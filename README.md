# Quasimorph Co-op

An experimental mod that adds a second player-controlled merc to Quasimorph missions, with the goal of eventual online co-op.

**Status: milestone 2 (online, first pass).** Two full mercs (class, perks, implants, wounds, inventory) share one mission and take turns, either on one PC (hotseat) or online over Steam with a friend. Online play is new and has only been tested on one machine so far (recorded and replayed sessions).

## How it works

The game assumes exactly one `Player` and reads `Creatures.Player` about 960 times. Instead of patching all of those, the mod keeps the *active* merc in that slot:

- When a party merc's action point comes up (`Player.ProcessActionPoint`), it is swapped into `Creatures.Player`. Input, HUD, camera and fog of war follow it with no changes.
- Mercs not in the slot are added back into creature lookups (`Creatures.Values`, `GetCreature`), so enemies still see and target them.
- Saves and mission results always see the primary merc (the one you deployed).
- The mission only ends in death when every party merc is dead.

See [docs/design.md](docs/design.md) for the full plan, including the networking milestones.

## Build

Requires the .NET SDK (8+) and a Steam install of Quasimorph. Game DLLs are referenced in place and never copied into the repo.

```sh
dotnet build src/QuasimorphCoop -c Release -p:Deploy=true
```

`Deploy=true` copies the mod to the game's local mod folder:
`<persistentDataPath>/LocalUserPresets/QuasimorphCoop/`. Defaults assume Steam on Linux with Proton. Override `QuasimorphDir` and/or `QuasimorphUserData` for other setups.

## Local hotseat with console commands

Back up your save folder first. Steam Cloud will sync whatever happens to it.

1. Start a mission.
2. Open the dev console with the backquote key (`` ` ``). Using the console flags the run's achievements.
3. `coop_add` lists living roster mercs that aren't on the mission.
4. Point the mouse at an empty, explored floor cell, then run `coop_add <index>`. You can also use `coop_add temp` for a throwaway merc with your merc's profile and no gear.
5. Play. Turns alternate between the mercs as their action points come up. `coop_status` shows the party, who holds the slot, and whose turn it is.

Everything the mod does is logged to `Player.log` with the prefix `[QMCoop]`:

```sh
grep QMCoop ~/.local/share/Steam/steamapps/compatdata/2059170/pfx/drive_c/users/steamuser/AppData/LocalLow/Magnum\ Scriptum\ Ltd/Quasimorph/Player.log
```

## Playing online (milestone 2)

Both players need the **same game version and the exact same mod build**. The host checks a hash of the DLL and refuses a mismatch. Use the same set of other mods too; a different mod list only gives a warning, but it can cause desyncs.

**Install for the friend (Windows):** unzip `QuasimorphCoop.zip` into
`%USERPROFILE%\AppData\LocalLow\Magnum Scriptum Ltd\Quasimorph\LocalUserPresets\`
so that you get `...\LocalUserPresets\QuasimorphCoop\QuasimorphCoop.dll`. Start the game once and check that the mod is listed.

**Starting a session (no console needed):**
1. Host: load your game. Press Esc, open **Co-op**, and click **Partner** until it shows the merc your friend will play. The choice is remembered for that save. From now on the partner joins you automatically at every mission start and floor change; this also works for local hotseat without going online.
2. Host: in the same menu, click **Host online**. A friends-only lobby is created and the Steam invite dialog opens (**Invite friend** reopens it).
3. Friend: on the main menu, either accept the Steam invite, or open **Co-op** and click **Join <host name>**.
4. Whenever the host starts a level with the partner on it, the host's game saves and sends the level (about 1.3 MB), and both games load it. The friend's copy goes into hidden slot 9, so their own saves aren't touched. This repeats automatically after every floor change and at every mission start; you only connect once.
5. The host controls their own merc and the friend controls the partner. During the other player's turn your input is blocked and you watch their merc.

**Leave co-op** in the same menu ends the session; the mission continues as local hotseat. Going back to the main menu also ends it. The console commands (`coop_add`, `coop_host`, `coop_join <lobbyid>`, `coop_leave`, `coop_net`) still exist for troubleshooting.

**What's synced:** moving, shooting, reloading (key, HUD or dragging ammo onto a gun), swapping weapons and fire modes, movement speed, skip turn, facing, doors, ultimate perks, and everything in the inventory, loot and health screens: picking up, dropping, equipping, looting corpses and containers, eating and drinking, medkits and splints, amputation, repairs, unloading, splitting and sorting.

How the inventory part works: while your merc acts, your game watches every item storage it can reach (its own gear, the floor, nearby corpses and containers). When one changes, the new contents go to the other game in the game's own save format. Actions with effects beyond items (eating, medkits, repairs, reloads) are replayed as the same calls on the other game.

During the other player's turn you watch their merc. You can open the inventory and health screens to look at their gear, but not change anything.

After every action point both games compare a fingerprint of the state (positions, facing, HP, every carried item, the gameplay random number generator). **If they ever differ, the host's game sends its level again and both reload it** (a few seconds), and play carries on. If that keeps happening, you get a message to send both `Player.log` files to the mod author.

**Between levels:** when a level ends, the friend's game waits; the next level loads by itself when the host starts it. The ship isn't shared, so only the host manages the ship, crew and cargo. The friend plays in a copy of the host's campaign (hidden slot 9), so nothing carries back to the friend's own saves. That's by design: it's the host's campaign.

## How the party behaves

- **Shared vision.** The map shows everything any party merc can see, and an enemy seen by either merc counts as spotted.
- **Deaths.** If one merc dies the other carries on, and you can loot the fallen merc's corpse. If your deployed merc dies, the survivor becomes the deployed merc, so extracting with them is a normal successful extraction. Back on the ship, a merc who died gets the game's usual treatment for your difficulty (gear-loss penalty, then cloning or permadeath). If both die, the game's normal death screen follows.
- **Saves.** The dungeon save has room for only one player, so the other party merc goes into a small file next to each dungeon save (`[autosave_]slot_N_coop_<location>.dat`). The merc respawns when you reload and follows you to the next floor. Only roster mercs come back; `coop_add temp` mercs are dropped.
- **Gear sticks immediately.** The partner's gear and wounds live on their roster entry, so changes stick even if they don't extract, as long as they're alive.

## Known limitations

- **Untested between two real PCs.** Everything online has been tested with a recorded-and-replayed session on one machine, not yet with a friend.
- **The ship isn't shared** (see above).
- **Autodoc and scanner popups** (the options window on heal stations and scanners) aren't synced. Using one during an online session causes a resync.
- **AI details.** A few AI checks that read `Creatures.Player` directly (curse-based enemy regen, invisibility) use whichever merc is active.
