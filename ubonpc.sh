#!/bin/sh
# uBonPC on Linux and macOS: runs uBonPC-SDL.exe with modern .NET (8 or later) or with Mono.
# Needs SDL2, and zenity or kdialog for file dialogs (optional: you can drop files on the window instead).
#   Fedora:        sudo dnf install dotnet-runtime-8.0 SDL2 zenity
#   Debian/Ubuntu: sudo apt install dotnet-runtime-8.0 libsdl2-2.0-0 zenity
#   Arch:          sudo pacman -S dotnet-runtime sdl2 zenity
#   macOS:         brew install dotnet sdl2
# Any arguments are passed on (a disk image, a .COM, --model 256tc, --fullscreen, --vis ...; see README.md).
DIR=$(cd "$(dirname "$0")" && pwd)
if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft.NETCore.App'; then
  exec dotnet "$DIR/uBonPC-SDL.exe" "$@"
elif command -v mono >/dev/null 2>&1; then
  exec mono "$DIR/uBonPC-SDL.exe" "$@"
else
  echo "uBonPC needs the .NET runtime (Fedora: sudo dnf install dotnet-runtime-8.0;" >&2
  echo "Ubuntu: sudo apt install dotnet-runtime-8.0; macOS: brew install dotnet) or Mono." >&2
  exit 1
fi
