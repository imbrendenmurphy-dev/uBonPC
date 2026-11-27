#!/bin/sh
# ubemu (the headless, scripted uBonPC) on Linux and macOS, with modern .NET (8 or later) or Mono.
# Example: ./ubemu.sh -e "disk GAME.DS8; typeat A> game\r; seconds 30; screen s.png both"
DIR=$(cd "$(dirname "$0")" && pwd)
if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft.NETCore.App'; then
  exec dotnet "$DIR/ubemu.exe" "$@"
elif command -v mono >/dev/null 2>&1; then
  exec mono "$DIR/ubemu.exe" "$@"
else
  echo "ubemu needs the .NET runtime (8 or later) or Mono." >&2
  exit 1
fi
