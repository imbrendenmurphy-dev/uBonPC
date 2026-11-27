@echo off
rem Builds uBonPC.exe (the emulator window), ubemu.exe (headless test runner) and uBonPC-SDL.exe (the cross-platform
rem version for Linux and macOS) with the C# compiler that ships with Windows (.NET Framework 4). No installs needed.
rem   build.bat            all three
rem   build.bat headless   ubemu.exe only
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set CORE=src\Z80.cs src\Z80Dis.cs src\Font6847.cs src\Portable.cs src\Chips.cs src\Disk.cs src\Fdc.cs src\Mouse.cs src\DiskSound.cs src\Video.cs src\Bee.cs src\Trace.cs src\Vis.cs src\Bezel.cs src\Headless.cs
set REFS=-r:System.Drawing.dll -r:System.Windows.Forms.dll

"%CSC%" -nologo -optimize+ -target:exe -out:ubemu.exe -main:Headless %CORE%
if errorlevel 1 exit /b 1
if "%1"=="headless" goto done
"%CSC%" -nologo -optimize+ -target:winexe -out:uBonPC.exe -main:BeeApp -win32icon:src\bee.ico %REFS% %CORE% src\WinIO.cs src\MainForm.cs
if errorlevel 1 exit /b 1
rem The cross-platform version (Linux, macOS; Windows too with SDL2.dll): the same core on SDL2, no Windows Forms.
"%CSC%" -nologo -optimize+ -target:exe -platform:anycpu -out:uBonPC-SDL.exe -main:BeeSdl -win32icon:src\bee.ico %CORE% src\Sdl.cs src\SdlApp.cs
if errorlevel 1 exit /b 1
:done
echo Built uBonPC.exe, ubemu.exe and uBonPC-SDL.exe
