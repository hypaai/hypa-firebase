#!/usr/bin/env bash
# Starts the Firebase emulators so phones on the same Wi-Fi can play the web build against them (G08: one device to another).
# Open http://<this computer's address>:5002 on the phone; the address is printed below. Everyone on that network can then reach
# the emulators and the Hypa proxy (and its key in functions/.secret.local), so use it on a network you trust.
# Without this script the emulators answer this computer only (firebase.json).
set -euo pipefail
cd "$(dirname "$0")/.."
firebase=${FIREBASE:-firebase}
lan=.firebase-lan.json # git-ignored; next to firebase.json so its relative paths still work
node -e '
const c = require("./firebase.json");
for (const name of ["auth", "firestore", "database", "hosting"]) c.emulators[name].host = "0.0.0.0";
require("fs").writeFileSync(process.argv[1], JSON.stringify(c, null, 2));
' "$lan"
ip=$(ipconfig getifaddr en0 2>/dev/null || hostname -I 2>/dev/null | cut -d" " -f1 || true)
echo "On the phone (same Wi-Fi): http://${ip:-THIS-COMPUTER-ADDRESS}:5002"
exec "$firebase" emulators:start --project demo-hypa --config "$lan"
