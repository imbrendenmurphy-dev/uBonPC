#!/bin/sh
# Builds ubemu.exe (headless) and uBonPC-SDL.exe (the window) on Linux or macOS with Mono's C# compiler
# (Fedora: sudo dnf install mono-devel; Debian/Ubuntu: sudo apt install mono-devel; macOS: brew install mono).
# The Windows version, uBonPC.exe, is built on Windows with build.bat.
set -e
cd "$(dirname "$0")"
CSC=$(command -v csc || command -v mcs)
CORE="src/Z80.cs src/Z80Dis.cs src/Font6847.cs src/Portable.cs src/Chips.cs src/Disk.cs src/Fdc.cs src/Mouse.cs src/DiskSound.cs src/Video.cs src/Bee.cs src/Trace.cs src/Vis.cs src/Bezel.cs src/Headless.cs"
$CSC -nologo -optimize+ -target:exe -out:ubemu.exe -main:Headless $CORE
$CSC -nologo -optimize+ -target:exe -platform:anycpu -out:uBonPC-SDL.exe -main:BeeSdl $CORE src/Sdl.cs src/SdlApp.cs
echo "Built ubemu.exe and uBonPC-SDL.exe"
