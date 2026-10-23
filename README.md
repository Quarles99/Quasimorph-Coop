# Quasimorph Co-op

An experimental mod that adds a second player-controlled merc to Quasimorph missions, with the goal of eventual online co-op.

**Status: milestone 1 (local hotseat).** Two full mercs (class, perks, implants, wounds, inventory) share one mission on one PC and take turns. There's no networking yet.

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

## Testing milestone 1

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

## Known limitations (milestone 1)

- **Party saves use a side file.** The dungeon save has room for only one player, so party mercs are stored in a small file next to each dungeon save (`[autosave_]slot_N_coop_<location>.dat`). They respawn when you reload, and follow you to the next floor next to your merc. Only roster mercs come back; `coop_add temp` mercs are dropped. Saves made before this feature have no side file, so re-add the merc once.
- **Second merc's state persists immediately.** Their `Mercenary` data is shared with the roster, so gear and wounds stick whether or not they extract. If they die, their roster entry is left dead and untested.
- **Mission result follows the primary merc.** If the primary dies and the second merc extracts, the mission resolves as the primary's.
- **Fog of war.** It shows only the active merc's vision.
- **AI details.** A few AI checks that read `Creatures.Player` directly (curse-based enemy regen, invisibility) use whichever merc is active.
