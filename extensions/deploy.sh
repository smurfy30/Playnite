#!/usr/bin/env bash
# Build our own Playnite plugins and copy them into the user data Extensions dir.
# Usage: extensions/deploy.sh [PluginName...]   (default: every dir with an extension.yaml)
# Playnite must be closed (it locks loaded DLLs); this script closes it if running.
set -euo pipefail
cd "$(dirname "$0")"
DATA="${PLAYNITE_DATA:-E:/projects/playnite-data}"
plugins=("$@")
[ ${#plugins[@]} -eq 0 ] && plugins=($(ls -d */ | tr -d / | while read d; do [ -f "$d/extension.yaml" ] && echo "$d"; done))

taskkill //IM Playnite.DesktopApp.exe //F >/dev/null 2>&1 && sleep 2 || true
for p in "${plugins[@]}"; do
  id=$(grep -E '^Id:' "$p/extension.yaml" | awk '{print $2}' | tr -d '\r\357\273\277')
  dotnet build "$p" -c Release -nologo -v q
  dest="$DATA/Extensions/$id"
  rm -rf "$dest" && mkdir -p "$dest"
  # Playnite.SDK.dll is provided by Playnite itself; shipping it can cause version clashes.
  (cd "$p/bin/Release/net462" && tar cf - --exclude=Playnite.SDK.dll .) | (cd "$dest" && tar xf -)
  echo "deployed $p -> $dest"
done
