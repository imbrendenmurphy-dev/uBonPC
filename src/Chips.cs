// uBonPC support chips: the Z80 PIO (ports 00-03: A = parallel/joystick, B = speaker, cassette, RS-232, B7 interrupt)
// and the MC146818 real-time clock (ports 04/06/07) fitted to the Premium Plus, 256TC and the later disk models.
using System;

public sealed class PioPort
{
    public int Mode = 1;            // 0 output, 1 input, 2 bidirectional, 3 bit control
    public int Dir = 0xFF;          // mode 3: 1 = input
    public int Out;                 // output register
    public int Vector;
    public bool IntEnable, AndMode, HighActive;
    public int IntMask = 0xFF;      // mode 3: 0 = bit monitored
    bool wantDir, wantMask;
    public bool Pending, InService;
    bool lastCond;
    public int Writes, Reads;

    public void Reset() { Mode = 1; Dir = 0xFF; IntEnable = false; IntMask = 0xFF; wantDir = wantMask = false; Pending = InService = false; lastCond = false; }

    public void Control(int v)
    {
        if (wantDir) { Dir = v; wantDir = false; return; }
        if (wantMask) { IntMask = v; wantMask = false; return; }
        if ((v & 1) == 0) { Vector = v; return; }
        switch (v & 0x0F)
        {
            case 0x0F: Mode = (v >> 6) & 3; if (Mode == 3) wantDir = true; break;
            case 0x07: IntEnable = (v & 0x80) != 0; AndMode = (v & 0x40) != 0; HighActive = (v & 0x20) != 0; if ((v & 0x10) != 0) { wantMask = true; Pending = false; } break;
            case 0x03: IntEnable = (v & 0x80) != 0; break;
        }
    }

    /// <summary>The value the CPU reads, given the input pins.</summary>
    public int Read(int pins)
    {
        Reads++;
        if (Mode == 0) return Out;
        if (Mode == 3) return (pins & Dir) | (Out & ~Dir & 0xFF);
        return pins;
    }
    /// <summary>The level on each pin (outputs from the register, inputs from the outside, pulled high otherwise).</summary>
    public int Pins(int ext) { if (Mode == 0) return Out; if (Mode == 3) return (ext & Dir) | (Out & ~Dir & 0xFF); return ext; }

    /// <summary>Mode 3 interrupt logic: an interrupt is requested when the monitored condition becomes true.</summary>
    public void Update(int pins)
    {
        if (Mode != 3) { lastCond = false; return; }
        int mon = ~IntMask & Dir & 0xFF;
        if (mon == 0) { lastCond = false; return; }
        int act = HighActive ? pins & mon : ~pins & mon;
        bool cond = AndMode ? act == mon : act != 0;
        if (cond && !lastCond && IntEnable) Pending = true;
        lastCond = cond;
    }
}

public sealed class Z80Pio
{
    public readonly PioPort A = new PioPort(), B = new PioPort();
    public void Reset() { A.Reset(); B.Reset(); }
    /// <summary>INT is low while a port has a request and nothing of higher priority is in service.</summary>
    public bool Int
    {
        get
        {
            if (A.InService) return false;
            if (A.Pending && A.IntEnable) return true;
            if (B.InService) return false;
            return B.Pending && B.IntEnable;
        }
    }
    /// <summary>Interrupt acknowledge: returns the vector of the port being served.</summary>
    public int Ack()
    {
        if (A.Pending && A.IntEnable && !A.InService) { A.Pending = false; A.InService = true; return A.Vector; }
        if (B.Pending && B.IntEnable && !B.InService) { B.Pending = false; B.InService = true; return B.Vector; }
        return 0xFF;
    }
    public void Reti() { if (A.InService) A.InService = false; else if (B.InService) B.InService = false; }
}

public sealed class Rtc146818
{
    public readonly byte[] Ram = new byte[64];
    public int Addr;
    DateTime time;
    long tPerSec, nextSecT, nextPerT, periodT;
    public bool Irq { get { return (Ram[12] & 0x80) != 0; } }
    public int Reads, Writes;

    public void PowerOn(DateTime start, long nowT, long cpuHz)
    {
        tPerSec = cpuHz; time = start;
        Array.Clear(Ram, 0, 64);
        Ram[10] = 0x26; Ram[11] = 0x02; Ram[13] = 0x80;   // 32.768 kHz, 1024 Hz rate; 24-hour BCD; RAM valid
        nextSecT = nowT + tPerSec; SetPeriod(nowT);
        Store();
    }

    void SetPeriod(long nowT)
    {
        int rs = Ram[10] & 15;
        if (rs == 0 || ((Ram[10] >> 4) & 7) != 2) { periodT = 0; return; }
        if (rs < 3) rs += 7;
        double hz = 65536.0 / (1 << rs);              // RS 3 = 8192 Hz ... RS 15 = 2 Hz
        periodT = Math.Max(1, (long)(tPerSec / hz));
        nextPerT = nowT + periodT;
    }

    int Bcd(int v) { return (Ram[11] & 4) != 0 ? v : ((v / 10) << 4) | (v % 10); }
    int FromBcd(int v) { return (Ram[11] & 4) != 0 ? v : (v >> 4) * 10 + (v & 15); }
    void Store()
    {
        Ram[0] = (byte)Bcd(time.Second); Ram[2] = (byte)Bcd(time.Minute);
        int h = time.Hour;
        if ((Ram[11] & 2) != 0) Ram[4] = (byte)Bcd(h);
        else { int h12 = h % 12; if (h12 == 0) h12 = 12; Ram[4] = (byte)(Bcd(h12) | (h >= 12 ? 0x80 : 0)); }
        Ram[6] = (byte)((int)time.DayOfWeek + 1); Ram[7] = (byte)Bcd(time.Day); Ram[8] = (byte)Bcd(time.Month); Ram[9] = (byte)Bcd(time.Year % 100);
    }
    void Load()
    {
        try
        {
            int h = (Ram[11] & 2) != 0 ? FromBcd(Ram[4]) : (FromBcd(Ram[4] & 0x7F) % 12) + ((Ram[4] & 0x80) != 0 ? 12 : 0);
            time = new DateTime(2000 + FromBcd(Ram[9]), Math.Max(1, Math.Min(12, FromBcd(Ram[8]))), Math.Max(1, Math.Min(28, FromBcd(Ram[7]))),
                Math.Min(23, h), Math.Min(59, FromBcd(Ram[2])), Math.Min(59, FromBcd(Ram[0])));
        }
        catch { }
    }

    public void Sync(long t)
    {
        while (t >= nextSecT)
        {
            if ((Ram[11] & 0x80) == 0)
            {
                time = time.AddSeconds(1); Store();
                Ram[12] |= 0x10;                                       // update ended
                if (Ram[1] == Ram[0] || (Ram[1] & 0xC0) == 0xC0)
                    if (Ram[3] == Ram[2] || (Ram[3] & 0xC0) == 0xC0)
                        if (Ram[5] == Ram[4] || (Ram[5] & 0xC0) == 0xC0) Ram[12] |= 0x20;   // alarm
            }
            nextSecT += tPerSec;
        }
        if (periodT > 0) while (t >= nextPerT) { Ram[12] |= 0x40; nextPerT += periodT; }
        if ((Ram[12] & Ram[11] & 0x70) != 0) Ram[12] |= 0x80; else Ram[12] &= 0x7F;
    }

    public bool Uip(long t) { return (Ram[11] & 0x80) == 0 && nextSecT - t < tPerSec / 2000; }   // the last ~0.5 ms before an update

    public void WriteAddr(int v) { Addr = v & 63; }
    public void WriteData(int v, long t)
    {
        Writes++;
        Sync(t);
        if (Addr == 12 || Addr == 13) return;
        if (Addr == 10) { Ram[10] = (byte)(v & 0x7F); SetPeriod(t); return; }
        Ram[Addr] = (byte)v;
        if (Addr == 11 && (v & 0x80) != 0) Ram[11] &= 0xEF;
        if (Addr < 10 && Addr != 1 && Addr != 3 && Addr != 5) Load();
        if (Addr == 11) { if ((v & 0x80) == 0) Load(); }
    }
    public int ReadData(long t)
    {
        Reads++;
        Sync(t);
        int v = Ram[Addr];
        if (Addr == 10) v = (v & 0x7F) | (Uip(t) ? 0x80 : 0);
        if (Addr == 12) { Ram[12] = 0; }
        return v;
    }
    public string TimeText { get { return time.ToString("yyyy-MM-dd HH:mm:ss"); } }
}
