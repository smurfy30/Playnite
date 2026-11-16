#!/usr/bin/env bash
# Deploy plugins, start Playnite, render the Ledger page to PNG. For checking UI changes without
# clicking. Prints the PNG path. Usage: extensions/render.sh [--no-deploy]
set -euo pipefail
cd "$(dirname "$0")/.."
DATA="${PLAYNITE_DATA:-E:/projects/playnite-data}"
OUT=$(ls -d "$DATA"/ExtensionsData/0ca0d44d-*)/render.png
[ "${1:-}" = "--no-deploy" ] || bash extensions/deploy.sh >/dev/null
if ! tasklist | grep -qi Playnite.DesktopApp; then
  (cd source/Playnite.DesktopApp/bin/net462/win-x86 && cmd //c start "" Playnite.DesktopApp.exe)
  sleep 30
fi
before=$(stat -c %Y "$OUT" 2>/dev/null || echo 0)
cmd //c start "" "playnite://ledger/render"
for i in $(seq 1 30); do
  sleep 1
  [ "$(stat -c %Y "$OUT" 2>/dev/null || echo 0)" != "$before" ] && break
done
echo "$OUT"
