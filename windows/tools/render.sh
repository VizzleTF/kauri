#!/bin/bash
# Render Windows screens to tools/out/<theme>/<target>.png (2x) using an isolated seeded data dir.
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXE="$ROOT/Kauri/bin/Debug/net10.0-windows/Kauri.exe"
export DOTNET_ROOT="$USERPROFILE/.dotnet"
export KAURI_DATA_DIR="$ROOT/tools/out/data"
mkdir -p "$ROOT/tools/out/dark" "$ROOT/tools/out/light"
targets_dark="${TARGETS_DARK:-menu menu-hl sub-history sub-snippet settings-general settings-menu settings-type settings-exclude settings-shortcuts settings-beta settings-about alert-login}"
targets_light="${TARGETS_LIGHT:-settings-general settings-menu settings-type settings-exclude settings-shortcuts settings-beta settings-about editor-folder}"
for t in $targets_dark; do "$EXE" --render $t --theme dark --active true --out "$ROOT/tools/out/dark/$t.png"; done
for t in $targets_light; do
  act=false; [[ $t == editor-* ]] && act=true
  "$EXE" --render $t --theme light --active $act --out "$ROOT/tools/out/light/$t.png"
done
