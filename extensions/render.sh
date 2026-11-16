#!/usr/bin/env bash
# Deploy plugins, start Playnite, render Ledger tabs to PNG. For checking UI changes without
# clicking. Prints the PNG paths.
#   extensions/render.sh [--no-deploy] [Tab ...]     Tabs: Overview Backlog Installs Stats Timeline Mods
set -euo pipefail
cd "$(dirname "$0")/.."
DATA="${PLAYNITE_DATA:-E:/projects/playnite-data}"
DIR=$(ls -d "$DATA"/ExtensionsData/0ca0d44d-*)
if [ "${1:-}" = "--no-deploy" ]; then shift; else bash extensions/deploy.sh >/dev/null; fi
tabs=("$@"); [ ${#tabs[@]} -eq 0 ] && tabs=(Overview)
if ! tasklist 2>/dev/null | grep -i Playnite.DesktopApp >/dev/null; then
  # detach fully: a started GUI app inheriting our stdout pipe makes the shell wait on it forever
  (cd source/Playnite.DesktopApp/bin/net462/win-x86 && cmd //c start "" Playnite.DesktopApp.exe >/dev/null 2>&1 </dev/null)
  sleep 30
fi
for t in "${tabs[@]}"; do
  out="$DIR/render-$(echo "$t" | tr 'A-Z' 'a-z').png"
  before=$(stat -c %Y "$out" 2>/dev/null || echo 0)
  cmd //c start "" "playnite://ledger/render/$t" >/dev/null 2>&1 </dev/null
  for i in $(seq 1 30); do
    sleep 1
    [ "$(stat -c %Y "$out" 2>/dev/null || echo 0)" != "$before" ] && break
  done
  echo "$out"
done
