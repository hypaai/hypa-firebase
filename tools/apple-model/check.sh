#!/usr/bin/env bash
# Runs the iOS on-device plugin (Assets/Plugins/iOS/HypaFoundationModels.swift) on this Mac, as C# calls it on an iPhone:
# status, one answer, one stream, one cancel. Needs an Apple silicon Mac on macOS 26 with Apple Intelligence on, and Xcode 26.
set -euo pipefail
cd "$(dirname "$0")/../.."
out=$(mktemp -d)
xcrun swiftc -swift-version 5 -o "$out/check" Assets/Plugins/iOS/HypaFoundationModels.swift tools/apple-model/check.swift
"$out/check"
