#!/bin/bash
# Test harness launcher (see src/QuasimorphCoop/Lockstep/LockstepTest.cs).
# Usage: tools/run_lockstep.sh <run-name> key=value...
# Writes lockstep_test.cfg into the deployed mod folder, starts the game through Steam, waits for
# the harness to quit it, and prints the last line of lockstep_<run>.log.
# Refuses to do anything while the game is already running (someone may be playing).
M="${QUASIMORPH_MOD_DIR:-$HOME/.local/share/Steam/steamapps/compatdata/2059170/pfx/drive_c/users/steamuser/AppData/LocalLow/Magnum Scriptum Ltd/Quasimorph/LocalUserPresets/QuasimorphCoop}"
running() { pgrep -f '[Q]uasimorph.exe' >/dev/null; }
if running; then echo "game already running, aborting"; exit 1; fi
run=$1; shift
{ echo "run=$run"; for kv in "$@"; do echo "$kv"; done; } > "$M/lockstep_test.cfg"
rm -f "$M/lockstep_$run.log"
for attempt in 1 2 3; do
  steam steam://rungameid/2059170 >/dev/null 2>&1 &
  for i in $(seq 1 30); do running && break; sleep 2; done
  running && break
  echo "launch attempt $attempt didn't start the game"; sleep 5
done
running || { echo "game never started"; rm -f "$M/lockstep_test.cfg"; exit 3; }
for i in $(seq 1 540); do running || break; sleep 2; done
if running; then echo "STILL RUNNING after 18 min"; exit 2; fi
tail -1 "$M/lockstep_$run.log"
