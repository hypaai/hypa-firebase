#!/usr/bin/env bash
# Runs the iOS on-device plugin (Assets/Plugins/iOS/HypaFoundationModels.swift) as C# calls it on an iPhone:
# status, one answer, one stream, one cancel. Needs an Apple silicon Mac on macOS 26 with Apple Intelligence on, and Xcode 26.
#   tools/apple-model/check.sh             on this Mac
#   tools/apple-model/check.sh simulator   inside a booted iPhone simulator (boots one if none is running)
# The first run after a boot can time out while the model loads; run it again.
set -euo pipefail
cd "$(dirname "$0")/../.."
out=$(mktemp -d)
src=(Assets/Plugins/iOS/HypaFoundationModels.swift tools/apple-model/check.swift)
if [ "${1:-}" = simulator ]; then
  xcrun --sdk iphonesimulator swiftc -swift-version 5 -target arm64-apple-ios26.0-simulator -o "$out/check" "${src[@]}"
  dev=$(xcrun simctl list devices booted | grep -m1 -oE '[0-9A-F-]{36}' || true)
  if [ -z "$dev" ]; then
    dev=$(xcrun simctl list devices available | grep -m1 'iPhone' | grep -oE '[0-9A-F-]{36}')
    xcrun simctl boot "$dev"
  fi
  xcrun simctl bootstatus "$dev" -b > /dev/null
  xcrun simctl spawn "$dev" "$out/check"
else
  xcrun swiftc -swift-version 5 -o "$out/check" "${src[@]}"
  "$out/check"
fi
