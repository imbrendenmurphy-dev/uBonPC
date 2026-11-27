// uBonPC visualiser: live panels drawn into two 320x1080 bitmaps, shared by the window (sidebars in fullscreen, its
// own window otherwise) and ubemu ("vis" command).
//  Left:  Z80 registers | code | hot spots | frame budget (busy, ISR, video wait, idle) | 6545 CRTC | opcodes | floppy | ports
//  Right: 64K memory map | 1 MB bank map | beam and CPU | screen cells | PCG banks | speaker | keyboard matrix
// Page 2, raster and I/O (Shift+F10, or "vispage raster" in ubemu):
//  Left:  6545 beam line by line | beam-timed writes (CRTC registers, ports 08 / 0B / 1C / 50 with line and T)
//  Right: Z80 PIO pins | colour and mono | MC146818 RTC | video RAM by line | beam and CPU
using System;
using System.Collections.Generic;
using System.IO;

public sealed class BeeVis
{
    public const int W = 320, H = 1080;
    public readonly int[] Left = new int[W * H], Right = new int[W * H];
    readonly BeeMachine m;
    public BeeTrace T;
    public readonly Dictionary<int, string> Sym = new Dictionary<int, string>();
    public string SymFile;
    public int CellMode;          // screen cells panel: 0 writes, 1 PCG bank, 2 colours
    public int Page;              // 0 machine, 1 raster and I/O
    public static readonly string[] PageNames = { "machine", "raster" };
    public int MonoTint = 1; public bool Brown = true;   // for the colour and mono panel

    const int BG = 0x06080C, PANEL = 0x0A0E16, FRAME = 0x23345A, TBG = 0x121C34, TITLE = 0x58A8FF, TXT = 0xB8C8E0,
        DIM = 0x56627A, HI = 0xFFFFFF, AMBER = 0xFFB700, RED = 0xFF4848, CYAN = 0x41E0E0, MAG = 0xD060FF, BLUE = 0x5A84FF,
        GREEN = 0x40E040, ORANGE = 0xFF8A00, IDLEC = 0x14264A, WAITC = 0xE05A28;

    int[] buf;
    public BeeVis(BeeMachine machine) { m = machine; }

    // ================================================================ symbols
    /// <summary>Symbols for a program: NAME.sym next to it, or in ../build, ../out, .. (zasm "ADDR NAME" or sjasmplus "NAME: EQU 0xADDR").</summary>
    public void FindSymbols(string path)
    {
        Sym.Clear(); SymFile = null;
        if (path == null) return;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)), bn = Path.GetFileNameWithoutExtension(path);
        var dirs = new List<string> { dir };
        string par = Path.GetDirectoryName(dir);
        if (par != null) { dirs.Add(Path.Combine(par, "build")); dirs.Add(Path.Combine(par, "out")); dirs.Add(par); }
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            string p = Path.Combine(d, bn + ".sym");
            if (File.Exists(p)) { LoadSymbols(p); return; }
        }
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            var s = Directory.GetFiles(d, "*.sym");
            if (s.Length == 1) { LoadSymbols(s[0]); return; }
        }
    }
    public void LoadSymbols(string path)
    {
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                int a; string n;
                try
                {
                    if (p.Length >= 3 && p[1].ToUpperInvariant() == "EQU") { n = p[0].TrimEnd(':'); string v = p[2]; a = v.StartsWith("0x") ? Convert.ToInt32(v.Substring(2), 16) : v.EndsWith("h") ? Convert.ToInt32(v.TrimEnd('h'), 16) : int.Parse(v); }
                    else { a = Convert.ToInt32(p[0].TrimStart('$'), 16); n = p[1]; }
                }
                catch { continue; }
                n = n.ToUpperInvariant();
                if (n.StartsWith(".") || n.StartsWith("@") || n.Contains(".")) continue;
                if (a < 0x0100 || a > 0xFFFF) continue;
                if (!Sym.ContainsKey(a)) Sym[a] = n;
            }
            SymFile = path;
        }
        catch { }
    }
    string Label(int a)
    {
        string s; if (Sym.TryGetValue(a, out s)) return s;
        if (T != null && routine[a]) return "SUB_" + a.ToString("X4");
        return null;
    }

    // ================================================================ drawing primitives
    void Fill(int x, int y, int w, int h, int c)
    {
        if (x < 0) { w += x; x = 0; } if (y < 0) { h += y; y = 0; }
        if (x + w > W) w = W - x; if (y + h > H) h = H - y;
        for (int j = 0; j < h; j++) { int o = (y + j) * W + x; for (int i = 0; i < w; i++) buf[o + i] = c; }
    }
    void Px(int x, int y, int c) { if ((uint)x < W && (uint)y < H) buf[y * W + x] = c; }
    void Rect(int x, int y, int w, int h, int c) { Fill(x, y, w, 1, c); Fill(x, y + h - 1, w, 1, c); Fill(x, y, 1, h, c); Fill(x + w - 1, y, 1, h, c); }

    static int Glyph(char ch)
    {
        if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 32);
        if (ch >= '@' && ch <= '_') return ch - 64;
        if (ch >= ' ' && ch <= '?') return ch;
        return 63;
    }
    int Text(int x, int y, string s, int c)
    {
        foreach (char ch in s)
        {
            if (x > W - 6) break;
            if (ch == '_') { for (int i = 0; i < 5; i++) Px(x + i, y + 6, c); }
            else if (ch != ' ')
            {
                int g = Glyph(ch);
                for (int r = 0; r < 7; r++)
                {
                    int bits = Font6847.F[g, r];
                    if (bits == 0 || y + r >= H || y + r < 0) continue;
                    int o = (y + r) * W + x;
                    for (int i = 0; i < 5; i++) if (((bits >> (4 - i)) & 1) != 0) buf[o + i] = c;
                }
            }
            x += 6;
        }
        return x;
    }
    int TextR(int xr, int y, string s, int c) { return Text(xr - s.Length * 6, y, s, c); }

    static int Lerp(int a, int b, double t)
    {
        if (t < 0) t = 0; if (t > 1) t = 1;
        int r = (int)(((a >> 16) & 255) + (((b >> 16) & 255) - ((a >> 16) & 255)) * t);
        int g = (int)(((a >> 8) & 255) + (((b >> 8) & 255) - ((a >> 8) & 255)) * t);
        int bl = (int)((a & 255) + ((b & 255) - (a & 255)) * t);
        return (r << 16) | (g << 8) | bl;
    }
    static int Heat(double t)
    {
        if (t <= 0) return 0x0C1018;
        if (t < 0.33) return Lerp(0x0C1A40, 0x2A70FF, t / 0.33);
        if (t < 0.66) return Lerp(0x2A70FF, 0x40F0FF, (t - 0.33) / 0.33);
        return Lerp(0x40F0FF, 0xFFFFFF, (t - 0.66) / 0.34);
    }

    int Panel(int y, int h, string title, string info)
    {
        Fill(0, y, W, h, PANEL);
        Rect(0, y, W, h, FRAME);
        Fill(1, y + 1, W - 2, 11, TBG);
        Text(5, y + 3, title, TITLE);
        if (info != null) TextR(W - 5, y + 3, info, DIM);
        return y + 15;
    }
    static string H2(int v) { return (v & 255).ToString("X2"); }
    static string H4(int v) { return (v & 0xFFFF).ToString("X4"); }
    static string Pct(double f) { return ((int)Math.Round(f * 100)) + "%"; }

    // ================================================================ render
    int[] prevRegs = new int[12];

    public void Render()
    {
        if (T == null) return;
        if (++routineScan % 25 == 1) UpdateRoutines();
        if (Page == 1) { RenderRaster(); return; }
        buf = Left; Fill(0, 0, W, H, BG);
        int y = 0;
        y = PRegs(y) + 6;
        y = PCode(y, 13) + 6;
        y = PHot(y, 7) + 6;
        y = PBudget(y) + 6;
        y = PCrtc(y) + 6;
        y = POps(y) + 6;
        if (m.Model.Fdc) y = PFloppy(y) + 6;
        PPorts(y, H - y);

        buf = Right; Fill(0, 0, W, H, BG);
        y = 0;
        y = PMem(y) + 6;
        if (m.Model.Dram) y = PBanks(y) + 6;
        y = PBeam(y) + 6;
        y = PCells(y) + 6;
        y = PPcg(y) + 6;
        y = PSound(y) + 6;
        PKeys(y, H - y);
    }

    // ---------------------------------------------------------------- Z80 registers
    int PRegs(int y0)
    {
        var c = m.Cpu;
        int h = 15 + 6 * 11 + 4;
        int y = Panel(y0, h, "Z80 REGISTERS", "3.375 MHZ  " + m.Model.Id.ToUpperInvariant());
        int[] v = { (c.A << 8) | c.F, (c.B << 8) | c.C, (c.D << 8) | c.E, (c.H << 8) | c.L,
                    (c.A_ << 8) | c.F_, (c.B_ << 8) | c.C_, (c.D_ << 8) | c.E_, (c.H_ << 8) | c.L_, c.IX, c.IY, c.SP, c.PC };
        string[] n = { "AF ", "BC ", "DE ", "HL ", "AF'", "BC'", "DE'", "HL'", "IX ", "IY ", "SP ", "PC " };
        for (int i = 0; i < 12; i++)
        {
            int x = 5 + (i % 4) * 78, yy = y + (i / 4) * 11;
            Text(x, yy, n[i], DIM);
            Text(x + 20, yy, H4(v[i]), v[i] != prevRegs[i] ? AMBER : TXT);
            prevRegs[i] = v[i];
        }
        int y3 = y + 33;
        int x3 = Text(5, y3, "I ", DIM); x3 = Text(x3, y3, H2(c.I), TXT);
        x3 = Text(x3 + 8, y3, "R ", DIM); x3 = Text(x3, y3, H2(c.R), TXT);
        x3 = Text(x3 + 8, y3, "IM ", DIM); x3 = Text(x3, y3, c.IM.ToString(), TXT);
        x3 = Text(x3 + 8, y3, "IFF ", DIM); x3 = Text(x3, y3, (c.IFF1 ? "1" : "0") + (c.IFF2 ? "1" : "0"), c.IFF1 ? GREEN : RED);
        x3 = Text(x3 + 8, y3, "INT ", DIM); x3 = Text(x3, y3, c.IntLine ? "LOW" : "HIGH", c.IntLine ? AMBER : TXT);
        x3 = Text(x3 + 8, y3, "INTS/F ", DIM); Text(x3, y3, T.Ints.ToString(), TXT);
        if (c.Halted) TextR(W - 5, y3, "HALT", AMBER);
        int y4 = y + 45;
        Text(5, y4 + 1, "FLAGS", DIM);
        string fl = "SZYHXPNC";
        for (int i = 0; i < 8; i++)
        {
            bool on = ((c.F >> (7 - i)) & 1) != 0;
            int x = 44 + i * 25;
            Fill(x, y4 - 1, 21, 10, on ? 0x2A70FF : 0x121A2C);
            Text(x + 8, y4 + 1, fl[i].ToString(), on ? HI : DIM);
        }
        int y5 = y + 57;
        int x5 = Text(5, y5, "FIELD ", DIM); x5 = Text(x5, y5, m.Frames.ToString(), TXT);
        x5 = Text(x5 + 10, y5, "SP LOW ", DIM); x5 = Text(x5, y5, T.SpMinSecond == 0xFFFF ? "----" : H4(T.SpMinSecond), AMBER);
        x5 = Text(x5 + 10, y5, "STACK ", DIM);
        int ret = m.Peek(c.SP) | (m.Peek((c.SP + 1) & 0xFFFF) << 8);
        Text(x5, y5, H4(ret), TXT);
        return y0 + h;
    }

    // ---------------------------------------------------------------- code
    int PCode(int y0, int lines)
    {
        int h = 15 + lines * 9 + 4;
        int y = Panel(y0, h, "CODE", "SAMPLED WHILE BUSY");
        int pc = T.ShownPc >= 0 ? T.ShownPc : m.Cpu.PC;
        int start = pc, back = 0;
        for (int a = pc - 1; a >= pc - 60 && back < 6; a--) if (a >= 0 && T.Start[a]) { start = a; back++; }
        Func<int, int> rd = m.Peek;
        int ad = start, ln = 0;
        while (ln < lines)
        {
            string lab = Label(ad);
            if (lab != null) { Text(5, y + ln * 9, lab + ":", AMBER); ln++; if (ln >= lines) break; }
            int len;
            string s = Z80Dis.Dis(rd, ad, out len, Label);
            int yy = y + ln * 9;
            bool isPc = ad == pc;
            if (isPc) Fill(2, yy - 1, W - 4, 9, 0x1E2E58);
            int hx = T.HX[ad];
            int col = isPc ? HI : hx > 0 ? Lerp(DIM, TXT, hx / 255.0) : DIM;
            if (ad == m.Cpu.PC) Text(5, yy, ">", RED);
            Text(12, yy, H4(ad), isPc ? HI : DIM);
            string bytes = "";
            for (int i = 0; i < len && i < 4; i++) bytes += H2(rd((ad + i) & 0xFFFF));
            Text(42, yy, bytes, isPc ? TXT : 0x3C4A68);
            Text(96, yy, s, col);
            ad = (ad + Math.Max(1, len)) & 0xFFFF; ln++;
        }
        return y0 + h;
    }

    // ---------------------------------------------------------------- hot spots
    readonly bool[] routine = new bool[65536];
    int routineScan;
    void UpdateRoutines()
    {
        int last = -100;
        for (int a = 0; a < 65536; a++)
        {
            routine[a] = false;
            if (!T.CallTarget[a]) continue;
            if (a - last >= 16 || Sym.ContainsKey(a)) routine[a] = true;
            last = a;
        }
    }
    int hotVersion = -1; List<KeyValuePair<string, long>> hot = new List<KeyValuePair<string, long>>();
    void BuildHot()
    {
        hotVersion = T.ProfVersion; hot.Clear();
        var starts = new List<int>();
        foreach (var k in Sym.Keys) starts.Add(k);
        UpdateRoutines();
        for (int a = 0; a < 65536; a++) if (routine[a] && !Sym.ContainsKey(a)) starts.Add(a);
        starts.Sort();
        var sums = new Dictionary<string, long>();
        for (int a = 0; a < 65536; a++)
        {
            long t = T.ProfShown[a]; if (t == 0) continue;
            string name;
            if (m.Model.Dram && m.RomOn && a >= 0x8000 && a < 0xC000) name = "BOOT ROM $" + H4(a & 0xFF00);
            else
            {
                int i = starts.BinarySearch(a); if (i < 0) i = ~i - 1;
                if (i >= 0 && a - starts[i] < 0x1000) { int s = starts[i]; string sn; name = Sym.TryGetValue(s, out sn) ? sn : "SUB_" + H4(s); }
                else name = "$" + H4(a & 0xFF00) + " BLOCK";
            }
            long o; sums.TryGetValue(name, out o); sums[name] = o + t;
        }
        foreach (var kv in sums) hot.Add(kv);
        hot.Sort((p, q) => q.Value.CompareTo(p.Value));
    }
    int PHot(int y0, int lines)
    {
        if (hotVersion != T.ProfVersion) BuildHot();
        int h = 15 + lines * 11 + 2;
        int y = Panel(y0, h, "HOT SPOTS", Sym.Count > 0 ? "CPU TIME, SYMBOLS" : "CPU TIME, LAST SECOND");
        long tot = Math.Max(1, T.ProfShownT);
        for (int i = 0; i < lines && i < hot.Count; i++)
        {
            int yy = y + i * 11;
            double f = (double)hot[i].Value / tot;
            string nm = hot[i].Key; if (nm.Length > 17) nm = nm.Substring(0, 17);
            Text(5, yy, nm, i == 0 ? HI : TXT);
            TextR(146, yy, (f * 100).ToString(f < 0.1 ? "0.0" : "0") + "%", AMBER);
            Fill(150, yy - 1, 166, 9, 0x0E1424);
            Fill(150, yy - 1, Math.Max(1, (int)(f * 166)), 9, Heat(0.25 + f * 0.75));
        }
        if (hot.Count == 0) Text(5, y, "COLLECTING...", DIM);
        return y0 + h;
    }

    // ---------------------------------------------------------------- frame budget
    int PBudget(int y0)
    {
        int h = 15 + 12 + 4 + 10 + 44 + 4;
        int y = Panel(y0, h, "FRAME BUDGET", "FIELD " + T.FieldShownT + " T, " + (m.Video.DoneFrameT > 0 ? (BeeMachine.CPU_HZ / (double)m.Video.DoneFrameT).ToString("0.00") : "--") + " HZ");
        int x = 5, bw = W - 10;
        int wb = (int)(T.Busy * bw), wi = (int)(T.Isr * bw), ww = (int)(T.VWait * bw);
        Fill(x, y, bw, 12, IDLEC);
        Fill(x, y, wb, 12, GREEN); Fill(x + wb, y, wi, 12, MAG); Fill(x + wb + wi, y, ww, 12, WAITC);
        Text(x + 4, y + 3, Pct(T.Busy + T.Isr + T.VWait) + " USED", 0x002000);
        int yt = y + 16;
        int xt = Text(5, yt, "BUSY ", GREEN); xt = Text(xt, yt, Pct(T.Busy), TXT);
        xt = Text(xt + 8, yt, "ISR ", MAG); xt = Text(xt, yt, Pct(T.Isr), TXT);
        xt = Text(xt + 8, yt, "VIDEO WAIT ", WAITC); xt = Text(xt, yt, Pct(T.VWait), TXT);
        xt = Text(xt + 8, yt, "IDLE ", BLUE); Text(xt, yt, Pct(T.Idle), TXT);
        int gy = yt + 12, gh = 42;
        Fill(5, gy, 300, gh, 0x080C14);
        for (int i = 0; i < BeeTrace.HIST; i++)
        {
            int k = (T.HPos + i) % BeeTrace.HIST;
            int b = T.HBusy[k] * gh / 100, s = T.HIsr[k] * gh / 100, w = T.HWait[k] * gh / 100;
            int yb = gy + gh;
            Fill(5 + i, yb - b, 1, b, 0x28A028); yb -= b;
            Fill(5 + i, yb - s, 1, s, MAG); yb -= s;
            Fill(5 + i, yb - w, 1, w, WAITC);
        }
        Fill(5, gy + gh / 2, 300, 1, 0x1A2438);
        TextR(W - 4, gy, "100", DIM); TextR(W - 4, gy + gh - 8, "0", DIM);
        return y0 + h;
    }

    // ---------------------------------------------------------------- 6545 CRTC
    int PCrtc(int y0)
    {
        var v = m.Video;
        int h = 15 + 10 * 9 + 4 + 4 * 10 + 2;
        int y = Panel(y0, h, "6545 CRTC", "R" + v.Sel + " SELECTED");
        for (int i = 0; i < 20; i++)
        {
            int col = i / 10, row = i % 10;
            int x = 5 + col * 157, yy = y + row * 9;
            Text(x, yy, (i < 10 ? "R" + i + " " : "R" + i), i == v.Sel ? AMBER : DIM);
            Text(x + 20, yy, H2(v.R[i]), TXT);
            Text(x + 36, yy, BeeVideo.RegName[i], DIM);
        }
        int ly = y + 92;
        int cols = v.R[1], rows = v.R[6] & 127, spr = v.ScansPerRow;
        Text(5, ly, string.Format("{0} X {1} CHARS OF 8 X {2}  LINE {3} T  {4} MHZ DOTS", cols, rows, spr, v.LineT, v.CharT == 2 ? "13.5" : "6.75"), TXT); ly += 10;
        Text(5, ly, string.Format("CRTC FRAME {0} LINES  PICTURE {1} LINES  {2:0.00} HZ", v.FrameLines, v.LastPictureLines, v.DoneFrameT > 0 ? BeeMachine.CPU_HZ / (double)v.DoneFrameT : 0), TXT); ly += 10;
        int x2 = Text(5, ly, "START " + H4(v.StartAddr) + " CURSOR " + H4(v.CursorAddr) + " UPDATE " + H4(v.UpdateAddr), DIM);
        ly += 10;
        x2 = Text(5, ly, "STATUS ", DIM);
        x2 = Text(x2, ly, v.UpdateReady ? "READY" : "UPDATE", v.UpdateReady ? TXT : AMBER);
        x2 = Text(x2 + 6, ly, v.LpenFull ? "LPEN " + H4(v.Lpen) : "LPEN -", v.LpenFull ? AMBER : DIM);
        x2 = Text(x2 + 6, ly, v.VDisp ? "DISPLAY" : "VBLANK", v.VDisp ? GREEN : 0xC09040);
        TextR(W - 4, ly, "BEAM " + v.Pl + ":" + v.Hc, DIM);
        return y0 + h;
    }

    // ---------------------------------------------------------------- opcode heatmap
    int POps(int y0)
    {
        int cw = 18, ch = 7;
        int h = 15 + 9 + 16 * ch + 4 + 20;
        int y = Panel(y0, h, "OPCODES", "FIRST BYTE, LAST SECOND");
        int gx = 17;
        for (int i = 0; i < 16; i++) Text(gx + i * cw + 6, y, i.ToString("X"), DIM);
        y += 9;
        long n = Math.Max(1, T.OpsShownN);
        int max = 1; for (int i = 0; i < 256; i++) if (T.OpsShown[i] > max) max = T.OpsShown[i];
        double lmax = Math.Log(1 + max);
        for (int r = 0; r < 16; r++)
        {
            Text(5, y + r * ch, r.ToString("X"), DIM);
            for (int col = 0; col < 16; col++)
            {
                int cnt = T.OpsShown[r * 16 + col];
                Fill(gx + col * cw, y + r * ch, cw - 1, ch - 1, cnt == 0 ? 0x0C1018 : Heat(Math.Log(1 + cnt) / lmax));
            }
        }
        var idx = new List<int>(); for (int i = 0; i < 256; i++) if (T.OpsShown[i] > 0) idx.Add(i);
        idx.Sort((a, b) => T.OpsShown[b].CompareTo(T.OpsShown[a]));
        int ty = y + 16 * ch + 3;
        for (int i = 0; i < 4 && i < idx.Count; i++)
        {
            int op = idx[i]; int len;
            byte[] fake = { (byte)op, 0, 0, 0 };
            string s = op == 0xCB ? "CB PREFIX" : op == 0xED ? "ED PREFIX" : op == 0xDD ? "IX PREFIX" : op == 0xFD ? "IY PREFIX"
                : Z80Dis.Dis(a => fake[a & 3], 0, out len, null, true);
            string line = ((int)Math.Round(100.0 * T.OpsShown[op] / n)) + "% " + s;
            if (line.Length > 25) line = line.Substring(0, 25);
            Text(5 + (i % 2) * 156, ty + (i / 2) * 10, line, i == 0 ? HI : TXT);
        }
        return y0 + h;
    }

    // ---------------------------------------------------------------- WD2793 floppy
    int PFloppy(int y0)
    {
        var f = m.Fdc;
        int h = 15 + 4 * 10 + 2 * 7 + 4;
        var dk = f.Disk[f.Drive];
        int y = Panel(y0, h, "FLOPPY  WD2793", f.Busy ? f.StateName : "IDLE");
        // all four drives the latch can select: the selected one amber (bright while busy)
        for (int d = 0; d < BeeFdc.DRIVES; d++)
        {
            var dd = f.Disk[d];
            string n = dd == null ? "--" : Path.GetFileNameWithoutExtension(dd.Name).ToUpperInvariant();
            if (n.Length > 9) n = n.Substring(0, 9);
            int c = d == f.Drive ? (f.Busy ? AMBER : 0xB08030) : dd == null ? DIM : TXT;
            Text(5 + d * 78, y, (char)('A' + d) + " " + n, c);
        }
        y += 10;
        int x = Text(5, y, (char)('A' + f.Drive) + ": ", f.Busy ? AMBER : DIM);
        string nm = dk == null ? "EMPTY" : dk.Name.ToUpperInvariant(); if (nm.Length > 24) nm = nm.Substring(0, 24);
        Text(x, y, nm, dk == null ? DIM : TXT);
        if (dk != null) TextR(W - 4, y, (dk.WriteProtect ? "PROT " : "") + (dk.Dirty ? "CHANGED" : dk.Format), dk.Dirty ? RED : DIM);
        y += 10;
        Text(5, y, f.LastCmd, f.Busy ? HI : DIM);
        TextR(W - 4, y, "ST " + H2(f.Status) + (f.Drq ? " DRQ" : "") + (f.Intrq ? " INT" : ""), f.Drq ? GREEN : DIM);
        y += 10;
        Text(5, y, "TRK " + f.Track + " SEC " + f.Sector + " HEAD " + f.HeadCyl[f.Drive] + "/" + f.Side, TXT);
        TextR(W - 4, y, "R " + f.SectorsRead + " W " + f.SectorsWritten + (f.LostData > 0 ? " LOST " + f.LostData : ""), f.LostData > 0 ? RED : DIM);
        y += 10;
        int cw = 3;
        for (int hd = 0; hd < 2; hd++)
            for (int c = 0; c < 82; c++)
            {
                int i = Math.Min(199, c * 2 + hd), r = f.HeatR[i], w = f.HeatW[i];
                bool has = dk != null && dk.HasTrack(c, hd);
                int col = w > 0 ? Lerp(0x301010, RED, w / 255.0) : r > 0 ? Lerp(0x103010, GREEN, r / 255.0) : has ? 0x1A2438 : 0x0C1018;
                if (c == f.HeadCyl[f.Drive] && hd == f.Side) col = f.Busy ? AMBER : 0x604010;
                Fill(5 + c * cw + (c / 10) , y + hd * 7, cw - 1, 6, col);
                f.HeatR[i] = (byte)(r * 7 / 8); f.HeatW[i] = (byte)(w * 7 / 8);
            }
        return y0 + h;
    }

    // ---------------------------------------------------------------- ports
    int PPorts(int y0, int avail)
    {
        int h = Math.Max(30, avail);
        int y = Panel(y0, h, "PORTS", "RECENT, VALUE, PER FIELD");
        int ly = y;
        for (int k = 0; k < T.Recent.Count && ly + 9 < y0 + h - 20; k++)
        {
            int key = T.Recent[k]; bool isOut = key >= 256; int p = key & 255;
            int heat = isOut ? T.POut[p] : T.PIn[p];
            int x = Text(5, ly, (isOut ? "OUT " : "IN  ") + H2(p) + "=" + H2(isOut ? T.PValOut[p] : T.PValIn[p]), Lerp(isOut ? 0x602020 : 0x206020, isOut ? RED : GREEN, heat / 255.0));
            Text(x + 6, ly, PortName(p), DIM);
            TextR(W - 4, ly, T.PCountShown[(isOut ? 256 : 0) + p].ToString(), DIM);
            ly += 9;
        }
        if (T.Recent.Count == 0) Text(5, ly, "NO PORT I/O", DIM);
        int by = y0 + h - 14;
        string rtc = m.Model.Rtc ? "RTC " + m.Rtc.TimeText.Substring(11) : "";
        Text(5, by, "50=" + H2(m.Port50) + " 1C=" + H2(m.Port1C) + " 08=" + H2(m.Port08) + " 0B=" + (m.Latch0B ? "1" : "0"), TXT);
        TextR(W - 4, by, rtc, DIM);
        return y0 + h;
    }
    static string PortName(int p)
    {
        switch (p)
        {
            case 0x00: return "PIO A DATA (JOYSTICK)"; case 0x01: return "PIO A CONTROL"; case 0x02: return "PIO B DATA (SPEAKER)"; case 0x03: return "PIO B CONTROL";
            case 0x04: return "RTC ADDRESS"; case 0x06: return "RTC DATA"; case 0x07: return "RTC DATA";
            case 0x08: return "COLOUR RAM SELECT"; case 0x09: return "SPEED / COLOUR WAIT"; case 0x0A: return "PAK ROM";
            case 0x0B: return "CHARACTER ROM LATCH"; case 0x0C: return "CRTC ADDRESS/STATUS"; case 0x0D: return "CRTC DATA";
            case 0x18: return "256TC KEYBOARD"; case 0x1C: return "PCG BANK / ATTRIBUTES";
            case 0x40: case 0x44: return "FDC COMMAND/STATUS"; case 0x41: case 0x45: return "FDC TRACK"; case 0x42: case 0x46: return "FDC SECTOR"; case 0x43: case 0x47: return "FDC DATA";
            case 0x48: return "DRIVE / SIDE / DRQ"; case 0x50: return "MEMORY BANK";
        }
        return "";
    }

    // ================================================================ right column
    int PMem(int y0)
    {
        int h = 15 + 256 + 4 + 10 + 2;
        int y = Panel(y0, h, "MEMORY MAP", "64K AS THE CPU SEES IT");
        int mx = 4;
        bool dram = m.Model.Dram;
        for (int a = 0; a < 65536; a++)
        {
            int tr = 8, tg = 10, tb = 16;
            bool vid = m.VidOn && (a & 0xF000) == m.VidBase;
            if (vid) { if ((a & 0x800) == 0) { tr = 0; tg = 30; tb = 26; } else { tr = 34; tg = 12; tb = 34; } }
            else if (dram && m.RomOn && a >= 0x8000 && a < 0xC000) { tr = 14; tg = 16; tb = 40; }
            else if (!dram && a >= 0x8000) { tr = 14; tg = 16; tb = 40; }
            int v = vid ? 0 : m.Peek(a);
            int lum = v >> 3;
            int r = tr + lum, g = tg + lum, b = tb + lum;
            int hr = T.HR[a], hw = T.HW[a], hx = T.HX[a];
            if (hw > r) r = hw;
            if (hw / 3 > g) g = hw / 3;
            if (hr * 7 / 8 > g) g = hr * 7 / 8;
            if (hx > b) b = hx;
            if (hx / 2 > g) g = Math.Max(g, hx / 2);
            if (hx / 4 > r) r = Math.Max(r, hx / 4);
            buf[(y + (a >> 8)) * W + mx + (a & 255)] = (Math.Min(255, r) << 16) | (Math.Min(255, g) << 8) | Math.Min(255, b);
        }
        int pc = m.Cpu.PC, sp = m.Cpu.SP;
        Fill(mx + 257, y + (pc >> 8), 5, 1, HI);
        Fill(mx + 257, y + (sp >> 8), 5, 1, AMBER);
        for (int d = -2; d <= 2; d++) { Px(mx + (pc & 255) + d, y + (pc >> 8), HI); Px(mx + (pc & 255), y + (pc >> 8) + d, HI); }
        int lx = mx + 264;
        if (dram)
        {
            LabelAt(lx, y, 0x0000, "BLK " + m.LowBlock, TXT, 0x8000);
            if (m.VidOn && m.VidBase == 0x8000) { LabelAt(lx, y, 0x8000, "VIDEO", GREEN, 0x9000); LabelAt(lx, y, 0x9000, m.RomOn ? "ROM" : "BLK 0", m.RomOn ? 0x8090D0 : TXT, 0xC000); }
            else LabelAt(lx, y, 0x8000, m.RomOn ? "ROM" : "BLK 0", m.RomOn ? 0x8090D0 : TXT, 0xC000);
            LabelAt(lx, y, 0xC000, "BLK 0", TXT, 0xF000);
            if (m.VidOn && m.VidBase == 0xF000) { LabelAt(lx, y, 0xF000, "SCR", GREEN, 0xF800); LabelAt(lx, y, 0xF800, (m.Port08 & 0x40) != 0 && m.Colour ? "COL" : "PCG" + m.PcgWindowBank, MAG, 0x10000); }
        }
        else
        {
            LabelAt(lx, y, 0x0000, "RAM", TXT, 0x8000);
            LabelAt(lx, y, 0x8000, m.Model.Sram56 ? "RAM" : "BASIC", 0x8090D0, 0xC000);
            LabelAt(lx, y, 0xF000, "VIDEO", GREEN, 0x10000);
        }
        int ly = y + 256 + 4;
        int x = Text(5, ly, "READ", GREEN); x = Text(x + 10, ly, "WRITE", RED); x = Text(x + 10, ly, "EXECUTE", BLUE);
        x = Text(x + 14, ly, "PC", HI); Text(x + 10, ly, "SP", AMBER);
        return y0 + h;
    }
    void LabelAt(int x, int y, int addr, string name, int c, int next)
    {
        int yy = y + (addr >> 8);
        Fill(x - 4, yy, 3, 1, c);
        Text(x, yy + 1, name, c);
        if (((next - addr) >> 8) >= 18) Text(x, yy + 10, H4(addr), DIM);
    }

    // ---------------------------------------------------------------- 1 MB of banked RAM
    int PBanks(int y0)
    {
        int blocks = Math.Max(1, m.Model.RamK / 32);
        int rows = (blocks + 1) / 2;
        int bh = rows <= 16 ? 4 : 2;
        int h = 15 + rows * bh + 14;
        int y = Panel(y0, h, "BANKED RAM", m.Model.RamK + "K, " + blocks + " BLOCKS OF 32K");
        for (int b = 0; b < blocks; b++)
        {
            int cx = 18 + (b / rows) * 152, cy = y + (b % rows) * bh;
            bool low = b == m.LowBlock, up = b == 0;
            for (int px = 0; px < 128; px++)
            {
                int page = (b * 128 + px) & 4095;
                int r = T.PR[page], w = T.PW[page];
                int col = w > 0 ? Lerp(0x301818, RED, w / 255.0) : r > 0 ? Lerp(0x103018, GREEN, r / 255.0) : low ? 0x1E2E58 : up ? 0x182440 : 0x0E1220;
                Fill(cx + px, cy, 1, bh - 1, col);
            }
            if (b % 4 == 0) Text(cx - 15, cy, b.ToString("D2"), DIM);
            if (low) Fill(cx + 129, cy, 3, bh - 1, AMBER);
        }
        int ly = y + rows * bh + 3;
        int x = Text(5, ly, "0000-7FFF = BLOCK " + m.LowBlock, AMBER);
        Text(x + 10, ly, "8000-FFFF = BLOCK 0", DIM);
        return y0 + h;
    }

    // ---------------------------------------------------------------- beam / CPU timing
    static readonly int[] stCol = { 0x0A0C14, 0x1E8A2A, IDLEC, 0x9A3CC8, WAITC };
    static readonly int[] ovCol = { 0, HI, 0xA0FFA0, 0xFF80FF, AMBER, CYAN, RED, 0xFF9040, 0x60C060, 0x60A0FF, 0xE0E060, 0xFF60C0 };
    static readonly int[] stPri = { 0, 2, 1, 4, 5 };

    int PBeam(int y0)
    {
        var v = m.Video;
        int lt = Math.Max(16, Math.Min(300, v.LineT));
        int lines = Math.Max(2, Math.Min(400, v.LastPictureLines));
        int rh = (lines + 1) / 2;
        int h = 15 + rh + 4 + 20;
        int y = Panel(y0, h, "BEAM AND CPU", lt + " T X " + lines + " LINES");
        int bx = 4;
        var ras = T.Ras; var ov = T.Ov; int ft = Math.Max(1, T.FieldShownT);
        for (int r = 0; r < rh; r++)
        {
            int o = (y + r) * W + bx;
            for (int t = 0; t < lt && t < W - 70; t++)
            {
                int i0 = r * 2 * lt + t, i1 = (r * 2 + 1) * lt + t;
                if (i0 >= ft) { buf[o + t] = 0x050608; continue; }
                if (i1 >= ft) i1 = i0;
                int ovv = ov[i0] != 0 ? ov[i0] : ov[i1];
                if (ovv != 0) { buf[o + t] = ovCol[ovv]; continue; }
                int s = stPri[ras[i0]] >= stPri[ras[i1]] ? ras[i0] : ras[i1];
                buf[o + t] = stCol[s];
            }
        }
        // the displayed part of each line
        int dispT = Math.Min(lt, v.R[1] * v.CharT);
        for (int r = 0; r < rh; r += 3) Px(bx + dispT, y + r, 0x50A060);
        int lx = bx + Math.Min(lt, W - 70) + 6;
        Text(lx, y, "LINE 0", 0x70C080); Text(lx, y + 9, "= VSYNC", DIM); Text(lx, y + rh - 8, lines.ToString(), DIM);
        Text(lx, y + 30, "DOTS = END", DIM); Text(lx, y + 39, "OF PICTURE", DIM);
        int ly = y + rh + 4;
        int x = Text(5, ly, "BUSY", 0x30C040); x = Text(x + 6, ly, "ISR", MAG); x = Text(x + 6, ly, "WAIT", WAITC); x = Text(x + 6, ly, "IDLE", BLUE);
        x = Text(x + 6, ly, "CRTC", ovCol[BeeTrace.O_CRTC]); x = Text(x + 6, ly, "FDC", ovCol[BeeTrace.O_FDC]); Text(x + 6, ly, "KEY", ovCol[BeeTrace.O_KEY]);
        ly += 10;
        x = Text(5, ly, "SCR", HI); x = Text(x + 6, ly, "ATTR", ovCol[BeeTrace.O_ATTR]); x = Text(x + 6, ly, "COL", ovCol[BeeTrace.O_COL]); x = Text(x + 6, ly, "PCG", AMBER);
        x = Text(x + 6, ly, "VREAD", CYAN); x = Text(x + 6, ly, "SPK", RED); Text(x + 6, ly, "OUT", ovCol[BeeTrace.O_OUT]);
        return y0 + h;
    }

    // ---------------------------------------------------------------- screen cells
    int PCells(int y0)
    {
        var v = m.Video;
        int cols = Math.Max(1, Math.Min(128, v.R[1])), rows = Math.Max(1, Math.Min(64, v.R[6] & 127));
        int cw = Math.Max(1, Math.Min(6, 300 / cols)), ch = Math.Max(1, Math.Min(6, 80 / rows));
        int gh = rows * ch;
        int h = 15 + Math.Max(gh, 20) + 4 + 20;
        string[] modes = { "WRITES THIS SECOND", "PCG BANK OF EACH CELL", "COLOURS" };
        int y = Panel(y0, h, "SCREEN CELLS", modes[CellMode % 3]);
        int start = v.StartAddr;
        int[] prem = BeeVideo.PremiumRgb(true);
        int[] bankCol = { 0x404040, 0xE04040, 0x40E040, 0xE0E040, 0x4040E0, 0xE040E0, 0x40E0E0, 0xE0E0E0, 0x804020, 0xFF8080, 0x80FF80, 0xFFFF80, 0x8080FF, 0xFF80FF, 0x80FFFF, 0xFFFFFF };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int a = (start + r * cols + c) & 0x7FF;
                int col;
                int ch8 = m.Scr[a];
                if (CellMode % 3 == 0)
                {
                    int w = Math.Max(T.CellW[a], Math.Max(T.CellC[a], T.CellA[a]));
                    col = w > 0 ? Lerp(0x203040, T.CellC[a] >= T.CellW[a] ? MAG : HI, w / 255.0) : ch8 >= 0x80 ? 0x182030 : ch8 != 0x20 ? 0x102018 : 0x0A0C10;
                }
                else if (CellMode % 3 == 1) col = ch8 >= 0x80 ? bankCol[m.Extended ? m.Attr[a] & 15 : 0] : 0x101010;
                else { int cl = m.Col[a]; col = (r + c) % 2 == 0 ? prem[cl & 15] : prem[cl >> 4]; }
                Fill(5 + c * cw, y + r * ch, Math.Max(1, cw - (cw > 2 ? 1 : 0)), Math.Max(1, ch - (ch > 2 ? 1 : 0)), col);
            }
        int ly = y + Math.Max(gh, 20) + 4;
        int x = Text(5, ly, "SCR ", DIM); x = Text(x, ly, T.ScrWrites.ToString(), HI);
        x = Text(x + 8, ly, "ATTR ", DIM); x = Text(x, ly, T.AttrWrites.ToString(), TXT);
        x = Text(x + 8, ly, "COL ", DIM); x = Text(x, ly, T.ColWrites.ToString(), MAG);
        x = Text(x + 8, ly, "PCG ", DIM); x = Text(x, ly, T.PcgWrites.ToString(), AMBER);
        TextR(W - 4, ly, "READS " + T.VidReads, DIM);
        ly += 10;
        x = Text(5, ly, (m.Extended ? "EXTENDED (1C B7)" : "STANDARD") + (m.Premium ? " PREMIUM" : ""), m.Extended ? GREEN : DIM);
        TextR(W - 4, ly, "WAIT " + m.LastFieldWait + " T/FIELD", m.LastFieldWait > 0 ? WAITC : DIM);
        return y0 + h;
    }

    // ---------------------------------------------------------------- PCG banks
    int shownBank = 0; int bankTimer;
    int PPcg(int y0)
    {
        int h = 15 + 12 + 128 + 6;
        // which bank to show: the one being written, else cycle through the banks on screen
        int written = -1; int best = 0;
        for (int b = 0; b < 16; b++) if (T.PcgBankShown[b] > best) { best = T.PcgBankShown[b]; written = b; }
        var used = new bool[16]; var v = m.Video;
        int cols = Math.Min(128, v.R[1]), rows = Math.Min(64, v.R[6] & 127);
        for (int i = 0; i < cols * rows; i++) { int a = (v.StartAddr + i) & 0x7FF; if (m.Scr[a] >= 0x80) used[m.Extended ? m.Attr[a] & 15 : 0] = true; }
        if (written >= 0) { shownBank = written; bankTimer = 50; }
        else if (--bankTimer <= 0)
        {
            bankTimer = 75;
            for (int k = 1; k <= 16; k++) { int b = (shownBank + k) & 15; if (used[b]) { shownBank = b; break; } }
        }
        int y = Panel(y0, h, "PCG RAM", m.PcgBanks + " BANKS OF 2K");
        for (int b = 0; b < 16; b++)
        {
            bool exists = b < m.PcgBanks;
            int bx = 5 + b * 19;
            int col = !exists ? 0x0A0C10 : b == shownBank ? 0x2A70FF : used[b] ? 0x1A3A20 : 0x121A2C;
            Fill(bx, y - 1, 18, 9, col);
            if (T.PcgBankShown[b] > 0) Fill(bx, y + 7, 18, 1, AMBER);
            if (exists) Text(bx + (b < 10 ? 6 : 3), y, b.ToString(), b == shownBank ? HI : used[b] ? GREEN : DIM);
        }
        int gy = y + 11, gx = 5;
        int bank = shownBank;
        for (int ch = 0; ch < 128; ch++)
        {
            int cx = gx + (ch & 15) * 9, cy = gy + (ch >> 4) * 16;
            int heat = T.PcgCharW[(bank << 7) | ch];
            int on = heat > 0 ? Lerp(TXT, AMBER, heat / 255.0) : TXT;
            for (int r = 0; r < 16; r++)
            {
                int bits = bank < m.PcgBanks ? m.Pcg[(bank << 11) | (ch << 4) | r] : 0;
                int o = (cy + r) * W + cx;
                for (int i = 0; i < 8; i++) buf[o + i] = (bits & (0x80 >> i)) != 0 ? on : 0x0C1018;
            }
        }
        int tx = gx + 16 * 9 + 6;
        Text(tx, gy, "BANK " + bank, HI);
        Text(tx, gy + 10, "CODES 80-FF", DIM);
        Text(tx, gy + 22, "WINDOW F800: " + ((m.Port08 & 0x40) != 0 && m.Colour ? "COLOUR" : "PCG " + m.PcgWindowBank), TXT);
        Text(tx, gy + 32, "WRITES/FIELD " + T.PcgWrites, T.PcgWrites > 0 ? AMBER : DIM);
        int ub = 0; for (int b = 0; b < 16; b++) if (used[b]) ub++;
        Text(tx, gy + 42, "BANKS ON SCREEN " + ub, DIM);
        // the character ROM, half size
        Text(tx, gy + 58, "CHARACTER ROM", DIM);
        int set = (v.StartAddr & 0x2000) != 0 ? 0x800 : 0;
        for (int ch = 0; ch < 128; ch++)
        {
            int cx = tx + (ch & 15) * 8, cy = gy + 68 + (ch >> 4) * 7;
            for (int r = 0; r < 14; r += 2)
            {
                int bits = m.CharRom[set + (ch << 4) + r + 1] | m.CharRom[set + (ch << 4) + r];
                int o = (cy + r / 2) * W + cx;
                if (cx + 8 >= W) continue;
                for (int i = 0; i < 8; i++) if ((bits & (0x80 >> i)) != 0) buf[o + i] = DIM;
            }
        }
        return y0 + h;
    }

    // ---------------------------------------------------------------- speaker
    readonly double[] fre = new double[1024], fim = new double[1024];
    readonly double[] spec = new double[512];
    static readonly string[] notes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    static string Note(double hz)
    {
        if (hz < 20 || hz > 20000) return "";
        double n = 12 * Math.Log(hz / 440.0, 2) + 57;
        int k = (int)Math.Round(n);
        return notes[((k % 12) + 12) % 12] + (k / 12);
    }

    int PSound(int y0)
    {
        int sh = 30, gh = 28;
        int h = 15 + sh + 4 + gh + 4 + 10 + 2;
        int y = Panel(y0, h, "SPEAKER  PIO B6", m.Speaker ? "HIGH" : "LOW");
        int sx = 5, sw = 300;
        Fill(sx, y, sw, sh, 0x080C14);
        Fill(sx, y + sh / 2, sw, 1, 0x16203A);
        var s = T.SpkShown; int n = Math.Max(1, T.SpkShownN);
        for (int col = 0; col < sw; col++)
        {
            int a = col * n / sw, b = Math.Max(a + 1, (col + 1) * n / sw);
            float lo = 1, hi = -1;
            for (int i = a; i < b && i < n; i++) { if (s[i] < lo) lo = s[i]; if (s[i] > hi) hi = s[i]; }
            if (hi < lo) continue;
            int ylo = y + sh / 2 - (int)(hi * (sh / 2 - 2)), yhi = y + sh / 2 - (int)(lo * (sh / 2 - 2));
            Fill(sx + col, ylo, 1, yhi - ylo + 1, hi - lo > 0.05 ? 0x60C0FF : 0x3070C0);
        }
        int rp = T.SpkRingPos;
        for (int i = 0; i < 1024; i++) { double wv = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 1023); fre[i] = T.SpkRing[(rp - 1024 + i) & 2047] * wv; fim[i] = 0; }
        double mean = 0; for (int i = 0; i < 1024; i++) mean += fre[i]; mean /= 1024;
        for (int i = 0; i < 1024; i++) fre[i] -= mean * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 1023));
        Fft(fre, fim);
        double rate = BeeMachine.RATE;
        int maxBin = (int)(6000 / (rate / 1024)); if (maxBin > 511) maxBin = 511;
        int peak = 0; double pv = 0;
        for (int i = 2; i <= maxBin; i++) { spec[i] = Math.Sqrt(fre[i] * fre[i] + fim[i] * fim[i]); if (spec[i] > pv) { pv = spec[i]; peak = i; } }
        int gy = y + sh + 4;
        Fill(sx, gy, sw, gh, 0x080C14);
        for (int col = 0; col < sw; col++)
        {
            int bin = 1 + col * maxBin / sw;
            double vv = spec[bin];
            int bh = vv <= 0 ? 0 : (int)Math.Min(gh, Math.Max(0, Math.Log10(1 + vv * 4) * 14));
            Fill(sx + col, gy + gh - bh, 1, bh, Heat(0.3 + 0.7 * bh / gh));
        }
        TextR(sx + sw, gy, "6 KHZ", DIM); Text(sx + 2, gy, "0", DIM);
        int ty = gy + gh + 4;
        int xs = Text(5, ty, "TOGGLES/FIELD ", DIM); xs = Text(xs, ty, T.SoundWrites.ToString(), TXT);
        if (pv > 2) { xs = Text(xs + 12, ty, "PEAK ", DIM); double hz = peak * rate / 1024; Text(xs, ty, ((int)hz) + " HZ " + Note(hz), AMBER); }
        else Text(xs + 12, ty, "SILENT", DIM);
        return y0 + h;
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    // ---------------------------------------------------------------- keyboard matrix + joystick
    int PKeys(int y0, int avail)
    {
        int h = Math.Max(40, avail);
        var v = m.Video;
        int y = Panel(y0, h, "KEYBOARD VIA LIGHT PEN", "TESTS/FIELD " + T.KeyTests);
        for (int r = 0; r < 8; r++)
        {
            int yy = y + r * 10;
            for (int b = 0; b < 8; b++)
            {
                int k = r * 8 + b;
                string lab = BeeMachine.KeyNames[k];
                if (lab == "SPACE") lab = "SPC"; else if (lab == "SHIFT") lab = "SHF"; else if (lab == "BREAK") lab = "BRK"; else if (lab == "RIGHT") lab = "RGT"; else if (lab == "LEFT") lab = "LFT"; else if (lab == "DOWN") lab = "DWN"; else if (lab == "LOCK") lab = "LCK"; else if (lab == "CTRL") lab = "CTL";
                else if (lab.Length > 3) lab = lab.Substring(0, 3);
                int x = 5 + b * 27;
                bool down = m.KeyDown[k];
                int tested = T.KeyTested[k];
                int bg = down ? AMBER : tested > 0 ? Lerp(0x121A2C, 0x2A70FF, tested / 255.0) : 0x121A2C;
                Fill(x, yy - 1, 25, 9, bg);
                Text(x + (25 - lab.Length * 6) / 2 + 1, yy, lab, down ? 0x201000 : TXT);
            }
        }
        int jx = 228, jy = y + 2;
        Text(jx, jy, "JOYSTICK", DIM);
        int j = m.Joy;
        string[] dn = { "UP", "DN", "LF", "RT", "P1", "P2", "--", "FIRE" };
        for (int i = 0; i < 8; i++)
        {
            bool on = (j & (1 << i)) == 0;
            Fill(jx + (i % 2) * 44, jy + 10 + (i / 2) * 10, 42, 9, on ? RED : 0x121A2C);
            Text(jx + (i % 2) * 44 + 3, jy + 11 + (i / 2) * 10, dn[i], on ? HI : DIM);
        }
        int ly = y + 84;
        int x2 = Text(5, ly, "LPEN ", DIM);
        x2 = Text(x2, ly, v.LpenFull ? H4(v.Lpen) + " KEY " + BeeMachine.KeyNames[(v.Lpen >> 4) & 63] : "EMPTY", v.LpenFull ? AMBER : DIM);
        x2 = Text(x2 + 8, ly, "LAST TEST ", DIM);
        Text(x2, ly, v.LastUpdateKey >= 0 ? BeeMachine.KeyNames[v.LastUpdateKey] : "-", TXT);
        ly += 10;
        if (ly + 8 < y0 + h)
        {
            var ms = m.Mouse;
            if (ms.On)
            {
                bool busy = ms.Busy(m.Cpu.Cyc);
                int xm = Text(5, ly, "MOUSE " + BeeMouse.ModeNames[(int)ms.Mode].ToUpperInvariant() + (ms.Invert ? " INV" : ""), busy ? AMBER : TXT);
                Text(xm + 8, ly, ms.Packets + " PACKETS  LAST " + ms.LastDx + "," + ms.LastDy + (ms.LastL ? " L" : "") + (ms.LastR ? " R" : ""), DIM);
            }
            else Text(5, ly, "BLUE = TESTED BY R18/R19 + R31   AMBER = HELD", DIM);
        }
        return y0 + h;
    }

    // ================================================================ page 2: raster and I/O
    void RenderRaster()
    {
        buf = Left; Fill(0, 0, W, H, BG);
        int y = PLines(0) + 6;
        PWrites(y, H - y);

        buf = Right; Fill(0, 0, W, H, BG);
        y = 0;
        y = PPio(y) + 6;
        y = PColours(y) + 6;
        if (m.Model.Rtc) y = PRtc(y) + 6;
        y = PVidLines(y) + 6;
        int beamH = 15 + (Math.Max(2, Math.Min(400, m.Video.LastPictureLines)) + 1) / 2 + 24;
        if (y + beamH <= H) PBeam(y);
    }

    int Key(int x, int y, int c, string s) { Fill(x, y + 1, 5, 5, c); return Text(x + 7, y, s, DIM) + 6; }

    // ---------------------------------------------------------------- 6545 beam, line by line
    // Every line of the last picture at one pixel per T-state, from VSYNC down: what the beam was doing (picture,
    // blanking, HSYNC, VSYNC; alternate CRTC frames in two greens), where the CPU was held for video RAM and where it
    // wrote; and at the right the 6545's scanline counter (RA), the line's start address and the frame / row-0 marks.
    static readonly int[] beamCol = { 0x080B12, 0x1A4A2A, 0x2C1E50, 0x4A3410 };
    static readonly int[] beamCol2 = { 0x080B12, 0x2A6A2A, 0x2C1E50, 0x4A3410 };

    int PLines(int y0)
    {
        int n = Math.Max(1, Math.Min(T.PicLines, 420));
        int lt = 16; for (int l = 0; l < T.PicLines; l++) lt = Math.Max(lt, T.LLen[l]);
        lt = Math.Min(220, lt);
        int h = 15 + n + 3 + 9 + 30;
        int y = Panel(y0, h, "6545 LINE BY LINE", lt + "T X " + T.PicLines + ", " + T.PicFrames + (T.PicFrames == 1 ? " FRAME" : " FRAMES"));
        int bx = 4, sx = bx + lt + 4, mx = sx + 18, fx = mx + 50;
        for (int l = 0; l < n; l++)
        {
            int yy = y + l;
            if (l >= T.PicLines) { Fill(bx, yy, W - 8, 1, BG); continue; }
            int o = yy * W + bx, lo = l * BeeTrace.LW, len = Math.Min(lt, T.LLen[l]);
            int[] bc = (T.LFrame[l] & 1) != 0 ? beamCol2 : beamCol;
            for (int t = 0; t < lt; t++)
            {
                int c;
                if (t >= len) c = BG;
                else
                {
                    byte ovv = T.LOv[lo + t], cs = T.LCpu[lo + t];
                    if (ovv != 0 && ovv != BeeTrace.O_IN) c = ovCol[ovv];
                    else if (cs == BeeTrace.S_WAIT) c = WAITC;
                    else c = bc[T.LBeam[lo + t] & 3];
                }
                buf[o + t] = c;
            }
            int f = T.LFlags[l];
            Fill(sx, yy, 16, 1, 0x0C1220);
            Px(sx + (T.LRa[l] & 15), yy, (f & BeeVideo.LF_ADJ) != 0 ? AMBER : (f & BeeVideo.LF_VDISP) != 0 ? GREEN : DIM);
            Fill(mx, yy, 48, 1, 0x0C1220);
            Px(mx + (T.LMa[l] & 0x7FF) * 47 / 0x7FF, yy, (T.LFrame[l] & 1) != 0 ? CYAN : 0x6090FF);
            if ((f & BeeVideo.LF_NEWFRAME) != 0) Fill(fx, yy, 6, 1, AMBER);
            else if ((f & BeeVideo.LF_ROW0) != 0 && (f & BeeVideo.LF_VDISP) != 0) Fill(fx, yy, 3, 1, CYAN);
            if ((f & BeeVideo.LF_VSYNC) != 0) Fill(fx + 7, yy, 2, 1, 0xC09040);
        }
        int ay = y + n + 3;
        Text(bx, ay, "T 0", DIM); TextR(bx + lt, ay, lt.ToString(), DIM);
        Text(sx, ay, "RA", DIM); Text(mx, ay, "START", DIM); Text(fx, ay, "F", DIM);
        int ly = ay + 10;
        int x = Key(5, ly, beamCol[1], "PICTURE"); x = Key(x, ly, beamCol[0] + 0x101010, "BLANK"); x = Key(x, ly, beamCol[2], "HSYNC"); x = Key(x, ly, beamCol[3], "VSYNC"); Key(x, ly, WAITC, "CPU HELD");
        ly += 10;
        x = Key(5, ly, ovCol[BeeTrace.O_CRTC], "CRTC"); x = Key(x, ly, ovCol[BeeTrace.O_SCR], "SCR"); x = Key(x, ly, ovCol[BeeTrace.O_ATTR], "ATTR"); x = Key(x, ly, ovCol[BeeTrace.O_COL], "COL");
        x = Key(x, ly, ovCol[BeeTrace.O_PCG], "PCG"); x = Key(x, ly, ovCol[BeeTrace.O_VR], "READ"); Key(x, ly, ovCol[BeeTrace.O_OUT], "OUT");
        ly += 10;
        x = Key(5, ly, GREEN, "RA"); x = Key(x, ly, AMBER, "ADJUST"); x = Key(x, ly, AMBER, "NEW FRAME"); x = Key(x, ly, CYAN, "ROW 0 (R12/R13)");
        return y0 + h;
    }

    // ---------------------------------------------------------------- beam-timed writes
    static string CodeName(int c) { return c < 0x100 ? "R" + c : "P" + (c & 0xFF).ToString("X2"); }
    static int CodeCol(int c)
    {
        if (c >= 0x100) return MAG;
        if (c == 12 || c == 13) return CYAN;
        if (c == 18 || c == 19 || c == 31) return DIM;
        if (c <= 9) return AMBER;
        return TXT;
    }

    int PWrites(int y0, int h)
    {
        int y = Panel(y0, h, "BEAM-TIMED WRITES", T.BwShownTotal + " IN THE LAST PICTURE");
        var cnt = new SortedDictionary<int, int>();
        foreach (var w in T.BwShown) { int k; cnt.TryGetValue(w.Code, out k); cnt[w.Code] = k + 1; }
        int x = 5, yy = y;
        if (cnt.Count == 0) Text(5, yy, "NO 6545 OR VIDEO PORT WRITES", DIM);
        foreach (var kv in cnt)
        {
            string s = CodeName(kv.Key) + " X" + kv.Value;
            if (x + s.Length * 6 > W - 5) { x = 5; yy += 9; }
            x = Text(x, yy, s, CodeCol(kv.Key)) + 8;
        }
        yy += 12;
        Text(5, yy, "LINE T   WRITE (FROM VSYNC; P = PORT)", DIM);
        yy += 10;
        int rows = Math.Max(1, (y0 + h - 4 - yy) / 9), colsN = 3, cw = 104;
        int shown = Math.Min(T.BwShown.Count, rows * colsN);
        bool more = T.BwShownTotal > shown;
        if (more) shown = Math.Max(0, shown - 1);
        for (int i = 0; i < shown; i++)
        {
            var w = T.BwShown[i];
            int cx = 5 + (i / rows) * cw, cy = yy + (i % rows) * 9;
            int xx = Text(cx, cy, w.Line.ToString("D3") + " " + w.T.ToString("D3"), TXT);
            Text(xx + 6, cy, CodeName(w.Code) + "=" + H2(w.V), CodeCol(w.Code));
        }
        if (more) { int i = shown; Text(5 + (i / rows) * cw, yy + (i % rows) * 9, "+" + (T.BwShownTotal - shown) + " MORE", AMBER); }
        return y0 + h;
    }

    // ---------------------------------------------------------------- Z80 PIO pins
    static readonly string[] pioName = { "B0 TAPE IN", "B1 TAPE OUT", "B2 RS232 DTR", "B3 RS232 CTS", "B4 RS232 RX", "B5 RS232 TX", "B6 SPEAKER", "B7" };

    void PinTrace(int tx, int ry, int tw, int n, Func<int, bool> level, int col)
    {
        Fill(tx, ry + 1, tw, 8, 0x101826);
        for (int px = 0; px < tw && n > 0; px++)
        {
            int s0 = (int)((long)px * n / tw), s1 = Math.Max(s0 + 1, (int)((long)(px + 1) * n / tw));
            bool any1 = false, any0 = false;
            for (int s = s0; s < s1 && s < n; s++) { if (level(s)) any1 = true; else any0 = true; }
            if (any1 && any0) Fill(tx + px, ry + 2, 1, 6, col);
            else if (any1) Px(tx + px, ry + 2, col);
            else Px(tx + px, ry + 7, col);
        }
    }

    int PPio(int y0)
    {
        var b = m.Pio.B; var a = m.Pio.A;
        int h = 15 + 9 * 10 + 4 + 4 * 9 + 2;
        int y = Panel(y0, h, "Z80 PIO PINS", "1 FIELD, " + T.PioShownN + " SAMPLES");
        int n = T.PioShownN, tx = 82, tw = W - 4 - tx;
        int mon = b.Mode == 3 && b.IntEnable ? ~b.IntMask & b.Dir & 0xFF : 0;
        string b7 = m.Model.PioB7 == "vsync" ? "B7 VSYNC" : m.Model.PioB7 == "rtc" ? "B7 RTC INT" : "B7 NET/INT";
        for (int bit = 0; bit < 8; bit++)
        {
            int ry = y + bit * 10, bb = bit;
            bool isIn = b.Mode == 3 ? ((b.Dir >> bit) & 1) != 0 : b.Mode != 0;
            bool isMon = ((mon >> bit) & 1) != 0;
            Text(4, ry + 1, bit == 7 ? b7 : pioName[bit], isMon ? MAG : isIn ? TXT : AMBER);
            PinTrace(tx, ry, tw, n, s => ((T.PioBShown[s] >> bb) & 1) != 0, isMon ? MAG : isIn ? GREEN : AMBER);
        }
        int iy = y + 80;
        Text(4, iy + 1, "INT LINE", MAG);
        PinTrace(tx, iy, tw, n, s => T.PioIShown[s], MAG);
        int ly = iy + 14;
        Text(5, ly, "B MODE " + b.Mode + " DIR " + H2(b.Dir) + " OUT " + H2(b.Out) + " WATCH " + H2(mon) + " VEC " + H2(b.Vector), TXT);
        ly += 9;
        Text(5, ly, "A MODE " + a.Mode + " DIR " + H2(a.Dir) + " PINS " + H2(n > 0 ? T.PioAShown[n - 1] : 0) + "  INTS/FIELD " + T.Ints, TXT);
        ly += 9;
        var ms = m.Mouse;
        if (ms.On) Text(5, ly, "MOUSE " + BeeMouse.ModeNames[(int)ms.Mode].ToUpperInvariant() + " " + ms.Packets + " PKTS LAST " + ms.LastDx + "," + ms.LastDy + (ms.LastL ? " L" : "") + (ms.LastR ? " R" : ""), AMBER);
        else Text(5, ly, "MOUSE OFF (INPUT MENU, SERIAL MOUSE)", DIM);
        ly += 9;
        int x = Key(5, ly, GREEN, "IN"); x = Key(x, ly, AMBER, "OUT"); Key(x, ly, MAG, "INTERRUPT SOURCE");
        return y0 + h;
    }

    // ---------------------------------------------------------------- colour and mono
    // The Premium's 16 colours, how each shows on a mono monitor (as foreground: bit 3 = full or half brightness; as
    // background: bit 3 = the half-intensity glow), how much of the screen uses each, and which colours mono merges.
    int PColours(int y0)
    {
        int h = 15 + 92 + 3 * 9 + 2;
        string info = !m.Colour ? "NO COLOUR BOARD" : m.Premium ? "PREMIUM RGBI, " + BeeVideo.MonoNames[MonoTint].ToUpperInvariant() + " MONO" : "STANDARD COLOUR BOARD";
        int y = Panel(y0, h, "COLOUR AND MONO", info);
        var pal = BeeVideo.PremiumRgb(Brown); var mono = BeeVideo.MonoTables[Math.Max(0, Math.Min(2, MonoTint))];
        bool half = m.HalfIntensity;
        int sx = 52, sw = 16;
        Text(4, y + 2, "COLOUR", DIM); Text(4, y + 13, "MONO FG", DIM); Text(4, y + 23, "MONO BG", DIM);
        Text(4, y + 40, "FG USE", DIM); Text(4, y + 64, "BG USE", DIM);
        int[] fgN = new int[16], bgN = new int[16];
        var v = m.Video;
        int cols = Math.Max(1, Math.Min(128, v.R[1])), rows = Math.Max(1, Math.Min(64, v.R[6] & 127)), st = v.StartAddr;
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) { int col = m.Col[(st + r * cols + c) & 0x7FF]; fgN[col & 15]++; bgN[col >> 4]++; }
        int fgMax = 1, bgMax = 1; for (int i = 0; i < 16; i++) { fgMax = Math.Max(fgMax, fgN[i]); bgMax = Math.Max(bgMax, bgN[i]); }
        for (int i = 0; i < 16; i++)
        {
            int x = sx + i * sw;
            Fill(x, y, sw - 1, 10, pal[i]);
            Fill(x, y + 12, sw - 1, 8, mono[half ? ((i & 8) != 0 ? 3 : 2) : 3]);
            Fill(x, y + 22, sw - 1, 8, mono[half ? ((i & 8) != 0 ? 1 : 0) : 0]);
            if (mono[half ? ((i & 8) != 0 ? 1 : 0) : 0] == 0) Rect(x, y + 22, sw - 1, 8, 0x1A2234);
            int fh = fgN[i] == 0 ? 0 : Math.Max(1, fgN[i] * 20 / fgMax), bh = bgN[i] == 0 ? 0 : Math.Max(1, bgN[i] * 20 / bgMax);
            Fill(x, y + 34, sw - 1, 20, 0x0A0E18); Fill(x, y + 54 - fh, sw - 1, fh, pal[i] == 0 ? 0x303848 : pal[i]);
            Fill(x, y + 58, sw - 1, 20, 0x0A0E18); Fill(x, y + 78 - bh, sw - 1, bh, pal[i] == 0 ? 0x303848 : pal[i]);
            Text(x + 5, y + 81, i.ToString("X"), DIM);
        }
        int ly = y + 92;
        Text(5, ly, Merge("FG", fgN, i => half ? ((i & 8) != 0 ? 3 : 2) : 3), TXT); ly += 9;
        Text(5, ly, Merge("BG", bgN, i => half ? ((i & 8) != 0 ? 1 : 0) : 0), TXT); ly += 9;
        Text(5, ly, "HALF INTENSITY " + (half ? "ON: FG BIT 3 BRIGHT, BG BIT 3 GLOW" : "OFF"), DIM);
        return y0 + h;
    }
    static string Merge(string what, int[] n, Func<int, int> shade)
    {
        int used = 0; var shades = new HashSet<int>();
        for (int i = 0; i < 16; i++) if (n[i] > 0) { used++; shades.Add(shade(i)); }
        return what + ": " + used + (used == 1 ? " COLOUR" : " COLOURS") + " ON SCREEN -> " + shades.Count + (shades.Count == 1 ? " MONO SHADE" : " MONO SHADES");
    }

    // ---------------------------------------------------------------- MC146818
    int PRtc(int y0)
    {
        var r = m.Rtc;
        int h = 15 + 3 * 9 + 2;
        int y = Panel(y0, h, "MC146818 RTC", r.TimeText);
        int a = r.Ram[10], b = r.Ram[11], rs = a & 15;
        string rate = rs == 0 || ((a >> 4) & 7) != 2 ? "OFF" : ((int)(65536.0 / (1 << (rs < 3 ? rs + 7 : rs)))) + " HZ";
        Text(5, y, "A " + H2(a) + "  B " + H2(b) + "  C " + H2(r.Ram[12]) + "  D " + H2(r.Ram[13]) + "   " + ((b & 4) != 0 ? "BINARY" : "BCD") + ((b & 2) != 0 ? " 24H" : " 12H"), TXT);
        Text(5, y + 9, "PERIODIC " + rate + ((b & 0x40) != 0 ? " PIE" : "") + ((b & 0x20) != 0 ? " AIE" : "") + ((b & 0x10) != 0 ? " UIE" : "") + "   IRQ " + (r.Irq ? "ON" : "OFF"), TXT);
        Text(5, y + 18, "READS " + r.Reads + "  WRITES " + r.Writes + (m.Model.PioB7 == "rtc" ? "  IRQ -> PIO B7" : ""), DIM);
        return y0 + h;
    }

    // ---------------------------------------------------------------- video RAM by line
    int PVidLines(int y0)
    {
        int n = Math.Max(1, Math.Min(T.PicLines, 420));
        int sum = 0; for (int l = 0; l < T.PicLines; l++) sum += T.LWait[l];
        int h = 15 + n + 3 + 9 + 10;
        int y = Panel(y0, h, "VIDEO RAM BY LINE", sum + " T HELD IN THE PICTURE");
        int wx = 4, ww = 200, vx = 208, vw = 50, rx = 262, rw = W - 4 - rx;
        for (int l = 0; l < n; l++)
        {
            int yy = y + l;
            bool shown = l < T.PicLines && (T.LFlags[l] & BeeVideo.LF_VDISP) != 0;
            Fill(4, yy, W - 8, 1, shown ? 0x0E1C14 : 0x080B12);
            if (l >= T.PicLines) continue;
            Fill(wx, yy, Math.Min(ww, T.LWait[l]), 1, WAITC);
            Fill(vx, yy, Math.Min(vw, T.LVW[l]), 1, HI);
            Fill(rx, yy, Math.Min(rw, T.LVR[l]), 1, CYAN);
        }
        int ly = y + n + 3;
        Text(wx, ly, "T HELD (0-200)", WAITC); Text(vx, ly, "WRITES", HI); Text(rx, ly, "READS", CYAN);
        ly += 10;
        Key(5, ly, 0x1E3A28, "LINES WITH PICTURE: THE CPU IS HELD ON THESE");
        return y0 + h;
    }
}
