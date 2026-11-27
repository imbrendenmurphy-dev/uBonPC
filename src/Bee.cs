// uBonPC machine: the Microbee range, centred on the Premium Plus (1 MB, 32K colour PCG, RTC, WD2793 floppy).
// Memory banking, the video RAM windows, the I/O ports, keyboard, speaker, interrupts and the run loop.
// Hardware references: ubee512 6.0 (memmap.c, vdu.c, crtc.c, keystd.c) and MAME's mbee driver.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class BeeModel
{
    public string Id, Name; public int RamK; public bool Premium, Colour, Dram, Rtc, Fdc, NewKb; public int PcgBanks;
    public string Rom1, PioB7 = "pup"; public bool RomModel, Sram56;
    public BeeModel(string id, string name, int ram, bool prem, bool col, int pcg, bool dram, bool rtc, bool fdc, string rom, string b7)
    { Id = id; Name = name; RamK = ram; Premium = prem; Colour = col; PcgBanks = pcg; Dram = dram; Rtc = rtc; Fdc = fdc; Rom1 = rom; PioB7 = b7; }

    public static readonly BeeModel[] All = {
        // PIO B7 is the frame signal on the Premium Plus: Empires' MOUSE.COM sees it toggling on the real machine
        new BeeModel("pp",     "Premium Plus (1 MB, 32K PCG)",       1024, true,  true,  16, true,  true,  true,  "pp.bin",   "vsync"),   // its own ROM (BN60 2.03 rebuilt, 2012); falls back to bn60.rom
        new BeeModel("p512k",  "Premium 512K",                         512, true,  true,  16, true,  true,  true,  "bn60.rom", "rtc"),
        new BeeModel("p256k",  "Premium 256K",                         256, true,  true,  16, true,  true,  true,  "bn60.rom", "rtc"),
        new BeeModel("p128k",  "Premium 128K",                         128, true,  true,   8, true,  false, true,  "bn60.rom", "pup"),
        new BeeModel("p64k",   "Premium 64K",                           64, true,  true,   8, true,  false, true,  "bn60.rom", "pup"),
        new BeeModel("256tc",  "256TC Telecomputer",                   256, true,  true,   8, true,  true,  true,  "256tc.rom", "rtc") { NewKb = true },
        new BeeModel("1024k",  "Standard 1024K (monochrome)",         1024, false, false,  1, true,  true,  true,  "bn60.rom", "rtc"),
        new BeeModel("128k",   "Standard 128K (monochrome)",           128, false, false,  1, true,  false, true,  "bn60.rom", "pup"),
        new BeeModel("56k",    "56K disk (monochrome)",                 56, false, false,  1, false, false, true,  "56k.rom",  "pup") { Sram56 = true },
        new BeeModel("ppc85",  "Premium PC85 (ROM BASIC, PAKs, Telcom)", 32, true,  true,   8, false, false, false, "premium_pc85_a.rom", "vsync") { RomModel = true },
        new BeeModel("pc85",   "PC85 (ROM BASIC, colour board)",        32, false, true,   1, false, false, false, "basic.rom", "vsync") { RomModel = true },
        new BeeModel("ic",     "32K IC (ROM BASIC, monochrome)",        32, false, false,  1, false, false, false, "basic.rom", "pup") { RomModel = true },
    };
    public static BeeModel Find(string id) { foreach (var x in All) if (string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) return x; return null; }
}

public sealed class BeeMachine
{
    public const long CPU_HZ = 3375000;
    public readonly Z80 Cpu = new Z80();
    public readonly BeeVideo Video;
    public readonly Z80Pio Pio = new Z80Pio();
    public readonly Rtc146818 Rtc = new Rtc146818();
    public readonly BeeFdc Fdc = new BeeFdc(CPU_HZ);
    public readonly DiskSound DiskSnd = new DiskSound(CPU_HZ);     // floppy drive sounds (off unless switched on)
    public readonly BeeMouse Mouse = new BeeMouse();
    public BeeModel Model = BeeModel.All[0];
    public BeeTrace Tr;
    public Action<string> Log = s => { };
    public bool Quiet;

    // ---- memory
    public byte[] Ram = new byte[1024 * 1024];
    public byte[] BasicA = new byte[0x4000], BasicB = new byte[0x2000];
    public int NetBank;
    public byte[] Rom1 = new byte[0x4000], Rom2 = new byte[0x4000], Rom3 = new byte[0x2000], Net = new byte[0x2000];
    public byte[][] Pak = new byte[8][];
    public byte[] Scr = new byte[0x800], Attr = new byte[0x800], Col = new byte[0x800], Pcg = new byte[16 * 0x800];
    public byte[] CharRom = new byte[0x1000];
    public string RomName = "", CharRomName = "";
    public bool HasRom;
    public static string RomDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "roms");
    public static string SysDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "system");
    public string RomOverride;      // a boot ROM chosen by the user

    // ---- banking / video state
    public int Port50, Port08, Port1C, Port0A;
    public bool Latch0B;
    public int LowBlock; int lowOfs;
    public bool RomOn = true, VidOn = true; public int VidBase = 0xF000; public bool Rom3Sel;
    public bool Premium { get { return Model.Premium; } }
    public bool Colour = true;
    public bool HalfIntensity = true;
    public int PcgBanks { get { return Model.PcgBanks; } }
    public bool Extended { get { return Model.Premium && (Port1C & 0x80) != 0; } }
    public int PcgWindowBank { get { return Extended ? Port1C & 15 : 0; } }
    public bool FlashPhase { get { return (Video.FrameCount & 16) != 0; } }
    public int FieldCount { get { return Video.FrameCount; } }

    // ---- keyboard and joystick
    public readonly bool[] KeyDown = new bool[64];
    public int Joy = 0xFF;                    // PIO port A, active low: 1 up 2 down 4 left 8 right 80 fire
    public readonly Queue<int> NewKbQ = new Queue<int>(); bool[] newKbWas = new bool[128]; bool newKbStrobe;

    // ---- sound
    public const int RATE = 44100;
    public short[] Audio = new short[8192]; public int AudioN;
    public bool AudioOn = true; public int Volume = 9000;
    public Action<short> SampleHook;
    public Action<long, int> SoundHook;
    public bool Speaker; public int SpeakerToggles;

    // ---- run state
    public long Frames;            // emulator fields (one per CRTC frame, within limits)
    public int CurPc;
    public Func<bool> PreStep;
    public Action<int, int> WriteHook;
    public Action<int, int, bool> PortHook;
    public long FieldStartT;
    public int IntsTaken;

    public BeeMachine()
    {
        Video = new BeeVideo(this);
        Cpu.Rd = Rd; Cpu.Wr = Wr; Cpu.In = In; Cpu.Out = Out;
        // The PIO lets INT go as soon as it is acknowledged (and looks again at RETI), not at the next line event:
        // a handler shorter than a line would otherwise take a second, vectorless interrupt.
        Cpu.IntAck = () => { Cpu.IntData = Pio.Ack(); IntsTaken++; if (Mouse.Mode == MouseMode.Clocked && Pio.B.InService) { Mouse.ClockAck(); UpdateInt(); } Cpu.IntLine = Pio.Int; };
        Cpu.Reti = () => { Pio.Reti(); Cpu.IntLine = Pio.Int; };
        Fdc.Snd = DiskSnd;
        Fdc.Log = s => Log(s + " (PC " + Cpu.PC.ToString("X4") + ", line " + Video.Pl + ", char " + Video.Hc + ")");
    }

    public long NowT { get { return Cpu.Cyc; } }
    long AccT { get { return Cpu.Cyc - 3; } }            // a bus access: about 3 T before the instruction ends

    // ================================================================ configuration / power
    public void SetModel(BeeModel md) { Model = md; Colour = md.Colour; }

    // ubee512-style ROM names and the names MAME's romset uses for the same dumps (roms\ may hold either; MAME's sets
    // sit in a folder per machine: roms\mbee256\, roms\mbeeppc\ ...)
    static readonly Dictionary<string, string> MameName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "256tc_boot_1.20.rom", "256tc_boot_1.20.u38" }, { "256tc_boot_1.15.rom", "256tc_boot_1.15.u38" },
        { "character_4k_256tc_1.20.rom", "char256.u53" }, { "character_4k_256tc_1.15.rom", "char256.u53" },
        { "pp.rom", "pp.bin" }, { "56k.rom", "56kb.rom" },
        { "premium_pc85_a.rom", "bas529a.rom" }, { "premium_pc85_b.rom", "bas529b.rom" }, { "premium_pc85_c.rom", "wbee13r3.mbp" },
        { "premium_pc85_d.rom", "telcom321a.mbn" }, { "premium_pc85_e.rom", "help2.mbp" }, { "premium_pc85_f.rom", "busy-p.mbp" },
        { "premium_pc85_g.rom", "graphics.mbp" }, { "premium_pc85_h.rom", "vtex235.mbp" }, { "premium_pc85_i.rom", "ppcshell.mbp" },
    };

    /// <summary>Finds a ROM file: the name as given, in roms\, in a folder under roms\ (MAME's layout), or under MAME's name.</summary>
    string FindRom(string name)
    {
        if (name == null) return null;
        if (File.Exists(name)) return name;
        name = Path.GetFileName(name);           // a path that isn't there: look for the file by name in roms\
        foreach (var n in new[] { name, MameName.ContainsKey(name) ? MameName[name] : null })
        {
            if (n == null) continue;
            string p = Path.Combine(RomDir, n);
            if (File.Exists(p)) return p;
            if (Directory.Exists(RomDir))
                foreach (var d in Directory.GetDirectories(RomDir)) { p = Path.Combine(d, n); if (File.Exists(p)) return p; }
        }
        return null;
    }

    byte[] LoadRomFile(string name, int size, out string found)
    {
        found = null;
        string c = FindRom(name);
        if (c == null) return null;
        var b = File.ReadAllBytes(c); var o = new byte[size]; for (int i = 0; i < size; i++) o[i] = 0xFF;
        Array.Copy(b, o, Math.Min(size, b.Length)); found = Path.GetFileName(c); return o;
    }

    /// <summary>A 16K BASIC from two 8K halves (MAME's ic5 / ic10 or a / b dumps).</summary>
    byte[] LoadRomPair(string lo, string hi, out string found)
    {
        string f1, f2; found = null;
        var a = LoadRomFile(lo, 0x2000, out f1); var b = LoadRomFile(hi, 0x2000, out f2);
        if (a == null || b == null) return null;
        var o = new byte[0x4000]; Array.Copy(a, o, 0x2000); Array.Copy(b, 0, o, 0x2000, 0x2000); found = f1 + " + " + f2; return o;
    }

    void LoadRoms()
    {
        string f;
        HasRom = false; RomName = "";
        for (int i = 0; i < Rom1.Length; i++) Rom1[i] = 0xFF;
        if (Model.Sram56)
        {
            var b = LoadRomFile(RomOverride ?? Model.Rom1, 0x2000, out f);
            if (b != null) { Array.Copy(b, Rom3, 0x2000); HasRom = true; RomName = f; }
        }
        else
        {
            var b = LoadRomFile(RomOverride ?? Model.Rom1, 0x4000, out f);
            if (b == null && Model.NewKb && RomOverride == null) foreach (var alt in new[] { "256tc_boot_1.20.rom", "256tc_boot_1.15.rom", "256tc_boot.rom" }) { b = LoadRomFile(alt, 0x4000, out f); if (b != null) break; }
            if (b == null && RomOverride == null && Model.Id == "pc85") b = LoadRomPair("bas525a.rom", "bas525b.rom", out f);
            if (b == null && RomOverride == null && Model.Id == "ic") b = LoadRomPair("bas522e.ic5", "bas522e.ic10", out f) ?? LoadRomPair("bas522d.ic5", "bas522e.ic10", out f) ?? LoadRomPair("bas524e.ic5", "bas524e.ic10", out f);
            if (b == null && Model.Dram && !Model.NewKb && RomOverride == null) foreach (var alt in new[] { "pp.rom", "p1024k.rom", "1024k.rom", "bn60.rom", "bn56.rom", "bn54.rom", "rom1.bin" }) { b = LoadRomFile(alt, 0x4000, out f); if (b != null) break; }
            if (b != null) { Rom1 = b; HasRom = true; RomName = f; }
        }
        if (Model.RomModel)
        {
            for (int i = 0; i < 8; i++) Pak[i] = null;
            for (int i = 0; i < Net.Length; i++) Net[i] = 0xFF;
            if (Model.Id == "ppc85")
            {
                // the Premium PC85 set (ubee512's PPC85_*.ROM names, or the ROM letters A-I)
                string[] pakNames = { "c", "e", "f", "g", "h", "i" };
                var a = RomOverride != null ? LoadRomFile(RomOverride, 0x4000, out f) : (LoadRomFile("premium_pc85_a.rom", 0x4000, out f) ?? LoadRomFile("ppc85_a.rom", 0x4000, out f));
                if (a != null) { BasicA = a; HasRom = true; RomName = f; }
                var b2 = LoadRomFile("premium_pc85_b.rom", 0x2000, out f) ?? LoadRomFile("ppc85_b.rom", 0x2000, out f);
                if (b2 != null) BasicB = b2;
                for (int i = 0; i < 6; i++) Pak[i] = LoadRomFile("premium_pc85_" + pakNames[i] + ".rom", 0x4000, out f) ?? LoadRomFile("ppc85_pak" + i + ".rom", 0x4000, out f);
                var n = LoadRomFile("premium_pc85_d.rom", 0x2000, out f) ?? LoadRomFile("ppc85_network.rom", 0x2000, out f);
                if (n != null) Net = n;
            }
            else
            {
                for (int i = 0; i < 8; i++) Pak[i] = LoadRomFile("pak" + i + ".rom", 0x4000, out f);
                var n = LoadRomFile("net.rom", 0x2000, out f); if (n != null) Net = n;
                // MAME's sets: the PC85 has Wordbee in slot 0, Help in 1 and the Shell (its menu) in 5; the IC has
                // Wordbee and Help, and the 4K Telcom 1.0
                if (Model.Id == "pc85")
                {
                    if (Pak[0] == null) Pak[0] = LoadRomFile("wbee13r3.mbp", 0x4000, out f);
                    if (Pak[1] == null) Pak[1] = LoadRomFile("help2.mbp", 0x4000, out f);
                    if (Pak[5] == null) Pak[5] = LoadRomFile("shell.mbp", 0x4000, out f);
                    if (n == null) { n = LoadRomFile("telcom321a.mbn", 0x2000, out f); if (n != null) Net = n; }
                }
                else if (Model.Id == "ic")
                {
                    if (Pak[0] == null) Pak[0] = LoadRomFile("wbee12.mbp", 0x4000, out f);
                    if (Pak[1] == null) Pak[1] = LoadRomFile("help1.mbp", 0x4000, out f);
                    if (n == null) { n = LoadRomFile("telcom10.mbn", 0x2000, out f); if (n != null) { Array.Copy(n, 0, n, 0x1000, 0x1000); Net = n; } }
                }
            }
        }
        byte[] cr = null;
        if (Model.NewKb) cr = LoadRomFile(RomName.Contains("1.15") ? "character_4k_256tc_1.15.rom" : "character_4k_256tc_1.20.rom", 0x1000, out f) ?? LoadRomFile("character_4k_256tc_1.15.rom", 0x1000, out f);
        if (cr == null) cr = LoadRomFile("charrom.bin", 0x1000, out f) ?? LoadRomFile("char.rom", 0x1000, out f);
        if (cr != null) { CharRom = cr; CharRomName = f; }
        else { CharRom = BuiltinFont(); CharRomName = "built-in"; }
    }

    static byte[] BuiltinFont()
    {
        // a rough 8x16 font from the visualiser's 5x7 glyphs, used only when roms\charrom.bin is missing
        var o = new byte[0x1000];
        for (int ch = 32; ch < 128; ch++)
        {
            char c = (char)ch; if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            int g = c >= '@' && c <= '_' ? c - 64 : c >= ' ' && c <= '?' ? c : 63;
            for (int r = 0; r < 7; r++) { int bits = Font6847.F[g, r] << 2; o[ch * 16 + 3 + r] = (byte)bits; o[0x800 + ch * 16 + 3 + r] = (byte)bits; }
        }
        return o;
    }

    /// <summary>Power on: RAM in its power-up pattern, everything reset, the CPU at the boot ROM.</summary>
    public void PowerOn(int seed = -1)
    {
        LoadRoms();
        var rnd = seed >= 0 ? new Random(seed) : null;
        for (int i = 0; i < Ram.Length; i++) Ram[i] = rnd != null ? (byte)rnd.Next(256) : (byte)(((i >> 7) & 1) != 0 ? 0xFF : 0x00);
        for (int i = 0; i < 0x800; i++) { Scr[i] = 0x20; Attr[i] = 0; Col[i] = 0x02; }
        for (int i = 0; i < Pcg.Length; i++) Pcg[i] = 0;
        DateTime start = Environment.GetEnvironmentVariable("UBEE_RTC") == "fixed" ? new DateTime(2026, 1, 1, 12, 0, 0) : DateTime.Now;
        Rtc.PowerOn(start, Cpu.Cyc, CPU_HZ);
        for (int i = 0; i < 64; i++) KeyDown[i] = false;
        Reset();
        Frames = 0;
    }

    /// <summary>The reset button: the CPU, PIO, CRTC, FDC and the bank latch reset; memory is kept.</summary>
    public void Reset()
    {
        Cpu.Reset();
        Cpu.PC = Model.Sram56 ? 0xE000 : 0x8000;
        Port50 = 0; Port08 = 0; Port1C = 0; Port0A = 0; Latch0B = false;
        MapConfigure();
        Pio.Reset(); Fdc.Reset(); Mouse.Reset(); DiskSnd.Reset();
        Video.Reset(Cpu.Cyc);
        NewKbQ.Clear(); newKbStrobe = false; Array.Clear(newKbWas, 0, newKbWas.Length); Array.Clear(TcDown, 0, TcDown.Length);
        FieldStartT = Cpu.Cyc; audT = Cpu.Cyc;
        typeQueue.Clear(); typing = null; waitText = null;
        UpdateInt();
    }

    // ================================================================ memory
    void MapConfigure()
    {
        int p = Port50;
        RomOn = (p & 4) == 0; VidOn = (p & 8) == 0; VidBase = (p & 0x10) != 0 ? 0x8000 : 0xF000; Rom3Sel = (p & 0x20) != 0;
        if (!Model.Dram) { LowBlock = 1; lowOfs = 0x8000; RomOn = true; VidOn = true; VidBase = 0xF000; return; }
        int bs;
        switch (Model.RamK)
        {
            case 1024: bs = ((p & 0xE0) >> 3) | (p & 3); break;
            case 512: bs = ((p & 0xC0) >> 4) | (p & 3); break;
            case 256: bs = Model.NewKb ? ((p & 0x20) >> 3) | (p & 3) : ((p & 0x40) >> 4) | (p & 3); break;
            default: bs = p & 3; break;
        }
        if ((p & 4) != 0) bs ^= 2;                       // no ROMs: DRAM select bit 1 is inverted
        bs &= Model.RamK / 32 - 1;
        LowBlock = bs; lowOfs = bs * 0x8000;
    }

    bool NoRom23 { get { return Model.Id == "pp" || Model.Id == "256tc" || Model.Id == "1024k"; } }

    public int Rd(int a)
    {
        if (Tr != null) Tr.R(a);
        if (VidOn && (a & 0xF000) == VidBase) return VidRead(a, false);
        return RdMem(a);
    }
    int RdMem(int a)
    {
        if (!Model.Dram)
        {
            if (Model.Sram56) return a >= 0xE000 ? Rom3[a & 0x1FFF] : Ram[a];
            if (a < 0x8000) return Ram[a];
            if (Model.Id == "ppc85")
            {
                if (a < 0xA000) return BasicA[((Port1C & 0x20) != 0 ? 0x2000 : 0) | (a & 0x1FFF)];   // port 1C bit 5 banks BASIC
                if (a < 0xC000) return BasicB[a & 0x1FFF];
            }
            else if (a < 0xC000) return Rom1[a & 0x3FFF];
            if (a < 0xE000) { var pk = Pak[Port0A & 7]; return pk != null ? pk[(a & 0x1FFF) | ((Port0A & 8) != 0 ? 0x2000 : 0)] : 0xFF; }
            return Net[(NetBank << 12 | (a & 0xFFF)) & 0x1FFF];
        }
        if (a < 0x8000) return Ram[lowOfs + a];
        if (RomOn)
        {
            if (a < 0xC000) return Rom1[a & 0x3FFF];
            if (!NoRom23) { if (Rom3Sel) return a >= 0xE000 ? Rom3[a & 0x1FFF] : 0xFF; return Rom2[a & 0x3FFF]; }
        }
        return Ram[a & 0x7FFF];
    }
    /// <summary>Reads memory as the CPU would, without side effects.</summary>
    public int Peek(int a)
    {
        a &= 0xFFFF;
        if (VidOn && (a & 0xF000) == VidBase) return VidRead(a, true);
        return RdMem(a);
    }
    public int PeekBlock(int block, int a) { return Ram[(block * 0x8000 + (a & 0x7FFF)) & (Ram.Length - 1)]; }

    public void Wr(int a, int v)
    {
        if (Tr != null) Tr.W(a, v);
        if (WriteHook != null) WriteHook(a, v);
        if (VidOn && (a & 0xF000) == VidBase) { VidWrite(a, v); return; }
        if (!Model.Dram)
        {
            if (Model.Sram56) { if (a < 0xE000) Ram[a] = (byte)v; return; }
            if (a < 0x8000) Ram[a] = (byte)v;
            return;
        }
        if (a < 0x8000) { Ram[lowOfs + a] = (byte)v; return; }
        if (RomOn && (a < 0xC000 || !NoRom23)) return;     // writes to ROM are ignored
        Ram[a & 0x7FFF] = (byte)v;
    }

    // ---- video RAM wait states: the Premium holds the CPU while a line's picture is drawn
    public bool VramWait = true;
    public long WaitT, WaitTField, WaitAccesses;
    void VideoAccess()
    {
        Video.CatchUp(AccT);
        if (!VramWait) return;
        int w = Video.WaitFor();
        if (w <= 0) return;
        if (Tr != null) Tr.Wait(AccT, w);
        Cpu.Cyc += w; WaitT += w; WaitTField += w; WaitAccesses++;
        Video.CatchUp(AccT);
    }

    int VidRead(int a, bool peek)
    {
        if (!peek) { VideoAccess(); Video.NoteAccess(false); if (Tr != null) Tr.Vid(a, false); }
        int o = a & 0x7FF;
        if ((a & 0x800) != 0)
        {
            if (Colour && (Port08 & 0x40) != 0) return Col[o];
            int bank = PcgWindowBank;
            return bank < PcgBanks ? Pcg[(bank << 11) | o] : 0xFF;
        }
        if (Latch0B) return CharRom[((Video.StartAddr & 0x2000) != 0 && CharRom.Length > 0x800 ? 0x800 : 0) + o];
        if (Premium && (Port1C & 0x10) != 0) return Attr[o];
        return Scr[o];
    }

    void VidWrite(int a, int v)
    {
        VideoAccess(); Video.NoteAccess(true);
        if (Tr != null) Tr.Vid(a, true);
        int o = a & 0x7FF;
        if ((a & 0x800) != 0)
        {
            if (Colour && (Port08 & 0x40) != 0) { Col[o] = (byte)v; return; }
            int bank = PcgWindowBank;
            if (bank < PcgBanks) { Pcg[(bank << 11) | o] = (byte)v; if (Tr != null) Tr.PcgWrite(bank, o); }
            return;
        }
        if (Latch0B) return;
        if (Premium && (Port1C & 0x10) != 0) { if ((Port1C & 0x80) != 0) Attr[o] = (byte)v; return; }
        Scr[o] = (byte)v;
    }

    // ================================================================ I/O
    public int In(int port)
    {
        int p = port & 0xFF;
        int v = InPort(port, p);
        if (Tr != null) Tr.Port(p, v, false);
        if (PortHook != null) PortHook(p, v, false);
        return v;
    }

    int InPort(int port, int p)
    {
        long t = AccT;
        if (Model.RomModel && !Premium && p >= 0x10 && p < 0x20) p &= 0x0F;   // ROM models mirror 00-0F at 10-1F
        switch (p)
        {
            case 0x00: return Pio.A.Read(Joy);
            case 0x01: return 0xFF;
            case 0x02: return Pio.B.Read(PioBPins());
            case 0x03: return 0xFF;
            case 0x07: if (Model.Rtc) return Rtc.ReadData(t); break;
            case 0x08: return Port08;
            case 0x09: return 0xFF;
            case 0x0A: NetBank = (port >> 8) & 3; return 0xFF;    // reading 0Ah with A8-A9 picks the network ROM's 4K half
            case 0x0C: Video.CatchUp(t); return Video.Status();
            case 0x0D: Video.CatchUp(t); return Video.ReadData();
            case 0x0E: case 0x0F: return 0xFF;
            case 0x18: case 0x19: case 0x1A: case 0x1B:
                if (Model.NewKb) { int k = NewKbQ.Count > 0 ? NewKbQ.Dequeue() : 0; newKbStrobe = NewKbQ.Count > 0; UpdateInt(); return k; }
                break;
            case 0x1C: case 0x1D: case 0x1E: case 0x1F: if (Premium) return Port1C; break;
        }
        if (Model.Fdc)
        {
            if (p >= 0x40 && p <= 0x47)
            {
                int r = p & 3;
                if (r == 0) return Fdc.ReadStatus(t);
                if (r == 1) return Fdc.Track;
                if (r == 2) return Fdc.Sector;
                return Fdc.ReadData(t);
            }
            if (p >= 0x48 && p <= 0x4F) { Fdc.Sync(t); return Fdc.Port48; }
        }
        return 0xFF;
    }

    public void Out(int port, int v)
    {
        int p = port & 0xFF; v &= 0xFF;
        long t = AccT;
        if (Tr != null) Tr.Port(p, v, true);
        if (PortHook != null) PortHook(p, v, true);
        if (Model.RomModel && !Premium && p >= 0x10 && p < 0x20) p &= 0x0F;
        switch (p)
        {
            case 0x00: Pio.A.Out = v; Pio.A.Writes++; return;
            case 0x01: Pio.A.Control(v); UpdateInt(); return;
            case 0x02: Pio.B.Out = v; Pio.B.Writes++; SpeakerUpdate(t); return;
            case 0x03: Pio.B.Control(v); SpeakerUpdate(t); UpdateInt(); return;
            case 0x04: if (Model.Rtc) Rtc.WriteAddr(v); return;
            case 0x06: if (Model.Rtc) { Rtc.WriteData(v, t); UpdateInt(); } return;
            case 0x08: Video.CatchUp(t); if (Tr != null) Tr.BeamWrite(0x108, v); Port08 = v; return;
            case 0x09: return;
            case 0x0A: Port0A = v & 15; return;
            case 0x0B: Video.CatchUp(t); if (Tr != null) Tr.BeamWrite(0x10B, v); Latch0B = (v & 1) != 0; return;
            case 0x0C: Video.CatchUp(t); Video.WriteIndex(v); return;
            case 0x0D: Video.CatchUp(t); Video.WriteData(v); return;
            case 0x1C: case 0x1D: case 0x1E: case 0x1F:
                if (Premium) { Video.CatchUp(t); if (Tr != null) Tr.BeamWrite(0x11C, v); Port1C = v & 0xBF; }
                return;
        }
        if (Model.Fdc)
        {
            if (p >= 0x40 && p <= 0x47)
            {
                int r = p & 3;
                if (r == 0) Fdc.Command(v, t); else if (r == 1) Fdc.WriteTrack(v, t); else if (r == 2) Fdc.WriteSector(v, t); else Fdc.WriteData(v, t);
                return;
            }
            if (p >= 0x48 && p <= 0x4F) { Fdc.Latch(v, t); return; }
        }
        if (Model.Dram && p >= 0x50 && p <= 0x57) { Video.CatchUp(t); if (Tr != null) Tr.BeamWrite(0x150, v); Port50 = v; MapConfigure(); return; }
    }

    public int PioBPins()
    {
        int pins = 0x08;                                   // CTS held high (low = a mouse is plugged in)
        if (Mouse.On) { int mk; int mp = Mouse.Pins(Cpu.Cyc - 3, out mk); pins = (pins & ~mk) | mp; }
        if (newKbStrobe) pins |= 0x02;
        switch (Model.PioB7)
        {
            case "rtc": if (Rtc.Irq) pins |= 0x80; break;
            case "vsync": if (Video.VSync) pins |= 0x80; break;
            default: pins |= 0x80; break;
        }
        return Pio.B.Pins(pins);
    }

    public void VsyncChanged(bool on) { if (Model.PioB7 == "vsync") UpdateInt(); }

    void UpdateInt()
    {
        Pio.B.Update(PioBPins());
        Pio.A.Update(Pio.A.Pins(Joy));
        Cpu.IntLine = Pio.Int;
    }

    // ================================================================ keyboard
    public bool KeyPressed(int k) { return KeyDown[k & 63]; }

    public static readonly string[] KeyNames = {
        "@", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O",
        "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "[", "\\", "]", "^", "DEL",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ":", ";", ",", "-", ".", "/",
        "ESC", "BS", "TAB", "LF", "RET", "LOCK", "BREAK", "SPACE", "UP", "CTRL", "DOWN", "LEFT", "K60", "K61", "RIGHT", "SHIFT" };
    public static int KeyIndex(string name)
    {
        string n = name.ToUpperInvariant();
        if (n == "RETURN" || n == "ENTER" || n == "CR") return 52;
        if (n == "BACKSPACE") return 49;
        if (n == "ESCAPE") return 48;
        if (n == "BRK") return 54;
        if (n == "SPC" || n == " ") return 55;
        if (n == "COLON") return 42; if (n == "SEMICOLON") return 43; if (n == "COMMA") return 44; if (n == "MINUS") return 45; if (n == "PERIOD" || n == "DOT") return 46; if (n == "SLASH") return 47;
        if (n == "LINEFEED") return 51; if (n == "CAPS") return 53;
        for (int i = 0; i < 64; i++) if (KeyNames[i] == n) return i;
        return -1;
    }
    public bool SetKey(string name, bool down)
    {
        if (Model.NewKb) { int t = TcIndex(name); if (t < 0) return false; TcDown[t] = down; return true; }
        int k = KeyIndex(name); if (k < 0) return false; KeyDown[k] = down; return true;
    }

    /// <summary>The keys to press for an ASCII character (lower case letters unshifted, upper case with SHIFT).</summary>
    public static int[] CharKeys(char c)
    {
        const int SH = 63, CT = 57;
        if (c >= 'a' && c <= 'z') return new[] { 1 + c - 'a' };
        if (c >= 'A' && c <= 'Z') return new[] { 1 + c - 'A', SH };
        if (c >= '0' && c <= '9') return new[] { 32 + c - '0' };
        switch (c)
        {
            case ' ': return new[] { 55 };
            case '\r': case '\n': return new[] { 52 };
            case '\b': return new[] { 49 };
            case '\t': return new[] { 50 };
            case (char)27: return new[] { 48 };
            case '@': return new[] { 0 }; case '`': return new[] { 0, SH };
            case '[': return new[] { 27 }; case '{': return new[] { 27, SH };
            case '\\': return new[] { 28 }; case '|': return new[] { 28, SH };
            case ']': return new[] { 29 }; case '}': return new[] { 29, SH };
            case '^': return new[] { 30 }; case '~': return new[] { 30, SH };
            case '!': return new[] { 33, SH }; case '"': return new[] { 34, SH }; case '#': return new[] { 35, SH }; case '$': return new[] { 36, SH };
            case '%': return new[] { 37, SH }; case '&': return new[] { 38, SH }; case '\'': return new[] { 39, SH }; case '(': return new[] { 40, SH }; case ')': return new[] { 41, SH };
            case ':': return new[] { 42 }; case '*': return new[] { 42, SH };
            case ';': return new[] { 43 }; case '+': return new[] { 43, SH };
            case ',': return new[] { 44 }; case '<': return new[] { 44, SH };
            case '-': return new[] { 45 }; case '=': return new[] { 45, SH };
            case '.': return new[] { 46 }; case '>': return new[] { 46, SH };
            case '/': return new[] { 47 }; case '?': return new[] { 47, SH };
            case '_': return new[] { 31, SH };
        }
        if (c >= (char)1 && c <= (char)26) return new[] { c, CT };
        return null;
    }

    // typing (shared by the window and the headless runner): each key held 3 fields, then 3 fields up
    readonly Queue<int[]> typeQueue = new Queue<int[]>();
    int[] typing; int typePhase, typeHeld;
    string waitText; long waitUntil;
    public bool Typing { get { return typing != null || typeQueue.Count > 0 || waitText != null; } }
    public void TypeString(string s) { foreach (char c in s) { var k = Model.NewKb ? TcCharKeys(c) : CharKeys(c); if (k != null) typeQueue.Enqueue(k); } }
    // a typed key: 0-63 the matrix keyboard, 128 + scan code the 256TC keyboard
    void SetIdx(int k, bool down) { if (k >= 128) TcDown[(k - 128) & 127] = down; else KeyDown[k & 63] = down; }
    /// <summary>Types s once the screen shows prompt (within maxFields).</summary>
    public void TypeAfter(string prompt, string s, int maxFields) { waitText = prompt; waitUntil = Frames + maxFields; pendingType = s; }
    string pendingType;
    public int TypeHold = 3, TypeGap = 3;

    void TypeTick()
    {
        if (waitText != null)
        {
            if (ScreenText().Contains(waitText) || Frames >= waitUntil) { waitText = null; TypeString(pendingType); pendingType = null; }
            return;
        }
        if (typing == null && typeQueue.Count > 0) { typing = typeQueue.Dequeue(); typePhase = 0; foreach (int k in typing) SetIdx(k, true); typeHeld = 1; return; }
        if (typing == null) return;
        typePhase++;
        if (typePhase == TypeHold) foreach (int k in typing) SetIdx(k, false);
        if (typePhase >= TypeHold + TypeGap) typing = null;
    }

    // ---------------------------------------------------------------- the 256TC keyboard
    // A keyboard controller (an MK3870) scans the 256TC's own keyboard and queues each change as a scan code
    // (row * 8 + bit, + 80h when pressed), announced on PIO B1 and read from port 18h. The layout is MAME's mbee256
    // map: 15 rows of 8, with function keys F1-F12, a numeric keypad and the cursor keys.
    public readonly bool[] TcDown = new bool[128];
    public static readonly string[] TcNames = {
        "F1",  "ESC", "TAB", "BREAK", null,  "KP0",  "KPDEL", "SPACE",
        "F2",  "1",   "Q",   "A",     null,  "LOCK", "LF",    "INS",
        "F3",  "2",   "W",   "S",     "KP+", "KP2",  "KP3",   "Z",
        "F4",  "3",   "E",   "D",     "KP-", "KP5",  "KP6",   "X",
        "F5",  "4",   "R",   "F",     "KP*", "KP8",  "KP9",   "C",
        "F6",  "5",   "T",   "G",     "KP7", "KP1",  "KP4",   "V",
        "F7",  "6",   "Y",   "H",     "KP/", "DOWN", "RIGHT", "B",
        "F8",  "7",   "U",   "J",     null,  "LEFT", null,    "N",
        "F9",  "8",   "I",   "K",     null,  null,   "UP",    "M",
        "F10", "9",   "O",   "L",     null,  "BS",   "RET",   ",",
        "F11", "0",   "P",   ";",     "DEL", "`",    "\\",    ".",
        "F12", "-",   "[",   "'",     null,  "=",    "]",     "/",
        null, null, null, null, null, null, null, "SHIFT",
        null, null, null, null, null, null, null, "CTRL",
        null, null, null, null, null, null, null, "ALT" };
    const int TC_SHIFT = 12 * 8 + 7, TC_CTRL = 13 * 8 + 7;

    public static int TcIndex(string name)
    {
        string n = name.ToUpperInvariant();
        switch (n)
        {
            case "RETURN": case "ENTER": case "CR": n = "RET"; break;
            case "BACKSPACE": n = "BS"; break;
            case "ESCAPE": n = "ESC"; break;
            case "BRK": n = "BREAK"; break;
            case "SPC": case " ": n = "SPACE"; break;
            case "CAPS": n = "LOCK"; break;
            case "LINEFEED": n = "LF"; break;
            case "INSERT": n = "INS"; break;
            case "DELETE": n = "DEL"; break;
            case "COMMA": n = ","; break; case "PERIOD": case "DOT": n = "."; break; case "SLASH": n = "/"; break;
            case "SEMICOLON": n = ";"; break; case "MINUS": n = "-"; break; case "QUOTE": n = "'"; break; case "EQUALS": n = "="; break;
            // the old keyboard's names for keys the 256TC has in other places
            case ":": n = ";"; break; case "^": n = "="; break; case "@": n = "`"; break;
        }
        for (int i = 0; i < TcNames.Length; i++) if (TcNames[i] == n) return i;
        return -1;
    }

    /// <summary>The 256TC keys for an ASCII character, as 128 + scan code (US-style shifted symbols).</summary>
    public static int[] TcCharKeys(char c)
    {
        Func<string, bool, int[]> k = (name, sh) => { int i = TcIndex(name); return i < 0 ? null : sh ? new[] { 128 + i, 128 + TC_SHIFT } : new[] { 128 + i }; };
        if (c >= 'a' && c <= 'z') return k(((char)(c - 32)).ToString(), false);
        if (c >= 'A' && c <= 'Z') return k(c.ToString(), true);
        if (c >= '0' && c <= '9') return k(c.ToString(), false);
        if (c >= (char)1 && c <= (char)26 && c != '\r' && c != '\n' && c != '\b' && c != '\t') { int i = TcIndex(((char)('A' + c - 1)).ToString()); return new[] { 128 + i, 128 + TC_CTRL }; }
        switch (c)
        {
            case ' ': return k("SPACE", false);
            case '\r': case '\n': return k("RET", false);
            case '\b': return k("BS", false);
            case '\t': return k("TAB", false);
            case (char)27: return k("ESC", false);
            case (char)127: return k("DEL", false);
            case '!': return k("1", true); case '@': return k("2", true); case '#': return k("3", true); case '$': return k("4", true);
            case '%': return k("5", true); case '^': return k("6", true); case '&': return k("7", true); case '*': return k("8", true);
            case '(': return k("9", true); case ')': return k("0", true);
            case '-': return k("-", false); case '_': return k("-", true);
            case '=': return k("=", false); case '+': return k("=", true);
            case '[': return k("[", false); case '{': return k("[", true);
            case ']': return k("]", false); case '}': return k("]", true);
            case '\\': return k("\\", false); case '|': return k("\\", true);
            case ';': return k(";", false); case ':': return k(";", true);
            case '\'': return k("'", false); case '"': return k("'", true);
            case ',': return k(",", false); case '<': return k(",", true);
            case '.': return k(".", false); case '>': return k(".", true);
            case '/': return k("/", false); case '?': return k("/", true);
            case '`': return k("`", false); case '~': return k("`", true);
        }
        return null;
    }

    public void ClearKeys() { Array.Clear(KeyDown, 0, KeyDown.Length); Array.Clear(TcDown, 0, TcDown.Length); }

    void NewKbScan()
    {
        if (!Model.NewKb) return;
        for (int sc = 0; sc < 120; sc++)
            if (TcDown[sc] != newKbWas[sc]) { newKbWas[sc] = TcDown[sc]; if (NewKbQ.Count < 9) NewKbQ.Enqueue(sc | (TcDown[sc] ? 0x80 : 0)); }
        if (NewKbQ.Count > 0) { newKbStrobe = true; UpdateInt(); }
    }

    // ================================================================ sound: PIO B6 -> 44.1 kHz
    long audT; double acc, sampFrac, hp, hpPrev;
    const double SAMP_T = (double)CPU_HZ / RATE;

    void SpeakerUpdate(long t)
    {
        int pins = Pio.B.Pins(0);
        bool on = (pins & 0x40) != 0;
        if (on == Speaker) return;
        AudioTo(t);
        Speaker = on; SpeakerToggles++;
        if (SoundHook != null) SoundHook(t, on ? 1 : 0);
        if (Tr != null) Tr.Sound();
    }

    void AudioTo(long t)
    {
        if (t <= audT) return;
        double lvl0 = Speaker ? 1 : -1;
        while (true)
        {
            double need = SAMP_T - sampFrac;
            long left = t - audT;
            if (left < need) { acc += lvl0 * left; sampFrac += left; audT = t; break; }
            acc += lvl0 * need; audT += (long)Math.Ceiling(need); sampFrac = 0;
            double lvl = acc / SAMP_T; acc = 0;
            double y = 0.9986 * (hp + lvl - hpPrev); hpPrev = lvl; hp = y;
            double ds = DiskSnd.On ? DiskSnd.Sample(audT, 1.0 / RATE) : 0;
            short s = (short)Math.Max(-32768, Math.Min(32767, (y + ds) * Volume));
            if (AudioOn && AudioN < Audio.Length) Audio[AudioN++] = s;
            if (SampleHook != null) SampleHook(s);
            if (Tr != null) Tr.Sample(lvl);
        }
    }

    // ================================================================ running
    bool fieldDone;
    long mouseCheckT;
    long lastRtc;

    /// <summary>Executes one instruction (or an interrupt). Returns false at the end of a field (nothing executed).</summary>
    public bool StepOne()
    {
        long now = Cpu.Cyc;
        if (fieldDone) { fieldDone = false; return false; }
        if (now >= Video.NextEventT) { Video.CatchUp(now); LineEvent(now); if (fieldDone) return true; }
        if (Fdc.Busy && now >= Fdc.NextT) Fdc.Sync(now);
        if (Mouse.On && now >= mouseCheckT) { mouseCheckT = now + 400; if (Mouse.Busy(now)) UpdateInt(); }
        CurPc = Cpu.PC;
        if (PreStep != null && !PreStep()) return true;
        if (Tr == null) { Cpu.Step(); return true; }
        long t0 = Cpu.Cyc;
        bool intTaken = Cpu.IntLine && Cpu.IFF1 && Cpu.EiDelay == 0;
        bool halted = Cpu.Halted && !intTaken;
        Cpu.Step();
        Tr.Step(CurPc, t0, Cpu.Cyc, intTaken, halted);
        return true;
    }

    void LineEvent(long now)
    {
        if (Tr != null) Tr.PioSample(PioBPins(), Pio.A.Pins(Joy), Cpu.IntLine);
        if (Model.Rtc && now - lastRtc > 2000) { Rtc.Sync(now); lastRtc = now; UpdateInt(); }
        else if (Model.PioB7 == "vsync") UpdateInt();
        long len = now - FieldStartT;
        bool pic = Video.PictureDone;
        if ((pic && len >= 20000) || len >= 160000)
        {
            Video.PictureDone = false;
            EndField(now);
        }
        else if (pic) Video.PictureDone = false;
    }

    void EndField(long now)
    {
        AudioTo(now);
        if (Tr != null) Tr.EndFrame(now - FieldStartT);
        FieldStartT = now; Frames++; LastFieldWait = WaitTField; WaitTField = 0;
        NewKbScan();
        TypeTick();
        fieldDone = true;
    }

    public void RunFrame() { AudioN = 0; long end = Frames + 1; while (Frames < end) StepOne(); }
    public void RunFrames(long n) { long t = Frames + n; while (Frames < t) StepOne(); }

    // ================================================================ disks and programs
    public void InsertDisk(int drive, BeeDisk d) { Fdc.Disk[drive] = d; }

    /// <summary>Power-cycles the machine with the disks in place, so the boot ROM boots drive A.</summary>
    public void BootDisk() { PowerOn(); }

    /// <summary>Makes a bootable CP/M DS80 disk holding the .COM (and its companion files), boots it and types its name at A>.</summary>
    public string RunCom(string path)
    {
        var files = CompanionFiles(path);
        string sysTracks = Path.Combine(SysDir, "boot_tracks.bin"), ccp = Path.Combine(SysDir, "CCP.SYS");
        if (!File.Exists(sysTracks)) throw new Exception("system\\boot_tracks.bin is missing (the CP/M system tracks)");
        if (File.Exists(ccp) && !files.Exists(f => f.Key.ToUpperInvariant() == "CCP.SYS")) files.Insert(0, new KeyValuePair<string, byte[]>("CCP.SYS", File.ReadAllBytes(ccp)));
        string tmp = Path.Combine(Path.GetTempPath(), "uBonPC_" + Path.GetFileNameWithoutExtension(path).ToUpperInvariant() + ".DS80");
        var d = BeeDisk.BuildCpm(tmp, File.ReadAllBytes(sysTracks), files);
        d.Name = Path.GetFileName(path) + " (CP/M disk)";
        Fdc.Disk[0] = d;
        PowerOn();
        string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        TypeAfter("A>", name.ToLowerInvariant() + "\r", 50 * 20);
        var sb = new StringBuilder(); foreach (var f in files) sb.Append(f.Key + " ");
        return "CP/M disk with " + sb.ToString().Trim() + "; typing " + name + " at A>";
    }

    /// <summary>The .COM plus the data files beside it it is likely to open: files with the same name (any data extension),
    /// and .DAT / .BIN / .BNK / .OVL / .SYS / .TXT files. Build output and sources (.LST, .SYM, .ASM ...) are left out.</summary>
    static readonly string[] DataExt = { ".DAT", ".BIN", ".BNK", ".OVL", ".SYS", ".TXT", ".ME", ".HLP", ".PCG", ".MAP", ".LVL", ".SCR", ".MUS" };
    static readonly string[] NotData = { ".LST", ".SYM", ".ASM", ".Z80", ".S", ".INC", ".MAC", ".PRN", ".REL", ".HEX", ".MAP2", ".PNG", ".WAV", ".DS80", ".DS8", ".DSK", ".COM", ".PY", ".PS1", ".BAT", ".CMD", ".MD", ".LOG", ".TMP", ".BAK" };
    public static List<KeyValuePair<string, byte[]>> CompanionFiles(string com)
    {
        var l = new List<KeyValuePair<string, byte[]>>();
        string dir = Path.GetDirectoryName(Path.GetFullPath(com));
        l.Add(new KeyValuePair<string, byte[]>(Path.GetFileName(com).ToUpperInvariant(), File.ReadAllBytes(com)));
        long total = l[0].Value.Length;
        foreach (var f in Directory.GetFiles(dir))
        {
            string n = Path.GetFileName(f).ToUpperInvariant(); string e = Path.GetExtension(n);
            string stem = Path.GetFileNameWithoutExtension(n);
            if (n == Path.GetFileName(com).ToUpperInvariant()) continue;
            if (stem.Length > 8 || e.Length > 4 || stem.Contains(" ") || stem.Contains(".")) continue;
            bool sameName = stem == Path.GetFileNameWithoutExtension(com).ToUpperInvariant();
            if (Array.IndexOf(NotData, e) >= 0) continue;
            if (!sameName && Array.IndexOf(DataExt, e) < 0) continue;
            long len = new FileInfo(f).Length;
            if (total + len > 760 * 1024) continue;
            total += len;
            l.Add(new KeyValuePair<string, byte[]>(n, File.ReadAllBytes(f)));
        }
        return l;
    }

    // ================================================================ screen
    /// <summary>The screen as text, using the CRTC's current geometry.</summary>
    public string ScreenText()
    {
        int cols = Video.R[1], rows = Video.R[6], start = Video.StartAddr;
        if (cols <= 0 || rows <= 0 || cols > 128 || rows > 64) return "";
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            var line = new StringBuilder();
            for (int c = 0; c < cols; c++)
            {
                int ch = Scr[(start + r * cols + c) & 0x7FF];
                line.Append(ch >= 32 && ch < 127 ? (char)ch : ' ');
            }
            sb.Append(line.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    public enum View { Colour, Mono, Both }
    public enum Frame { Monitor, Picture, Raster }
    public long LastFieldWait;

    /// <summary>The last finished picture as RGB. Monitor = the 720 x 320 dot area a monitor shows (fixed, like the
    /// reference model); Picture = cropped to where the display was enabled; Raster = everything the beam swept,
    /// blanking grey, HSYNC blue, VSYNC purple. aspect doubles the lines (a 13.5 MHz dot is about half a line high).</summary>
    public int[] Snapshot(View view, int monoTint, Frame frame, bool aspect, bool brown, out int w, out int h)
    {
        var v = Video;
        int x0, y0, cw, ch;
        if (frame == Frame.Raster) { x0 = 0; y0 = 0; cw = Math.Max(16, v.DoneWidth); ch = Math.Max(16, v.DoneLines); }
        else if (frame == Frame.Picture && v.DonePicLines > 0 && v.DonePicWidth > 0) { x0 = v.DonePicLeft; y0 = v.DonePicTop; cw = v.DonePicWidth; ch = v.DonePicLines; }
        else { x0 = MonitorLeft(); y0 = 0; cw = BeeVideo.MONW; ch = BeeVideo.MONH; }
        if (frame != Frame.Monitor) cw = Math.Min(cw, BeeVideo.FBW - x0);
        ch = Math.Min(ch, BeeVideo.FBH - y0);
        int ym = aspect ? 2 : 1;
        int gap = view == View.Both ? 8 : 0;
        w = view == View.Both ? cw * 2 + gap : cw; h = ch * ym;
        var o = new int[w * h];
        int[] prem = BeeVideo.PremiumRgb(brown);
        int[] pal = new int[256];
        for (int i = 0; i < 16; i++) pal[i] = prem[i];
        for (int i = 0; i < 64; i++) pal[64 + i] = BeeVideo.StdRgb(i);
        bool raster = frame == Frame.Raster;
        pal[BeeVideo.BLANK] = raster ? 0x141414 : 0; pal[BeeVideo.HSYNC] = raster ? 0x102048 : 0; pal[BeeVideo.VSYNCC] = raster ? 0x301048 : 0;
        int[] mpal = new int[256];
        int[] mono = BeeVideo.MonoTables[Math.Max(0, Math.Min(2, monoTint))];
        for (int i = 0; i < 4; i++) mpal[i] = mono[i];
        mpal[BeeVideo.BLANK] = pal[BeeVideo.BLANK]; mpal[BeeVideo.HSYNC] = pal[BeeVideo.HSYNC]; mpal[BeeVideo.VSYNCC] = pal[BeeVideo.VSYNCC];
        // the columns that fall inside the framebuffer (the monitor window can start before it or run past it: black)
        int xa = Math.Max(0, -x0), xb = Math.Min(cw, BeeVideo.FBW - x0);
        for (int y = 0; y < ch; y++)
        {
            int so = (y0 + y) * BeeVideo.FBW + x0;
            for (int yy = 0; yy < ym; yy++)
            {
                int ro = (y * ym + yy) * w;
                if (view == View.Colour) for (int x = xa; x < xb; x++) o[ro + x] = pal[v.DoneC[so + x]];
                else if (view == View.Mono) for (int x = xa; x < xb; x++) o[ro + x] = mpal[v.DoneM[so + x]];
                else for (int x = xa; x < xb; x++) { o[ro + x] = pal[v.DoneC[so + x]]; o[ro + cw + gap + x] = mpal[v.DoneM[so + x]]; }
            }
        }
        return o;
    }

    /// <summary>Where the monitor's 720-dot window starts in the framebuffer. A monitor places the picture by the
    /// horizontal sync, not by where the 6545 starts its line: the window opens a fixed time (56 dots at 13.5 MHz,
    /// 4.1 us) after the end of HSYNC. That puts the standard 64 x 16 picture (R0 6Bh, R1 40h, R2 51h, 7-character sync)
    /// in the middle, as on a real monitor, and follows any change to R2, R3 or the line length the way a monitor would.
    /// (It used to start a fixed 40 dots before the display, which left most pictures well left of centre.)</summary>
    public int MonitorLeft()
    {
        var v = Video;
        int dpc = Math.Max(1, v.DoneCharT) * 4;                        // framebuffer dots per character
        int lineDots = Math.Max(16, (v.R[0] + 1) * dpc);
        int hsw = v.R[3] & 15; if (hsw == 0) hsw = 16;
        int start = (v.R[2] + hsw) * dpc + MON_AFTER_SYNC - lineDots;   // dots from the display start (usually before it)
        while (start < -lineDots / 2) start += lineDots;
        while (start > lineDots / 2) start -= lineDots;
        return BeeVideo.LEFT + start;
    }
    const int MON_AFTER_SYNC = 56;

    public string Banks()
    {
        if (!Model.Dram) return Model.Sram56 ? "56K RAM, ROM E000" : "RAM 0000-7FFF, BASIC 8000, PAK " + (Port0A & 7) + ", NET E000";
        return string.Format("50={0:X2}: block {1} at 0000, block 0 at 8000{2}{3}", Port50, LowBlock, RomOn ? ", ROM 8000-BFFF" : "", VidOn ? ", video at " + VidBase.ToString("X4") : ", video off");
    }
}
