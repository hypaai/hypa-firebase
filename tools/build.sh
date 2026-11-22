#!/usr/bin/env bash
# Builds Hypa Stories without opening the editor: tools/build.sh quest|android|ios|web. Output: Builds/<Target>.
# Close the project in the Unity editor first (one editor per project). Set UNITY=/path/to/Unity to pick the editor.
set -euo pipefail
cd "$(dirname "$0")/.."
target=${1:-}
case $target in
  quest|android) bt=Android ;;
  ios) bt=iOS ;;
  web) bt=WebGL ;;
  *) echo "usage: tools/build.sh quest|android|ios|web" >&2; exit 2 ;;
esac
ver=$(sed -n 's/^m_EditorVersion: //p' ProjectSettings/ProjectVersion.txt)
unity=${UNITY:-}
for c in \
  "/Applications/Unity/Hub/Editor/$ver/Unity.app/Contents/MacOS/Unity" \
  "$HOME/Applications/Unity/Hub/Editor/$ver/Unity.app/Contents/MacOS/Unity" \
  "C:/Program Files/Unity/Hub/Editor/$ver/Editor/Unity.exe" \
  "$HOME/Unity/Hub/Editor/$ver/Editor/Unity"; do
  if [ -z "$unity" ] && [ -x "$c" ]; then unity=$c; fi
done
[ -n "$unity" ] || { echo "Unity $ver not found; set UNITY=/path/to/Unity" >&2; exit 1; }
mkdir -p Logs
log="Logs/build-$target.log"
: > "$log" # A previous successful build must not mask a failure on this run.
# Unity can abort while shutting down after a good build, so the result comes from the build's own log line.
"$unity" -batchmode -projectPath . -buildTarget "$bt" \
  -executeMethod HypaCore.EditorTools.HypaBuild.FromCommandLine -hypaTarget "$target" -logFile "$log" || true
grep -E "^Hypa: .* build |error CS" "$log" | sort -u
grep -q "^Hypa: .* build Succeeded" "$log"
