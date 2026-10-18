#!/usr/bin/bash

IPOD_NAME={$1:-"MIKE_S IPOD"}

flatpak run io.github.therealsavi.iOpenPod --json-from-itdb "/run/media/$USER/$IPOD_NAME/iPod_Control/iTunes/iTunesDB" ./iTunesDB.json
dotnet run ./iTunesDB.json