#!/usr/bin/bash

IPOD_NAME="${1:-MIKE_S IPOD}"

echo "Pulling iTunesDB from $IPOD_NAME via iOpenPod"
flatpak run io.github.therealsavi.iOpenPod --json-from-itdb "/run/media/$USER/$IPOD_NAME/iPod_Control/iTunes/iTunesDB" ./iTunesDB.json || exit 1
echo "Scrobbling to Rufin..."
dotnet run ./iTunesDB.json
