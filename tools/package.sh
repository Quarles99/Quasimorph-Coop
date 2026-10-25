#!/bin/bash
# Builds the mod and assembles the Steam Workshop upload folder (dist/QuasimorphCoop) and the
# zip for manual installs. Stops at the first failure.
# Usage: tools/package.sh
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build src/QuasimorphCoop -c Release

out=dist/QuasimorphCoop
rm -rf "$out" dist/QuasimorphCoop.zip
mkdir -p "$out"
# Exactly what Steam uploads: manifest, assembly, preview image.
cp src/QuasimorphCoop/modmanifest.json src/QuasimorphCoop/bin/Release/QuasimorphCoop.dll workshop/thumbnail.png "$out/"
(cd dist && zip -qr QuasimorphCoop.zip QuasimorphCoop)

echo
ls -l "$out"
echo "sha256 $(sha256sum "$out/QuasimorphCoop.dll" | cut -d' ' -f1)"
# The game runs under Proton, where Z: is the Linux root.
win="Z:$(realpath "$out" | sed 's|/|\\|g')"
echo
# Workshop item: https://steamcommunity.com/sharedfiles/filedetails/?id=3826739727
echo "Update the Workshop item (in-game console, backquote key):"
echo "  mod_updateworkshopitem 3826739727 $win true"
