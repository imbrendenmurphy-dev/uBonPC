// uBonPC live trace: what the Z80, the bus, the video RAM, the PCG, the keyboard scan, the 1 MB of banked RAM, the
// speaker and the ports are doing, for the visualiser panels (Vis.cs). Attach with m.Tr = new BeeTrace(m).
using System;
using System.Collections.Generic;

public sealed class BeeTrace
{
    readonly BeeMachine m;
    public const int FTMAX = 160000;

    // memory heat, CPU view (255 = touched this field, fades over ~1/2 second)
    public readonly byte[] HR = new byte[65536], HW = new byte[65536], HX = new byte[65536];
    public readonly bool[] Start = new bool[65536];
    public readonly bool[] CallTarget = new bool[65536];
    readonly long[] visit = new long[65536];
    // physical RAM heat: 1 MB in 256-byte pages
    public readonly byte[] PR = new byte[4096], PW = new byte[4096];

    public readonly long[] Prof = new long[65536]; public long[] ProfShown = new long[65536]; public long ProfShownT;
    public readonly int[] Ops = new int[256]; public int[] OpsShown = new int[256]; public long OpsShownN;
    public int ProfVersion; int window;

    // per-T-state activity for one field and bus overlays (double buffered: *Shown = the last whole field)
    public const byte S_NONE = 0, S_BUSY = 1, S_IDLE = 2, S_ISR = 3, S_WAIT = 4;
    public const byte O_SCR = 1, O_ATTR = 2, O_COL = 3, O_PCG = 4, O_VR = 5, O_SND = 6, O_OUT = 7, O_IN = 8, O_CRTC = 9, O_FDC = 10, O_KEY = 11;
    byte[] ras = new byte[FTMAX], ov = new byte[FTMAX];
    public byte[] Ras = new byte[FTMAX], Ov = new byte[FTMAX];
    public int FieldT = 67500, FieldShownT = 67500;

    // video accesses by raster line (from the CRTC)
    public int[] LineVW = new int[BeeVideo.FBH], LineVR = new int[BeeVideo.FBH]; public int LinesShown = 312;

    long lastWriteT, idleMark; bool inIsr; int isrSp; long isrT0;
    int intsFrame; public int Ints;

    public const int HIST = 300;
    public readonly byte[] HBusy = new byte[HIST], HIsr = new byte[HIST], HWait = new byte[HIST];
    public readonly int[] HSp = new int[HIST], HLen = new int[HIST]; public int HPos;
    public double Busy, Isr, Idle, VWait;
    int spMin = 0xFFFF; public int SpMinSecond = 0xFFFF; int spMinWin = 0xFFFF; int spMaxWin; public int SpMaxSecond = 0xFFFF;

    // ports
    public readonly byte[] PIn = new byte[256], POut = new byte[256];
    public readonly int[] PValIn = new int[256], PValOut = new int[256];
    readonly int[] pCount = new int[512]; public int[] PCountShown = new int[512];
    public readonly List<int> Recent = new List<int>();

    // video, PCG, keyboard
    public readonly byte[] CellW = new byte[2048], CellC = new byte[2048], CellA = new byte[2048];   // screen / colour / attribute writes per address
    public readonly byte[] PcgCharW = new byte[16 * 128]; public readonly int[] PcgBankW = new int[16]; int[] pcgBankN = new int[16]; public int[] PcgBankShown = new int[16];
    int scrW, colW, attrW, pcgW, vidR; public int ScrWrites, ColWrites, AttrWrites, PcgWrites, VidReads;
    public readonly byte[] KeyTested = new byte[64];
    int keyTests; public int KeyTests;
    public int LastPcgBank = -1;

    // speaker
    public float[] Spk = new float[4000], SpkShown = new float[4000]; int spkN; public int SpkShownN;
    public readonly float[] SpkRing = new float[2048]; public int SpkRingPos;
    int sndW; public int SoundWrites;

    readonly Random rnd = new Random(1);
    int sampleT, sampleCand = -1, lastSample = -1; public int ShownPc = -1; int frameNo;

    static readonly byte[] decay = new byte[256];
    static BeeTrace() { for (int i = 0; i < 256; i++) decay[i] = (byte)Math.Max(0, i - (i >> 4) - 2); }

    public BeeTrace(BeeMachine machine) { m = machine; sampleT = rnd.Next(60000); }

    int RelOf(long t) { long r = t - m.FieldStartT; return r < 0 ? 0 : r >= FTMAX ? FTMAX - 1 : (int)r; }
    int Rel { get { return RelOf(m.Cpu.Cyc - 3); } }

    int Phys(int a)
    {
        if (!m.Model.Dram) return a < 0x8000 ? a : -1;
        if (m.VidOn && (a & 0xF000) == m.VidBase) return -1;
        if (a < 0x8000) return m.LowBlock * 0x8000 + a;
        if (m.RomOn && a < 0xC000) return -1;
        return a & 0x7FFF;
    }

    // ---------------------------------------------------------------- bus events
    public void R(int a) { HR[a] = 255; int p = Phys(a); if (p >= 0) PR[(p >> 8) & 4095] = 255; }
    public void X(int a) { HX[a] = 255; }
    public void W(int a, int v)
    {
        HW[a] = 255; lastWriteT = m.Cpu.Cyc;
        int p = Phys(a); if (p >= 0) PW[(p >> 8) & 4095] = 255;
    }
    public void Vid(int a, bool write)
    {
        int o = a & 0x7FF;
        if (!write) { vidR++; if (ov[Rel] == 0) ov[Rel] = O_VR; return; }
        lastWriteT = m.Cpu.Cyc;
        byte kind;
        if ((a & 0x800) != 0) kind = m.Colour && (m.Port08 & 0x40) != 0 ? O_COL : O_PCG;
        else kind = m.Premium && (m.Port1C & 0x10) != 0 ? O_ATTR : O_SCR;
        if (kind == O_COL) { colW++; CellC[o] = 255; }
        else if (kind == O_ATTR) { attrW++; CellA[o] = 255; }
        else if (kind == O_SCR) { scrW++; CellW[o] = 255; }
        ov[Rel] = kind;
    }
    public void PcgWrite(int bank, int o) { pcgW++; PcgCharW[(bank << 7) | (o >> 4)] = 255; pcgBankN[bank]++; LastPcgBank = bank; }
    public void KeyTest(int key) { KeyTested[key & 63] = 255; keyTests++; ov[Rel] = O_KEY; }
    public void Sound() { sndW++; ov[Rel] = O_SND; }
    public void Port(int lo, int v, bool isOut)
    {
        if (isOut) { POut[lo] = 255; PValOut[lo] = v; pCount[256 + lo]++; lastWriteT = m.Cpu.Cyc; }
        else { PIn[lo] = 255; PValIn[lo] = v; pCount[lo]++; }
        int r = Rel;
        if (ov[r] == 0 || ov[r] == O_IN || ov[r] == O_OUT)
            ov[r] = (lo == 0x0C || lo == 0x0D) ? O_CRTC : (lo >= 0x40 && lo <= 0x4F) ? O_FDC : isOut ? O_OUT : O_IN;
        int key = (isOut ? 256 : 0) + lo;
        if (Recent.Count == 0 || Recent[0] != key) { Recent.Remove(key); Recent.Insert(0, key); if (Recent.Count > 20) Recent.RemoveAt(20); }
    }
    readonly List<long> waitT = new List<long>(); readonly List<int> waitN = new List<int>();
    public void Wait(long t, int n) { waitT.Add(t); waitN.Add(n); }
    public void Sample(double lvl)
    {
        float f = (float)lvl;
        if (spkN < Spk.Length) Spk[spkN++] = f;
        SpkRing[SpkRingPos] = f; SpkRingPos = (SpkRingPos + 1) & 2047;
    }
    public void CrtcFrame(int[] vw, int[] vr, int lines)
    {
        Array.Copy(vw, LineVW, BeeVideo.FBH); Array.Copy(vr, LineVR, BeeVideo.FBH); LinesShown = Math.Max(1, lines);
    }

    // ---------------------------------------------------------------- the last picture, line by line
    // Video hands over each finished picture's line records; at the end of the field they are joined with what the
    // CPU was doing at each T-state (from this field's and the last field's buffers) into one image per line.
    public const int LMAX = BeeVideo.LMAX, LW = 256;
    readonly long[] pStart = new long[LMAX];
    readonly int[] pRow = new int[LMAX], pRa = new int[LMAX], pMa = new int[LMAX], pFrame = new int[LMAX], pFlags = new int[LMAX], pChars = new int[LMAX], pCt = new int[LMAX];
    readonly byte[] pBeam = new byte[LMAX * BeeVideo.LCH];
    int pLines; bool picPend; long prevFieldStart = long.MinValue;
    public readonly byte[] LCpu = new byte[LMAX * LW], LOv = new byte[LMAX * LW], LBeam = new byte[LMAX * LW];
    public readonly int[] LRow = new int[LMAX], LRa = new int[LMAX], LMa = new int[LMAX], LFrame = new int[LMAX], LFlags = new int[LMAX], LLen = new int[LMAX], LCt = new int[LMAX], LWait = new int[LMAX];
    public readonly int[] LVW = new int[LMAX], LVR = new int[LMAX];
    public int PicLines, PicFrames;

    public void Picture(BeeVideo v, int lines)
    {
        pLines = Math.Max(0, Math.Min(LMAX, lines));
        Array.Copy(v.LStart, pStart, LMAX); Array.Copy(v.LRow, pRow, LMAX); Array.Copy(v.LRa, pRa, LMAX); Array.Copy(v.LMa, pMa, LMAX);
        Array.Copy(v.LFrame, pFrame, LMAX); Array.Copy(v.LFlags, pFlags, LMAX); Array.Copy(v.LChars, pChars, LMAX); Array.Copy(v.LCt, pCt, LMAX);
        Array.Copy(v.LBeam, pBeam, pBeam.Length);
        Array.Clear(LVW, 0, LMAX); Array.Clear(LVR, 0, LMAX);
        Array.Copy(v.LineVW, LVW, Math.Min(LMAX, BeeVideo.FBH)); Array.Copy(v.LineVR, LVR, Math.Min(LMAX, BeeVideo.FBH));
        var t = bwPend; bwPend = bwCur; bwCur = t; bwCur.Clear(); bwPendTotal = bwTotal; bwTotal = 0;
        picPend = true;
    }

    void BuildLines()
    {
        long f0 = m.FieldStartT, fp = prevFieldStart;
        PicLines = pLines; PicFrames = 0;
        for (int l = 0; l < pLines; l++)
        {
            int ct = Math.Max(1, pCt[l]), len = Math.Min(LW, pChars[l] * ct);
            LLen[l] = len; LCt[l] = ct; LRow[l] = pRow[l]; LRa[l] = pRa[l]; LMa[l] = pMa[l]; LFrame[l] = pFrame[l]; LFlags[l] = pFlags[l];
            if (pFrame[l] + 1 > PicFrames) PicFrames = pFrame[l] + 1;
            long s = pStart[l]; int o = l * LW, bo = l * BeeVideo.LCH, w = 0;
            for (int t = 0; t < len; t++)
            {
                long a = s + t; byte c = 0, ovv = 0;
                if (a >= f0) { long r = a - f0; if (r < FTMAX) { c = ras[r]; ovv = ov[r]; } }
                else if (fp != long.MinValue && a >= fp) { long r = a - fp; if (r < FTMAX) { c = Ras[r]; ovv = Ov[r]; } }
                LCpu[o + t] = c; LOv[o + t] = ovv;
                int ch = t / ct; LBeam[o + t] = ch < BeeVideo.LCH ? pBeam[bo + ch] : (byte)0;
                if (c == S_WAIT) w++;
            }
            LWait[l] = w;
        }
        var b = BwShown; BwShown = bwPend; bwPend = b; bwPend.Clear(); BwShownTotal = bwPendTotal;
    }

    // writes that change the picture (6545 registers, ports 08 / 0B / 1C / 50) with where the beam was
    public struct BeamW { public int Line, T, Code, V; }
    List<BeamW> bwCur = new List<BeamW>(), bwPend = new List<BeamW>();
    public List<BeamW> BwShown = new List<BeamW>();
    int bwTotal, bwPendTotal; public int BwShownTotal;
    public void BeamWrite(int code, int v)
    {
        bwTotal++;
        if (bwCur.Count < 4000) bwCur.Add(new BeamW { Line = m.Video.Pl, T = m.Video.Hc * m.Video.CharT, Code = code, V = v });
    }

    // the PIO's pins, sampled once a scanline
    public const int PMAX = 1200;
    byte[] pioB = new byte[PMAX], pioA = new byte[PMAX]; bool[] pioI = new bool[PMAX]; int pioN;
    public byte[] PioBShown = new byte[PMAX], PioAShown = new byte[PMAX]; public bool[] PioIShown = new bool[PMAX]; public int PioShownN;
    public void PioSample(int b, int a, bool intLine)
    {
        if (pioN >= PMAX) return;
        pioB[pioN] = (byte)b; pioA[pioN] = (byte)a; pioI[pioN] = intLine; pioN++;
    }

    // ---------------------------------------------------------------- instructions
    public void Step(int pc, long t0, long t1, bool intTaken, bool halted)
    {
        byte st;
        X(pc);
        if (intTaken)
        {
            if (!inIsr) { inIsr = true; isrSp = (m.Cpu.SP + 2) & 0xFFFF; isrT0 = t0; }
            intsFrame++; st = S_ISR; Prof[m.Cpu.PC] += t1 - t0;
        }
        else
        {
            Start[pc] = true;
            Prof[pc] += t1 - t0;
            int op = m.Peek(pc);
            Ops[op]++;
            st = inIsr ? S_ISR : halted ? S_IDLE : S_BUSY;
            if (!halted)
            {
                if (op == 0xCD || (op & 0xC7) == 0xC4) { if (m.Cpu.PC != ((pc + 3) & 0xFFFF)) CallTarget[m.Cpu.PC] = true; }
                else if ((op & 0xC7) == 0xC7) CallTarget[op & 0x38] = true;
            }
            if (st == S_BUSY)
            {
                long pv = visit[pc]; visit[pc] = t0;
                // a tight loop that hasn't written anything since it last passed here is waiting (polling)
                if (pv != 0 && pv < t0 && pv > lastWriteT && t0 - pv < 300)
                {
                    int from = RelOf(Math.Max(pv, idleMark)), to = RelOf(t0);
                    for (int r = from; r < to; r++) if (ras[r] == S_BUSY) ras[r] = S_IDLE;
                    st = S_IDLE;
                }
            }
            if (inIsr && (m.Cpu.SP >= isrSp || t1 - isrT0 > 200000)) inIsr = false;
        }
        if (st == S_IDLE) idleMark = t1;
        int r0 = RelOf(t0), r1 = RelOf(t1);
        for (int r = r0; r < r1; r++) ras[r] = st;
        for (int i = 0; i < waitT.Count; i++) { int w0 = RelOf(waitT[i]); for (int r = w0; r < w0 + waitN[i] && r < FTMAX; r++) ras[r] = S_WAIT; }
        waitT.Clear(); waitN.Clear();
        if (sampleT >= r0 && sampleT < r1 && st == S_BUSY) sampleCand = pc;
        int sp = m.Cpu.SP; if (sp < spMin) spMin = sp; if (sp > spMaxWin) spMaxWin = sp;
    }

    public void ResetTimes() { Array.Clear(visit, 0, visit.Length); lastWriteT = idleMark = 0; inIsr = false; }

    // ---------------------------------------------------------------- end of a field
    public void EndFrame(long len)
    {
        FieldT = (int)Math.Min(FTMAX, Math.Max(1, len));
        if (picPend) { picPend = false; BuildLines(); }
        { var pb = PioBShown; PioBShown = pioB; pioB = pb; var pa = PioAShown; PioAShown = pioA; pioA = pa; var pi = PioIShown; PioIShown = pioI; pioI = pi; PioShownN = pioN; pioN = 0; }
        var sw = SpkShown; SpkShown = Spk; Spk = sw; SpkShownN = spkN; spkN = 0;
        SoundWrites = sndW; sndW = 0;

        int nb = 0, ni = 0, nx = 0, nw = 0;
        for (int i = 0; i < FieldT; i++) switch (ras[i]) { case S_BUSY: case S_NONE: nb++; break; case S_ISR: ni++; break; case S_WAIT: nw++; break; default: nx++; break; }
        Busy = (double)nb / FieldT; Isr = (double)ni / FieldT; Idle = (double)nx / FieldT; VWait = (double)nw / FieldT;
        HBusy[HPos] = (byte)Math.Round(Busy * 100); HIsr[HPos] = (byte)Math.Round(Isr * 100); HWait[HPos] = (byte)Math.Round(VWait * 100);
        HSp[HPos] = spMin; HLen[HPos] = FieldT; HPos = (HPos + 1) % HIST;
        if (spMin < spMinWin) spMinWin = spMin;
        spMin = 0xFFFF;

        var b = Ras; Ras = ras; ras = b; Array.Clear(ras, 0, FTMAX);
        b = Ov; Ov = ov; ov = b; Array.Clear(ov, 0, FTMAX);
        FieldShownT = FieldT;
        prevFieldStart = m.FieldStartT;

        Ints = intsFrame; intsFrame = 0;
        ScrWrites = scrW; ColWrites = colW; AttrWrites = attrW; PcgWrites = pcgW; VidReads = vidR; scrW = colW = attrW = pcgW = vidR = 0;
        KeyTests = keyTests; keyTests = 0;
        Array.Copy(pcgBankN, PcgBankShown, 16); Array.Clear(pcgBankN, 0, 16);
        Array.Copy(pCount, PCountShown, 512); Array.Clear(pCount, 0, 512);

        for (int i = 0; i < 65536; i++) { HR[i] = decay[HR[i]]; HW[i] = decay[HW[i]]; HX[i] = decay[HX[i]]; }
        for (int i = 0; i < 4096; i++) { PR[i] = decay[PR[i]]; PW[i] = decay[PW[i]]; }
        for (int i = 0; i < 2048; i++) { CellW[i] = decay[CellW[i]]; CellC[i] = decay[CellC[i]]; CellA[i] = decay[CellA[i]]; PcgCharW[i] = decay[PcgCharW[i]]; }
        for (int i = 0; i < 256; i++) { PIn[i] = decay[PIn[i]]; POut[i] = decay[POut[i]]; }
        for (int i = 0; i < 64; i++) KeyTested[i] = decay[decay[KeyTested[i]]];

        if (sampleCand >= 0 && Ras[sampleT] == S_BUSY) lastSample = sampleCand;
        sampleCand = -1; sampleT = rnd.Next(Math.Max(1, FieldT));
        frameNo++;
        if (frameNo % 6 == 0 || ShownPc < 0) ShownPc = lastSample >= 0 ? lastSample : m.Cpu.PC;

        if (++window >= 50)
        {
            window = 0;
            long tot = 0;
            for (int i = 0; i < 65536; i++) { ProfShown[i] = Prof[i]; tot += Prof[i]; Prof[i] = 0; }
            ProfShownT = tot;
            long n = 0;
            for (int i = 0; i < 256; i++) { OpsShown[i] = Ops[i]; n += Ops[i]; Ops[i] = 0; }
            OpsShownN = n;
            SpMinSecond = spMinWin; spMinWin = 0xFFFF; SpMaxSecond = spMaxWin == 0 ? 0xFFFF : spMaxWin; spMaxWin = 0;
            ProfVersion++;
        }
    }
}
