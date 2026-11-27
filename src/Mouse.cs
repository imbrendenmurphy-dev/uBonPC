// uBonPC serial mouse: a Microsoft 2-button mouse (1200 baud, 7N1, 3-byte packets) on the Microbee's serial lines.
//  SerialCts  - raw serial on PIO B bit 3 (CTS): the user's Premium Plus interface, read in software by Empires.
//  SerialRx   - raw serial on PIO B bit 4 (RX), the standard serial input.
//  Clocked    - ubee512's Microbee mouse interface: bit 2 (DTR) pulses high at each serial bit and interrupts the
//               Z80 through the PIO; bit 3 (CTS) carries the inverted data. Cleared when the interrupt is taken.
// On the serial lines mark (idle, stop, data 1) reads 0 and space (start, data 0) reads 1, as the RS-232 receivers
// give it (a plugged-in mouse holds CTS low); Invert swaps that.
using System;
using System.Collections.Generic;

public enum MouseMode { Off, SerialCts, SerialRx, Clocked }

public sealed class BeeMouse
{
    public MouseMode Mode = MouseMode.Off;      // a plugged-in mouse holds CTS low, which some software reads as "mouse present"
    public bool Invert;
    public const int BIT_T = 2812;                 // 3.375 MHz / 1200 baud
    readonly Queue<byte> pending = new Queue<byte>();
    byte cur; long curT = long.MinValue; bool active;
    public int Packets, Dropped;
    public int LastDx, LastDy; public bool LastL, LastR;
    // clocked interface state
    long clkNext; int clkBit, clkByte; byte[] clkPkt; bool clkActive; public bool Dtr, Cts;

    public bool On { get { return Mode != MouseMode.Off; } }
    public bool Busy(long t) { return active || pending.Count > 0 || clkActive; }

    public void Reset() { pending.Clear(); active = false; clkActive = false; Dtr = Cts = false; }

    /// <summary>Queues a Microsoft mouse packet: dx, dy (-128..127, y down), buttons.</summary>
    public void Packet(int dx, int dy, bool l, bool r, long t)
    {
        dx = Math.Max(-128, Math.Min(127, dx)); dy = Math.Max(-128, Math.Min(127, dy));
        byte b1 = (byte)(0x40 | (l ? 0x20 : 0) | (r ? 0x10 : 0) | (((dy >> 6) & 3) << 2) | ((dx >> 6) & 3));
        byte b2 = (byte)(dx & 0x3F), b3 = (byte)(dy & 0x3F);
        LastDx = dx; LastDy = dy; LastL = l; LastR = r; Packets++;
        if (Mode == MouseMode.Clocked) { if (clkActive) { Dropped++; return; } clkPkt = new[] { b1, b2, b3 }; clkActive = true; clkBit = 0; clkByte = 0; clkNext = t; return; }
        if (pending.Count > 6) { Dropped++; return; }
        pending.Enqueue(b1); pending.Enqueue(b2); pending.Enqueue(b3);
        if (!active) Next(t);
    }

    void Next(long t)
    {
        if (pending.Count == 0) { active = false; return; }
        long start = Math.Max(t, curT == long.MinValue ? t : curT + 9 * BIT_T + BIT_T / 3);
        cur = pending.Dequeue(); curT = start; active = true;
    }

    /// <summary>The serial line at T-state t: 1 = space (start bit, data 0), 0 = mark (idle, data 1, stop).</summary>
    public int Line(long t)
    {
        int v = 0;
        while (active)
        {
            long dt = t - curT;
            if (dt < 0) break;
            int bit = (int)(dt / BIT_T);
            if (bit >= 9) { Next(curT + 9 * BIT_T); if (!active) break; continue; }
            int mark = bit == 0 ? 0 : bit <= 7 ? (cur >> (bit - 1)) & 1 : 1;
            v = mark != 0 ? 0 : 1;
            break;
        }
        return v ^ (Invert ? 1 : 0);
    }

    /// <summary>The next T-state at which the line can change (for interrupts and the visualiser).</summary>
    public long NextT
    {
        get
        {
            if (Mode == MouseMode.Clocked) return clkActive ? clkNext : long.MaxValue;
            return active ? curT : long.MaxValue;
        }
    }

    // ---------------------------------------------------------------- ubee512's clocked interface
    public void ClockSync(long t)
    {
        if (!clkActive || t < clkNext) return;
        while (clkActive && t >= clkNext)
        {
            clkNext += BIT_T;
            if (clkBit == 0)
            {
                if (clkByte == 3) { clkActive = false; Dtr = false; Cts = false; break; }
                clkBit = 1; Cts = true; Dtr = true;                     // start bit
            }
            else if (clkBit == 8) { clkBit = 0; clkByte++; Cts = false; Dtr = true; }     // stop bit
            else { Cts = ((clkPkt[clkByte] >> (clkBit - 1)) & 1) == 0; Dtr = true; clkBit++; }
        }
    }
    public void ClockAck() { Dtr = false; }

    /// <summary>PIO B input bits the mouse drives (and the mask of bits it owns).</summary>
    public int Pins(long t, out int mask)
    {
        switch (Mode)
        {
            case MouseMode.SerialCts: mask = 0x08; return Line(t) << 3;
            case MouseMode.SerialRx: mask = 0x10; return Line(t) << 4;
            case MouseMode.Clocked: ClockSync(t); mask = 0x0C; return (Dtr ? 0x04 : 0) | (Cts ? 0x08 : 0);
        }
        mask = 0; return 0;
    }

    public static readonly string[] ModeNames = { "off", "cts", "rx", "clocked" };
}
