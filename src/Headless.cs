// uBonPC headless runner (ubemu.exe): scripted tests, screenshots (colour, mono, both), WAV capture, profiling,
// CRTC / raster logs, crash checks, the visualiser as PNG. Same emulation core as the window.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class Headless
{
    readonly BeeMachine m = new BeeMachine();
    BeeVis vis;
    readonly Bezel fsBezel = new Bezel();
    Z80 cpu { get { return m.Cpu; } }
    readonly Dictionary<string, int> sym = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    bool booted = false;
    string baseDir = ".";

    bool crashCheck = false, crashed = false;
    int[] ringPc = new int[4096]; int ringPos = 0; bool ringOn;
    int tmarkAddr = -1; long tmarkLast = -1; int[] tmarkHist = new int[16];
    long[] prof = null;
    int minSp = 0xFFFF, spLo = 0x0000, spHi = 0x10000;
    List<int[]> watches = new List<int[]>();
    BinaryWriter wav; long wavN;
    StreamWriter crtcLog;
    int monoTint = 1; bool brown = true;

    public Headless()
    {
        m.AudioOn = false;
        m.Log = s => Console.Error.WriteLine(s);
        m.PreStep = PreStep;
        m.WriteHook = WriteHook;
        Environment.SetEnvironmentVariable("UBEE_RTC", Environment.GetEnvironmentVariable("UBEE_RTC") ?? "fixed");
    }

    void Boot()
    {
        if (booted) return;
        string rr = Environment.GetEnvironmentVariable("RANDRAM");
        m.PowerOn(rr != null ? int.Parse(rr) : -1);
        booted = true;
    }

    bool PreStep()
    {
        int pc = m.CurPc;
        if (ringOn || crashCheck) { ringPc[ringPos] = pc | (cpu.SP << 16); ringPos = (ringPos + 1) & 4095; }
        if (crashCheck && !crashed)
        {
            // a crash: an RST 38h storm through empty memory, or HALT with interrupts off (the machine is dead)
            bool bad = (pc == 0x38 && m.Peek(0x38) == 0xFF && m.Peek(0x37) == 0xFF) || (cpu.Halted && !cpu.IFF1);
            if (bad)
            {
                crashed = true;
                Console.Error.WriteLine("CRASH? pc={0:X4} sp={1:X4} field {2}", pc, cpu.SP, m.Frames);
                using (var w = new StreamWriter(Path.Combine(baseDir, "crash.txt")))
                    for (int i = 0; i < 4096; i++) { int k = (ringPos + i) & 4095; w.WriteLine("{0:X4} SP={1:X4} {2}", ringPc[k] & 0xFFFF, (ringPc[k] >> 16) & 0xFFFF, Near(ringPc[k] & 0xFFFF)); }
                return false;
            }
        }
        if (cpu.SP >= spLo && cpu.SP < spHi && cpu.SP < minSp) minSp = cpu.SP;
        return true;
    }

    void WriteHook(int a, int v)
    {
        if (a == tmarkAddr)
        {
            long t = cpu.Cyc;
            if (tmarkLast >= 0) { int f = (int)((t - tmarkLast + 33800) / 67608); if (f > 15) f = 15; tmarkHist[f]++; }
            tmarkLast = t;
        }
        foreach (var wr in watches)
            if (a >= wr[0] && a <= wr[1])
                Console.Error.WriteLine("write {0:X4}={1:X2} pc={2:X4} {3} field {4} line {5} char {6}", a, v & 255, m.CurPc, Near(m.CurPc), m.Frames, m.Video.Pl, m.Video.Hc);
    }

    void Step()
    {
        if (prof == null) { m.StepOne(); return; }
        int pc = cpu.PC; long c0 = cpu.Cyc;
        m.StepOne();
        prof[pc] += cpu.Cyc - c0;
    }
    void RunFrames(long n) { long t = m.Frames + n; while (m.Frames < t && !crashed) Step(); }

    string Near(int a)
    {
        string best = null; int bv = -1;
        foreach (var kv in sym) if (kv.Value <= a && kv.Value > bv && a - kv.Value < 0x800) { bv = kv.Value; best = kv.Key; }
        return best == null ? "" : best + "+" + (a - bv);
    }

    int Num(string s)
    {
        int plus = s.IndexOf('+', 1);
        if (plus > 0) return Num(s.Substring(0, plus)) + Num(s.Substring(plus + 1));
        int v; if (sym.TryGetValue(s, out v)) return v;
        if (s.StartsWith("$")) return Convert.ToInt32(s.Substring(1), 16);
        if (s.StartsWith("0x") || s.StartsWith("0X")) return Convert.ToInt32(s.Substring(2), 16);
        if ((s.EndsWith("h") || s.EndsWith("H")) && s.Length > 1) return Convert.ToInt32(s.Substring(0, s.Length - 1), 16);
        int d; if (int.TryParse(s, out d)) return d;
        return Convert.ToInt32(s, 16);
    }
    static double Dbl(string s) { return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

    void SetKey(string k, bool down)
    {
        string K = k.ToUpperInvariant();
        int bit = -1;
        switch (K) { case "JUP": bit = 1; break; case "JDOWN": bit = 2; break; case "JLEFT": bit = 4; break; case "JRIGHT": bit = 8; break; case "JFIRE": bit = 0x80; break; case "JPLAY1": bit = 0x10; break; case "JPLAY2": bit = 0x20; break; }
        if (bit > 0) { if (down) m.Joy &= ~bit; else m.Joy |= bit; return; }
        if (!m.SetKey(K, down)) Console.Error.WriteLine("unknown key " + k);
    }

    public void SaveScreen(string fn, int scale, BeeMachine.View view, BeeMachine.Frame frame, bool aspect)
    {
        int w, h; int[] px = m.Snapshot(view, monoTint, frame, aspect, brown, out w, out h);
        SavePng(fn, px, w, h, scale);
    }
    // plain PNG writing (no System.Drawing), so ubemu runs the same on Linux and macOS
    public static void SavePng(string fn, int[] src, int w, int h, int scale) { Portable.SavePng(fn, src, w, h, scale); }

    void WavStart(string fn)
    {
        wav = new BinaryWriter(File.Create(fn)); wav.Write(new byte[44]); wavN = 0;
        m.SampleHook = s => { wav.Write(s); wavN++; };
    }
    void WavEnd()
    {
        if (wav == null) return;
        m.SampleHook = null;
        var s = wav.BaseStream; long n = wavN;
        s.Seek(0, SeekOrigin.Begin);
        var w = new BinaryWriter(s);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((int)(36 + n * 2)); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write((int)(n * 2));
        w.Flush(); s.Close(); wav = null;
        Console.Error.WriteLine("wav: {0:F1} s", n / 44100.0);
    }

    string Regs()
    {
        return string.Format("field {0} line {1} char {2} PC={3:X4} {12} SP={4:X4} AF={5:X2}{6:X2} BC={7:X4} DE={8:X4} HL={9:X4} IX={10:X4} IY={11:X4} IFF1={13} IM={14} | {15}",
            m.Frames, m.Video.Pl, m.Video.Hc, cpu.PC, cpu.SP, cpu.A, cpu.F, (cpu.B << 8) | cpu.C, (cpu.D << 8) | cpu.E, (cpu.H << 8) | cpu.L, cpu.IX, cpu.IY, Near(cpu.PC), cpu.IFF1, cpu.IM, m.Banks());
    }

    public int RunScript(string path)
    {
        baseDir = Path.GetDirectoryName(Path.GetFullPath(path));
        return RunLines(File.ReadAllLines(path));
    }

    public int RunLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            string ln = raw.Trim(); if (ln.Length == 0 || ln.StartsWith(";") || ln.StartsWith("#")) continue;
            bool go;
            try { go = Exec(ln); }
            catch (Exception ex) { Console.Error.WriteLine("error in '" + ln + "': " + ex.Message); return 1; }
            if (!go) break;
            if (crashed) { Console.Error.WriteLine("stopping: crash"); break; }
        }
        if (wav != null) WavEnd();
        if (crtcLog != null) crtcLog.Close();
        Console.Error.WriteLine("done at field {0}; min SP {1:X4}; video waits {2} T in {3} accesses", m.Frames, minSp, m.WaitT, m.WaitAccesses);
        return crashed ? 2 : 0;
    }

    void VisOn()
    {
        if (vis != null) return;
        m.Tr = new BeeTrace(m); vis = new BeeVis(m) { T = m.Tr };
        foreach (var kv in sym) if (!vis.Sym.ContainsKey(kv.Value & 0xFFFF)) vis.Sym[kv.Value & 0xFFFF] = kv.Key.ToUpperInvariant();
    }

    void LoadSym(string f)
    {
        foreach (var sl in File.ReadAllLines(f))
        {
            var p = sl.Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            try
            {
                if (p.Length >= 3 && p[1].ToUpperInvariant() == "EQU") { sym[p[0].TrimEnd(':')] = Num(p[2]) & 0xFFFF; continue; }
                sym[p[1]] = Convert.ToInt32(p[0].TrimStart('$'), 16);
            }
            catch { }
        }
    }

    static readonly HashSet<string> configCmds = new HashSet<string> { "model", "machine", "rom", "romdir", "sym", "quiet", "crash", "stack", "print", "watch", "ring", "disk", "diskeject", "diskprotect", "colour", "color", "halfint", "dotclock", "vramwait", "fastdisk", "disksound", "mousemode", "mouseinv", "mono", "palette", "vis" };

    BeeMachine.View ParseView(string s, BeeMachine.View def) { s = s.ToLowerInvariant(); return s == "mono" ? BeeMachine.View.Mono : s == "both" ? BeeMachine.View.Both : s == "colour" || s == "color" ? BeeMachine.View.Colour : def; }

    /// <summary>A drive given as A-D, A: - D: or 1-4; -1 if it isn't one.</summary>
    static int DriveArg(string s)
    {
        s = (s ?? "").Trim().TrimEnd(':').ToUpperInvariant();
        if (s.Length != 1) return -1;
        char c = s[0];
        if (c >= 'A' && c < 'A' + BeeFdc.DRIVES) return c - 'A';
        if (c >= '1' && c < '1' + BeeFdc.DRIVES) return c - '1';
        return -1;
    }

    bool Exec(string ln)
    {
        var a = ln.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string cmd = a[0].ToLowerInvariant();
        string a1 = a.Length > 1 ? a[1] : "", a2 = a.Length > 2 ? a[2] : "";
        string rest = ln.Trim().Substring(a[0].Length).Trim().Trim('"');
        if (!configCmds.Contains(cmd)) Boot();
        switch (cmd)
        {
            // ---- configuration
            case "model": case "machine":
                {
                    var md = BeeModel.Find(a1);
                    if (md == null) { Console.Error.WriteLine("models: " + string.Join(" ", Array.ConvertAll(BeeModel.All, x => x.Id))); return false; }
                    m.SetModel(md); booted = false;
                }
                break;
            case "rom": m.RomOverride = a1.ToLowerInvariant() == "auto" ? null : Path.GetFullPath(rest); booted = false; break;
            case "romdir": BeeMachine.RomDir = Path.GetFullPath(rest); booted = false; break;
            case "colour": case "color": m.Colour = a1 != "off" && a1 != "0"; break;
            case "halfint": m.HalfIntensity = a1 != "off" && a1 != "0"; break;
            case "mono": monoTint = Array.IndexOf(BeeVideo.MonoNames, a1.ToLowerInvariant()); if (monoTint < 0) monoTint = 1; break;
            case "palette": brown = a1.ToLowerInvariant() != "rgbi"; break;
            case "dotclock": m.Video.SetClock(a1.StartsWith("13") ? DotClock.Mhz13_5 : a1.StartsWith("6") ? DotClock.Mhz6_75 : DotClock.Auto); break;
            case "fastdisk": m.Fdc.Fast = a1 != "off" && a1 != "0"; break;
            case "disksound": m.DiskSnd.On = a1 != "off" && a1 != "0"; if (a2 != "") m.DiskSnd.Volume = double.Parse(a2, System.Globalization.CultureInfo.InvariantCulture); break;
            case "mousemode": { int i = Array.IndexOf(BeeMouse.ModeNames, a1.ToLowerInvariant()); if (i < 0) { Console.Error.WriteLine("mousemode off|cts|rx|clocked"); return false; } m.Mouse.Mode = (MouseMode)i; } break;
            case "mouseinv": m.Mouse.Invert = a1 != "off" && a1 != "0"; break;
            case "vramwait": m.VramWait = a1 != "off" && a1 != "0"; break;
            case "sym": LoadSym(rest); break;
            case "quiet": m.Quiet = true; break;
            case "crash": crashCheck = true; break;
            case "stack": spLo = Num(a1); spHi = Num(a2); break;
            case "watch": watches.Add(new[] { Num(a1), a2 != "" ? Num(a2) : Num(a1) }); break;
            case "print": Console.WriteLine(rest); break;
            case "disk":
                {
                    int dn = 0; string f = rest;
                    if (a.Length > 2 && DriveArg(a1) >= 0) { dn = DriveArg(a1); f = rest.Substring(a1.Length).Trim().Trim('"'); }
                    var d = BeeDisk.Load(Path.GetFullPath(f));
                    m.Fdc.Disk[dn] = d;
                    Console.Error.WriteLine("disk {0}: {1} ({2})", (char)('A' + dn), d.Name, d.Format);
                }
                break;
            case "diskeject": m.Fdc.Disk[Math.Max(0, DriveArg(a1 == "" ? "A" : a1))] = null; break;
            case "diskprotect": { var d = m.Fdc.Disk[Math.Max(0, DriveArg(a1))]; if (d != null) d.WriteProtect = a2 != "off" && a2 != "0"; } break;

            // ---- running
            case "boot": m.PowerOn(); booted = true; break;
            case "com": case "run":
                {
                    string r = m.RunCom(Path.GetFullPath(rest));
                    booted = true;
                    Console.Error.WriteLine("com: " + r);
                    if (vis != null && vis.Sym.Count == 0) vis.FindSymbols(rest);
                }
                break;
            case "reset": m.Reset(); break;
            case "power": m.PowerOn(); break;
            case "frames": case "fields": RunFrames(Num(a1)); break;
            case "seconds": RunFrames((long)(Dbl(a1) * 50)); break;
            case "down": SetKey(a1, true); break;
            case "up": SetKey(a1, false); break;
            case "tap": SetKey(a1, true); RunFrames(a2 != "" ? Num(a2) : 4); SetKey(a1, false); RunFrames(4); break;
            case "type": m.TypeString(ln.Substring(4).TrimStart().Replace("\\r", "\r")); while (m.Typing && !crashed) RunFrames(1); break;
            case "typeat":
                {
                    // typeat PROMPT TEXT: wait for PROMPT on the screen (up to 60 s), then type TEXT
                    string t = a.Length > 2 ? ln.Substring(ln.IndexOf(a[2], ln.IndexOf(a1) + a1.Length)).Replace("\\r", "\r") : "";
                    m.TypeAfter(a1.Replace('_', ' '), t, 3000); while (m.Typing && !crashed) RunFrames(1);
                }
                break;
            case "release": for (int i = 0; i < 64; i++) m.KeyDown[i] = false; m.Joy = 0xFF; break;
            case "rand":
                {
                    int n = Num(a1); var rnd = new Random(a2 != "" ? Num(a2) : 1);
                    string[] ks = a.Length > 3 ? a[3].Split(',') : new[] { "SPACE", "LEFT", "RIGHT", "UP", "DOWN" };
                    for (int i = 0; i < n && !crashed; i += 8) { for (int k = 0; k < ks.Length; k++) SetKey(ks[k], rnd.Next(3) == 0 || (k == 0 && rnd.Next(4) == 0)); RunFrames(8); }
                    for (int k = 0; k < ks.Length; k++) SetKey(ks[k], false);
                }
                break;
            case "joy": m.Joy = Num(a1) & 0xFF; break;
            case "mouse":
                {
                    // mouse DX DY [L] [R]: one Microsoft mouse packet (DX/DY -128..127, Y down; L/R 1 = held)
                    int dx = int.Parse(a1), dy = a2 != "" ? int.Parse(a2) : 0;
                    m.Mouse.Packet(dx, dy, a.Length > 3 && a[3] == "1", a.Length > 4 && a[4] == "1", m.Cpu.Cyc);
                }
                break;
            case "mousemove":
                {
                    // mousemove DX DY [fields]: a smooth move, one packet per field
                    int dx = int.Parse(a1), dy = int.Parse(a2), n = a.Length > 3 ? Num(a[3]) : Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)) / 8);
                    int sx = 0, sy = 0;
                    for (int i = 1; i <= n && !crashed; i++) { int tx = dx * i / n, ty = dy * i / n; m.Mouse.Packet(tx - sx, ty - sy, false, false, m.Cpu.Cyc); sx = tx; sy = ty; RunFrames(1); }
                }
                break;
            case "mouseclick": { bool r = a1.ToLowerInvariant() == "r" || a1.ToLowerInvariant() == "right"; m.Mouse.Packet(0, 0, !r, r, m.Cpu.Cyc); RunFrames(4); m.Mouse.Packet(0, 0, false, false, m.Cpu.Cyc); RunFrames(4); } break;
            case "mousestat": Console.WriteLine("mouse {0}{1}: {2} packets, {3} dropped, last dx {4} dy {5} {6}{7}", BeeMouse.ModeNames[(int)m.Mouse.Mode], m.Mouse.Invert ? " inverted" : "", m.Mouse.Packets, m.Mouse.Dropped, m.Mouse.LastDx, m.Mouse.LastDy, m.Mouse.LastL ? "L" : "-", m.Mouse.LastR ? "R" : "-"); break;
            case "poke": m.Wr(Num(a1), Num(a2)); break;
            case "pokeb": { int b = Num(a1), ad = Num(a2); m.Ram[(b * 0x8000 + (ad & 0x7FFF)) & (m.Ram.Length - 1)] = (byte)Num(a[3]); } break;
            case "out": m.Out(Num(a1), Num(a2)); break;
            case "in": Console.WriteLine("in {0:X2} = {1:X2}", Num(a1), m.In(Num(a1))); break;
            case "bin":
                {
                    var b = File.ReadAllBytes(a1); int ad = Num(a2);
                    for (int i = 0; i < b.Length; i++) m.Wr((ad + i) & 0xFFFF, b[i]);
                    Console.WriteLine("bin: {0} bytes at {1:X4}", b.Length, ad);
                }
                break;
            case "pc": cpu.PC = Num(a1); break;
            case "sp": cpu.SP = Num(a1); break;

            // ---- inspection
            case "screen":
                {
                    // screen FILE [scale] [colour|mono|both] [monitor|picture|raster] [native]
                    int sc = 1; var view = BeeMachine.View.Colour; var fr = BeeMachine.Frame.Monitor; bool asp = true;
                    for (int i = 2; i < a.Length; i++)
                    {
                        string o = a[i].ToLowerInvariant(); int n;
                        if (int.TryParse(o, out n)) sc = n;
                        else if (o == "mono" || o == "both" || o == "colour" || o == "color") view = ParseView(o, view);
                        else if (o == "picture" || o == "crop") fr = BeeMachine.Frame.Picture;
                        else if (o == "raster" || o == "overscan" || o == "border") fr = BeeMachine.Frame.Raster;
                        else if (o == "monitor") fr = BeeMachine.Frame.Monitor;
                        else if (o == "native") asp = false;
                    }
                    SaveScreen(a1, sc, view, fr, asp);
                }
                break;
            case "shots":
                { int n = Num(a2), ev = a.Length > 3 ? Num(a[3]) : 25; for (int i = 0; i < n; i++) { RunFrames(ev); SaveScreen(a1 + i.ToString("D3") + ".png", 1, BeeMachine.View.Colour, BeeMachine.Frame.Monitor, true); } }
                break;
            case "text": Console.WriteLine(m.ScreenText()); break;
            case "regs": Console.Error.WriteLine(Regs()); break;
            case "crtc":
                {
                    var v = m.Video; var sb = new StringBuilder();
                    for (int i = 0; i < 20; i++) sb.AppendFormat("R{0}={1:X2} ", i, v.R[i]);
                    Console.WriteLine(sb.ToString());
                    Console.WriteLine("line {0} T ({1} chars x {2} T), {3} lines per CRTC frame; last picture {4} lines, {5} T ({6:F2} Hz); display {7} x {8} dots at y {9}; beam line {10} char {11} row {12} ra {13}{14}",
                        v.LineT, v.LineChars, v.CharT, v.FrameLines, v.LastPictureLines, v.DoneFrameT, v.DoneFrameT > 0 ? BeeMachine.CPU_HZ / (double)v.DoneFrameT : 0,
                        v.DonePicWidth, v.DonePicLines, v.DonePicTop, v.Pl, v.Hc, v.Row, v.Ra, v.Adj ? " (adjust)" : "");
                    Console.WriteLine("port 50={0:X2} 1C={1:X2} 08={2:X2} 0B={3}  lpen {4:X4}{5}  scan hits {6}, update tests {7}", m.Port50, m.Port1C, m.Port08, m.Latch0B ? 1 : 0, v.Lpen, v.LpenFull ? " (full)" : "", v.ScanHits, v.UpdateCycles);
                }
                break;
            case "crtclog":
                // crtclog FILE: every CRTC register write as "field line char Rn=VV"; crtclogend closes it
                crtcLog = new StreamWriter(rest);
                m.PortHook = (p, v, o) => { if (o && p == 0x0D && crtcLog != null) crtcLog.WriteLine("{0} {1} {2} R{3}={4:X2}", m.Frames, m.Video.Pl, m.Video.Hc, m.Video.Sel, v); };
                break;
            case "crtclogend": m.PortHook = null; if (crtcLog != null) crtcLog.Close(); crtcLog = null; break;
            case "peek":
                { int ad = Num(a1), n = a2 != "" ? Num(a2) : 1; var sb = new StringBuilder(); sb.AppendFormat("{0} {1:X4}:", a1, ad); for (int i = 0; i < n; i++) sb.AppendFormat(" {0:X2}", m.Peek((ad + i) & 0xFFFF)); Console.WriteLine(sb.ToString()); }
                break;
            case "peekb":
                { int b = Num(a1), ad = Num(a2), n = a.Length > 3 ? Num(a[3]) : 16; var sb = new StringBuilder(); sb.AppendFormat("block {0} {1:X4}:", b, ad); for (int i = 0; i < n; i++) sb.AppendFormat(" {0:X2}", m.PeekBlock(b, ad + i)); Console.WriteLine(sb.ToString()); }
                break;
            case "peekw": { int ad = Num(a1); Console.WriteLine("{0} = {1}", a1, m.Peek(ad) | (m.Peek((ad + 1) & 0xFFFF) << 8)); } break;
            case "dump": { var d = new byte[65536]; for (int i = 0; i < 65536; i++) d[i] = (byte)m.Peek(i); File.WriteAllBytes(a1, d); } break;
            case "dumpram": File.WriteAllBytes(a1, m.Ram); break;
            case "dumpvideo":
                {
                    var o = new MemoryStream(); o.Write(m.Scr, 0, 2048); o.Write(m.Attr, 0, 2048); o.Write(m.Col, 0, 2048); o.Write(m.Pcg, 0, m.Pcg.Length);
                    File.WriteAllBytes(a1, o.ToArray()); Console.Error.WriteLine("dumpvideo: screen, attribute, colour (2K each), PCG (32K)");
                }
                break;
            case "wav": WavStart(rest); break;
            case "wavend": WavEnd(); break;
            case "waits": Console.WriteLine("video waits {0} T in {1} accesses; last field {2} T", m.WaitT, m.WaitAccesses, m.LastFieldWait); break;
            case "waitreset": m.WaitT = m.WaitAccesses = 0; break;
            case "fdc":
                {
                    var f = m.Fdc;
                    Console.WriteLine("FDC {0} cmd {1:X2} ({2}) status {3:X2} track {4} sector {5} data {6:X2} drq {7} intrq {8}", f.StateName, f.Cmd, f.LastCmd, f.Status, f.Track, f.Sector, f.Data, f.Drq ? 1 : 0, f.Intrq ? 1 : 0);
                    Console.WriteLine("drive {0} side {1} head cyl {2}; sectors read {3} written {4} lost data {5} commands {6}", (char)('A' + f.Drive), f.Side, f.HeadCyl[f.Drive], f.SectorsRead, f.SectorsWritten, f.LostData, f.Commands);
                    for (int i = 0; i < BeeFdc.DRIVES; i++) if (f.Disk[i] != null) Console.WriteLine("drive {0}: {1} ({2}{3}{4})", (char)('A' + i), f.Disk[i].Name, f.Disk[i].Format, f.Disk[i].WriteProtect ? ", protected" : "", f.Disk[i].Dirty ? ", CHANGED" : "");
                }
                break;
            case "diskdir": { var d = m.Fdc.Disk[Math.Max(0, DriveArg(a1 == "" ? "A" : a1))]; if (d == null) Console.WriteLine("drive empty"); if (d != null) foreach (var l in d.Directory()) Console.WriteLine(l); } break;
            case "disksave":
                {
                    int dn = DriveArg(a1) >= 0 ? DriveArg(a1) : 0;
                    string f = DriveArg(a1) >= 0 ? a2 : rest;
                    var d = m.Fdc.Disk[dn];
                    if (d == null) { Console.Error.WriteLine("disksave: drive empty"); break; }
                    if (f != "") d.Save(Path.GetFullPath(f)); else d.Save();
                    Console.Error.WriteLine("disksave: " + d.Path);
                }
                break;
            case "tmark": tmarkAddr = Num(a1); tmarkLast = -1; Array.Clear(tmarkHist, 0, 16); break;
            case "tstats":
                {
                    long tot = 0; for (int i = 0; i < 16; i++) tot += tmarkHist[i];
                    var sb = new StringBuilder("fields per frame:"); for (int i = 0; i < 16; i++) if (tmarkHist[i] > 0) sb.AppendFormat("  {0}:{1:F1}%", i, 100.0 * tmarkHist[i] / Math.Max(1, tot));
                    Console.WriteLine(sb.ToString() + "  (" + tot + " frames)");
                }
                break;
            case "prof": prof = Num(a1) != 0 ? new long[65536] : null; break;
            case "profsave":
                {
                    var agg = new Dictionary<string, long>(); long tot = 0;
                    for (int i = 0; i < 65536; i++) if (prof[i] > 0) { string n = Near(i); int plus = n.IndexOf('+'); if (plus > 0) n = n.Substring(0, plus); if (n == "") n = i.ToString("X4"); long v; agg.TryGetValue(n, out v); agg[n] = v + prof[i]; tot += prof[i]; }
                    var l = new List<KeyValuePair<string, long>>(agg); l.Sort((x, y) => y.Value.CompareTo(x.Value));
                    using (var w = new StreamWriter(a1)) foreach (var kv in l) w.WriteLine("{0,8:F2}% {1,12} {2}", 100.0 * kv.Value / tot, kv.Value, kv.Key);
                }
                break;
            case "dis":
                {
                    int ad = Num(a1), n = a2 != "" ? Num(a2) : 16;
                    for (int i = 0; i < n; i++)
                    {
                        int len; string t = Z80Dis.Dis(m.Peek, ad, out len);
                        var hx = new StringBuilder(); for (int k = 0; k < len; k++) hx.Append(m.Peek((ad + k) & 0xFFFF).ToString("X2"));
                        Console.WriteLine("{0:X4}  {1,-9} {2,-20} {3}", ad, hx, t, Near(ad));
                        ad = (ad + Math.Max(1, len)) & 0xFFFF;
                    }
                }
                break;
            case "ring":
                if (a1.ToLowerInvariant() == "on") ringOn = true;
                else using (var w = new StreamWriter(Path.GetFullPath(a1)))
                    for (int i = 0; i < 4096; i++) { int k = (ringPos + i) & 4095; w.WriteLine("{0:X4} SP={1:X4} {2}", ringPc[k] & 0xFFFF, (ringPc[k] >> 16) & 0xFFFF, Near(ringPc[k] & 0xFFFF)); }
                break;
            case "until":
                {
                    int pc = Num(a1); long mx = m.Frames + (a2 != "" ? Num(a2) : 500); bool hit = false;
                    while (m.Frames < mx && !crashed) { Step(); if (cpu.PC == pc) { hit = true; break; } }
                    Console.Error.WriteLine("until {0}: {1} at field {2}", a1, hit ? "reached" : "NOT reached", m.Frames);
                }
                break;
            case "untilmem":
                {
                    int ad = Num(a1), v = Num(a2); long mx = m.Frames + (a.Length > 3 ? Num(a[3]) : 500); bool hit = false;
                    while (m.Frames < mx && !crashed) { RunFrames(1); if (m.Peek(ad) == v) { hit = true; break; } }
                    Console.Error.WriteLine("untilmem {0}={1}: {2} at field {3}", a1, a2, hit ? "reached" : "NOT reached", m.Frames);
                }
                break;
            case "untiltext":
                {
                    string t = a1.Replace('_', ' '); long mx = m.Frames + (a2 != "" ? Num(a2) : 500); bool hit = false;
                    while (m.Frames < mx && !crashed) { RunFrames(1); if (m.ScreenText().Contains(t)) { hit = true; break; } }
                    Console.Error.WriteLine("untiltext {0}: {1} at field {2}", a1, hit ? "reached" : "NOT reached", m.Frames);
                    if (!hit && a.Length > 3 && a[3] == "fail") return false;
                }
                break;
            case "minsp": Console.WriteLine("min SP {0:X4}", minSp); break;
            case "frame": case "field": Console.WriteLine("field {0}, pictures {1}, CRTC frames {2}", m.Frames, m.Video.Pictures, m.Video.CrtcFrames); break;
            case "rtc": Console.WriteLine("RTC " + m.Rtc.TimeText + " regs A-D " + m.Rtc.Ram[10].ToString("X2") + " " + m.Rtc.Ram[11].ToString("X2") + " " + m.Rtc.Ram[12].ToString("X2") + " " + m.Rtc.Ram[13].ToString("X2")); break;
            case "lines":
                {
                    // lines [FIRST] [COUNT]: the last picture line by line (needs "vis on" first): 6545 row / scanline,
                    // start address, CRTC frame, flags, and the beam across the line (. blank, # picture, h HSYNC, v VSYNC,
                    // w CPU held for video RAM), one character per 2 T-states
                    VisOn();
                    var tr = m.Tr;
                    int first = a1 != "" ? int.Parse(a1) : 0, count = a2 != "" ? int.Parse(a2) : tr.PicLines;
                    Console.WriteLine("picture: " + tr.PicLines + " lines, " + tr.PicFrames + " CRTC frames");
                    for (int l = first; l < Math.Min(tr.PicLines, first + count); l++)
                    {
                        int f = tr.LFlags[l], o = l * BeeTrace.LW;
                        var sb = new System.Text.StringBuilder();
                        for (int t = 0; t < tr.LLen[l]; t += 2)
                            sb.Append(tr.LCpu[o + t] == BeeTrace.S_WAIT ? 'w' : ".#hv"[tr.LBeam[o + t] & 3]);
                        Console.WriteLine("{0,3} row {1,3} ra {2,2} ma {3:X4} fr {4,2} {5}{6}{7}{8}{9} {10}", l, tr.LRow[l], tr.LRa[l], tr.LMa[l], tr.LFrame[l],
                            (f & BeeVideo.LF_VDISP) != 0 ? 'D' : '-', (f & BeeVideo.LF_ADJ) != 0 ? 'A' : '-', (f & BeeVideo.LF_NEWFRAME) != 0 ? 'F' : '-',
                            (f & BeeVideo.LF_ROW0) != 0 ? '0' : '-', (f & BeeVideo.LF_VSYNC) != 0 ? 'V' : '-', sb);
                    }
                }
                break;
            case "fullscreen":
                {
                    // fullscreen FILE [bezel|plain] [colour|mono] [novis]: a 1920 x 1080 PNG laid out as the window's
                    // full screen: the visualiser columns at the edges, the picture between them, with or without the
                    // monitor bezel
                    VisOn(); vis.MonoTint = monoTint; vis.Brown = brown;
                    string opt = (a2 + " " + (a.Length > 3 ? a[3] : "") + " " + (a.Length > 4 ? a[4] : "")).ToLowerInvariant();
                    bool withVis = !opt.Contains("novis"), bz = !opt.Contains("plain");
                    var fview = opt.Contains("mono") ? BeeMachine.View.Mono : opt.Contains("both") ? BeeMachine.View.Both : BeeMachine.View.Colour;
                    if (fview == BeeMachine.View.Both) bz = false;
                    if (withVis) vis.Render();
                    int fw, fh; int[] fpx = m.Snapshot(fview, monoTint, BeeMachine.Frame.Monitor, true, brown, out fw, out fh);
                    const int CW = 1920, CH = 1080;
                    var img = new int[CW * CH];
                    int pw = withVis ? BeeVis.W : 0, aw = CW - 2 * pw;
                    if (withVis)
                        for (int y = 0; y < CH && y < BeeVis.H; y++) { Array.Copy(vis.Left, y * BeeVis.W, img, y * CW, BeeVis.W); Array.Copy(vis.Right, y * BeeVis.W, img, y * CW + CW - BeeVis.W, BeeVis.W); }
                    if (bz)
                    {
                        var b = fsBezel.Compose(fpx, fw, fh, aw, CH);
                        for (int y = 0; y < CH; y++) Array.Copy(b, y * aw, img, y * CW + pw, aw);
                    }
                    else
                    {
                        // as the window draws it: whole-pixel steps when they fill 85% of the space, else as large as fits
                        double k = Math.Min((double)aw / fw, (double)CH / fh), si = Math.Floor(k);
                        double s = k >= 1 && si / k >= 0.85 ? si : k;
                        int dw = Math.Min(aw, (int)Math.Round(fw * s)), dh = Math.Min(CH, (int)Math.Round(fh * s));
                        int ox = pw + (aw - dw) / 2, oy = (CH - dh) / 2;
                        for (int y = 0; y < dh; y++) { int sy = Math.Min(fh - 1, (int)(y / s)); for (int x = 0; x < dw; x++) img[(oy + y) * CW + ox + x] = fpx[sy * fw + Math.Min(fw - 1, (int)(x / s))]; }
                    }
                    SavePng(a1, img, CW, CH, 1);
                }
                break;
            case "vispage":
                {
                    int pg = Array.IndexOf(BeeVis.PageNames, a1.ToLowerInvariant());
                    if (pg < 0 && !int.TryParse(a1, out pg)) pg = 0;
                    if (pg > 1) pg = pg - 1;            // "vispage 2" = the second page
                    VisOn(); vis.Page = Math.Max(0, Math.Min(1, pg));
                    Console.WriteLine("visualiser page: " + BeeVis.PageNames[vis.Page]);
                }
                break;
            case "vis":
                VisOn();
                if (a1 != "" && a1.ToLowerInvariant() != "on")
                {
                    vis.MonoTint = monoTint; vis.Brown = brown;
                    string pa = (a2 + " " + (a.Length > 3 ? a[3] : "")).ToLowerInvariant();
                    if (pa.Contains("raster")) vis.Page = 1; else if (pa.Contains("machine")) vis.Page = 0;
                    vis.Render();
                    int vw, vh; int[] sp = m.Snapshot(BeeMachine.View.Colour, monoTint, BeeMachine.Frame.Monitor, true, brown, out vw, out vh);
                    int[] img; int iw, ih;
                    if (pa.Contains("panels"))
                    {
                        iw = BeeVis.W * 2; ih = BeeVis.H; img = new int[iw * ih];
                        for (int y = 0; y < ih; y++) { Array.Copy(vis.Left, y * BeeVis.W, img, y * iw, BeeVis.W); Array.Copy(vis.Right, y * BeeVis.W, img, y * iw + BeeVis.W, BeeVis.W); }
                    }
                    else
                    {
                        iw = 1920; ih = 1080; img = new int[iw * ih];
                        int sx = Math.Max(1, Math.Min((iw - 2 * BeeVis.W) / vw, ih / vh));
                        int ox = (iw - vw * sx) / 2, oy = (ih - vh * sx) / 2;
                        for (int y = 0; y < ih; y++)
                        {
                            Array.Copy(vis.Left, y * BeeVis.W, img, y * iw, BeeVis.W);
                            Array.Copy(vis.Right, y * BeeVis.W, img, y * iw + iw - BeeVis.W, BeeVis.W);
                            int sy = y - oy; if (sy < 0 || sy >= vh * sx) continue;
                            for (int x = 0; x < vw * sx; x++) img[y * iw + ox + x] = sp[(sy / sx) * vw + x / sx];
                        }
                    }
                    SavePng(a1, img, iw, ih, 1);
                }
                break;
            case "quit": case "exit": return false;
            default: Console.Error.WriteLine("?? " + cmd); break;
        }
        return true;
    }

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("ubemu script.txt                  run a test script (see README.md)");
            Console.Error.WriteLine("ubemu -e \"cmd; cmd; ...\"          run commands given inline");
            Console.Error.WriteLine("ubemu DISK.DS80 [fields] [out.png]   boot a disk, run, screenshot");
            Console.Error.WriteLine("ubemu GAME.COM [fields] [out.png]    run a CP/M program on a made-up boot disk");
            return 1;
        }
        var h = new Headless();
        if (args[0] == "-e") return h.RunLines(string.Join(" ", args, 1, args.Length - 1).Split(';'));
        if (BeeDisk.IsDiskName(args[0]) || args[0].EndsWith(".com", StringComparison.OrdinalIgnoreCase))
        {
            var l = new List<string>();
            l.Add(args[0].EndsWith(".com", StringComparison.OrdinalIgnoreCase) ? "com " + args[0] : "disk " + args[0]);
            l.Add("frames " + (args.Length > 1 ? args[1] : "500"));
            l.Add("screen " + (args.Length > 2 ? args[2] : "screen.png") + " 1");
            return h.RunLines(l);
        }
        return h.RunScript(args[0]);
    }
}
