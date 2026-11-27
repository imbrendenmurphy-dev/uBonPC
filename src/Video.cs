// uBonPC video: the 6545 CRTC run character by character in step with the Z80, and the Premium / standard video
// circuits that turn its refresh address into pixels. Every character cell is drawn at the moment the beam passes it,
// so a write to screen, attribute, colour or PCG RAM, a CRTC register change or a port 1C/08 change shows up from that
// point of the picture on. The picture is framed by the monitor: it restarts at each VSYNC, not at each CRTC frame,
// so stacks of one-line CRTC frames (R9 = 0) and variable-height frames build one picture as on the real machine.
//
// Counter semantics follow the raster model the Microbee Megademo was tuned with on a real Premium Plus
// (6845 type 0 / MAME r6545): a line is R0+1 characters; RA counts to R9, the row counter to R4 (equality compares),
// then R5 adjust lines; the vertical display is on while row < R6, re-evaluated every scanline; while the row counter
// is 0 every scanline restarts from R12/R13; VSYNC starts on a row start equal to R7 and lasts R3 b7-4 lines.
//
// The CRTC also scans the keyboard: its refresh address (MA4-MA9) selects a key, and a pressed key pulls the light pen
// input, latching MA into R16/R17 (status bit 6). Software can also test one key through the transparent update
// address (R18/R19 + a dummy access to R31).
using System;

public enum DotClock { Auto, Mhz13_5, Mhz6_75 }

public sealed class BeeVideo
{
    readonly BeeMachine m;
    public readonly int[] R = new int[32];
    public int Sel;
    static readonly int[] Mask = { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x1F, 0x7F, 0x7F, 0xFF, 0x1F, 0x7F, 0x1F, 0x3F, 0xFF, 0x3F, 0xFF, 0x3F, 0xFF, 0x3F, 0xFF };
    public static readonly string[] RegName = { "H TOTAL", "H DISPLAYED", "H SYNC POS", "SYNC WIDTHS", "V TOTAL", "V ADJUST", "V DISPLAYED", "V SYNC POS",
        "MODE", "SCANLINES", "CURSOR START", "CURSOR END", "START HI", "START LO", "CURSOR HI", "CURSOR LO", "LPEN HI", "LPEN LO", "UPDATE HI", "UPDATE LO" };

    // ---- beam state
    public int Hc, Ra, Row, AdjC, VsLines, HsLeft;
    public bool Adj, VDisp = true;
    public int MaRow, Ma;
    public int Pl;                        // physical line: lines since the last VSYNC started (the monitor's beam)
    public long BeamT;                    // T-state at which character Hc starts
    public int CharT = 2;                 // T-states per character (2 = 13.5 MHz dot clock, 4 = 6.75 MHz)
    public DotClock Clock = DotClock.Auto;
    public long NextEventT;               // the machine calls CatchUp at least this often (once a line)
    public bool VSync { get { return VsLines > 0; } }
    public bool HDispNow { get { return Hc < R[1]; } }

    // ---- light pen / keyboard / transparent update
    public bool LpenFull, UpdateReady = true, UpdatePending;
    public int Lpen;
    public int LpenHits, UpdateCycles, ScanHits;
    public int LastUpdateKey = -1;

    // ---- the monitor's picture: colour indices per 13.5 MHz dot. x = 40 + Hc * dots-per-character, y = Pl - 9,
    // so a standard 64 x 16 screen's first line lands on y = 16 (the reference model's layout).
    public const int FBW = 1024, FBH = 400, LEFT = 40, TOPSKIP = 9;
    public const int MONW = 720, MONH = 320;     // the part a monitor shows
    public byte[] FbC = new byte[FBW * FBH], FbM = new byte[FBW * FBH];
    public byte[] DoneC = new byte[FBW * FBH], DoneM = new byte[FBW * FBH];
    public int DoneLines, DoneWidth, DonePicTop, DonePicLines, DonePicLeft, DonePicWidth, DoneCharT = 2;
    public long DoneFrameT;
    long picStartT;
    int picTop, picBottom, picLeft, picRight;
    public int Pictures;                  // monitor frames finished (VSYNCs, or free-running)
    public int CrtcFrames;                // CRTC frames started (R4/R5 wrap-arounds)
    public bool PictureDone;              // set when a picture finishes (the machine clears it)
    public int FrameCount { get { return Pictures; } }
    public int[] LineVW = new int[FBH], LineVR = new int[FBH];
    public int LastPictureLines = 312;

    // the picture being drawn, line by line (recorded only while a trace is attached, for the visualiser):
    // when each line started, the 6545's row / scanline / start address for it, and what the beam was doing at
    // each character (0 blanking, 1 picture, 2 HSYNC, 3 VSYNC)
    public const int LMAX = 512, LCH = 256;
    public const int LF_VDISP = 1, LF_ADJ = 2, LF_ROW0 = 4, LF_NEWFRAME = 8, LF_VSYNC = 16, LF_PICSTART = 32;
    public readonly long[] LStart = new long[LMAX];
    public readonly int[] LRow = new int[LMAX], LRa = new int[LMAX], LMa = new int[LMAX], LFrame = new int[LMAX], LFlags = new int[LMAX], LChars = new int[LMAX], LCt = new int[LMAX];
    public readonly byte[] LBeam = new byte[LMAX * LCH];
    int crtcInPic, pendFlags;

    void NoteLine(long t)
    {
        int l = Pl; if ((uint)l >= LMAX) return;
        LStart[l] = t; LRow[l] = Row; LRa[l] = Adj ? AdjC : Ra; LMa[l] = MaRow; LFrame[l] = crtcInPic;
        LFlags[l] = (VDisp ? LF_VDISP : 0) | (Adj ? LF_ADJ : 0) | (Row == 0 && !Adj ? LF_ROW0 : 0) | (VsLines > 0 ? LF_VSYNC : 0) | pendFlags;
        pendFlags = 0;
        LChars[l] = LineChars; LCt[l] = CharT;
    }

    // colour index codes: 0-15 Premium RGBI, 64-127 standard colour board (64 + 6-bit value), 200 blanking, 201 HSYNC,
    // 202 VSYNC. Mono codes: 0 bg, 1 bg half-intensity, 2 fg half-intensity, 3 fg, 200+ as above.
    public const byte BLANK = 200, HSYNC = 201, VSYNCC = 202;

    public BeeVideo(BeeMachine machine) { m = machine; }

    public void Reset(long t)
    {
        Array.Clear(R, 0, 32); Sel = 0;
        // standard 64 x 16 Microbee timing until software programs the CRTC
        int[] init = { 0x6B, 0x40, 0x51, 0x37, 0x12, 0x09, 0x10, 0x12, 0x48, 0x0F, 0x20, 0x0F, 0x00, 0x00, 0x00, 0x00 };
        for (int i = 0; i < 16; i++) R[i] = init[i];
        Hc = Ra = Row = AdjC = VsLines = HsLeft = 0; Adj = false; VDisp = true;
        MaRow = StartAddr; Pl = TOPSKIP + 16 - 25 + 25; BeamT = t; NextEventT = t; picStartT = t;
        LpenFull = false; UpdateReady = true; UpdatePending = false;
        ClearPic();
        Array.Clear(FbC, 0, FbC.Length); Array.Clear(FbM, 0, FbM.Length);
        UpdateCharT();
    }
    void ClearPic() { picTop = int.MaxValue; picBottom = -1; picLeft = int.MaxValue; picRight = -1; }

    public int StartAddr { get { return ((R[12] & 0x3F) << 8) | R[13]; } }
    public int CursorAddr { get { return ((R[14] & 0x3F) << 8) | R[15]; } }
    public int UpdateAddr { get { return ((R[18] & 0x3F) << 8) | R[19]; } }
    public int ScansPerRow { get { return (R[9] & 31) + 1; } }
    public int LineChars { get { return R[0] + 1; } }
    public int FrameLines { get { return ((R[4] & 127) + 1) * ScansPerRow + (R[5] & 31); } }
    public int LineT { get { return LineChars * CharT; } }
    public int DotsPerChar { get { return CharT * 4; } }

    void UpdateCharT()
    {
        if (Clock == DotClock.Mhz13_5) CharT = 2;
        else if (Clock == DotClock.Mhz6_75) CharT = 4;
        else CharT = R[0] + 1 <= 64 ? 4 : 2;      // auto: a 54-character total (40-column timing) only makes a 15.6 kHz line at 6.75 MHz
    }
    public void SetClock(DotClock c) { Clock = c; UpdateCharT(); }

    // ---------------------------------------------------------------- CPU access
    public void WriteIndex(int v) { Sel = v & 31; }
    public void WriteData(int v)
    {
        int r = Sel;
        if (m.Tr != null) m.Tr.BeamWrite(r, v);
        if (r < 20) R[r] = v & Mask[r];
        else R[r] = v;
        if (r == 0) UpdateCharT();
        if (r == 31) StartUpdate();
    }
    public int ReadData()
    {
        int r = Sel;
        if (r == 16 || r == 17) { LpenFull = false; return r == 16 ? (Lpen >> 8) & 0x3F : Lpen & 0xFF; }
        if (r == 14 || r == 15 || r == 18 || r == 19) return R[r];
        if (r == 31) { StartUpdate(); return 0xFF; }
        return 0;   // the 6545's write-only registers read as 0
    }
    public int Status()
    {
        int s = 0;
        if (UpdateReady) s |= 0x80;
        if (LpenFull) s |= 0x40;
        if (!VDisp) s |= 0x20;
        return s;
    }

    // the transparent update cycle: the update address goes out on MA, so it tests one key
    void StartUpdate() { UpdatePending = true; UpdateReady = false; if (!HDispNow || !VDisp) DoUpdate(); }
    void DoUpdate()
    {
        UpdatePending = false; UpdateReady = true; UpdateCycles++;
        int a = UpdateAddr;
        int key = (a >> 4) & 63; LastUpdateKey = key;
        if (m.KeyPressed(key) && !LpenFull) { LpenFull = true; Lpen = a; LpenHits++; }
        if (m.Tr != null) m.Tr.KeyTest(key);
        int na = (a + 1) & 0x3FFF; R[18] = na >> 8; R[19] = na & 0xFF;
    }

    /// <summary>The wait for a CPU access to video RAM: the Premium holds the CPU while a line's picture is being drawn,
    /// until the horizontal (or vertical) blanking.</summary>
    public int WaitFor() { return VDisp && Hc < R[1] ? (R[1] - Hc) * CharT : 0; }

    // ---------------------------------------------------------------- the beam
    /// <summary>Runs the beam up to T-state t, drawing every character it passes.</summary>
    public void CatchUp(long t)
    {
        while (BeamT + CharT <= t) { int ct = CharT; Tick(); BeamT += ct; }
        NextEventT = BeamT + (long)Math.Max(1, LineChars - Hc) * CharT;
    }

    void Tick()
    {
        int r1 = R[1], ct0 = CharT;
        bool disp = VDisp && Hc < r1;
        Ma = (MaRow + Hc) & 0x3FFF;
        bool rec = m.Tr != null;
        if (rec && (uint)Pl < LMAX && Hc < LCH) LBeam[Pl * LCH + Hc] = (byte)(disp ? 1 : VsLines > 0 ? 3 : HsLeft > 0 ? 2 : 0);
        int y = Pl - TOPSKIP;
        if (y >= 0 && y < FBH)
        {
            int dpc = CharT * 4;
            int x = LEFT + Hc * dpc;
            if (x + dpc <= FBW)
            {
                int o = y * FBW + x;
                if (disp)
                {
                    DrawChar(o, Ma, dpc);
                    if (y < picTop) picTop = y; if (y > picBottom) picBottom = y;
                    if (x < picLeft) picLeft = x; if (x + dpc > picRight) picRight = x + dpc;
                }
                else
                {
                    byte c = VsLines > 0 ? VSYNCC : HsLeft > 0 ? HSYNC : BLANK;
                    for (int i = 0; i < dpc; i++) { FbC[o + i] = c; FbM[o + i] = c; }
                }
            }
        }
        // the keyboard: the refresh address runs through the whole line and frame, testing one key each 16 characters
        if ((Ma & 15) == 0 && !m.Latch0B && !LpenFull)
        {
            int key = (Ma >> 4) & 63;
            if (m.KeyPressed(key)) { LpenFull = true; Lpen = Ma; ScanHits++; }
        }
        if (!disp && UpdatePending) DoUpdate();
        if (HsLeft > 0) HsLeft--;
        if (Hc == R[2]) { int w = R[3] & 15; HsLeft = w == 0 ? 16 : w; }
        if (Hc == R[0]) { Hc = 0; EndLine(); if (rec) NoteLine(BeamT + ct0); }
        else Hc = (Hc + 1) & 255;
    }

    void EndLine()
    {
        EndLine2();
        VDisp = Row < (R[6] & 127);                              // compared on every scanline
        if (Row == 0 && !Adj) MaRow = StartAddr;                 // row 0: every scanline starts from R12/R13
        if (Pl - TOPSKIP >= FBH + 80) Picture(false);            // no VSYNC: the monitor free-runs (the picture rolls)
    }

    void EndLine2()
    {
        Pl++;
        if (VsLines > 0) { VsLines--; if (VsLines == 0) m.VsyncChanged(false); }
        if (Adj)
        {
            AdjC = (AdjC + 1) & 31;
            if (AdjC == (R[5] & 31)) { CrtcFrame(); RowStart(); }
            return;
        }
        if (Ra == (R[9] & 31))
        {
            Ra = 0;
            if (Row == (R[4] & 127))
            {
                if ((R[5] & 31) != 0) { Adj = true; AdjC = 0; Row = (Row + 1) & 127; MaRow = (MaRow + R[1]) & 0x3FFF; }
                else { CrtcFrame(); RowStart(); }
            }
            else { Row = (Row + 1) & 127; MaRow = (MaRow + R[1]) & 0x3FFF; RowStart(); }
        }
        else Ra = (Ra + 1) & 31;
    }

    void CrtcFrame()
    {
        Row = 0; Ra = 0; Adj = false; AdjC = 0;
        MaRow = StartAddr; VDisp = true;
        CrtcFrames++; crtcInPic++; pendFlags |= LF_NEWFRAME;
        UpdateCharT();
    }

    void RowStart()
    {
        if (Row == (R[7] & 127) && VsLines == 0)
        {
            int w = R[3] >> 4; VsLines = w == 0 ? 16 : w;
            m.VsyncChanged(true);
            Picture(true);                                       // the monitor's beam returns: a picture is complete
        }
    }

    void Picture(bool vsync)
    {
        var t = DoneC; DoneC = FbC; FbC = t;
        t = DoneM; DoneM = FbM; FbM = t;
        DoneLines = Math.Max(0, Math.Min(FBH, Pl - TOPSKIP)); DoneWidth = Math.Min(FBW, LEFT + LineChars * CharT * 4); DoneCharT = CharT;
        if (picBottom >= 0) { DonePicTop = picTop; DonePicLines = picBottom - picTop + 1; DonePicLeft = picLeft; DonePicWidth = picRight - picLeft; }
        else { DonePicTop = 16; DonePicLines = 0; DonePicLeft = LEFT; DonePicWidth = 0; }
        long now = BeamT + CharT;
        DoneFrameT = now - picStartT; picStartT = now;
        LastPictureLines = Pl;
        if (m.Tr != null) { m.Tr.CrtcFrame(LineVW, LineVR, Math.Min(FBH, Pl)); m.Tr.Picture(this, Pl); }
        Array.Clear(LineVW, 0, FBH); Array.Clear(LineVR, 0, FBH);
        Pl = 0; ClearPic(); crtcInPic = 0; pendFlags |= LF_PICSTART;
        // what is not drawn next time (lines the beam doesn't reach) stays black
        Array.Clear(FbC, 0, FbC.Length); Array.Clear(FbM, 0, FbM.Length);
        Pictures++; PictureDone = true;
    }

    public void NoteAccess(bool write) { int l = Math.Max(0, Math.Min(FBH - 1, Pl)); if (write) LineVW[l]++; else LineVR[l]++; }

    // ---------------------------------------------------------------- one character cell, one scanline
    void DrawChar(int o, int ma, int dpc)
    {
        int addr = ma & 0x7FF;
        int ch = m.Scr[addr];
        bool ext = m.Extended;
        int attr = ext ? m.Attr[addr] : 0;
        int ra = (Adj ? AdjC : Ra) & 15;
        int alt = (ma & 0x2000) != 0 && m.CharRom.Length > 0x800 ? 0x800 : 0;
        int bits;
        if (ext && (attr & 0x80) != 0 && m.FlashPhase) bits = m.CharRom[alt | (0x20 << 4) | ra];
        else if ((ch & 0x80) != 0)
        {
            int bank = ext ? attr & 15 : 0;
            bits = bank < m.PcgBanks ? m.Pcg[(bank << 11) | ((ch & 0x7F) << 4) | ra] : 0;
        }
        else bits = m.CharRom[alt | (ch << 4) | ra];
        if (ext && (attr & 0x40) != 0) bits ^= 0xFF;
        if (ma == CursorAddr)
        {
            int cs = R[10] & 31, ce = R[11] & 31, mode = (R[10] >> 5) & 3;
            int rr = Adj ? AdjC : Ra;
            bool on = mode == 0 || (mode == 2 && (Pictures & 16) != 0) || (mode == 3 && (Pictures & 32) != 0);
            bool inRange = cs <= ce ? (rr >= cs && rr <= ce) : (rr >= cs || rr <= ce);
            if (on && inRange) bits ^= 0xFF;
        }
        byte fg, bg, mfg, mbg;
        if (m.Colour)
        {
            int col = m.Col[addr];
            if (m.Premium) { fg = (byte)(col & 15); bg = (byte)(col >> 4); }
            else { fg = (byte)(64 + StdFg[col & 31]); bg = (byte)(64 + ((StdBg[(m.Port08 >> 1) & 7] << 3) | StdBg[col >> 5])); }
            if (m.HalfIntensity && m.Premium) { mfg = (byte)((col & 8) != 0 ? 3 : 2); mbg = (byte)((col & 0x80) != 0 ? 1 : 0); }
            else { mfg = 3; mbg = 0; }
        }
        else { fg = 15; bg = 0; mfg = 3; mbg = 0; }
        var fc = FbC; var fm = FbM;
        if (dpc == 8)
            for (int i = 0; i < 8; i++)
            {
                bool px = (bits & (0x80 >> i)) != 0;
                fc[o + i] = px ? fg : bg; fm[o + i] = px ? mfg : mbg;
            }
        else
        {
            int k = dpc / 8;
            for (int i = 0; i < 8; i++)
            {
                bool px = (bits & (0x80 >> i)) != 0;
                byte a = px ? fg : bg, b = px ? mfg : mbg;
                for (int j = 0; j < k; j++) { fc[o + i * k + j] = a; fm[o + i * k + j] = b; }
            }
        }
    }

    // the standard (pre-Premium) colour board: 5-bit foreground through the 82S23 PROM, 3-bit background plus the
    // port 08 background intensity bits (ubee512's tables)
    static readonly int[] StdFg = { 0x00, 0x09, 0x12, 0x1b, 0x24, 0x2d, 0x36, 0x3f, 0x0b, 0x13, 0x0d, 0x2d, 0x16, 0x26, 0x07, 0x3f,
                                    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0f, 0x17, 0x1f, 0x27, 0x2f, 0x37, 0x07, 0x3f };
    static readonly int[] StdBg = { 0, 4, 2, 6, 1, 5, 3, 7 };

    // ---------------------------------------------------------------- palettes
    public static int[] PremiumRgb(bool brown)
    {
        int[] p = new int[16];
        for (int i = 0; i < 16; i++)
        {
            bool hi = (i & 8) != 0;
            int on = hi ? 0xFF : 0xAA, off = hi ? 0x55 : 0x00;
            int r = (i & 1) != 0 ? on : off, g = (i & 2) != 0 ? on : off, b = (i & 4) != 0 ? on : off;
            if (i == 3 && brown) g = 0x55;           // RGBI monitors (and CGA converters) show dark yellow as brown
            p[i] = (r << 16) | (g << 8) | b;
        }
        return p;
    }
    public static int StdRgb(int v)
    {
        int b = (v & 1) != 0 ? ((v & 8) != 0 ? 0xFF : 0xAA) : 0;
        int g = (v & 2) != 0 ? ((v & 16) != 0 ? 0xFF : 0xAA) : 0;
        int r = (v & 4) != 0 ? ((v & 32) != 0 ? 0xFF : 0xAA) : 0;
        return (r << 16) | (g << 8) | b;
    }
    // monochrome monitors: background, background half-intensity, foreground half-intensity, foreground (ubee512's values)
    public static readonly int[][] MonoTables = {
        new[] { 0x000000, 0x005000, 0x00D200, 0x00FC00 },   // green
        new[] { 0x000000, 0x9E6E00, 0xEEA600, 0xEEC800 },   // amber
        new[] { 0x000000, 0x505050, 0xB4B4B4, 0xFFFFFF },   // white
    };
    public static readonly string[] MonoNames = { "green", "amber", "white" };
}
