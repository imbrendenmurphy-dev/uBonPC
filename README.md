# uBonPC

An emulator for the **Microbee** range for Windows, Linux and macOS, centred on the **Premium Plus** (1 MB, 32K colour PCG), for which new games
are being written. It is the Microbee sibling of VZonPC and
MZ700onPC: the same visualiser, the same kind of headless runner, the same build.

- **Machine:** Z80 at 3.375 MHz, 1 MB in 32 blocks of 32K banked by port 50h, the boot ROM (the Premium Plus's own `pp.bin`, or `bn60.rom`) at 8000h.
- **Video, raster exact:** the 6545 CRTC is run character by character in step with the Z80. Every cell is drawn
  as the beam passes it, so mid-line and mid-frame changes to registers, screen, attribute, colour or PCG RAM show
  where they happen. The picture is framed by the monitor's VSYNC, so stacks of one-line CRTC frames and
  variable-height frames build one picture, as on the real machine. The Microbee Megademo runs.
- **Colour and monochrome at once:** the Premium RGBI colour output and the monochrome output (with the Premium's
  half intensity) are both rendered for every pixel. Watch either, or **both side by side** (F5). Green, amber or
  white mono monitors.
- **Video RAM wait states:** the Premium holds the CPU while a line's picture is being drawn. That is on by default.
- **Keyboard:** the real light-pen scan (the refresh address tests one key every 16 characters) and the transparent
  update test (R18/R19 + R31) that games use. The 256TC's keyboard controller is emulated for that model.
- **Floppy:** WD2793 with real byte timing (32 µs a byte, lost data), stepping, Read/Write Sector, Read Address,
  Read/Write Track (format) and Force Interrupt. The disk has no rotational delay (as an SD card or floppy emulator),
  or optionally it is a real 300 rpm drive. Four drives, A to D, as the Premium Plus controller latch selects. Raw DS80/DS8, DS40, SS80, DS82, DS84 and DS8B images, and CPC DSK/EDSK.
- **Run a .COM directly:** uBonPC builds a bootable CP/M disk with the program and its data files, boots it and types
  its name at `A>`.
- **Serial mouse:** a Microsoft mouse on the serial lines, as raw 1200-baud serial on CTS (the interface Empires was
  tested with on a real Premium Plus) or RX, or ubee512's clocked interface. Click the picture to capture it.
- **Also:** the MC146818 real-time clock, the Z80 PIO with interrupts (IM 2 daisy chain), the speaker on PIO B6 through
  the sound card, the joystick on PIO A (PC gamepads work).
- **Testing:** `ubemu.exe`, a headless runner for scripted tests, screenshots in colour, mono or both, WAV capture,
  CRTC write logs, profiling and crash checks.

It builds with the C# compiler that comes with Windows (`build.bat`). There is nothing to install. On Linux and macOS it runs as `uBonPC-SDL.exe` (see [Linux and macOS](#linux-and-macos)).

## Files

| | |
|---|---|
| `uBonPC.exe` | the emulator window |
| `ubemu.exe` | headless test runner (same emulation core) |
| `uBonPC-SDL.exe`, `ubonpc.sh`, `ubemu.sh` | the Linux and macOS version (SDL2), and launch scripts for it and `ubemu` |
| `build.bat` | rebuilds all three from `src\` (`build.bat headless` builds only `ubemu.exe`); `build.sh` builds `ubemu.exe` and `uBonPC-SDL.exe` on Linux or macOS with Mono |
| `roms\` | the ROMs: not included. Put MAME's Microbee ROM set here (see `roms\roms.txt`) |
| `system\` | `boot_tracks.bin` (the Premium Plus CP/M system tracks) and `CCP.SYS`, used to make boot disks for .COM files |
| `ubonpc.ini` | settings, written on exit |

## Using it

- **F8**, or dropping a disk image on the window, boots it in drive A: the machine is power-cycled and the boot ROM
  loads CP/M from the disk. **Shift+F8** puts a disk in drive A without booting.
- **Drives A to D:** the **Disk** menu has a submenu for each of the four drives: insert, eject, write-protect and
  directory. Dropping several images at once puts them in A, B, C and D and boots A. **Ctrl** while dropping one image
  puts it in the first empty drive of B to D without rebooting. An empty drive gives CP/M's "Err On B: Select".
- **F7**, or dropping a `.COM`, runs a CP/M program. uBonPC makes a DS80 boot disk (in `%TEMP%`) holding the system
  tracks, `CCP.SYS`, the program, every file beside it with the same name (`GAME.HI`, `GAME.OVL` …), and the
  `.DAT`, `.BIN`, `.BNK`, `.OVL`, `.SYS`, `.TXT`, `.PCG`, `.MAP`, `.LVL`, `.SCR` and `.MUS` files beside it. Build output
  and sources (`.LST`, `.SYM`, `.ASM`, images, disk images …) are left out. It boots it and types the program's name at
  `A>`. **Shift+F7** does it again (after a rebuild, say). A file the game needs under some other extension has to be
  on a real disk image instead.
- Disk changes are written back to the image file on eject and exit (**Disk → Write disk changes back**).
- Symbols are found automatically when there is a `.sym` next to the program or disk, or in `..\build`. Named routines
  then show in the visualiser. Both the sjasmplus format (`NAME: EQU 0x1234`) and zasm's (`1234 NAME`) work. You can
  also drop a `.sym` on the window.

| Key | |
|---|---|
| F5 | colour → monochrome → both side by side |
| F6 / Shift+F6 / Ctrl+F6 | pause / reset / power cycle |
| F7 / Shift+F7 | run a .COM / run the last disk or program again |
| F8 / Shift+F8 / Ctrl+F8 | boot a disk / insert a disk in drive A / paste the clipboard as typing |
| F9 / Shift+F9 | warp speed / mute |
| F10 | visualiser (sidebars in fullscreen, its own window otherwise) |
| F11 or Alt+Enter, or double-click | fullscreen |
| F12 / Shift+F12 | screenshot (to `screenshots\`) / keys help |

**Microbee keyboard:** letters, digits, SPACE, RETURN, SHIFT, CTRL, the arrows and the punctuation keys work as typed.

| PC key | Microbee key |
|---|---|
| Backspace / Delete | BS / DEL |
| Esc, Tab, Caps Lock | ESC, TAB, LOCK |
| Home / End | LINE FEED / BREAK |
| `` ` `` / `'` / `=` | `@` / `:` / `^` |

Under **Input** the arrow keys can be the joystick instead, with Right Ctrl as fire.

**Mouse:** choose a **Serial mouse** under **Input**, then click the picture to capture the PC mouse. Its moves and
buttons are sent as Microsoft mouse packets. The middle button, or switching to another window, lets it go. For
Empires choose **On PIO B bit 3, CTS**. The game finds the line and its polarity itself (wiggle the mouse a little
first), and shows a mouse icon in its status bar once it hears it.

**View menu:** the colour palette can be an RGBI monitor or CGA converter's (dark yellow shown as brown, the default)
or plain RGBI. The **Frame** can be:
- **Monitor:** the 720 × 320 dot area a monitor shows, with the picture placed by the sync timing, as a monitor does.
  Vertically it starts 9 lines after VSYNC begins. Horizontally it opens 56 dots (4.1 µs) after HSYNC ends, so the
  standard 64 × 16 picture (R2 = 51h) is centred, and a program that moves R2, changes the sync width or the line
  length moves its picture just as it would on the real monitor.
- **Picture only:** cropped to where the display was on.
- **Whole raster:** everything the beam swept. Blanking is grey, HSYNC blue and VSYNC purple. Use it to see where a
  raster effect really lands.

**Monitor shape** doubles each scanline, so the picture has the proportions a monitor gives it.

**Size.**
- **View → Scale** sizes the window at ×1, ×1.5, ×2, ×3 or ×4. Sizes too big for the screen are greyed out. On a
  1920 × 1080 screen with monitor shape on (a 720 × 640 picture), only ×1 and ×1.5 fit.
- **Full screen** (F11): the picture fills the screen. With the visualiser on, its two columns run the full height
  flush against the left and right edges, and the picture fills the space between them.
- **Monitor bezel** (View → Monitor bezel in full screen, on by default): full screen draws the front of the
  microbee MONITOR round the picture, after photos of the owner's:
  - a rounded, slightly bulging CRT face, with the unlit phosphor as a faint grey sheen, darker towards the edges,
    and a soft reflection at the top left;
  - a black recess round the glass, the charcoal front with its pale case rim, and "microbee MONITOR" under the glass.

  The glass covers the whole Monitor frame, overscan included, as on the real tube. It is drawn for one monitor at a
  time: not in the split colour + mono view, and not with the Whole raster frame. The picture is about 20% smaller
  with it; switch it off for the largest picture.
- **Whole-pixel scaling** keeps pixels sharp by using whole steps (×2, ×3 …), but only when a whole step fills at
  least 85% of the space. Otherwise the picture is drawn as large as fits.

## Linux and macOS

`uBonPC-SDL.exe` is the same emulator with a cross-platform front end on SDL2, modelled on VZonPC's. It runs on Linux
and macOS under modern .NET (8 or later) or Mono, and on Windows too, given `SDL2.dll`. The emulation, the visualiser,
the full-screen bezel, sound, the keyboard (including the 256TC's), joysticks, the captured serial mouse, the four
drives, .COM booting and the command line are the Windows version's.

Install the .NET runtime and SDL2 (zenity gives file dialogs; without it, drop files on the window):

```
Fedora:        sudo dnf install dotnet-runtime-8.0 SDL2 zenity
Debian/Ubuntu: sudo apt install dotnet-runtime-8.0 libsdl2-2.0-0 zenity
Arch:          sudo pacman -S dotnet-runtime sdl2 zenity
macOS:         brew install dotnet sdl2
```

Then `./ubonpc.sh` (any arguments are passed on: a disk image, a `.COM`, `--model 256tc`, `--fullscreen`, `--vis`...)
and `./ubemu.sh` for the headless runner. If the scripts aren't executable after unzipping, `chmod +x *.sh`.

There are no menus. The F-keys are the Windows version's, and the menu options are Alt keys (Shift+F12 lists them):
- **F5** colour / mono / both, **F6** pause (Shift reset, Ctrl power cycle), **F7** run a .COM (Shift: again),
  **F8** boot a disk (Shift: insert without booting, Ctrl: paste text), **F9** warp (Shift: mute),
  **F10** visualiser (Shift: its second page), **F11** or **Alt+Enter** full screen, **F12** screenshot.
- **Alt+1..5** window size ×1, ×1.5, ×2, ×3, ×4; **Alt+6..9** insert a disk in drive A–D (with Shift: eject).
- **Alt+M** next model, **Alt+B** bezel, **Alt+I** whole-pixel scaling, **Alt+S** monitor shape, **Alt+T** mono
  colour, **Alt+H** half intensity, **Alt+P** palette, **Alt+R** frame, **Alt+A** arrow keys as joystick,
  **Alt+U** serial mouse, **Alt+N** floppy sounds, **Alt+K** fast disk, **Alt+W** video RAM waits,
  **Alt+C** visualiser cells, **Alt+Q** quit.
- Drop a disk image to boot it (Ctrl: put it in the next empty drive of B–D), several to fill A–D, a `.COM` to run
  it, a `.sym` for the visualiser, a `.rom` to boot from it.

Settings are kept in the same `ubonpc.ini`. `build.sh` builds `ubemu.exe` and `uBonPC-SDL.exe` on Linux or macOS
with Mono's compiler; `build.bat` builds them on Windows.

**Tested:** a GitHub Actions workflow (`.github/workflows/linux.yml`) builds uBonPC on Ubuntu with Mono and runs it
there on every push: ubemu under .NET 8 and under Mono, and the SDL window under a virtual display. ROMs aren't in
the repository, so the always-run checks use a tiny Z80 program that needs no ROM; with a `UBONPC_PP_ROM` secret
(the Premium Plus ROM, base64) it also runs the Megademo. It has not yet been tried on a Linux desktop or on macOS by
a person.
## Models

**Machine → Model.** The Premium Plus is the default, and the one tested on real hardware.

| id | | ROM needed in `roms\` |
|---|---|---|
| `pp` | Premium Plus: 1 MB, 16 PCG banks, colour, VSYNC on PIO B7, floppy | its own `pp.bin` (MAME set `mbeepp`), else `bn60.rom` |
| `p512k`, `p256k`, `p128k`, `p64k` | Premium disk models (smaller RAM, 8–16 PCG banks) | `bn60.rom` (or their own) |
| `256tc` | 256TC Telecomputer: its own keyboard (port 18h), RTC | `256tc_boot_1.20` (or `_1.15`), `char256` |
| `1024k`, `128k` | standard (non-Premium) disk models, monochrome | `bn60.rom` |
| `56k` | 56K disk model, ROM at E000 | `56kb.rom` |
| `ppc85` | Premium PC85: Premium BASIC 5.29e, the PAKs (Wordbee, Command help, BusyCalc, Graphics, Videotex, Shell) and Telcom | `premium_pc85_a.rom` … `_i.rom`, ubee512's `ppc85_*.rom`, or MAME's `mbeeppc` set |
| `pc85` | PC85: BASIC 5.25, Wordbee, Help and the Shell menu, Telcom 3.21 | MAME's `mbeepc85` set (or `basic.rom`, `pak0.rom`…, `net.rom`) |
| `ic` | 32K IC: BASIC 5.22e, Wordbee, Help, Telcom 1.0 | MAME's `mbeeic` set |

**ROM files.** Microbee ROMs are not included with uBonPC: download MAME's Microbee ROM set and extract it into `roms\` (see `roms\roms.txt`). `roms\` can hold ROMs under ubee512's names or as MAME's romset, a folder per machine (`roms\mbee256\`,
`roms\mbeeppc\`, `roms\mbeepp\` …). uBonPC looks in `roms\`, then in every folder under it, and knows MAME's names for
the ROMs it uses: `256tc_boot_1.20.u38`, `char256.u53` (its first 4K is the character set), `pp.bin`, `56kb.rom`, the
PC85 and IC BASIC halves (`bas525a` + `bas525b`, `bas522e.ic5` + `.ic10`) and the PAKs. `rom NAME` (ubemu) finds a ROM
by name the same way.

**What runs.**
- **Premium Plus:** uses its own 16K `pp.bin` by default ("microbee Premium PLUS+ Version 1.00", ©2012 Microbee
  Technology). It is BN60 version 2.03 reworked: the same Boot / Monitor / Self Test menu, rebuilt with code moved
  about. All 31 test games and the Megademo behave the same on it as on `bn60.rom`. Without `pp.bin` it falls back to
  `bn60.rom`, or choose one with `rom bn60.rom` (ubemu) or by dropping a `.rom` on the window.
- **Premium PC85, PC85 and IC:** boot to their System menu or BASIC.
- **256TC:** boots to its Version 1.20 menu (F1 boot disk, F2 monitor, F3 self-test, F4 set time) and passes its
  self-test's function-key checks. Its keyboard is MAME's 15 × 8 scan-code layout, with F1–F12, a numeric keypad,
  cursor keys and Alt. In the window, F1–F4 and the keypad go to the 256TC; F5–F12 stay the emulator's. The
  "last used" date on its menu is junk until something writes it into the clock chip's RAM, as on a 256TC with a
  new battery. Premium Plus CP/M disks boot on it, but their BIOS reads only the Premium keyboard, so they take no
  typing; a 256TC CP/M disk is needed for that.
- **56K:** its ROM loads, but it doesn't work yet (MAME marks it not working too).

**Machine → Colour board fitted** adds the old (82S23 PROM) colour board to the standard models.

## Hardware details

These come from ubee512 6.0 (`memmap.c`, `vdu.c`, `crtc.c`, `keystd.c`) and MAME's `mbee` driver. The raster
behaviour comes from the Premium Plus model the Microbee Megademo was tuned with on a real machine
(the Megademo's own development emulator, `src/tools/emu/mbee.c` in its repository).

**Memory (Premium Plus, port 50h)**
- 0000–7FFF is block `((b7 b6 b5) >> 3) | (b1 b0)`, and bit 1 is inverted while bit 2 (ROMs off) is set.
- 8000–FFFF is always block 0.
- With bit 2 = 0 the boot ROM is read at 8000–BFFF; writes there go nowhere, and C000–FFFF is RAM.
- Bit 3 = 1 removes the video window. Bit 4 moves it from F000 to 8000. The video window takes precedence over
  ROM and RAM.

**Video window**
- F000–F7FF is screen RAM, or attribute RAM while port 1Ch bit 4 is set (written only with bit 7, extended graphics).
  With port 0Bh bit 0 set it reads the character ROM; the set is chosen by R12 bit 5.
- F800–FFFF is the PCG bank in port 1Ch bits 0–3, or colour RAM while port 08h bit 6 is set.
- **Display:**
  - A code ≥ 80h is a PCG character. Its bank is the attribute's low nibble with extended graphics on, bank 0 without.
  - Attribute bit 6 inverts the cell, and bit 7 flashes it (blank on every other 16 fields).
  - Colour RAM: the low nibble is the foreground, the high nibble the background. Bit 0 is red, 1 green, 2 blue,
    3 intensity.
  - The mono output: a pixel is lit or not. With half intensity, colour bit 3 picks a bright or a dim foreground
    and bit 7 gives a dim background.

**6545 CRTC**
- A character is 2 T (13.5 MHz dots), so the standard R0 = 6Bh line is 216 T and a 313-line frame is 67608 T
  (49.92 Hz).
- **Counters:** RA counts to R9, then the row counter to R4, then R5 adjust lines. The compares are for equality,
  so a register lowered past the counter is missed until the 7-bit wrap.
- **Display enable:**
  - The vertical display is on while row < R6. That is re-evaluated on every scanline, so writing R6 mid-frame
    blanks the rest at once.
  - While the row counter is 0, every scanline restarts from R12/R13.
- **Sync:** VSYNC starts on a row start equal to R7 and lasts R3 bits 7–4 lines (0 = 16). HSYNC starts at
  character R2 and lasts R3 bits 3–0 (0 = 16).
- **Status:** bit 7 is update ready, bit 6 light pen full, bit 5 vertical blanking (row ≥ R6).
- **Light pen:** reading R16/R17 clears it. A dummy access to R31 tests the key at R18/R19 at the next blanking, then
  increments R18/R19.
- **40 columns:** a short line (R0 + 1 ≤ 64, as in Hamburger Sam II's 54-character line) gives a 15.6 kHz line only
  at 6.75 MHz. So by default the dot clock halves for such a line: 4 T a character. **Machine → 6545 dot clock**
  forces 13.5 or 6.75 MHz. This rule is an inference; it hasn't been checked on hardware.

**Video RAM waits:** a CPU access to the video window while a line's picture is being drawn (row < R6 and character
< R1) waits until that line's horizontal blanking. Without the waits a program runs faster than on the real
machine. Turn them off under **Machine** to compare.

**Keyboard:** the 64 keys sit on MA4–MA9: key = row × 8 + column. The rows are `@ A–G`, `H–O`, `P–W`, `X Y Z [ \ ] ^
DEL`, `0–7`, `8 9 : ; , - . /`, `ESC BS TAB LF RET LOCK BREAK SPACE`, `UP CTRL DOWN LEFT - - RIGHT SHIFT`. While port
0Bh bit 0 is clear, the refresh address tests a key every 16 characters and a held key latches the light pen.

**PIO:** port B bit 6 is the speaker, bit 1 the cassette output and bit 3 CTS (held high without a mouse). Bit 7 is
VSYNC on the Premium Plus and the PC85 models, the RTC interrupt on the other RTC models, and high elsewhere.
ubee512 gives the Premium Plus the RTC, but Empires' `MOUSE.COM` sees B7 toggle with the frame on the real machine.

**Serial mouse:** a Microsoft 2-button mouse sends 3-byte packets (`01LRYYXX`, then X and Y, each 6 bits), 7N1 at
1200 baud, one bit every 2812 T.
- **CTS and RX:** the raw line is on PIO B bit 3 or bit 4. Mark (idle, stop, a 1 bit) reads 0 and space reads 1.
  **Invert** swaps them.
- **Clocked:** ubee512's interface pulses bit 2 (DTR) at the start of each bit and carries the inverted bit on bit 3.
  The DTR edge interrupts the Z80 through the PIO, and the pulse ends when the interrupt is taken. Port A is the joystick, active low: 1 up, 2 down,
4 left, 8 right, 80h fire. Mode 3 interrupts go to the Z80 in IM 2, with the vector, acknowledge and RETI.

**Floppy:**
- **Ports:** 44h–47h, also 40h–43h, are the WD2793. Writing 48h sets bits 0–1 drive (A to D), bit 2 side, bit 3 density.
  Reading 48h gives bit 7 = INTRQ or DRQ.
- **DS80 disks:** cylinders 0–1 (both sides) hold sectors 1–10, and the rest hold 21–30. They are 512 bytes, with
  4K CP/M blocks and the BIOS's 3:1 skew.
- **Speed:** with real 300 rpm timing the BIOS reads every third sector, three revolutions a track side (about
  7 KB/s). The default fast mode loads at about 15 KB/s, the speed reported on a real Premium Plus.
- **Drive sounds** (**Disk → Floppy drive sounds**, off by default; `--disksound`, or `disksound on` in ubemu). The
  sounds are generated as they play, with no sample files, and mixed with the speaker:
  - a click for each head step (a buzz when seeking);
  - a clunk as the head loads when the motor starts, and a softer one as it lifts;
  - the spindle motor's hum and whirr with its 300 rpm wobble. The motor spins up on a command and runs down about
    2.5 s after the drive goes quiet.

  With fast disk access the controller steps far faster than a drive can, so steps are queued and played at least
  3 ms apart (a real drive's fastest rate). The sounds don't change the emulation: runs are identical with them on or
  off.

## The visualiser (F10)

**Left column**
- **Z80 registers and flags.** Values that changed are amber. Beside them: the lowest SP this second and the top of
  the stack.
- **Code** at a sampled busy PC, with symbol names. **Hot spots:** CPU time per routine over the last second.
- **Frame budget.** Each field split into busy, interrupt, **video wait** (orange-red) and idle, with a 6-second
  history. Tight polling loops count as idle.
- **6545 CRTC.** All 20 registers with their names; the geometry (columns × rows, scanlines, line T, dot clock); the
  CRTC frame and the monitor picture in lines and Hz; the start, cursor and update addresses; the status bits and
  the beam position.
- **Opcodes:** a heat map of the first opcode byte, and the top four.
- **Floppy.** What is in each of drives A to D (the selected one amber), the selected drive and image, the command and the FDC state, status, DRQ/INTRQ, track/sector/head, sectors read
  and written, and lost data in red. A map of 82 cylinders × 2 sides flashes green on reads and red on writes.
- **Ports.** The ports used most recently, named, with the last value and the accesses per field. Underneath: ports
  50h, 1Ch, 08h, 0Bh and the RTC time.

**Right column**
- **Memory map.** All 64K as the CPU sees it, with reads, writes and execution glowing. It is labelled with the
  current banking: the block at 0000, ROM, the screen, and PCG n or colour at F800.
- **Banked RAM.** The whole 1 MB as 32 blocks of 32K, glowing as they are read and written, with the block at 0000
  marked.
- **Beam and CPU.** One picture as line T × lines, from VSYNC, showing what the CPU was doing at every moment: busy,
  interrupt, video wait, idle. On top are the video RAM writes (screen, attribute, colour, PCG), video reads, CRTC and
  FDC port accesses, key tests, speaker toggles and other OUTs. Dots mark the end of the displayed part of the line.
- **Screen cells.** One box per character cell, showing writes this second, or the PCG bank each cell uses, or the
  colours (**View → Visualiser screen cells show**). Underneath: writes per field by kind, and video waits per field.
- **PCG RAM.**
  - The 16 banks: the ones on screen are green, the one shown is blue, and the ones written this field are
    underlined.
  - The 128 characters of one bank, glowing amber as they are written. It shows the bank being written, or cycles
    through the banks on screen.
  - The character ROM set in use.
- **Speaker.** The field's waveform, a 0–6 kHz spectrum and the peak note.
- **Keyboard via light pen.** The 8 × 8 matrix. Held keys are amber, and keys the program tests through R18/R19 + R31
  flash blue. Also the light pen latch and the joystick bits.

### Second page: raster and I/O (Shift+F10)

**Shift+F10** (or **View → Visualiser page**) switches between the two pages. Both columns of the first page are full,
so the raster panels have a page of their own.

**Left column**
- **6545 line by line.** Every line of the last picture at one pixel per T-state, starting at VSYNC. This is the
  Microbee counterpart of VZonPC's "6847 beam, line by line".
  - **The beam:** picture (two greens, alternating at each CRTC frame, so a stack of short frames shows as bands),
    blanking, HSYNC and VSYNC.
  - **On top:** where the CPU was held for video RAM (orange), and where it wrote to the CRTC, screen, attribute,
    colour or PCG RAM, read video RAM, or did an OUT.
  - **At the right, per line:** the 6545's scanline counter (RA; amber during vertical adjust), the line's start
    address (0–7FF), and marks where a new CRTC frame starts (amber) and where row 0 re-reads R12/R13 (cyan).
- **Beam-timed writes.** Every write that changes the picture in the last picture: 6545 registers, and ports 08h
  (colour RAM), 0Bh (character ROM latch), 1Ch (PCG bank and attributes) and 50h (memory map). Each is listed with the
  line and T-state the beam was at, after a count per register. Start-address registers are cyan, timing registers
  amber, keyboard-scan registers dim, ports magenta.

**Right column**
- **Z80 PIO pins.** Port B's eight pins over one field, sampled once a line, like a logic analyser: tape in and out,
  RS-232 DTR, CTS, RX and TX, the speaker, and B7 (VSYNC, RTC or network). Inputs are green, outputs amber, and pins the
  PIO watches for interrupts magenta. The INT line is below them. A serial mouse's bits show on CTS. Underneath: both
  ports' mode, direction, watch mask and vector; interrupts taken per field; and the mouse.
- **Colour and mono.** The Premium's 16 colours, and how each looks on the chosen mono monitor as foreground (full or
  half brightness) and as background (black or the half-intensity glow). Bars show how many cells on screen use each
  colour as foreground and as background. It also counts how many mono shades the colours on screen turn into, so a
  colour-only difference that vanishes on a mono monitor shows up.
- **MC146818 RTC** (models with one): the time, registers A–D, periodic rate and enabled interrupts, and IRQ.
- **Video RAM by line.** For each line of the last picture: the T-states the CPU was held, video RAM writes and reads.
  Lines with picture on them are tinted, because only those hold the CPU.
- **Beam and CPU.** The whole-field map from the first page, when there is room.

## For Claude Code sessions: testing Microbee programs

Use `ubemu.exe` instead of writing a new harness (no more per-game Python models or ubee512 runs):

```
ubemu.exe script.txt                     run a script
ubemu.exe -e "disk GAME.DS8; typeat A> game\r; seconds 30; screen s.png both"
ubemu.exe GAME.DS8 [fields] [out.png]    quick look: boot the disk, run, screenshot
ubemu.exe GAME.COM [fields] [out.png]    make a CP/M boot disk for the .COM, boot, type its name, run, screenshot
```

Commands in a script are one per line (`;` separates them with `-e`), and `#` starts a comment. Paths are relative to
the current directory. Exit code 2 means a crash check fired. The RTC starts at 2026-01-01 12:00 (set `UBEE_RTC=host`
for the real time), so runs repeat exactly.

**Configuration** (before the first run command)

| | |
|---|---|
| `model pp` / `p512k` / `256tc` / ... | the model (default `pp`) |
| `rom FILE` / `rom auto`, `romdir DIR` | the boot ROM |
| `disk [DRIVE] FILE` | put an image in a drive, A to D: `disk FILE` (A), `disk C: FILE`, `disk C FILE` or `disk 3 FILE` |
| `fastdisk off` | a real 300 rpm drive (default: no rotational delay) |
| `disksound on [VOLUME]` | floppy drive sounds in the audio and `wav` recordings (VOLUME 1.0 = normal) |
| `vramwait off` | no video RAM wait states |
| `dotclock auto\|13.5\|6.75` | the 6545 dot clock |
| `colour off`, `halfint off` | no colour board; no half intensity on the mono output |
| `mono green\|amber\|white`, `palette rgbi` | the mono monitor for screenshots; dark yellow instead of brown |
| `sym FILE` | symbols, so addresses can be names in other commands |
| `crash` | stop on a crash: an RST 38h storm through empty memory, or HALT with interrupts off; writes `crash.txt` (the last 4096 PCs) |

**Running and input**

| | |
|---|---|
| `boot` / `power` / `reset` | power on (boots drive A) / power cycle / reset |
| `com FILE` | make a boot disk for the .COM and its data files, boot, type its name at A> |
| `frames N` / `fields N`, `seconds N` | run (one field per monitor picture, normally 1/50 s) |
| `typeat PROMPT TEXT` | wait for PROMPT on the screen, then type TEXT (`_` = space in PROMPT, `\r` = RETURN) |
| `type TEXT` | type text now |
| `down KEY`, `up KEY`, `tap KEY [fields]`, `release` | KEY = A–Z 0–9 SPACE RET SHIFT CTRL ESC BS TAB LF LOCK BREAK DEL UP DOWN LEFT RIGHT , . / ; : - @ [ ] \ ^, or JUP JDOWN JLEFT JRIGHT JFIRE. On the 256TC also F1–F12, INS, ALT, ' = ` and the keypad KP0–KP9 KP+ KP- KP* KP/ KPDEL |
| `joy HEX` | set the joystick port byte (active low) |
| `mousemode off\|cts\|rx\|clocked`, `mouseinv on` | the serial mouse (configuration; default off) |
| `mouse DX DY [L] [R]`, `mousemove DX DY [fields]`, `mouseclick [L\|R]`, `mousestat` | send one packet; a smooth move, a packet a field; a click; the counters |
| `rand FIELDS [seed] [KEY,KEY,...]` | random-input soak |
| `until ADDR [maxfields]`, `untilmem ADDR VAL [max]`, `untiltext TEXT [max] [fail]` | run until PC, a byte, or screen text |
| `poke A V`, `pokeb BLOCK A V`, `out PORT V`, `in PORT`, `bin FILE ADDR`, `pc ADDR` | |

**Inspection**

| | |
|---|---|
| `screen FILE [scale] [colour\|mono\|both] [monitor\|picture\|raster] [native]` | screenshot; `native` = don't double the lines |
| `shots PREFIX COUNT [every]` | a screenshot every so many fields |
| `text` | the screen RAM as text (using the CRTC geometry) |
| `regs`, `crtc`, `fdc`, `rtc`, `field` | CPU, CRTC (registers, geometry, picture Hz, ports), FDC, clock, counters |
| `crtclog FILE` … `crtclogend` | every CRTC register write as `field line char Rn=VV` |
| `peek A [n]`, `peekb BLOCK A [n]`, `peekw A`, `dis A [n]` | memory, a bank block, a disassembly |
| `dump FILE`, `dumpram FILE`, `dumpvideo FILE` | 64K as the CPU sees it; all 1 MB; screen + attribute + colour + PCG |
| `diskdir [DRIVE]`, `disksave [DRIVE] [FILE]`, `diskeject DRIVE`, `diskprotect DRIVE on\|off` | the CP/M directory of a DS80 disk; write an image out; eject; write-protect (DRIVE = A–D, A:–D: or 1–4) |
| `wav FILE` … `wavend` | speaker audio, 44.1 kHz |
| `waits`, `waitreset` | video RAM wait T-states |
| `tmark ADDR` + `tstats` | fields per frame, from writes to ADDR |
| `prof 1` … `profsave FILE` | T-states per symbol |
| `minsp`, `stack LO HI`, `watch LO [HI]`, `ring on` / `ring FILE` | stack low-water mark; log writes (with beam line and character); the last 4096 PCs |
| `vis on` (before booting), `vis FILE [panels] [raster\|machine]` | the visualiser as a 1920 × 1080 PNG with the screen in the middle, or just the two panels; `raster` draws the second page |
| `fullscreen FILE [bezel|plain] [colour|mono] [novis]` | a 1920 × 1080 PNG laid out like the window's full screen: the visualiser columns at the edges and the picture between them, with or without the monitor bezel |
| `vispage machine\|raster` | which page later `vis FILE` captures draw |
| `lines [FIRST] [COUNT]` | the last picture as text, one line per scanline: 6545 row, RA, start address, CRTC frame, flags (D picture, A adjust, F new frame, 0 row 0, V VSYNC) and the beam across the line (`.` blank, `#` picture, `h` HSYNC, `v` VSYNC, `w` CPU held), 2 T per character. Needs `vis on` |
| `print TEXT`, `quit` | |

`UBEE_FDCDEBUG=1` logs every FDC command with its rotational position.

The GUI can check itself too: `uBonPC.exe GAME.DS8 --quit-after 20 --shot win.png [--view both] [--vis] [--type "game\r"]`
runs the real window, captures it (and the visualiser window as `win_vis.png`) and exits without touching the
settings. Other options: `--model ID`, `--frame monitor|picture|raster`, `--monitor amber`, `--disk2` / `--disk3` / `--disk4 FILE` (or `--diskb` / `--diskc` / `--diskd`; further disk files on the command line also fill B, C and D),
`--scale N` (1, 1.5, 2, 3 or 4), `--fullscreen`, `--mute`, `--no-settings`, `--vispage raster` (the visualiser on its second page).

## Source

- `src\Z80.cs`: the CPU. The ED-prefixed instructions count their T-states before their bus accesses, and the
  interrupt acknowledge and RETI hooks drive the PIO daisy chain.
- `src\Z80Dis.cs`: the disassembler.
- `src\Video.cs`: the 6545 beam, the Premium and standard video circuits, the palettes.
- `src\Bee.cs`: models, memory, ports, keyboard, speaker, run loop, .COM boot disks, screenshots.
- `src\Chips.cs`: the PIO and the RTC.
- `src\Mouse.cs`: the serial mouse.
- `src\Fdc.cs` and `src\Disk.cs`: the WD2793, the disk images and the CP/M disk builder.
- `src\Trace.cs` and `src\Vis.cs`: the visualiser.
- `src\Headless.cs`: `ubemu`.
- `src\MainForm.cs` and `src\WinIO.cs`: the window, waveOut audio, joysticks, GDI display.
- `src\SdlApp.cs` and `src\Sdl.cs`: the Linux and macOS front end on SDL2.
- `src\Bezel.cs`: the full-screen microbee MONITOR bezel. `src\Portable.cs`: PNG writing and file names on any platform.

Run `build.bat` after changes. C# 5 only: no string interpolation or `?.`.

## Not emulated (yet)

- **Cassette:** the PIO bits are there, but there's no tape deck.
- **Serial and parallel:** no RS-232 or printer.
- **The 256TC's keyboard controller** (an MK3870 whose program isn't dumped) is modelled as MAME does: a 9-key queue
  of scan codes on port 18h, announced on PIO B1.
- **The 56K model** doesn't run its ROM yet.
- **The Premium Plus's own extras:** the FPGA coreboard's SD card and 6.75 MHz modes. Disks are images in the WD2793.
- **The 2 MHz models:** their 12 MHz video clock.
