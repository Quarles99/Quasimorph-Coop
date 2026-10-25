#!/bin/bash
# Test harness launcher (see src/QuasimorphCoop/Lockstep/LockstepTest.cs).
# Usage: tools/run_lockstep.sh <run-name> key=value...
# Writes lockstep_test.cfg into the deployed mod folder, starts the game, waits for the harness to
# quit it, and prints the last line of lockstep_<run>.log.
# Refuses to do anything while the game is already running (someone may be playing).
#
# QM_HEADLESS=1 starts the game straight through Proton (Steam must be running, for the Steam API)
# inside a headless gamescope, with its sound sent to a temporary null sink: no window, no focus
# stealing, no audio. QM_PROTON overrides the Proton build (default: Proton - Experimental).
# Otherwise it launches through Steam as usual.
STEAM_ROOT="$HOME/.local/share/Steam"
M="${QUASIMORPH_MOD_DIR:-$STEAM_ROOT/steamapps/compatdata/2059170/pfx/drive_c/users/steamuser/AppData/LocalLow/Magnum Scriptum Ltd/Quasimorph/LocalUserPresets/QuasimorphCoop}"
running() { pgrep -f '[Q]uasimorph.exe' >/dev/null; }
if running; then echo "game already running, aborting"; exit 1; fi
run=$1; shift
{ echo "run=$run"; for kv in "$@"; do echo "$kv"; done; } > "$M/lockstep_test.cfg"
rm -f "$M/lockstep_$run.log"

launch() {
  if [ "$QM_HEADLESS" != 1 ]; then
    steam steam://rungameid/2059170 >/dev/null 2>&1 &
    return
  fi
  local game="$STEAM_ROOT/steamapps/common/Quasimorph"
  local proton="${QM_PROTON:-$STEAM_ROOT/steamapps/common/Proton - Experimental}/proton"
  (
    cd "$game" || exit
    STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT" \
    STEAM_COMPAT_DATA_PATH="$STEAM_ROOT/steamapps/compatdata/2059170" \
    SteamAppId=2059170 SteamGameId=2059170 PULSE_SINK=qmcoop_mute \
      gamescope --backend headless -W 1280 -H 720 -- "$proton" waitforexitandrun "$game/Quasimorph.exe"
  ) >/dev/null 2>&1 &
}

if [ "$QM_HEADLESS" = 1 ]; then
  sink=$(pactl load-module module-null-sink sink_name=qmcoop_mute sink_properties=device.description=qmcoop_mute)
  trap '[ -n "$sink" ] && pactl unload-module "$sink"' EXIT
fi

for attempt in 1 2 3; do
  launch
  for i in $(seq 1 30); do running && break; sleep 2; done
  running && break
  echo "launch attempt $attempt didn't start the game"; sleep 5
done
running || { echo "game never started"; rm -f "$M/lockstep_test.cfg"; exit 3; }
for i in $(seq 1 540); do running || break; sleep 2; done
if running; then echo "STILL RUNNING after 18 min"; exit 2; fi
tail -1 "$M/lockstep_$run.log"
