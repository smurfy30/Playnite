#!/usr/bin/env bash
# Local dev build: whole solution, Debug x86, then copy the CEF browser host
# next to the Desktop/Fullscreen exes (upstream's packaging does this; a plain
# solution build does not, and Playnite crashes on startup without it).
set -euo pipefail
cd "$(dirname "$0")/source"

MSBUILD="/c/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe"
"$MSBUILD" Playnite.slnx -restore -p:Configuration=Debug -p:Platform=x86 -m -nologo -v:m

BP=Playnite.BrowserProcess/bin/x86/Debug/net462/win-x86
for app in Playnite.DesktopApp Playnite.FullscreenApp; do
  cp -f "$BP"/Playnite.BrowserProcess.{exe,exe.config,pdb} "$app/bin/net462/win-x86/"
done
echo "Run: source/Playnite.DesktopApp/bin/net462/win-x86/Playnite.DesktopApp.exe"
