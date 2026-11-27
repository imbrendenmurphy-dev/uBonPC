// uBonPC's cross-platform front end (Linux, macOS, and Windows too), on SDL2. The same emulator core as the Windows
// version; instead of menus it has keys (Shift+F12 lists them), a command line and ubonpc.ini, and it uses zenity or
// kdialog for file dialogs when they're installed. Everything runs on one thread: events, one emulated field,
// drawing, and the sound card sets the pace. Modelled on VZonPC's SdlApp.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class BeeSdl
{
    [STAThread]
    public static int Main(string[] args)
    {
        Sdl.Setup();
        try { return new BeeSdlFront(args).Run(); }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("uBonPC needs the SDL2 library.\n" +
                "  Fedora:        sudo dnf install SDL2\n" +
                "  Debian/Ubuntu: sudo apt install libsdl2-2.0-0\n" +
                "  Arch:          sudo pacman -S sdl2\n" +
                "  macOS:         brew install sdl2\n" +
                "  Windows:       put SDL2.dll next to uBonPC-SDL.exe");
            return 1;
        }
    }
}

public sealed class BeeSdlFront
{
    readonly BeeMachine m = new BeeMachine();
    readonly BeeVis vis;
    readonly Bezel bezel = new Bezel();
    readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    readonly string iniPath;

    IntPtr win, ren, texPic, texL, texR, texMsg;
    int texPicW, texPicH, texMsgW, texMsgH; string texMsgText;
    uint audio;
    readonly IntPtr ev = Marshal.AllocHGlobal(64);

    // settings (the same ubonpc.ini keys as the Windows version)
    static readonly double[] ScaleSteps = { 1, 1.5, 2, 3, 4 };
    double scale = 1;
    bool integerScale = true, fullscreen, arrowsJoy, muted, aspect = true, brown = true, bezelOn = true, visOn, diskWriteBack = true;
    BeeMachine.View view = BeeMachine.View.Colour;
    BeeMachine.Frame frameMode = BeeMachine.Frame.Monitor;
    int monoTint = 1;
    bool paused, warp, running = true;
    string lastProgram, lastDir; bool lastWasCom;
    string message = ""; int messageFrames;
    double fps;

    int[] frame = new int[16]; int frameW = 4, frameH = 4;

    // command line
    string shotPath, autoType, startCom; double quitAfter; bool noSettings, muteArg;
    readonly string[] startDisk = new string[BeeFdc.DRIVES];

    public BeeSdlFront(string[] args)
    {
        iniPath = Path.Combine(baseDir, "ubonpc.ini");
        vis = new BeeVis(m);
        noSettings = Array.Exists(args, x => x == "--no-settings" || x == "--quit-after");
        if (!noSettings) LoadSettings();
        ParseArgs(args);
        if (muteArg) muted = true;
        m.Log = s => ShowMessage(s);
        m.Quiet = true;
    }

    // ------------------------------------------------------------------ startup and the main loop
    public int Run()
    {
        if (Sdl.SDL_Init(Sdl.INIT_VIDEO | Sdl.INIT_JOYSTICK | Sdl.INIT_GAMECONTROLLER) != 0)
        {
            Console.Error.WriteLine("SDL: " + Sdl.Error());
            return 1;
        }
        Sdl.SetHint("SDL_VIDEO_X11_NET_WM_BYPASS_COMPOSITOR", "0");
        Sdl.SetHint("SDL_MOUSE_FOCUS_CLICKTHROUGH", "1");
        int w, h; PictureSize(out w, out h);
        win = Sdl.CreateWindow("uBonPC", (int)(w * scale), (int)(h * scale), Sdl.WINDOW_RESIZABLE | Sdl.WINDOW_ALLOW_HIGHDPI);
        if (win == IntPtr.Zero) { Console.Error.WriteLine("SDL: can't open a window: " + Sdl.Error()); return 1; }
        Sdl.SDL_SetWindowMinimumSize(win, 360, 320);
        SetIcon();
        ren = Sdl.SDL_CreateRenderer(win, -1, Sdl.RENDERER_ACCELERATED);
        if (ren == IntPtr.Zero) ren = Sdl.SDL_CreateRenderer(win, -1, 0);
        if (ren == IntPtr.Zero) { Console.Error.WriteLine("SDL: no renderer: " + Sdl.Error()); return 1; }
        OpenAudio();
        ScanPads();
        if (fullscreen) { fullscreen = false; ToggleFullscreen(); }
        if (visOn) { visOn = false; ToggleVis(); }

        // as the Windows version starts: drives B-D, then a .COM, a disk to boot in A, or just the boot ROM
        for (int dn = 1; dn < BeeFdc.DRIVES; dn++) if (startDisk[dn] != null) InsertDiskNow(dn, startDisk[dn], false);
        if (startCom != null) RunComNow(startCom);
        else if (startDisk[0] != null) InsertDiskNow(0, startDisk[0], true);
        else PowerOnNow();
        if (autoType != null) m.TypeAfter(startCom == null ? "A>" : "", autoType, 3000);
        if (!m.HasRom && quitAfter <= 0)
            Sdl.MessageBox(win, "uBonPC: no ROMs", "uBonPC found no boot ROM for the " + m.Model.Name + ".\n\n" +
                "Microbee ROMs are not included. Download MAME's Microbee ROM set and extract it into the roms folder beside " +
                "uBonPC-SDL.exe, keeping its folder for each machine (the Premium Plus uses roms/mbeepp/pp.bin).\n\nSee roms/roms.txt.");

        var sw = Stopwatch.StartNew();
        double nextT = 0, fpsT = 0; int fpsFrames = 0, titleCount = 0;
        long quitAtFrame = quitAfter > 0 ? (long)(quitAfter * 50) : -1, framesRun = 0;
        while (running)
        {
            PumpEvents();
            if (!running) break;
            if (messageFrames > 0) messageFrames--;
            if (++titleCount >= 25) { titleCount = 0; UpdateTitle(); }
            if (paused)
            {
                Render(); Thread.Sleep(20); nextT = sw.Elapsed.TotalMilliseconds;
                if (audio != 0) Sdl.SDL_ClearQueuedAudio(audio);
                continue;
            }

            PollInput();
            PollMouse();
            m.AudioOn = !warp && audio != 0;
            long t0 = m.Cpu.Cyc;
            m.RunFrame();
            long fieldT = Math.Max(1000, m.Cpu.Cyc - t0);
            if (vis.T != null && visOn) { vis.MonoTint = monoTint; vis.Brown = brown; vis.Render(); }
            int fw, fh; frame = m.Snapshot(view, monoTint, frameMode, aspect, brown, out fw, out fh); frameW = fw; frameH = fh;
            if (!warp || (m.Frames & 7) == 0) Render();

            fpsFrames++; framesRun++;
            double now = sw.Elapsed.TotalMilliseconds;
            if (now - fpsT >= 1000) { fps = fpsFrames * 1000.0 / (now - fpsT); fpsFrames = 0; fpsT = now; }
            if (quitAtFrame >= 0 && framesRun >= quitAtFrame) { QuitWithShot(); break; }

            if (warp) { nextT = now; continue; }
            double frameMs = 1000.0 * fieldT / BeeMachine.CPU_HZ;
            if (m.AudioOn)
            {
                if (muted) for (int i = 0; i < m.AudioN; i++) m.Audio[i] = 0;
                Sdl.SDL_QueueAudio(audio, m.Audio, (uint)(m.AudioN * 2));
                // the sound card sets the pace: keep about three fields queued
                uint keep = (uint)(BeeMachine.RATE / 50 * 2 * 3);
                int guard = 0;
                while (running && Sdl.SDL_GetQueuedAudioSize(audio) > keep && guard++ < 100) Thread.Sleep(1);
                nextT = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                nextT += frameMs;
                double wait = nextT - sw.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
                else if (wait < -100) nextT = sw.Elapsed.TotalMilliseconds;
            }
        }
        Shutdown();
        return 0;
    }

    void OpenAudio()
    {
        if (Sdl.SDL_InitSubSystem(Sdl.INIT_AUDIO) != 0) { ShowMessage("No sound: " + Sdl.Error()); return; }
        var want = new Sdl.AudioSpec { freq = BeeMachine.RATE, format = Sdl.AUDIO_S16SYS, channels = 1, samples = 1024 };
        Sdl.AudioSpec got;
        audio = Sdl.SDL_OpenAudioDevice(IntPtr.Zero, 0, ref want, out got, 0);
        if (audio == 0) { ShowMessage("No sound: " + Sdl.Error()); return; }
        Sdl.SDL_PauseAudioDevice(audio, 0);
    }

    void Shutdown()
    {
        ReleaseMouse();
        if (diskWriteBack) SaveDisksNow(false);
        if (!noSettings) SaveSettings();
        if (audio != 0) Sdl.SDL_CloseAudioDevice(audio);
        foreach (var p in pads) p.Close();
        Sdl.SDL_DestroyRenderer(ren); Sdl.SDL_DestroyWindow(win);
        Sdl.SDL_Quit();
    }

    /// <summary>--quit-after with --shot: saves what the window shows (checks the drawing path end to end).</summary>
    void QuitWithShot()
    {
        Render();
        if (shotPath != null)
        {
            int cw, ch; Sdl.SDL_GetRendererOutputSize(ren, out cw, out ch);
            var px = new int[cw * ch];
            if (Sdl.SDL_RenderReadPixels(ren, IntPtr.Zero, Sdl.PIXELFORMAT_RGB888, px, cw * 4) == 0) Portable.SavePng(shotPath, px, cw, ch, 1);
            else Console.Error.WriteLine("can't read the window back: " + Sdl.Error());
        }
        Console.WriteLine("fps " + fps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        running = false;
    }

    void SetIcon()
    {
        // a 32x32 icon: amber "uB" on the charcoal of the microbee MONITOR
        var px = new int[32 * 32];
        for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFF202226);
        for (int y = 3; y < 29; y++) for (int x = 3; x < 29; x++) px[y * 32 + x] = unchecked((int)0xFF101010);
        DrawText(px, 32, 5, 9, "UB", unchecked((int)0xFFFFB000), 2);
        IntPtr s = Sdl.SDL_CreateRGBSurfaceWithFormatFrom(px, 32, 32, 32, 32 * 4, 0x16362004);   // ARGB8888
        if (s != IntPtr.Zero) { Sdl.SDL_SetWindowIcon(win, s); Sdl.SDL_FreeSurface(s); }
    }

    // ------------------------------------------------------------------ events
    readonly List<string> drops = new List<string>();

    void PumpEvents()
    {
        drops.Clear();
        while (Sdl.SDL_PollEvent(ev) != 0)
        {
            uint type = (uint)Marshal.ReadInt32(ev, 0);
            switch (type)
            {
                case Sdl.QUIT: running = false; break;
                case Sdl.KEYDOWN:
                    if (Marshal.ReadByte(ev, 13) == 0) KeyDown(Marshal.ReadInt32(ev, 16), (ushort)Marshal.ReadInt16(ev, 24));
                    break;
                case Sdl.MOUSEMOTION:
                    if (mouseCaptured) { mAccX += Marshal.ReadInt32(ev, 28); mAccY += Marshal.ReadInt32(ev, 32); }
                    break;
                case Sdl.MOUSEBUTTONDOWN:
                    {
                        int button = Marshal.ReadByte(ev, 16), clicks = Marshal.ReadByte(ev, 18);
                        if (button == 2) ReleaseMouse();                                   // middle button lets the mouse go
                        else if (button == 1 && m.Mouse.On && !mouseCaptured) CaptureMouse();
                        else if (button == 1 && !m.Mouse.On && clicks == 2) ToggleFullscreen();
                    }
                    break;
                case Sdl.WINDOWEVENT:
                    if (Marshal.ReadByte(ev, 12) == Sdl.WINDOWEVENT_FOCUS_LOST) ReleaseMouse();
                    break;
                case Sdl.JOYDEVICEADDED: case Sdl.JOYDEVICEREMOVED: ScanPads(); break;
                case Sdl.DROPFILE:
                    IntPtr f = Marshal.ReadIntPtr(ev, 8);
                    string path = Sdl.Str(f); Sdl.SDL_free(f);
                    if (path != null) drops.Add(path);
                    break;
            }
        }
        if (drops.Count > 0) OnDrop(drops.ToArray(), (Sdl.SDL_GetModState() & Sdl.KMOD_CTRL) != 0);
    }

    void KeyDown(int sc, ushort mod)
    {
        bool alt = (mod & Sdl.KMOD_ALT) != 0, shift = (mod & Sdl.KMOD_SHIFT) != 0, ctrl = (mod & Sdl.KMOD_CTRL) != 0;
        int fk = sc >= Sdl.SC_F1 && sc < Sdl.SC_F1 + 12 ? sc - Sdl.SC_F1 + 1 : 0;
        // F5-F12 are the emulator's, as in the Windows version (F1-F4 are the 256TC's own function keys)
        if (fk >= 5)
        {
            switch (fk)
            {
                case 5: SetView((BeeMachine.View)(((int)view + 1) % 3)); break;
                case 6:
                    if (ctrl) PowerOnNow();
                    else if (shift) { m.Reset(); ShowMessage("Reset"); }
                    else { paused = !paused; ShowMessage(paused ? "Paused (F6)" : "Running"); }
                    break;
                case 7:
                    if (shift) { if (lastProgram != null) { if (lastWasCom) RunComNow(lastProgram); else InsertDiskNow(0, lastProgram, true); } }
                    else OpenComDialog();
                    break;
                case 8:
                    if (ctrl) PasteText();
                    else OpenDiskDialog(0, !shift);
                    break;
                case 9:
                    if (shift) { muted = !muted; ShowMessage(muted ? "Sound muted (Shift+F9 to unmute; speed unchanged)" : "Sound on"); }
                    else { warp = !warp; ShowMessage(warp ? "WARP SPEED: running as fast as the PC can, sound off (F9 for normal speed)" : "Normal speed"); }
                    break;
                case 10: if (shift) { vis.Page = (vis.Page + 1) % 2; if (!visOn) ToggleVis(); ShowMessage("Visualiser page: " + (vis.Page == 0 ? "machine" : "raster and I/O")); } else ToggleVis(); break;
                case 11: ToggleFullscreen(); break;
                case 12: if (shift) Sdl.MessageBox(win, "uBonPC keys", HelpText); else Screenshot(); break;
            }
            return;
        }
        if (!alt) return;
        if (sc == Sdl.SC_RETURN || sc == Sdl.SC_KP_ENTER) { ToggleFullscreen(); return; }
        if (sc >= Sdl.SC_1 && sc <= Sdl.SC_1 + 4) { scale = ScaleSteps[sc - Sdl.SC_1]; if (fullscreen) ToggleFullscreen(); ApplyScale(); ShowMessage("Scale x" + scale); return; }
        if (sc >= Sdl.SC_1 + 5 && sc <= Sdl.SC_1 + 8)
        {
            int dn = sc - (Sdl.SC_1 + 5);                                   // Alt+6..9: drives A-D (with Shift: eject)
            if (shift) EjectDiskNow(dn); else OpenDiskDialog(dn, false);
            return;
        }
        char k = sc >= Sdl.SC_A && sc < Sdl.SC_A + 26 ? (char)('A' + sc - Sdl.SC_A) : '\0';
        switch (k)
        {
            case 'M':
                {
                    int i = Array.IndexOf(BeeModel.All, m.Model);
                    var md = BeeModel.All[(i + 1) % BeeModel.All.Length];
                    Reconfigure(() => m.SetModel(md));
                }
                break;
            case 'B': bezelOn = !bezelOn; ShowMessage(bezelOn ? "Monitor bezel in full screen" : "No bezel"); break;
            case 'I': integerScale = !integerScale; ShowMessage(integerScale ? "Whole-pixel scaling when it fills the space" : "Smooth scaling"); break;
            case 'S': aspect = !aspect; ApplyScale(); ShowMessage(aspect ? "Monitor shape (scanlines doubled)" : "One pixel per scanline"); break;
            case 'T': monoTint = (monoTint + 1) % 3; ShowMessage("Monochrome monitor: " + BeeVideo.MonoNames[monoTint]); break;
            case 'H': m.HalfIntensity = !m.HalfIntensity; ShowMessage(m.HalfIntensity ? "Half intensity on the mono output" : "No half intensity"); break;
            case 'P': brown = !brown; ShowMessage(brown ? "RGBI monitor palette (dark yellow as brown)" : "Plain RGBI palette"); break;
            case 'R':
                frameMode = (BeeMachine.Frame)(((int)frameMode + 1) % 3);
                ApplyScale();
                ShowMessage(frameMode == BeeMachine.Frame.Monitor ? "Frame: what a monitor shows" : frameMode == BeeMachine.Frame.Picture ? "Frame: the picture only" : "Frame: the whole raster");
                break;
            case 'A': arrowsJoy = !arrowsJoy; ShowMessage(arrowsJoy ? "Arrow keys + Right Ctrl = joystick" : "Arrow keys = Microbee cursor keys"); break;
            case 'U':
                m.Mouse.Mode = (MouseMode)(((int)m.Mouse.Mode + 1) % 4); m.Mouse.Reset(); ReleaseMouse();
                ShowMessage(m.Mouse.On ? "Serial mouse on " + BeeMouse.ModeNames[(int)m.Mouse.Mode].ToUpperInvariant() + ": click the picture to capture it, middle button to let go" : "No mouse");
                break;
            case 'N': m.DiskSnd.On = !m.DiskSnd.On; m.DiskSnd.Reset(); ShowMessage(m.DiskSnd.On ? "Floppy drive sounds on" : "Floppy drive sounds off"); break;
            case 'K': m.Fdc.Fast = !m.Fdc.Fast; ShowMessage(m.Fdc.Fast ? "Fast disk access (no rotational delay)" : "Real 300 rpm drive timing"); break;
            case 'W': m.VramWait = !m.VramWait; ShowMessage(m.VramWait ? "Video RAM wait states on" : "Video RAM wait states off (faster than the real machine)"); break;
            case 'C': vis.CellMode = (vis.CellMode + 1) % 3; ShowMessage("Visualiser screen cells: " + new[] { "writes", "PCG bank of each cell", "colours" }[vis.CellMode]); break;
            case 'Q': running = false; break;
        }
    }

    const string HelpText =
        "Microbee keyboard: letters, digits, SPACE, RETURN, SHIFT, CTRL, punctuation.\n" +
        "  Backspace = BS   Delete = DEL   Esc = ESC   Tab = TAB   Caps Lock = LOCK   Home = LF   End = BREAK\n" +
        "  ' = the Microbee's : key   = = ^   ` = @   (on the 256TC: F1-F4, Insert, the keypad, ' = and `)\n" +
        "Joystick: arrow keys + Right Ctrl (Alt+A), or a game controller.\n\n" +
        "F5 colour / mono / both   F6 pause (Shift reset, Ctrl power cycle)\n" +
        "F7 run a .COM (Shift+F7 run the last disk or program again)\n" +
        "F8 boot a disk in drive A (Shift: insert without booting; Ctrl: paste text as typing)\n" +
        "F9 warp speed (Shift: mute)   F10 visualiser (Shift: its second page)   F11 / Alt+Enter full screen\n" +
        "F12 screenshot   Shift+F12 these keys\n\n" +
        "Alt+1..5  window size x1, x1.5, x2, x3, x4    Alt+6..9  insert a disk in drive A..D (with Shift: eject)\n" +
        "Alt+M  next model   Alt+B  bezel in full screen   Alt+I  whole-pixel scaling   Alt+S  monitor shape\n" +
        "Alt+T  mono colour (green, amber, white)   Alt+H  half intensity   Alt+P  palette   Alt+R  frame\n" +
        "Alt+U  serial mouse (off, CTS, RX, clocked): click to capture, middle button to let go\n" +
        "Alt+N  floppy sounds   Alt+K  fast disk   Alt+W  video RAM waits   Alt+C  visualiser cells   Alt+Q  quit\n\n" +
        "Drop a disk image on the window to boot it (with Ctrl held: put it in the next empty drive of B-D);\n" +
        "drop several to fill A-D; drop a .COM to run it, a .sym for the visualiser, a .rom to boot from it.\n" +
        "File dialogs need zenity or kdialog. Command-line options are the Windows version's (see README.md).";

    // ------------------------------------------------------------------ input
    byte[] keys = new byte[512];
    bool K(int sc) { return keys[sc] != 0; }
    void Key(string k) { m.SetKey(k, true); }

    void PollInput()
    {
        if (!m.Typing) m.ClearKeys();
        int joy = 0xFF;
        if (Sdl.SDL_GetKeyboardFocus() == win && !m.Typing)
        {
            Marshal.Copy(Sdl.SDL_GetKeyboardState(IntPtr.Zero), keys, 0, keys.Length);
            bool alt = K(Sdl.SC_LALT) || K(Sdl.SC_RALT) || K(Sdl.SC_LGUI) || K(Sdl.SC_RGUI);   // Alt keys are uBonPC's
            bool fkey = false; for (int i = 4; i < 12; i++) if (K(Sdl.SC_F1 + i)) fkey = true;   // so are F5-F12
            if (!alt)
            {
                for (int i = 0; i < 26; i++) if (K(Sdl.SC_A + i)) Key(((char)('A' + i)).ToString());
                for (int i = 0; i < 9; i++) if (K(Sdl.SC_1 + i)) Key(((char)('1' + i)).ToString());
                if (K(Sdl.SC_0)) Key("0");
                if (K(Sdl.SC_SPACE)) Key("SPACE");
                if (K(Sdl.SC_RETURN) || K(Sdl.SC_KP_ENTER)) Key("RET");
                if (K(Sdl.SC_BACKSPACE)) Key("BS");
                if (K(76)) Key("DEL");                      // Delete
                if (K(Sdl.SC_ESCAPE)) Key("ESC");
                if (K(43)) Key("TAB");
                if (K(57)) Key("LOCK");                     // Caps Lock
                if (K(74)) Key("LF");                       // Home
                if (K(77)) Key("BREAK");                    // End
                if (!fkey && (K(Sdl.SC_LSHIFT) || K(Sdl.SC_RSHIFT))) Key("SHIFT");
                if (!fkey && (K(Sdl.SC_LCTRL) || (!arrowsJoy && K(Sdl.SC_RCTRL)))) Key("CTRL");
                if (K(Sdl.SC_COMMA)) Key(",");
                if (K(Sdl.SC_PERIOD)) Key(".");
                if (K(Sdl.SC_MINUS)) Key("-");
                if (K(Sdl.SC_SEMICOLON)) Key(";");
                if (K(56)) Key("/");
                if (K(47)) Key("[");
                if (K(48)) Key("]");
                if (K(49)) Key("\\");
                if (m.Model.NewKb)
                {
                    // the 256TC's own keyboard: F1-F4, its quote / equals / backquote keys, Insert and the keypad
                    for (int i = 0; i < 4; i++) if (K(Sdl.SC_F1 + i)) Key("F" + (i + 1));
                    if (K(Sdl.SC_APOSTROPHE)) Key("'");
                    if (K(Sdl.SC_EQUALS)) Key("=");
                    if (K(53)) Key("`");
                    if (K(Sdl.SC_INSERT)) Key("INS");
                    for (int i = 0; i < 9; i++) if (K(89 + i)) Key("KP" + (i + 1));
                    if (K(98)) Key("KP0");
                    if (K(85)) Key("KP*"); if (K(87)) Key("KP+"); if (K(86)) Key("KP-"); if (K(99)) Key("KPDEL"); if (K(84)) Key("KP/");
                }
                else
                {
                    if (K(Sdl.SC_APOSTROPHE)) Key(":");
                    if (K(Sdl.SC_EQUALS)) Key("^");
                    if (K(53)) Key("@");
                }
                bool up = K(Sdl.SC_UP), dn = K(Sdl.SC_DOWN), lf = K(Sdl.SC_LEFT), rt = K(Sdl.SC_RIGHT);
                if (arrowsJoy)
                {
                    if (up) joy &= ~1; if (dn) joy &= ~2; if (lf) joy &= ~4; if (rt) joy &= ~8;
                    if (K(Sdl.SC_RCTRL)) joy &= ~0x80;
                }
                else { if (up) Key("UP"); if (dn) Key("DOWN"); if (lf) Key("LEFT"); if (rt) Key("RIGHT"); }
            }
        }
        for (int n = 0; n < 2; n++)
        {
            int bits, arm; ReadPad(n, out bits, out arm);
            if ((bits & 1) == 0) joy &= ~1; if ((bits & 2) == 0) joy &= ~2; if ((bits & 4) == 0) joy &= ~4; if ((bits & 8) == 0) joy &= ~8;
            if ((bits & 16) == 0) joy &= ~0x80; if ((arm & 16) == 0) joy &= ~0x10;
        }
        m.Joy = joy;
    }

    // ---- game controllers (and plain joysticks SDL has no mapping for)
    sealed class Pad
    {
        public IntPtr Gc, Joy; public string Name;
        public void Close() { if (Gc != IntPtr.Zero) Sdl.SDL_GameControllerClose(Gc); else if (Joy != IntPtr.Zero) Sdl.SDL_JoystickClose(Joy); }
    }
    readonly List<Pad> pads = new List<Pad>();

    void ScanPads()
    {
        foreach (var p in pads) p.Close();
        pads.Clear();
        int n = Sdl.SDL_NumJoysticks();
        for (int i = 0; i < n && pads.Count < 4; i++)
        {
            var p = new Pad();
            if (Sdl.SDL_IsGameController(i) != 0) { p.Gc = Sdl.SDL_GameControllerOpen(i); if (p.Gc != IntPtr.Zero) p.Name = Sdl.GameControllerName(p.Gc); }
            if (p.Gc == IntPtr.Zero) { p.Joy = Sdl.SDL_JoystickOpen(i); if (p.Joy != IntPtr.Zero) p.Name = Sdl.JoystickName(p.Joy); }
            if (p.Gc != IntPtr.Zero || p.Joy != IntPtr.Zero) pads.Add(p);
        }
    }

    /// <summary>Controller n as joystick bits (active low): 1 up, 2 down, 4 left, 8 right, 16 fire; arm = second fire.</summary>
    void ReadPad(int n, out int bits, out int arm)
    {
        bits = 0xFF; arm = 0xFF;
        if (n >= pads.Count) return;
        var p = pads[n];
        const int T = 13000;
        if (p.Gc != IntPtr.Zero)
        {
            int x = Sdl.SDL_GameControllerGetAxis(p.Gc, 0), y = Sdl.SDL_GameControllerGetAxis(p.Gc, 1);
            if (x < -T || Sdl.SDL_GameControllerGetButton(p.Gc, 13) != 0) bits &= ~4;
            if (x > T || Sdl.SDL_GameControllerGetButton(p.Gc, 14) != 0) bits &= ~8;
            if (y < -T || Sdl.SDL_GameControllerGetButton(p.Gc, 11) != 0) bits &= ~1;
            if (y > T || Sdl.SDL_GameControllerGetButton(p.Gc, 12) != 0) bits &= ~2;
            if (Sdl.SDL_GameControllerGetButton(p.Gc, 0) != 0 || Sdl.SDL_GameControllerGetButton(p.Gc, 2) != 0) bits &= ~16;   // A, X
            if (Sdl.SDL_GameControllerGetButton(p.Gc, 1) != 0 || Sdl.SDL_GameControllerGetButton(p.Gc, 3) != 0) arm &= ~16;    // B, Y
        }
        else
        {
            if (Sdl.SDL_JoystickNumAxes(p.Joy) >= 2)
            {
                int x = Sdl.SDL_JoystickGetAxis(p.Joy, 0), y = Sdl.SDL_JoystickGetAxis(p.Joy, 1);
                if (x < -T) bits &= ~4; else if (x > T) bits &= ~8;
                if (y < -T) bits &= ~1; else if (y > T) bits &= ~2;
            }
            if (Sdl.SDL_JoystickNumHats(p.Joy) > 0)
            {
                int h = Sdl.SDL_JoystickGetHat(p.Joy, 0);
                if ((h & 1) != 0) bits &= ~1; if ((h & 2) != 0) bits &= ~8; if ((h & 4) != 0) bits &= ~2; if ((h & 8) != 0) bits &= ~4;
            }
            int nb = Sdl.SDL_JoystickNumButtons(p.Joy);
            for (int i = 0; i < Math.Min(4, nb); i++)
                if (Sdl.SDL_JoystickGetButton(p.Joy, i) != 0) { if ((i & 1) == 0) bits &= ~16; else arm &= ~16; }
        }
    }

    // ---- the serial mouse: captured (relative mode), it sends Microsoft mouse packets
    bool mouseCaptured; int mAccX, mAccY; bool mLastL, mLastR;
    void CaptureMouse()
    {
        if (Sdl.SDL_SetRelativeMouseMode(1) != 0) { ShowMessage("Can't capture the mouse: " + Sdl.Error()); return; }
        mouseCaptured = true; mAccX = mAccY = 0;
        ShowMessage("Mouse captured: middle button (or switching windows) lets it go");
    }
    void ReleaseMouse()
    {
        if (!mouseCaptured) return;
        mouseCaptured = false;
        Sdl.SDL_SetRelativeMouseMode(0);
    }
    void PollMouse()
    {
        if (!mouseCaptured || !m.Mouse.On) return;
        int mx, my; uint b = Sdl.SDL_GetMouseState(out mx, out my);
        bool l = (b & 1) != 0, r = (b & 4) != 0;
        if ((mAccX != 0 || mAccY != 0 || l != mLastL || r != mLastR) && !m.Mouse.Busy(m.Cpu.Cyc))
        {
            int dx = Math.Max(-127, Math.Min(127, mAccX)), dy = Math.Max(-127, Math.Min(127, mAccY));
            m.Mouse.Packet(dx, dy, l, r, m.Cpu.Cyc);
            mAccX -= dx; mAccY -= dy; mLastL = l; mLastR = r;
        }
    }

    // ------------------------------------------------------------------ drawing
    void PictureSize(out int w, out int h)
    {
        w = frameW >= 64 ? frameW : view == BeeMachine.View.Both ? BeeVideo.MONW * 2 + 8 : BeeVideo.MONW;
        h = frameH >= 64 ? frameH : BeeVideo.MONH * (aspect ? 2 : 1);
    }

    static IntPtr Texture(IntPtr ren, ref IntPtr tex, int w, int h, int scaleMode)
    {
        if (tex == IntPtr.Zero)
        {
            tex = Sdl.SDL_CreateTexture(ren, Sdl.PIXELFORMAT_RGB888, Sdl.TEXTUREACCESS_STREAMING, w, h);
            try { Sdl.SDL_SetTextureScaleMode(tex, scaleMode); } catch (EntryPointNotFoundException) { }
        }
        return tex;
    }

    /// <summary>The monitor front goes round the picture in full screen, for one monitor at a time.</summary>
    bool UseBezel() { return fullscreen && bezelOn && view != BeeMachine.View.Both && frameMode != BeeMachine.Frame.Raster; }

    /// <summary>Whole-pixel steps when that option is on and a whole step fills at least 85% of the space.</summary>
    double FitScale(int w, int h, int cw, int ch)
    {
        double k = Math.Min((double)cw / w, (double)ch / h);
        if (integerScale && k >= 1) { double si = Math.Floor(k); if (si / k >= 0.85) return si; }
        return k;
    }

    void Render()
    {
        int cw, ch;
        // draw to the whole window: after a switch to full screen some X11 set-ups (and Xvfb, with no window manager)
        // never send the resize event that would update the renderer's viewport, which left only a corner drawn
        Sdl.SDL_RenderSetViewport(ren, IntPtr.Zero);
        Sdl.SDL_GetRendererOutputSize(ren, out cw, out ch);
        Sdl.SDL_SetRenderDrawColor(ren, 0, 0, 0, 255);
        Sdl.SDL_RenderClear(ren);
        int w = frameW, h = frameH;

        int areaX = 0, areaW = cw;
        if (visOn && vis.T != null)
        {
            // the two columns run the full height, flush to the edges; the picture fills the space between
            double k = Math.Min((double)ch / BeeVis.H, cw * 0.25 / BeeVis.W);
            int pw = (int)Math.Round(BeeVis.W * k), ph = (int)Math.Round(BeeVis.H * k), py = (ch - ph) / 2;
            Texture(ren, ref texL, BeeVis.W, BeeVis.H, 1); Texture(ren, ref texR, BeeVis.W, BeeVis.H, 1);
            Sdl.SDL_UpdateTexture(texL, IntPtr.Zero, vis.Left, BeeVis.W * 4);
            Sdl.SDL_UpdateTexture(texR, IntPtr.Zero, vis.Right, BeeVis.W * 4);
            areaX = pw; areaW = cw - 2 * pw;
            var rl = new Sdl.Rect(0, py, pw, ph); Sdl.SDL_RenderCopy(ren, texL, IntPtr.Zero, ref rl);
            var rr = new Sdl.Rect(cw - pw, py, pw, ph); Sdl.SDL_RenderCopy(ren, texR, IntPtr.Zero, ref rr);
        }

        if (UseBezel() && areaW > 64)
        {
            // the bezel is drawn at the screen's own resolution, so it is copied 1:1
            var b = bezel.Compose(frame, w, h, areaW, ch);
            if (texPic != IntPtr.Zero && (texPicW != areaW || texPicH != ch)) { Sdl.SDL_DestroyTexture(texPic); texPic = IntPtr.Zero; }
            Texture(ren, ref texPic, areaW, ch, 0); texPicW = areaW; texPicH = ch;
            Sdl.SDL_UpdateTexture(texPic, IntPtr.Zero, b, areaW * 4);
            var rb = new Sdl.Rect(areaX, 0, areaW, ch);
            Sdl.SDL_RenderCopy(ren, texPic, IntPtr.Zero, ref rb);
        }
        else
        {
            if (texPic != IntPtr.Zero && (texPicW != w || texPicH != h)) { Sdl.SDL_DestroyTexture(texPic); texPic = IntPtr.Zero; }
            Texture(ren, ref texPic, w, h, integerScale ? 0 : 1); texPicW = w; texPicH = h;
            Sdl.SDL_UpdateTexture(texPic, IntPtr.Zero, frame, w * 4);
            double s = FitScale(w, h, Math.Max(1, areaW), ch);
            int dw = Math.Max(1, Math.Min(areaW, (int)Math.Round(w * s))), dh = Math.Max(1, Math.Min(ch, (int)Math.Round(h * s)));
            var r = new Sdl.Rect(areaX + (areaW - dw) / 2, (ch - dh) / 2, dw, dh);
            Sdl.SDL_RenderCopy(ren, texPic, IntPtr.Zero, ref r);
        }

        if (messageFrames > 0 && message.Length > 0) DrawMessage(cw, ch);
        Sdl.SDL_RenderPresent(ren);
    }

    /// <summary>Shows the current message along the top of the window.</summary>
    void DrawMessage(int cw, int ch)
    {
        string text = message.ToUpperInvariant();
        int t = Math.Max(1, Math.Min(3, ch / 360));
        int maxChars = Math.Max(10, cw / (6 * t) - 2);
        if (text.Length > maxChars) text = text.Substring(0, maxChars - 3) + "...";
        int tw = text.Length * 6 + 4, th = 11;
        if (texMsg == IntPtr.Zero || texMsgText != text)
        {
            if (texMsg != IntPtr.Zero && (texMsgW != tw || texMsgH != th)) { Sdl.SDL_DestroyTexture(texMsg); texMsg = IntPtr.Zero; }
            Texture(ren, ref texMsg, tw, th, 0); texMsgW = tw; texMsgH = th; texMsgText = text;
            var px = new int[tw * th];
            for (int i = 0; i < px.Length; i++) px[i] = 0x101828;
            DrawText(px, tw, 2, 2, text, 0xFFC040, 1);
            Sdl.SDL_UpdateTexture(texMsg, IntPtr.Zero, px, tw * 4);
        }
        var r = new Sdl.Rect((cw - tw * t) / 2, 4 * t, tw * t, th * t);
        Sdl.SDL_RenderCopy(ren, texMsg, IntPtr.Zero, ref r);
    }

    static void DrawText(int[] px, int stride, int x0, int y0, string s, int colour, int size)
    {
        int rows = px.Length / stride;
        for (int i = 0; i < s.Length; i++)
        {
            int c = s[i]; if (c >= 'a' && c <= 'z') c -= 32;
            if (c < 32 || c > 95) c = '?';
            int g = c >= '@' ? c - 64 : c;
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 5; x++)
                    if ((Font6847.F[g, y] & (16 >> x)) != 0)
                        for (int dy = 0; dy < size; dy++) for (int dx = 0; dx < size; dx++)
                        {
                            int X = x0 + (i * 6 + x) * size + dx, Y = y0 + y * size + dy;
                            if (X >= 0 && X < stride && Y >= 0 && Y < rows) px[Y * stride + X] = colour;
                        }
        }
    }

    void ApplyScale()
    {
        if (fullscreen || win == IntPtr.Zero) return;
        int w, h; PictureSize(out w, out h);
        int ww = (int)Math.Round(w * scale), wh = (int)Math.Round(h * scale);
        if (visOn) ww += 2 * (int)(BeeVis.W * (double)wh / BeeVis.H);
        // SDL sizes windows in points; on a HiDPI screen the renderer has more pixels than that
        int pw, ph, cw, ch; Sdl.SDL_GetWindowSize(win, out pw, out ph); Sdl.SDL_GetRendererOutputSize(ren, out cw, out ch);
        if (pw > 0 && cw > pw) { ww = ww * pw / cw; wh = wh * pw / cw; }
        Sdl.SDL_SetWindowSize(win, ww, wh);
    }

    void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        Sdl.SDL_SetWindowFullscreen(win, fullscreen ? Sdl.WINDOW_FULLSCREEN_DESKTOP : 0);
        Sdl.SDL_ShowCursor(fullscreen ? 0 : 1);
        if (!fullscreen) ApplyScale();
    }

    void ToggleVis()
    {
        visOn = !visOn;
        if (visOn) { if (m.Tr == null) m.Tr = new BeeTrace(m); vis.T = m.Tr; if (vis.Sym.Count == 0 && lastProgram != null) vis.FindSymbols(lastProgram); ShowMessage("Visualiser on (F10 to close, Shift+F10 its second page)"); }
        else { m.Tr = null; vis.T = null; }
        ApplyScale();
    }

    void SetView(BeeMachine.View v)
    {
        view = v;
        ShowMessage(v == BeeMachine.View.Colour ? "Colour monitor" : v == BeeMachine.View.Mono ? "Monochrome monitor (" + BeeVideo.MonoNames[monoTint] + ")" : "Colour and monochrome side by side");
        frameW = frameH = 4;      // re-measure the picture before resizing the window
        int fw, fh; frame = m.Snapshot(view, monoTint, frameMode, aspect, brown, out fw, out fh); frameW = fw; frameH = fh;
        ApplyScale();
    }

    void UpdateTitle()
    {
        string t = "uBonPC" + (lastProgram != null ? " - " + Path.GetFileName(lastProgram) : "") + "  |  " + ConfigText();
        var f = m.Fdc;
        if (f.Busy) t += "  |  " + (char)('A' + f.Drive) + ": " + f.HeadCyl[f.Drive] + "/" + f.Side;
        if (m.Mouse.On) t += "  |  MOUSE " + (mouseCaptured ? "CAPTURED" : "click to capture");
        t += "  |  " + (paused ? "PAUSED  " : "") + (warp ? "WARP  " : "") + (muted ? "MUTE  " : "") + fps.ToString("0") + " fps  |  Shift+F12 keys";
        Sdl.SetWindowTitle(win, t);
    }

    void ShowMessage(string s) { message = s ?? ""; messageFrames = 200; }

    // ------------------------------------------------------------------ machine
    void Reconfigure(Action change)
    {
        change();
        if (lastProgram != null && lastWasCom && File.Exists(lastProgram)) RunComNow(lastProgram);
        else PowerOnNow();
        ShowMessage(ConfigText());
    }

    void PowerOnNow()
    {
        m.PowerOn();
        ShowMessage(m.HasRom ? "Power on: " + m.Model.Name + ", boot ROM " + m.RomName + (m.Fdc.Disk[0] == null && m.Model.Fdc ? " - F8 boots a disk, F7 runs a .COM" : "")
            : "Power on: no boot ROM for " + m.Model.Name + " - see roms/roms.txt");
    }

    string ConfigText()
    {
        string s = m.Model.Name;
        if (!m.HasRom) s += " (no boot ROM)";
        if (m.Colour != m.Model.Colour) s += m.Colour ? " + colour board" : ", no colour";
        if (!m.VramWait) s += ", no video waits";
        return s;
    }

    void RunComNow(string path)
    {
        try
        {
            string r = m.RunCom(path);
            vis.FindSymbols(path);
            lastProgram = path; lastWasCom = true; lastDir = Path.GetDirectoryName(path);
            ShowMessage(r + (vis.SymFile != null ? "; symbols " + Path.GetFileName(vis.SymFile) : ""));
        }
        catch (Exception ex) { ShowMessage("Can't run " + Path.GetFileName(path) + ": " + ex.Message); }
    }

    static bool IsTemp(string p) { return p == null || p.StartsWith(Path.GetTempPath()); }

    void InsertDiskNow(int drive, string p, bool boot)
    {
        try
        {
            var old = m.Fdc.Disk[drive];
            if (diskWriteBack && old != null && old.Dirty && !IsTemp(old.Path)) old.Save();
            var d = BeeDisk.Load(p);
            m.Fdc.Disk[drive] = d;
            lastDir = Path.GetDirectoryName(p);
            if (boot)
            {
                m.BootDisk();
                lastProgram = p; lastWasCom = false;
                vis.FindSymbols(p);
                ShowMessage("Booting " + Path.GetFileName(p) + " (" + d.Format + ")" + (m.HasRom ? "" : " - but there is no boot ROM: see roms/roms.txt"));
            }
            else ShowMessage("Drive " + (char)('A' + drive) + ": " + d.Name + " (" + d.Format + ")");
        }
        catch (Exception ex) { ShowMessage("Disk: " + ex.Message); }
    }

    void EjectDiskNow(int drive)
    {
        var d = m.Fdc.Disk[drive]; if (d == null) { ShowMessage("Drive " + (char)('A' + drive) + " is empty"); return; }
        try { if (diskWriteBack && d.Dirty && !IsTemp(d.Path)) d.Save(); } catch (Exception ex) { ShowMessage("Disk: can't save " + d.Name + ": " + ex.Message); return; }
        m.Fdc.Disk[drive] = null;
        ShowMessage("Drive " + (char)('A' + drive) + " ejected" + (d.Writes > 0 ? (diskWriteBack ? ", changes saved" : ", changes discarded") : ""));
    }

    void SaveDisksNow(bool report)
    {
        int n = 0;
        for (int i = 0; i < BeeFdc.DRIVES; i++)
        {
            var d = m.Fdc.Disk[i];
            if (d == null || !d.Dirty || IsTemp(d.Path)) continue;
            try { d.Save(); n++; } catch (Exception ex) { if (report) ShowMessage("Disk: can't save " + d.Name + ": " + ex.Message); }
        }
        if (report) ShowMessage(n == 0 ? "No disk changes to save" : "Saved " + n + " disk image(s)");
    }

    void PasteText()
    {
        string t = Sdl.Clipboard();
        if (t.Length == 0) { ShowMessage("Clipboard has no text"); return; }
        m.TypeString(t.Replace("\r\n", "\r").Replace('\n', '\r'));
    }

    void Screenshot()
    {
        string dir = Path.Combine(baseDir, "screenshots");
        string name = (lastProgram != null ? Path.GetFileNameWithoutExtension(lastProgram) : "Microbee") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
        try { Portable.SavePng(Path.Combine(dir, name), frame, frameW, frameH, 1); ShowMessage("Saved screenshots/" + name); }
        catch (Exception ex) { ShowMessage("Screenshot: " + ex.Message); }
    }

    void OnDrop(string[] files, bool ctrl)
    {
        string p = files[0], ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext == ".com") RunComNow(p);
        else if (ext == ".sym") { vis.Sym.Clear(); vis.LoadSymbols(p); ShowMessage(vis.Sym.Count + " symbols from " + Path.GetFileName(p)); }
        else if (ext == ".rom") Reconfigure(() => m.RomOverride = p);
        else if (files.Length > 1)
        {
            // several disks: A, B, C, D in order, then boot A
            int n = Math.Min(BeeFdc.DRIVES, files.Length);
            for (int dn = n - 1; dn >= 0; dn--) InsertDiskNow(dn, files[dn], dn == 0);
        }
        else if (ctrl)
        {
            // Ctrl+drop: into the first empty drive of B-D (B if all are full), without rebooting
            int dn = 1; while (dn < BeeFdc.DRIVES - 1 && m.Fdc.Disk[dn] != null) dn++;
            if (m.Fdc.Disk[dn] != null) dn = 1;
            InsertDiskNow(dn, p, false);
        }
        else InsertDiskNow(0, p, true);
    }

    // ------------------------------------------------------------------ dialogs (zenity or kdialog, when installed)
    /// <summary>Asks for a file with zenity or kdialog. Returns null when cancelled or when neither is installed
    /// (then the message says to drop the file on the window).</summary>
    string Pick(string title, string filterName, string patterns, bool save, string start)
    {
        if (Portable.IsWindows) { ShowMessage("Drop the file on the window (file dialogs need zenity or kdialog)"); return null; }
        string pats = patterns + " " + patterns.ToUpperInvariant();
        string[][] tries = {
            new[] { "zenity", "--file-selection --title=" + Q(title) + (save ? " --save --confirm-overwrite" : "") + (start != null ? " --filename=" + Q(start) : "") +
                (patterns.Length > 0 ? " --file-filter=" + Q(filterName + " | " + pats) + " --file-filter=" + Q("All files | *") : "") },
            new[] { "kdialog", (save ? "--getsavefilename " : "--getopenfilename ") + Q(start ?? ".") + (patterns.Length > 0 ? " " + Q(pats + "|" + filterName) : "") + " --title " + Q(title) } };
        foreach (var t in tries)
        {
            try
            {
                var psi = new ProcessStartInfo(t[0], t[1]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (audio != 0) Sdl.SDL_ClearQueuedAudio(audio);
                    return p.ExitCode == 0 && o.Length > 0 ? o : null;
                }
            }
            catch (Exception) { }
        }
        ShowMessage("Drop the file on the window (file dialogs need zenity or kdialog)");
        return null;
    }
    static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    const string DiskPatterns = "*.ds80 *.ds8 *.d80 *.ds40 *.ds4 *.ss80 *.s80 *.ds82 *.ds84 *.ds8b *.dsk *.img";
    void OpenDiskDialog(int drive, bool boot)
    {
        string p = Pick(boot ? "Boot a disk in drive A" : "Insert a disk in drive " + (char)('A' + drive), "Microbee disk images", DiskPatterns, false, lastDir != null ? lastDir + "/" : null);
        if (p != null) InsertDiskNow(drive, p, boot);
    }
    void OpenComDialog()
    {
        string p = Pick("Run a CP/M program", "CP/M programs", "*.com", false, lastDir != null ? lastDir + "/" : null);
        if (p != null) RunComNow(p);
    }

    // ------------------------------------------------------------------ command line and settings (as the Windows version)
    void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string v = i + 1 < args.Length ? args[i + 1] : "";
            if (a == "--scale") { double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale); scale = Math.Max(1, Math.Min(4, scale)); i++; }
            else if (a == "--fullscreen") fullscreen = true;
            else if (a == "--window") fullscreen = false;
            else if (a == "--shot") { shotPath = Path.GetFullPath(v); i++; }
            else if (a == "--quit-after") { double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out quitAfter); i++; }
            else if (a == "--type") { autoType = v.Replace("\\r", "\r"); i++; }
            else if (a == "--model") { var md = BeeModel.Find(v); if (md != null) m.SetModel(md); i++; }
            else if (a == "--view") { v = v.ToLowerInvariant(); view = v == "mono" ? BeeMachine.View.Mono : v == "both" ? BeeMachine.View.Both : BeeMachine.View.Colour; i++; }
            else if (a == "--frame") { v = v.ToLowerInvariant(); frameMode = v.StartsWith("pic") ? BeeMachine.Frame.Picture : v.StartsWith("ras") ? BeeMachine.Frame.Raster : BeeMachine.Frame.Monitor; i++; }
            else if (a == "--monitor") { int t = Array.IndexOf(BeeVideo.MonoNames, v.ToLowerInvariant()); if (t >= 0) monoTint = t; i++; }
            else if (a == "--vis") visOn = true;
            else if (a == "--vispage") { visOn = true; vis.Page = v.ToLowerInvariant().StartsWith("r") || v == "2" ? 1 : 0; i++; }
            else if (a == "--bezel") { bezelOn = v != "0" && v.ToLowerInvariant() != "off"; i++; }
            else if (a == "--disksound") m.DiskSnd.On = true;
            else if (a == "--mute") muteArg = true;
            else if (a == "--no-settings") { }
            else if (a == "--disk" || a == "--disk1") { startDisk[0] = Path.GetFullPath(v); i++; }
            else if (a == "--disk2" || a == "--disk3" || a == "--disk4") { startDisk[a[6] - '1'] = Path.GetFullPath(v); i++; }
            else if (a == "--diskb" || a == "--diskc" || a == "--diskd") { startDisk[a[6] - 'a'] = Path.GetFullPath(v); i++; }
            else if (a == "--help" || a == "-h") { Console.WriteLine(HelpText); Environment.Exit(0); }
            else if (File.Exists(args[i]) && args[i].EndsWith(".com", StringComparison.OrdinalIgnoreCase)) startCom = Path.GetFullPath(args[i]);
            else if (File.Exists(args[i])) { int dn = Array.IndexOf(startDisk, null); if (dn >= 0) startDisk[dn] = Path.GetFullPath(args[i]); }
            else Console.Error.WriteLine("ignored: " + args[i]);
        }
    }

    void LoadSettings()
    {
        if (!File.Exists(iniPath)) return;
        foreach (var line in File.ReadAllLines(iniPath))
        {
            int eq = line.IndexOf('='); if (eq < 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
            try
            {
                switch (k)
                {
                    case "model": { var md = BeeModel.Find(v); if (md != null) m.SetModel(md); } break;
                    case "colour": m.Colour = v == "1"; break;
                    case "halfintensity": m.HalfIntensity = v == "1"; break;
                    case "vramwait": m.VramWait = v == "1"; break;
                    case "dotclock": m.Video.SetClock((DotClock)Enum.Parse(typeof(DotClock), v)); break;
                    case "view": view = (BeeMachine.View)Enum.Parse(typeof(BeeMachine.View), v); break;
                    case "frame": frameMode = (BeeMachine.Frame)Enum.Parse(typeof(BeeMachine.Frame), v); break;
                    case "mono": { int t = Array.IndexOf(BeeVideo.MonoNames, v); if (t >= 0) monoTint = t; } break;
                    case "brown": brown = v == "1"; break;
                    case "aspect": aspect = v == "1"; break;
                    case "diskwriteback": diskWriteBack = v == "1"; break;
                    case "fastdisk": m.Fdc.Fast = v == "1"; break;
                    case "disksound": m.DiskSnd.On = v == "1"; break;
                    case "mouse": { int i = Array.IndexOf(BeeMouse.ModeNames, v); if (i >= 0) m.Mouse.Mode = (MouseMode)i; } break;
                    case "mouseinvert": m.Mouse.Invert = v == "1"; break;
                    case "scale": scale = Math.Max(1, Math.Min(4, double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))); break;
                    case "integerscale": integerScale = v == "1"; break;
                    case "bezel": bezelOn = v == "1"; break;
                    case "fullscreen": fullscreen = v == "1"; break;
                    case "arrowsjoystick": arrowsJoy = v == "1"; break;
                    case "muted": muted = v == "1"; break;
                    case "visualiser": visOn = v == "1"; break;
                    case "cells": vis.CellMode = int.Parse(v); break;
                    case "vispage": vis.Page = Math.Max(0, Math.Min(1, int.Parse(v))); break;
                    case "lastprogram": lastProgram = File.Exists(v) ? v : null; break;
                    case "lastcom": lastWasCom = v == "1"; break;
                    case "lastdir": lastDir = Directory.Exists(v) ? v : null; break;
                }
            }
            catch (Exception) { }
        }
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllLines(iniPath, new[] {
                "model=" + m.Model.Id, "colour=" + (m.Colour ? 1 : 0), "halfintensity=" + (m.HalfIntensity ? 1 : 0), "vramwait=" + (m.VramWait ? 1 : 0),
                "dotclock=" + m.Video.Clock, "view=" + view, "frame=" + frameMode, "mono=" + BeeVideo.MonoNames[monoTint], "brown=" + (brown ? 1 : 0), "aspect=" + (aspect ? 1 : 0),
                "diskwriteback=" + (diskWriteBack ? 1 : 0), "fastdisk=" + (m.Fdc.Fast ? 1 : 0), "disksound=" + (m.DiskSnd.On ? 1 : 0), "mouse=" + BeeMouse.ModeNames[(int)m.Mouse.Mode], "mouseinvert=" + (m.Mouse.Invert ? 1 : 0), "scale=" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture), "integerscale=" + (integerScale ? 1 : 0), "bezel=" + (bezelOn ? 1 : 0), "fullscreen=" + (fullscreen ? 1 : 0),
                "arrowsjoystick=" + (arrowsJoy ? 1 : 0), "muted=" + (muted ? 1 : 0), "visualiser=" + (visOn ? 1 : 0), "cells=" + vis.CellMode, "vispage=" + vis.Page,
                "lastprogram=" + (lastProgram ?? ""), "lastcom=" + (lastWasCom ? 1 : 0), "lastdir=" + (lastDir ?? "") });
        }
        catch (Exception) { }
    }
}
