// uBonPC main window: menus, display (colour / mono / both side by side), scaling and fullscreen, keyboard and
// joystick input, the visualiser, and the emulation thread paced by the sound card.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

public static class BeeApp
{
    [STAThread]
    public static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args));
        return 0;
    }
}

sealed class DisplayPanel : Panel
{
    public Action Repaint;
    public DisplayPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true); BackColor = Color.Black; }
    protected override void OnPaint(PaintEventArgs e) { if (Repaint != null) Repaint(); }
}

public sealed class MainForm : Form
{
    readonly BeeMachine m = new BeeMachine();
    readonly ConcurrentQueue<Action> cmds = new ConcurrentQueue<Action>();
    readonly DisplayPanel display = new DisplayPanel();
    readonly MenuStrip menu = new MenuStrip();
    readonly StatusStrip status = new StatusStrip();
    readonly ToolStripStatusLabel stLeft = new ToolStripStatusLabel(), stRight = new ToolStripStatusLabel();
    Thread emuThread; volatile bool running = true;
    IntPtr displayHwnd, formHwnd;
    volatile int dispW, dispH;

    readonly string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ubonpc.ini");
    double scale = 1;
    static readonly double[] ScaleSteps = { 1, 1.5, 2, 3, 4 };
    bool bezelOn = true; readonly Bezel bezel = new Bezel(); ToolStripMenuItem miBezel;
    bool integerScale = true, fullscreen = false, arrowsJoy = false, muted = false, aspect = true, brown = true;
    BeeMachine.View view = BeeMachine.View.Colour;
    BeeMachine.Frame frameMode = BeeMachine.Frame.Monitor;
    int monoTint = 1;
    bool paused = false, warp = false;
    string lastProgram = null, lastDir = null; bool lastWasCom;
    string message = ""; int messageFrames = 0;

    readonly object frameLock = new object();
    int[] frame = new int[16]; int frameW = 4, frameH = 4;
    double fps; Rectangle savedBounds; FormWindowState savedState;

    readonly BeeVis vis;
    volatile bool visOn;
    VisForm visForm; volatile IntPtr visHwnd; volatile int visW, visH;

    public MainForm(string[] args)
    {
        Text = "uBonPC";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        KeyPreview = true;
        AllowDrop = true;
        vis = new BeeVis(m);
        if (!Array.Exists(args, x => x == "--no-settings" || x == "--quit-after")) LoadSettings();
        ParseArgs(args);
        if (muteArg) muted = true;

        display.Dock = DockStyle.Fill;
        display.Repaint = BlitLatest;
        display.Resize += (s, e) => { dispW = display.ClientSize.Width; dispH = display.ClientSize.Height; BlitLatest(); };
        display.MouseDoubleClick += (s, e) => { if (!mouseCaptured && !m.Mouse.On) ToggleFullscreen(); };
        display.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Middle) { ReleaseMouse(); return; }
            if (m.Mouse.On && !mouseCaptured) CaptureMouse();
        };
        Deactivate += (s, e) => ReleaseMouse();
        status.Items.Add(stLeft); stLeft.Spring = true; stLeft.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.Add(stRight);
        BuildMenu();
        Controls.Add(display);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += OnDrop;

        Load += (s, e) =>
        {
            displayHwnd = display.Handle; formHwnd = Handle;
            dispW = display.ClientSize.Width; dispH = display.ClientSize.Height;
            ApplyScale();
            if (fullscreen) { fullscreen = false; ToggleFullscreen(); }
            if (visOn) { visOn = false; ToggleVis(); }
            Joysticks.Scan();
            emuThread = new Thread(EmuLoop) { IsBackground = true, Name = "emulation" };
            emuThread.Start();
            // ROMs are not included in the release: say so once, plainly, if none were found
            cmds.Enqueue(() =>
            {
                if (m.HasRom || quitAfter > 0) return;
                BeginInvoke((Action)(() => MessageBox.Show(this,
                    "uBonPC found no boot ROM for the " + m.Model.Name + ".\n\n" +
                    "Microbee ROMs are not included. Download MAME's Microbee ROM set and extract it into the roms folder " +
                    "beside uBonPC.exe, keeping its folder for each machine (the Premium Plus uses roms\\mbeepp\\pp.bin).\n\n" +
                    "See roms\\roms.txt.", "uBonPC: no ROMs", MessageBoxButtons.OK, MessageBoxIcon.Information)));
            });
            var t = new System.Windows.Forms.Timer { Interval = 500 }; t.Tick += (s2, e2) => UpdateStatus(); t.Start();
            if (quitAfter > 0)
            {
                var q = new System.Windows.Forms.Timer { Interval = (int)(quitAfter * 1000) };
                q.Tick += (s2, e2) =>
                {
                    q.Stop();
                    UpdateStatus(); Refresh();
                    if (shotPath != null)
                    {
                        CaptureWindow(this, shotPath);
                        if (visForm != null) CaptureWindow(visForm, Path.ChangeExtension(shotPath, null) + "_vis.png");
                    }
                    Console.WriteLine("fps " + fps.ToString("0.0"));
                    Close();
                };
                q.Start();
            }
        };
        FormClosing += (s, e) => { running = false; if (emuThread != null) emuThread.Join(1000); if (diskWriteBack) SaveDisksNow(false); if (!noSettings) SaveSettings(); };

        m.Log = s => ShowMessage(s);
        m.Quiet = true;
        cmds.Enqueue(() =>
        {
            for (int dn = 1; dn < BeeFdc.DRIVES; dn++) if (startDisk[dn] != null) InsertDiskNow(dn, startDisk[dn], false);
            if (startCom != null) RunComNow(startCom);
            else if (startDisk[0] != null) InsertDiskNow(0, startDisk[0], true);
            else PowerOnNow();
            if (autoType != null) m.TypeAfter(startCom == null ? "A>" : "", autoType, 3000);
        });
    }

    static void CaptureWindow(Form f, string path)
    {
        var r = f.RectangleToScreen(f.ClientRectangle);
        using (var bmp = new Bitmap(r.Width, r.Height))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    string shotPath, autoType, startCom; double quitAfter; bool noSettings, muteArg;
    string[] startDisk = new string[BeeFdc.DRIVES];
    void ParseArgs(string[] args)
    {
        noSettings = Array.Exists(args, x => x == "--no-settings" || x == "--quit-after");
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
            else if (a == "--disksound") m.DiskSnd.On = true;
            else if (a == "--mute") muteArg = true;
            else if (a == "--vispage") { visOn = true; vis.Page = v.ToLowerInvariant().StartsWith("r") || v == "2" ? 1 : 0; i++; }
            else if (a == "--no-settings") { }
            else if (a == "--disk" || a == "--disk1") { startDisk[0] = Path.GetFullPath(v); i++; }
            else if (a == "--disk2" || a == "--disk3" || a == "--disk4") { startDisk[a[6] - '1'] = Path.GetFullPath(v); i++; }
            else if (a == "--diskb" || a == "--diskc" || a == "--diskd") { startDisk[a[6] - 'a'] = Path.GetFullPath(v); i++; }
            else if (File.Exists(args[i]) && args[i].EndsWith(".com", StringComparison.OrdinalIgnoreCase)) startCom = Path.GetFullPath(args[i]);
            else if (File.Exists(args[i])) { int dn = Array.IndexOf(startDisk, null); if (dn >= 0) startDisk[dn] = Path.GetFullPath(args[i]); }   // A, then B, C, D
        }
    }

    // ------------------------------------------------------------------ menus
    ToolStripMenuItem miVis, miInteger, miStatus, miFull, miArrowsJoy, miArrowsCursor, miPause, miWarp, miMute, miJoyList, miWriteBack,
        miColourBoard, miFastDisk, miDiskSound, miHalf, miWait, miAspect, miBrown, miRgbi, miClkAuto, miClk13, miClk6, miViewC, miViewM, miViewB, miFrMon, miFrPic, miFrRas;
    readonly List<ToolStripMenuItem> miModels = new List<ToolStripMenuItem>(), miTints = new List<ToolStripMenuItem>();
    ToolStripMenuItem miVisPage;
    ToolStripMenuItem[] miDiskEject = new ToolStripMenuItem[BeeFdc.DRIVES], miDiskProt = new ToolStripMenuItem[BeeFdc.DRIVES], miDiskDir = new ToolStripMenuItem[BeeFdc.DRIVES], miDrive = new ToolStripMenuItem[BeeFdc.DRIVES], miScale = new ToolStripMenuItem[5], miCells = new ToolStripMenuItem[3];
    bool diskWriteBack = true;
    ToolStripMenuItem[] miMouse = new ToolStripMenuItem[4]; ToolStripMenuItem miMouseInv;

    static ToolStripMenuItem Item(string text, Keys keys, EventHandler click)
    {
        var mi = new ToolStripMenuItem(text, null, click);
        if (keys != Keys.None) mi.ShortcutKeys = keys;
        return mi;
    }

    void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("Boot a disk in drive A (.DS80 / .DS8 / .DSK ...)...", Keys.F8, (s, e) => OpenDiskDialog(0, true)));
        file.DropDownItems.Add(Item("Run a CP/M program (.COM) on a made-up boot disk...", Keys.F7, (s, e) => OpenComDialog()));
        file.DropDownItems.Add(Item("Reload the last disk or program", Keys.Shift | Keys.F7, (s, e) => { if (lastProgram != null) { string p = lastProgram; bool c = lastWasCom; cmds.Enqueue(() => { if (c) RunComNow(p); else InsertDiskNow(0, p, true); }); } }));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Paste text as typing", Keys.Control | Keys.F8, (s, e) => PasteText()));
        file.DropDownItems.Add(Item("Load symbols (.sym) for the visualiser...", Keys.None, (s, e) => { using (var d = new OpenFileDialog { Filter = "Symbols (*.sym)|*.sym|All files|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) { string p = d.FileName; cmds.Enqueue(() => { vis.Sym.Clear(); vis.LoadSymbols(p); ShowMessage(vis.Sym.Count + " symbols from " + Path.GetFileName(p)); }); } }));
        file.DropDownItems.Add(Item("Save screenshot", Keys.F12, (s, e) => Screenshot()));
        file.DropDownItems.Add(Item("Open the screenshots folder", Keys.None, (s, e) => { string d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots"); Directory.CreateDirectory(d); Process.Start("explorer.exe", "\"" + d + "\""); }));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Exit", Keys.None, (s, e) => Close()));

        var disk = new ToolStripMenuItem("&Disk");
        // the Premium Plus's controller latch selects one of four drives (A-D); each gets its own submenu
        for (int d = 0; d < BeeFdc.DRIVES; d++)
        {
            int dn = d; char dl = (char)('A' + dn);
            var sub = new ToolStripMenuItem("Drive " + dl);
            miDrive[dn] = sub;
            sub.DropDownItems.Add(Item("Insert a disk in drive " + dl + "...", dn == 0 ? Keys.Shift | Keys.F8 : Keys.None, (s, e) => OpenDiskDialog(dn, false)));
            miDiskEject[dn] = Item("Eject", Keys.None, (s, e) => cmds.Enqueue(() => EjectDiskNow(dn)));
            miDiskProt[dn] = Item("Write-protect", Keys.None, (s, e) => cmds.Enqueue(() => { var dk = m.Fdc.Disk[dn]; if (dk != null) { dk.WriteProtect = !dk.WriteProtect; ShowMessage("Drive " + dl + (dk.WriteProtect ? " write-protected" : " writable")); } }));
            miDiskDir[dn] = Item("Directory (DS80)", Keys.None, (s, e) => { var dk = m.Fdc.Disk[dn]; MessageBox.Show(this, dk == null ? "Drive " + dl + " is empty" : string.Join("\n", dk.Directory().ToArray()), "Drive " + dl + (dk != null ? ": " + dk.Name : "")); });
            sub.DropDownItems.AddRange(new ToolStripItem[] { miDiskEject[dn], miDiskProt[dn], miDiskDir[dn] });
            disk.DropDownItems.Add(sub);
        }
        disk.DropDownItems.Add(Item("Boot drive A (power cycle)", Keys.None, (s, e) => cmds.Enqueue(() => { m.BootDisk(); ShowMessage("Booting drive A"); })));
        disk.DropDownItems.Add(new ToolStripSeparator());
        miFastDisk = Item("Fast disk access: no rotational delay (SD card / floppy emulator)", Keys.None, (s, e) => cmds.Enqueue(() => { m.Fdc.Fast = !m.Fdc.Fast; ShowMessage(m.Fdc.Fast ? "Fast disk access (no rotational delay)" : "Real 300 rpm drive timing"); }));
        disk.DropDownItems.Add(miFastDisk);
        miDiskSound = Item("Floppy drive sounds (head steps, head load, motor)", Keys.None, (s, e) => cmds.Enqueue(() => { m.DiskSnd.On = !m.DiskSnd.On; m.DiskSnd.Reset(); ShowMessage(m.DiskSnd.On ? "Floppy drive sounds on" : "Floppy drive sounds off"); }));
        disk.DropDownItems.Add(miDiskSound);
        miWriteBack = Item("Write disk changes back to the image file (on eject and exit)", Keys.None, (s, e) => { diskWriteBack = !diskWriteBack; });
        disk.DropDownItems.Add(miWriteBack);
        disk.DropDownItems.Add(Item("Save disk changes now", Keys.None, (s, e) => cmds.Enqueue(() => SaveDisksNow(true))));
        disk.DropDownItems.Add(Item("New blank DS80 disk...", Keys.None, (s, e) => NewDiskDialog()));

        var mach = new ToolStripMenuItem("&Machine");
        var models = new ToolStripMenuItem("Model");
        foreach (var md in BeeModel.All)
        {
            var mm = md;
            var mi = Item(md.Name + "  (" + md.Id + ")", Keys.None, (s, e) => Reconfigure(() => m.SetModel(mm)));
            mi.Tag = md; miModels.Add(mi); models.DropDownItems.Add(mi);
        }
        mach.DropDownItems.Add(models);
        mach.DropDownItems.Add(Item("Boot ROM...", Keys.None, (s, e) => { using (var d = new OpenFileDialog { Filter = "ROM images (*.rom;*.bin)|*.rom;*.bin|All files|*.*", InitialDirectory = BeeMachine.RomDir }) if (d.ShowDialog(this) == DialogResult.OK) { string p = d.FileName; Reconfigure(() => m.RomOverride = p); } }));
        miColourBoard = Item("Colour board fitted", Keys.None, (s, e) => cmds.Enqueue(() => { m.Colour = !m.Colour; ShowMessage(m.Colour ? "Colour board fitted" : "No colour board"); }));
        miWait = Item("Video RAM wait states (CPU held while the picture is drawn)", Keys.None, (s, e) => cmds.Enqueue(() => { m.VramWait = !m.VramWait; ShowMessage(m.VramWait ? "Video RAM wait states on" : "Video RAM wait states off (faster than the real machine)"); }));
        var clk = new ToolStripMenuItem("6545 dot clock");
        miClkAuto = Item("Auto (6.75 MHz when R0 gives a 40-column line)", Keys.None, (s, e) => cmds.Enqueue(() => m.Video.SetClock(DotClock.Auto)));
        miClk13 = Item("13.5 MHz always", Keys.None, (s, e) => cmds.Enqueue(() => m.Video.SetClock(DotClock.Mhz13_5)));
        miClk6 = Item("6.75 MHz always", Keys.None, (s, e) => cmds.Enqueue(() => m.Video.SetClock(DotClock.Mhz6_75)));
        clk.DropDownItems.AddRange(new ToolStripItem[] { miClkAuto, miClk13, miClk6 });
        mach.DropDownItems.AddRange(new ToolStripItem[] { miColourBoard, miWait, clk, new ToolStripSeparator() });
        mach.DropDownItems.Add(Item("Power cycle", Keys.Control | Keys.F6, (s, e) => cmds.Enqueue(PowerOnNow)));
        mach.DropDownItems.Add(Item("Reset", Keys.Shift | Keys.F6, (s, e) => cmds.Enqueue(() => { m.Reset(); ShowMessage("Reset"); })));
        miPause = Item("Pause", Keys.F6, (s, e) => { paused = !paused; });
        miWarp = Item("Warp speed (as fast as the PC can; sound off)", Keys.F9, (s, e) => { warp = !warp; ShowMessage(warp ? "WARP SPEED: running as fast as the PC can, sound off (F9 for normal speed)" : "Normal speed"); });
        miMute = Item("Mute sound", Keys.Shift | Keys.F9, (s, e) => { muted = !muted; ShowMessage(muted ? "Sound muted (Shift+F9 to unmute; speed unchanged)" : "Sound on"); });
        mach.DropDownItems.AddRange(new ToolStripItem[] { miPause, miWarp, miMute });

        var viewM = new ToolStripMenuItem("&View");
        miViewC = Item("Colour monitor", Keys.None, (s, e) => SetView(BeeMachine.View.Colour));
        miViewM = Item("Monochrome monitor", Keys.None, (s, e) => SetView(BeeMachine.View.Mono));
        miViewB = Item("Colour and monochrome side by side", Keys.None, (s, e) => SetView(BeeMachine.View.Both));
        viewM.DropDownItems.AddRange(new ToolStripItem[] { miViewC, miViewM, miViewB, Item("Next display   (F5)", Keys.F5, (s, e) => SetView((BeeMachine.View)(((int)view + 1) % 3))) });
        var tint = new ToolStripMenuItem("Monochrome monitor colour");
        for (int i = 0; i < 3; i++) { int ti = i; var mi = Item(char.ToUpper(BeeVideo.MonoNames[i][0]) + BeeVideo.MonoNames[i].Substring(1), Keys.None, (s, e) => { monoTint = ti; }); miTints.Add(mi); tint.DropDownItems.Add(mi); }
        miHalf = Item("Half intensity on the mono output (colour bit 3 / bit 7)", Keys.None, (s, e) => cmds.Enqueue(() => m.HalfIntensity = !m.HalfIntensity));
        tint.DropDownItems.Add(new ToolStripSeparator()); tint.DropDownItems.Add(miHalf);
        var pal = new ToolStripMenuItem("Colour palette");
        miBrown = Item("RGBI monitor / CGA converter (dark yellow shows as brown)", Keys.None, (s, e) => brown = true);
        miRgbi = Item("Plain RGBI (dark yellow)", Keys.None, (s, e) => brown = false);
        pal.DropDownItems.AddRange(new ToolStripItem[] { miBrown, miRgbi });
        var fr = new ToolStripMenuItem("Frame");
        miFrMon = Item("Monitor: the area a monitor shows (picture placed by the sync timing)", Keys.None, (s, e) => SetFrame(BeeMachine.Frame.Monitor));
        miFrPic = Item("Picture only (cropped to the displayed area)", Keys.None, (s, e) => SetFrame(BeeMachine.Frame.Picture));
        miFrRas = Item("Whole raster (blanking, HSYNC and VSYNC shown)", Keys.None, (s, e) => SetFrame(BeeMachine.Frame.Raster));
        fr.DropDownItems.AddRange(new ToolStripItem[] { miFrMon, miFrPic, miFrRas });
        viewM.DropDownItems.AddRange(new ToolStripItem[] { tint, pal, fr, new ToolStripSeparator() });
        for (int i = 0; i < ScaleSteps.Length; i++)
        {
            double s0 = ScaleSteps[i];
            miScale[i] = Item(ScaleName(s0), Keys.None, (s, e) => { scale = s0; if (fullscreen) ToggleFullscreen(); ApplyScale(); });
            viewM.DropDownItems.Add(miScale[i]);
        }
        miBezel = Item("Monitor bezel in full screen (the microbee MONITOR front)", Keys.None, (s, e) => { bezelOn = !bezelOn; BlitLatest(); });
        miAspect = Item("Monitor shape (double each scanline)", Keys.None, (s, e) => { aspect = !aspect; ApplyScale(); });
        miInteger = Item("Whole-pixel scaling when it fills the space (sharp)", Keys.None, (s, e) => { integerScale = !integerScale; BlitLatest(); });
        miFull = Item("Fullscreen   (F11 / Alt+Enter)", Keys.F11, (s, e) => ToggleFullscreen());
        miStatus = Item("Status bar", Keys.None, (s, e) => { status.Visible = !status.Visible; ApplyScale(); });
        viewM.DropDownItems.AddRange(new ToolStripItem[] { miAspect, miInteger, miBezel, miFull, miStatus, new ToolStripSeparator() });
        miVis = Item("Visualiser   (F10)", Keys.None, (s, e) => ToggleVis());
        viewM.DropDownItems.Add(miVis);
        var cells = new ToolStripMenuItem("Visualiser screen cells show");
        string[] cn = { "Writes", "PCG bank of each cell", "Colours" };
        for (int i = 0; i < 3; i++) { int ci = i; miCells[i] = Item(cn[i], Keys.None, (s, e) => vis.CellMode = ci); cells.DropDownItems.Add(miCells[i]); }
        viewM.DropDownItems.Add(cells);
        miVisPage = Item("Visualiser page: raster and I/O   (Shift+F10)", Keys.None, (s, e) => NextVisPage());
        viewM.DropDownItems.Add(miVisPage);

        var input = new ToolStripMenuItem("&Input");
        miArrowsCursor = Item("Arrow keys = Microbee cursor keys", Keys.None, (s, e) => arrowsJoy = false);
        miArrowsJoy = Item("Arrow keys + Right Ctrl = joystick (PIO port A)", Keys.None, (s, e) => arrowsJoy = true);
        miJoyList = new ToolStripMenuItem("PC joysticks found: 0") { Enabled = false };
        var mouseM = new ToolStripMenuItem("Serial mouse (Microsoft, 1200 baud)");
        string[] mn = { "None", "On PIO B bit 3, CTS (the Premium Plus interface Empires was tested with)", "On PIO B bit 4, RS-232 RX", "ubee512's clocked interface (DTR clock + CTS data, interrupts)" };
        for (int i = 0; i < 4; i++) { int mi2 = i; miMouse[i] = Item(mn[i], Keys.None, (s, e) => cmds.Enqueue(() => { m.Mouse.Mode = (MouseMode)mi2; m.Mouse.Reset(); ShowMessage(mi2 == 0 ? "No mouse" : "Mouse on " + BeeMouse.ModeNames[mi2].ToUpperInvariant() + ": click the picture to capture it, middle button to let go"); })); mouseM.DropDownItems.Add(miMouse[i]); }
        miMouseInv = Item("Invert the line polarity", Keys.None, (s, e) => cmds.Enqueue(() => m.Mouse.Invert = !m.Mouse.Invert));
        mouseM.DropDownItems.Add(new ToolStripSeparator()); mouseM.DropDownItems.Add(miMouseInv);
        input.DropDownItems.AddRange(new ToolStripItem[] { miArrowsCursor, miArrowsJoy, new ToolStripSeparator(), mouseM, new ToolStripSeparator(),
            Item("Rescan joysticks", Keys.None, (s, e) => { Joysticks.Scan(); ShowMessage(Joysticks.Count + " joystick(s): " + string.Join(", ", Joysticks.Names)); }), miJoyList });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("Keys and controls", Keys.Shift | Keys.F12, (s, e) => MessageBox.Show(this, HelpText, "uBonPC keys")));
        help.DropDownItems.Add(Item("About", Keys.None, (s, e) => MessageBox.Show(this,
            "uBonPC - Microbee emulator, centred on the Premium Plus\n\nZ80, raster-exact 6545 CRTC, Premium colour and 32K PCG, 1 MB banking, light-pen keyboard, " +
            "WD2793 floppy, RTC, speaker, visualiser.\n\nModel: " + m.Model.Name + "\nBoot ROM: " + (m.HasRom ? m.RomName : "none") +
            "\nCharacter ROM: " + m.CharRomName + "\nROM folder: " + BeeMachine.RomDir, "About uBonPC")));

        menu.Items.AddRange(new ToolStripItem[] { file, disk, mach, viewM, input, help });
        menu.MenuActivate += (s, e) => SyncMenu();
    }

    const string HelpText =
        "Microbee keyboard: letters, digits, SPACE, RETURN, SHIFT, CTRL and the punctuation keys.\n" +
        "  Backspace = BS    Delete = DEL    Esc = ESC    Tab = TAB    Caps Lock = LOCK\n" +
        "  Home = LINE FEED    End = BREAK    ` = @    ' = :    = = ^    [ ] \\ as marked\n" +
        "Arrow keys: Microbee cursor keys, or the joystick (Input menu; Right Ctrl = fire). PC gamepads are the joystick.\n\n" +
        "F5 colour / mono / both side by side\n" +
        "F6 pause          Shift+F6 reset          Ctrl+F6 power cycle\n" +
        "F7 run a .COM     Shift+F7 reload it      F8 boot a disk     Shift+F8 insert a disk     Ctrl+F8 paste text\n" +
        "F9 warp           Shift+F9 mute\n" +
        "F10 visualiser    Shift+F10 its second page (raster and I/O)\n" +
        "F11 / Alt+Enter fullscreen    F12 screenshot    Shift+F12 this help\n\n" +
        "Drop a disk image (.DS80, .DS8, .DS40, .SS80, .DS82, .DS84, .DSK) on the window to boot it.\n" +
        "Drop a .COM to run it: uBonPC builds a CP/M boot disk with it and its data files and types its name at A>.";

    void SetView(BeeMachine.View v) { view = v; ShowMessage(v == BeeMachine.View.Colour ? "Colour monitor" : v == BeeMachine.View.Mono ? "Monochrome monitor (" + BeeVideo.MonoNames[monoTint] + ")" : "Colour and monochrome side by side"); BeginInvoke((Action)ApplyScale); }
    void SetFrame(BeeMachine.Frame f) { frameMode = f; BeginInvoke((Action)ApplyScale); }

    void SyncMenu()
    {
        foreach (var mi in miModels) mi.Checked = mi.Tag == m.Model;
        for (int i = 0; i < 3; i++) miTints[i].Checked = monoTint == i;
        miColourBoard.Checked = m.Colour; miHalf.Checked = m.HalfIntensity; miWait.Checked = m.VramWait;
        miClkAuto.Checked = m.Video.Clock == DotClock.Auto; miClk13.Checked = m.Video.Clock == DotClock.Mhz13_5; miClk6.Checked = m.Video.Clock == DotClock.Mhz6_75;
        miViewC.Checked = view == BeeMachine.View.Colour; miViewM.Checked = view == BeeMachine.View.Mono; miViewB.Checked = view == BeeMachine.View.Both;
        miFrMon.Checked = frameMode == BeeMachine.Frame.Monitor; miFrPic.Checked = frameMode == BeeMachine.Frame.Picture; miFrRas.Checked = frameMode == BeeMachine.Frame.Raster;
        miBrown.Checked = brown; miRgbi.Checked = !brown; miAspect.Checked = aspect;
        miWriteBack.Checked = diskWriteBack; miFastDisk.Checked = m.Fdc.Fast; miDiskSound.Checked = m.DiskSnd.On;
        for (int d = 0; d < BeeFdc.DRIVES; d++)
        {
            var dk = m.Fdc.Disk[d];
            miDiskEject[d].Enabled = dk != null; miDiskProt[d].Enabled = dk != null; miDiskProt[d].Checked = dk != null && dk.WriteProtect; miDiskDir[d].Enabled = dk != null;
            miDrive[d].Text = "Drive " + (char)('A' + d) + ": " + (dk != null ? dk.Name + (dk.Dirty ? " (changed)" : "") + (dk.WriteProtect ? " (protected)" : "") : "empty");
        }
        for (int i = 0; i < ScaleSteps.Length; i++)
        {
            // sizes bigger than this screen are shown but greyed out (they used to fall back to x1 silently)
            bool fits = ScaleFits(ScaleSteps[i]);
            miScale[i].Enabled = fits;
            miScale[i].Text = ScaleName(ScaleSteps[i]) + (fits ? "" : "   (too big for this " + Screen.FromControl(this).Bounds.Width + " x " + Screen.FromControl(this).Bounds.Height + " screen)");
            miScale[i].Checked = Math.Abs(scale - ScaleSteps[i]) < 0.01 && !fullscreen;
        }
        for (int i = 0; i < 3; i++) miCells[i].Checked = vis.CellMode == i;
        miVis.Checked = visOn; miVisPage.Checked = vis.Page == 1; miInteger.Checked = integerScale; miBezel.Checked = bezelOn; miFull.Checked = fullscreen; miStatus.Checked = status.Visible;
        miArrowsJoy.Checked = arrowsJoy; miArrowsCursor.Checked = !arrowsJoy;
        miPause.Checked = paused; miWarp.Checked = warp; miMute.Checked = muted;
        for (int i = 0; i < 4; i++) miMouse[i].Checked = (int)m.Mouse.Mode == i;
        miMouseInv.Checked = m.Mouse.Invert;
        miJoyList.Text = "PC joysticks found: " + (Joysticks.Count == 0 ? "none" : string.Join(", ", Joysticks.Names));
    }

    void Reconfigure(Action change)
    {
        cmds.Enqueue(() =>
        {
            change();
            if (lastProgram != null && lastWasCom && File.Exists(lastProgram)) RunComNow(lastProgram);
            else PowerOnNow();
            ShowMessage(ConfigText());
        });
    }

    void PowerOnNow()
    {
        m.PowerOn();
        ShowMessage(m.HasRom ? "Power on: " + m.Model.Name + ", boot ROM " + m.RomName + (m.Fdc.Disk[0] == null && m.Model.Fdc ? " - press F8 to boot a disk or F7 to run a .COM" : "")
            : "Power on: no boot ROM for " + m.Model.Name + " - see roms\\roms.txt");
    }

    string ConfigText()
    {
        string s = m.Model.Name;
        if (!m.HasRom) s += " (no boot ROM)";
        if (m.Colour != m.Model.Colour) s += m.Colour ? " + colour board" : ", no colour";
        if (!m.VramWait) s += ", no video waits";
        return s;
    }

    // ------------------------------------------------------------------ disks and programs
    void RunComNow(string path)
    {
        try
        {
            if (diskWriteBack && m.Fdc.Disk[0] != null && m.Fdc.Disk[0].Dirty && m.Fdc.Disk[0].Path != null && !m.Fdc.Disk[0].Path.StartsWith(Path.GetTempPath())) m.Fdc.Disk[0].Save();
            string r = m.RunCom(path);
            vis.FindSymbols(path);
            lastProgram = path; lastWasCom = true; lastDir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            BeginInvoke((Action)(() => Text = "uBonPC - " + name));
            ShowMessage(r + (vis.SymFile != null ? "; symbols " + Path.GetFileName(vis.SymFile) : ""));
        }
        catch (Exception ex) { ShowMessage("Can't run " + Path.GetFileName(path) + ": " + ex.Message); }
    }

    void InsertDiskNow(int drive, string p, bool boot)
    {
        try
        {
            var old = m.Fdc.Disk[drive];
            if (diskWriteBack && old != null && old.Dirty && old.Path != null && !old.Path.StartsWith(Path.GetTempPath())) old.Save();
            var d = BeeDisk.Load(p);
            m.Fdc.Disk[drive] = d;
            lastDir = Path.GetDirectoryName(p);
            if (boot)
            {
                m.BootDisk();
                lastProgram = p; lastWasCom = false;
                vis.FindSymbols(p);
                string name = Path.GetFileName(p);
                BeginInvoke((Action)(() => Text = "uBonPC - " + name));
                ShowMessage("Booting " + name + " (" + d.Format + ")" + (m.HasRom ? "" : " - but there is no boot ROM: see roms\\roms.txt"));
            }
            else ShowMessage("Drive " + (char)('A' + drive) + ": " + d.Name + " (" + d.Format + ")");
        }
        catch (Exception ex) { ShowMessage("Disk: " + ex.Message); }
    }

    void EjectDiskNow(int drive)
    {
        var d = m.Fdc.Disk[drive]; if (d == null) return;
        try { if (diskWriteBack && d.Dirty && !d.Path.StartsWith(Path.GetTempPath())) d.Save(); } catch (Exception ex) { ShowMessage("Disk: can't save " + d.Name + ": " + ex.Message); return; }
        m.Fdc.Disk[drive] = null;
        ShowMessage("Drive " + (char)('A' + drive) + " ejected" + (d.Writes > 0 ? (diskWriteBack ? ", changes saved" : ", changes discarded") : ""));
    }

    void SaveDisksNow(bool report)
    {
        int n = 0;
        for (int i = 0; i < BeeFdc.DRIVES; i++)
        {
            var d = m.Fdc.Disk[i];
            if (d == null || !d.Dirty || d.Path == null || d.Path.StartsWith(Path.GetTempPath())) continue;
            try { d.Save(); n++; } catch (Exception ex) { if (report) ShowMessage("Disk: can't save " + d.Name + ": " + ex.Message); }
        }
        if (report) ShowMessage(n == 0 ? "No disk changes to save" : "Saved " + n + " disk image(s)");
    }

    const string DiskFilter = "Microbee disk images (*.ds80;*.ds8;*.d80;*.ds40;*.ds4;*.ss80;*.s80;*.ds82;*.ds84;*.ds8b;*.dsk;*.img)|*.ds80;*.ds8;*.d80;*.ds40;*.ds4;*.ss80;*.s80;*.ds82;*.d82;*.ds84;*.d84;*.ds8b;*.dsk;*.img|All files|*.*";
    void OpenDiskDialog(int drive, bool boot)
    {
        using (var d = new OpenFileDialog { Filter = DiskFilter, InitialDirectory = lastDir ?? "", Title = boot ? "Boot a disk in drive A" : "Insert a disk in drive " + (char)('A' + drive) })
            if (d.ShowDialog(this) == DialogResult.OK) { string p = d.FileName; cmds.Enqueue(() => InsertDiskNow(drive, p, boot)); }
    }
    void OpenComDialog()
    {
        using (var d = new OpenFileDialog { Filter = "CP/M programs (*.com)|*.com|All files|*.*", InitialDirectory = lastDir ?? "", Title = "Run a CP/M program" })
            if (d.ShowDialog(this) == DialogResult.OK) { string p = d.FileName; cmds.Enqueue(() => RunComNow(p)); }
    }
    void NewDiskDialog()
    {
        using (var f = new SaveFileDialog { Filter = "DS80 disk image (*.ds80)|*.ds80", FileName = "blank.ds80", Title = "New blank DS80 disk (unformatted contents E5)" })
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                string p = f.FileName;
                cmds.Enqueue(() =>
                {
                    try { BeeDisk.Blank(p, BeeDisk.RawFormats[0]).Save(); InsertDiskNow(m.Fdc.Disk[0] != null && m.Fdc.Disk[1] == null ? 1 : 0, p, false); }
                    catch (Exception ex) { ShowMessage("Disk: " + ex.Message); }
                });
            }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;
        string p = files[0];
        if (p.EndsWith(".com", StringComparison.OrdinalIgnoreCase)) cmds.Enqueue(() => RunComNow(p));
        else if (p.EndsWith(".sym", StringComparison.OrdinalIgnoreCase)) cmds.Enqueue(() => { vis.Sym.Clear(); vis.LoadSymbols(p); ShowMessage(vis.Sym.Count + " symbols from " + Path.GetFileName(p)); });
        else if (p.EndsWith(".rom", StringComparison.OrdinalIgnoreCase)) Reconfigure(() => m.RomOverride = p);
        else if (files.Length > 1)
        {
            // several disks: A, B, C, D in order, then boot A
            var ps = new string[Math.Min(BeeFdc.DRIVES, files.Length)]; Array.Copy(files, ps, ps.Length);
            cmds.Enqueue(() => { for (int dn = ps.Length - 1; dn >= 0; dn--) InsertDiskNow(dn, ps[dn], dn == 0); });
        }
        else if ((ModifierKeys & Keys.Control) != 0)
        {
            // Ctrl+drop: into the first empty drive of B-D (B if all are full), without rebooting
            cmds.Enqueue(() =>
            {
                int dn = 1; while (dn < BeeFdc.DRIVES - 1 && m.Fdc.Disk[dn] != null) dn++;
                if (m.Fdc.Disk[dn] != null) dn = 1;
                InsertDiskNow(dn, p, false);
            });
        }
        else cmds.Enqueue(() => InsertDiskNow(0, p, true));
    }

    void PasteText()
    {
        if (!Clipboard.ContainsText()) { ShowMessage("Clipboard has no text"); return; }
        string t = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
        cmds.Enqueue(() => m.TypeString(t));
    }

    void Screenshot()
    {
        int[] px; int w, h;
        lock (frameLock) { px = (int[])frame.Clone(); w = frameW; h = frameH; }
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        string name = (lastProgram != null ? Path.GetFileNameWithoutExtension(lastProgram) : "Microbee") + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
        Headless.SavePng(Path.Combine(dir, name), px, w, h, 1);
        ShowMessage("Saved screenshots\\" + name);
    }

    // ------------------------------------------------------------------ display
    static string ScaleName(double s) { return "Scale x" + s.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture); }

    /// <summary>The picture's size before scaling (the last frame, or what the current view will give).</summary>
    void BaseSize(out int w, out int h)
    {
        lock (frameLock) { w = frameW; h = frameH; }
        if (w < 64) { w = view == BeeMachine.View.Both ? BeeVideo.MONW * 2 + 8 : BeeVideo.MONW; h = BeeVideo.MONH * (aspect ? 2 : 1); }
    }

    bool ScaleFits(double s)
    {
        int w, h; BaseSize(out w, out h);
        var wa = Screen.FromControl(this).WorkingArea;
        int extra = (menu.Visible ? menu.Height : 24) + (status.Visible ? status.Height : 22) + 40;   // menu, status bar, title bar
        return w * s <= wa.Width - 16 && h * s + extra <= wa.Height;
    }

    void ApplyScale()
    {
        if (fullscreen) { BlitLatest(); return; }
        int w, h; BaseSize(out w, out h);
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        double s = scale;
        if (!ScaleFits(s))
        {
            // the largest listed size that fits; say so, rather than quietly showing x1
            double best = 1; foreach (var st in ScaleSteps) if (ScaleFits(st)) best = st;
            if (best < s) ShowMessage(ScaleName(s) + " is too big for this screen: using " + ScaleName(best).Substring(6));
            s = best;
        }
        ClientSize = new Size((int)Math.Round(w * s), (int)Math.Round(h * s) + (menu.Visible ? menu.Height : 0) + (status.Visible ? status.Height : 0));
        // keep the resized window on its screen (a big window placed low down ran off the bottom)
        var wa2 = Screen.FromControl(this).WorkingArea;
        int nx = Math.Max(wa2.Left, Math.Min(Left, wa2.Right - Width)), ny = Math.Max(wa2.Top, Math.Min(Top, wa2.Bottom - Height));
        if (nx != Left || ny != Top) Location = new Point(nx, ny);
        BlitLatest();
    }

    /// <summary>How big to draw a w x h picture in a cw x ch area: whole-pixel steps (sharp) when that option is on and
    /// a whole step fills at least 85% of the space, otherwise as large as fits.</summary>
    double FitScale(int w, int h, int cw, int ch)
    {
        double k = Math.Min((double)cw / w, (double)ch / h);
        if (integerScale && k >= 1) { double si = Math.Floor(k); if (si / k >= 0.85) return si; }
        return k;
    }
    int lastW, lastH;

    void ToggleFullscreen()
    {
        fullscreen = !fullscreen;
        if (fullscreen)
        {
            savedBounds = Bounds; savedState = WindowState;
            menu.Visible = false; status.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            Cursor.Hide();
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            menu.Visible = true; status.Visible = true;
            Bounds = savedBounds; WindowState = savedState;
            Cursor.Show();
        }
        UpdateVisWindow();
        BlitLatest();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.Enter)) { ToggleFullscreen(); return true; }
        if (keyData == Keys.F10) { ToggleVis(); return true; }
        if (keyData == (Keys.Shift | Keys.F10)) { NextVisPage(); return true; }
        if (keyData == Keys.Menu || keyData == (Keys.Alt | Keys.Menu)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e) { e.Handled = true; e.SuppressKeyPress = true; }

    void BlitLatest()
    {
        if (displayHwnd == IntPtr.Zero) return;
        lock (frameLock) Present(frame, frameW, frameH);
    }

    void Present(int[] px, int w, int h)
    {
        int cw = dispW, ch = dispH;
        if (cw <= 0 || ch <= 0) return;
        if (fullscreen && visOn) { PresentWithSidebars(px, w, h, cw, ch); return; }
        PresentVisWindow();
        if (UseBezel()) { var b = bezel.Compose(px, w, h, cw, ch); Native.Blit(displayHwnd, b, cw, ch, cw, ch, 0, 0, cw, ch); return; }
        double s = FitScale(w, h, cw, ch);
        int dw = Math.Min(cw, (int)Math.Round(w * s)), dh = Math.Min(ch, (int)Math.Round(h * s));
        Native.Blit(displayHwnd, px, w, h, cw, ch, (cw - dw) / 2, (ch - dh) / 2, dw, dh);
    }

    // the monitor front goes round the picture in full screen, for one monitor at a time (not the split view, and
    // not the whole-raster frame, which shows more than a monitor does)
    bool UseBezel() { return fullscreen && bezelOn && view != BeeMachine.View.Both && frameMode != BeeMachine.Frame.Raster; }

    void PresentWithSidebars(int[] px, int w, int h, int cw, int ch)
    {
        // the two visualiser columns run the full height of the screen, flush to its left and right edges (scaled
        // with the screen height, but never taking more than half the width); the picture fills the space between
        double k = Math.Min((double)ch / BeeVis.H, cw * 0.25 / BeeVis.W);
        int pw = (int)Math.Round(BeeVis.W * k), ph = (int)Math.Round(BeeVis.H * k);
        int py = (ch - ph) / 2;
        int aw = cw - 2 * pw;
        var parts = new List<Native.Part>();
        if (UseBezel()) { var b = bezel.Compose(px, w, h, aw, ch); parts.Add(new Native.Part { Px = b, W = aw, H = ch, X = pw, Y = 0, DW = aw, DH = ch }); }
        else
        {
            double s = FitScale(w, h, aw, ch);
            int dw = Math.Min(aw, (int)Math.Round(w * s)), dh = Math.Min(ch, (int)Math.Round(h * s));
            parts.Add(new Native.Part { Px = px, W = w, H = h, X = pw + (aw - dw) / 2, Y = (ch - dh) / 2, DW = dw, DH = dh });
        }
        parts.Add(new Native.Part { Px = vis.Left, W = BeeVis.W, H = BeeVis.H, X = 0, Y = py, DW = pw, DH = ph });
        parts.Add(new Native.Part { Px = vis.Right, W = BeeVis.W, H = BeeVis.H, X = cw - pw, Y = py, DW = pw, DH = ph });
        Native.BlitParts(displayHwnd, cw, ch, parts);
    }

    void PresentVisWindow()
    {
        IntPtr hw = visHwnd; int cw = visW, ch = visH;
        if (hw == IntPtr.Zero || cw <= 0 || ch <= 0 || fullscreen) return;
        double k = Math.Min((double)cw / (2 * BeeVis.W), (double)ch / BeeVis.H);
        if (k >= 2) k = Math.Floor(k); else if (k > 1) k = 1;
        int pw = (int)(BeeVis.W * k), ph = (int)(BeeVis.H * k);
        int x0 = (cw - 2 * pw) / 2;
        var parts = new List<Native.Part>();
        parts.Add(new Native.Part { Px = vis.Left, W = BeeVis.W, H = BeeVis.H, X = x0, Y = 0, DW = pw, DH = ph });
        parts.Add(new Native.Part { Px = vis.Right, W = BeeVis.W, H = BeeVis.H, X = x0 + pw, Y = 0, DW = pw, DH = ph });
        Native.BlitParts(hw, cw, ch, parts);
    }

    void NextVisPage()
    {
        vis.Page = (vis.Page + 1) % 2;
        if (!visOn) ToggleVis();
        ShowMessage("Visualiser page: " + (vis.Page == 0 ? "machine" : "raster and I/O") + " (Shift+F10 to switch)");
    }

    void ToggleVis()
    {
        visOn = !visOn;
        bool on = visOn;
        cmds.Enqueue(() =>
        {
            if (on) { if (m.Tr == null) m.Tr = new BeeTrace(m); vis.T = m.Tr; if (vis.Sym.Count == 0 && lastProgram != null) vis.FindSymbols(lastProgram); }
            else { m.Tr = null; vis.T = null; }
        });
        if (on) ShowMessage("Visualiser on (F10 to close)");
        UpdateVisWindow();
        BlitLatest();
    }

    void UpdateVisWindow()
    {
        bool want = visOn && !fullscreen;
        if (want && visForm == null)
        {
            var f = new VisForm(this) { NextPage = NextVisPage };
            var wa = Screen.FromControl(this).WorkingArea;
            int ch = Math.Min(BeeVis.H, wa.Height - 40);
            int cw = (int)Math.Round(2.0 * BeeVis.W * ch / BeeVis.H);
            f.ClientSize = new Size(cw, ch);
            f.StartPosition = FormStartPosition.Manual;
            int x = Right + 4; if (x + f.Width > wa.Right) x = Math.Max(wa.Left, wa.Right - f.Width);
            int extra = f.Height - f.ClientSize.Height;
            if (f.Height > wa.Height) { ch = wa.Height - extra; cw = (int)Math.Round(2.0 * BeeVis.W * ch / BeeVis.H); f.ClientSize = new Size(cw, ch); }
            f.Location = new Point(x, wa.Top);
            f.Panel.Resize += (s, e) => { visW = f.Panel.ClientSize.Width; visH = f.Panel.ClientSize.Height; };
            f.Panel.Repaint = () => { lock (frameLock) PresentVisWindow(); };
            f.FormClosed += (s, e) =>
            {
                if (visForm != f) return;
                visHwnd = IntPtr.Zero; visForm = null;
                if (!fullscreen && visOn) { visOn = false; cmds.Enqueue(() => { m.Tr = null; vis.T = null; }); }
            };
            visForm = f;
            f.Show(this);
            visW = f.Panel.ClientSize.Width; visH = f.Panel.ClientSize.Height;
            visHwnd = f.Panel.Handle;
            Activate();
        }
        else if (!want && visForm != null)
        {
            var f = visForm; visHwnd = IntPtr.Zero; visForm = null; f.Close();
        }
    }

    void ShowMessage(string s) { message = s; messageFrames = 200; }

    /// <summary>Full screen has no status bar: messages are drawn across the top of the picture instead (5x7 font, x2).</summary>
    static int[] WithMessage(int[] px, int w, int h, string s)
    {
        var o = (int[])px.Clone();
        int sc = 2, cw = 6 * sc, bh = 7 * sc + 8, bw = Math.Min(w, s.Length * cw + 12);
        for (int y = 0; y < bh && y < h; y++) for (int x = 0; x < bw; x++) o[y * w + x] = 0x101828;
        int cx = 6;
        foreach (char c0 in s.ToUpperInvariant())
        {
            if (cx + cw > w) break;
            char c = c0;
            int g = c >= '@' && c <= '_' ? c - 64 : c >= ' ' && c <= '?' ? c : 63;
            if (c != ' ')
                for (int r = 0; r < 7; r++)
                {
                    int bits = Font6847.F[g, r];
                    for (int i = 0; i < 5; i++)
                        if (((bits >> (4 - i)) & 1) != 0)
                            for (int dy = 0; dy < sc; dy++) for (int dx = 0; dx < sc; dx++) { int yy = 4 + r * sc + dy, xx = cx + i * sc + dx; if (yy < h && xx < w) o[yy * w + xx] = 0xFFC040; }
                }
            cx += cw;
        }
        return o;
    }

    // ------------------------------------------------------------------ the mouse: captured, it sends relative motion
    volatile bool mouseCaptured; Point mouseCentre; int mAccX, mAccY; bool mLastL, mLastR;
    void CaptureMouse()
    {
        var r = display.RectangleToScreen(display.ClientRectangle);
        mouseCentre = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        Cursor.Position = mouseCentre; Cursor.Clip = r; Cursor.Hide();
        mouseCaptured = true; mAccX = mAccY = 0;
        ShowMessage("Mouse captured: middle button (or switching windows) lets it go");
    }
    void ReleaseMouse()
    {
        if (!mouseCaptured) return;
        mouseCaptured = false;
        try { Cursor.Clip = Rectangle.Empty; Cursor.Show(); } catch { }
    }
    void PollMouse()
    {
        if (!mouseCaptured || !m.Mouse.On) return;
        var p = Cursor.Position;
        mAccX += p.X - mouseCentre.X; mAccY += p.Y - mouseCentre.Y;
        if (p != mouseCentre) Cursor.Position = mouseCentre;
        bool l = Native.KeyDown(0x01), r = Native.KeyDown(0x02);     // VK_LBUTTON / VK_RBUTTON (async: this is the emulation thread)
        if ((mAccX != 0 || mAccY != 0 || l != mLastL || r != mLastR) && !m.Mouse.Busy(m.Cpu.Cyc))
        {
            int dx = Math.Max(-127, Math.Min(127, mAccX)), dy = Math.Max(-127, Math.Min(127, mAccY));
            m.Mouse.Packet(dx, dy, l, r, m.Cpu.Cyc);
            mAccX -= dx; mAccY -= dy; mLastL = l; mLastR = r;
        }
    }

    void UpdateStatus()
    {
        stLeft.Text = messageFrames > 0 ? message : ConfigText();
        string st = "";
        var f = m.Fdc;
        if (f.Busy) st += (char)('A' + f.Drive) + ": " + f.HeadCyl[f.Drive] + "/" + f.Side + "  ";
        var v = m.Video;
        st += v.R[1] + "x" + (v.R[6] & 127) + " " + (v.DoneFrameT > 0 ? (BeeMachine.CPU_HZ / (double)v.DoneFrameT).ToString("0.0") : "--") + " Hz  ";
        if (m.Mouse.On) st += mouseCaptured ? "MOUSE  " : "mouse: click to capture  ";
        stRight.Text = st + (paused ? "PAUSED  " : "") + (warp ? "WARP  " : "") + (muted ? "MUTE  " : "") + fps.ToString("0") + " fps";
        if (frameW != lastW || frameH != lastH) { lastW = frameW; lastH = frameH; if (!fullscreen) ApplyScale(); }
    }

    // ------------------------------------------------------------------ input
    void K(string k) { m.SetKey(k, true); }

    void PollInput()
    {
        bool typing = m.Typing;
        if (!typing) m.ClearKeys();
        int joy = 0xFF;
        bool focused = Native.GetForegroundWindow() == formHwnd;
        if (focused && !typing)
        {
            bool fkey = false;
            for (int i = 4; i < 12; i++) if (Native.KeyDown(0x70 + i)) fkey = true;     // F5-F12 belong to the emulator
            for (int i = 0; i < 26; i++) if (Native.KeyDown('A' + i)) K(((char)('A' + i)).ToString());
            for (int i = 0; i < 10; i++) if (Native.KeyDown('0' + i)) K(((char)('0' + i)).ToString());
            if (Native.KeyDown(0x20)) K("SPACE");
            if (Native.KeyDown(0x0D)) K("RET");
            if (Native.KeyDown(0x08)) K("BS");
            if (Native.KeyDown(0x2E)) K("DEL");
            if (Native.KeyDown(0x1B)) K("ESC");
            if (Native.KeyDown(0x09)) K("TAB");
            if (Native.KeyDown(0x14)) K("LOCK");
            if (Native.KeyDown(0x24)) K("LF");
            if (Native.KeyDown(0x23)) K("BREAK");
            if (!fkey && (Native.KeyDown(0xA0) || Native.KeyDown(0xA1))) K("SHIFT");
            if (!fkey && (Native.KeyDown(0xA2) || (!arrowsJoy && Native.KeyDown(0xA3)))) K("CTRL");
            if (Native.KeyDown(0xBC)) K(",");
            if (Native.KeyDown(0xBE)) K(".");
            if (Native.KeyDown(0xBD)) K("-");
            if (Native.KeyDown(0xBA)) K(";");
            if (Native.KeyDown(0xBF)) K("/");
            bool tc = m.Model.NewKb;
            if (tc)
            {
                // the 256TC's own keyboard: F1-F4 (F5-F12 belong to the emulator), its quote / equals / backquote keys,
                // Insert, Alt and the numeric keypad
                for (int i = 0; i < 4; i++) if (Native.KeyDown(0x70 + i)) K("F" + (i + 1));
                if (Native.KeyDown(0xDE)) K("'");
                if (Native.KeyDown(0xBB)) K("=");
                if (Native.KeyDown(0xC0)) K("`");
                if (Native.KeyDown(0x2D)) K("INS");
                if (Native.KeyDown(0xA4) || Native.KeyDown(0xA5)) K("ALT");
                for (int i = 0; i < 10; i++) if (Native.KeyDown(0x60 + i)) K("KP" + i);
                if (Native.KeyDown(0x6A)) K("KP*"); if (Native.KeyDown(0x6B)) K("KP+"); if (Native.KeyDown(0x6D)) K("KP-");
                if (Native.KeyDown(0x6E)) K("KPDEL"); if (Native.KeyDown(0x6F)) K("KP/");
            }
            else
            {
                if (Native.KeyDown(0xDE)) K(":");
                if (Native.KeyDown(0xBB)) K("^");
                if (Native.KeyDown(0xC0)) K("@");
            }
            if (Native.KeyDown(0xDB)) K("[");
            if (Native.KeyDown(0xDD)) K("]");
            if (Native.KeyDown(0xDC)) K("\\");
            bool up = Native.KeyDown(0x26), dn = Native.KeyDown(0x28), lf = Native.KeyDown(0x25), rt = Native.KeyDown(0x27);
            if (arrowsJoy)
            {
                if (up) joy &= ~1; if (dn) joy &= ~2; if (lf) joy &= ~4; if (rt) joy &= ~8;
                if (Native.KeyDown(0xA3)) joy &= ~0x80;
            }
            else { if (up) K("UP"); if (dn) K("DOWN"); if (lf) K("LEFT"); if (rt) K("RIGHT"); }
        }
        for (int n = 0; n < 2; n++)
        {
            int bits, arm;
            Joysticks.Read(n, out bits, out arm);
            if ((bits & 1) == 0) joy &= ~1; if ((bits & 2) == 0) joy &= ~2; if ((bits & 4) == 0) joy &= ~4; if ((bits & 8) == 0) joy &= ~8;
            if ((bits & 16) == 0) joy &= ~0x80; if ((arm & 16) == 0) joy &= ~0x10;
        }
        m.Joy = joy;
    }

    // ------------------------------------------------------------------ emulation thread
    void EmuLoop()
    {
        Native.timeBeginPeriod(1);
        WaveOut wave = null;
        try { wave = new WaveOut(BeeMachine.RATE); } catch { wave = null; }
        var sw = Stopwatch.StartNew();
        double nextT = 0; int fpsFrames = 0; double fpsT = 0;
        try
        {
            while (running)
            {
                Action c;
                while (cmds.TryDequeue(out c)) { try { c(); } catch (Exception ex) { ShowMessage(ex.Message); } }
                if (messageFrames > 0) messageFrames--;
                if (paused) { Thread.Sleep(20); nextT = sw.Elapsed.TotalMilliseconds; BlitLatest(); continue; }

                PollInput();
                PollMouse();
                m.AudioOn = !warp && wave != null && wave.Ok;
                long t0 = m.Cpu.Cyc;
                m.RunFrame();
                long fieldT = Math.Max(1000, m.Cpu.Cyc - t0);
                if (vis.T != null && visOn) { vis.MonoTint = monoTint; vis.Brown = brown; vis.Render(); }

                int w, h; int[] px = m.Snapshot(view, monoTint, frameMode, aspect, brown, out w, out h);
                if (fullscreen && messageFrames > 0 && !string.IsNullOrEmpty(message)) px = WithMessage(px, w, h, message);
                lock (frameLock) { frame = px; frameW = w; frameH = h; Present(px, w, h); }

                fpsFrames++;
                double now = sw.Elapsed.TotalMilliseconds;
                if (now - fpsT >= 1000) { fps = fpsFrames * 1000.0 / (now - fpsT); fpsFrames = 0; fpsT = now; }

                if (warp) { nextT = now; continue; }
                double frameMs = 1000.0 * fieldT / BeeMachine.CPU_HZ;
                if (m.AudioOn)
                {
                    if (muted) for (int i = 0; i < m.AudioN; i++) m.Audio[i] = 0;
                    wave.Write(m.Audio, m.AudioN);
                    int guard = 0;
                    while (running && wave.Queued > 3 && guard++ < 100) Thread.Sleep(1);
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
        }
        finally
        {
            if (wave != null) wave.Dispose();
            Native.timeEndPeriod(1);
        }
    }

    // ------------------------------------------------------------------ settings
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
            catch { }
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
        catch { }
    }
}

/// <summary>The visualiser's own window (used when the emulator isn't fullscreen).</summary>
sealed class VisForm : Form
{
    public readonly DisplayPanel Panel = new DisplayPanel();
    public Action NextPage;
    public VisForm(Form owner)
    {
        Text = "uBonPC visualiser";
        try { Icon = owner.Icon; } catch { }
        Panel.Dock = DockStyle.Fill;
        Controls.Add(Panel);
        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.F10 && e.Shift) { if (NextPage != null) NextPage(); }
            else if (e.KeyCode == Keys.F10 || e.KeyCode == Keys.Escape) Close();
        };
    }
}
