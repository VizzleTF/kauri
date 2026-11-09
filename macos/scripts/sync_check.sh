#!/bin/sh
# Builds the Kauri Sync sources with scripts/sync_check.swift and runs it
# against the test vectors. Needs only the Swift toolchain of Xcode.
set -eu
cd "$(dirname "$0")/.."
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
cp scripts/sync_check.swift "$tmp/main.swift"
xcrun swiftc -O -o "$tmp/sync_check" "$tmp/main.swift" \
  Clipy/Sources/Sync/KauriCrypto.swift Clipy/Sources/Sync/KauriCurve.swift \
  Clipy/Sources/Sync/KauriPairing.swift Clipy/Sources/Sync/BIP39Words.swift \
  Clipy/Sources/Sync/KauriSyncModel.swift
"$tmp/sync_check" ClipyTests/Sync/kauri-sync-v1.json
