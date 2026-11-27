// uBonPC floppy controller: the WD2793 on ports 44-47 (also 40-43) with the drive / side / density latch on 48
// (read: bit 7 = INTRQ or DRQ). Real timing: 300 rpm, sectors spread round the track, 32 us a byte (MFM), step rates,
// head settle, lost data when a byte isn't taken in time, record-not-found after five revolutions.
using System;
using System.Collections.Generic;

public sealed class BeeFdc
{
    public const int DRIVES = 4;
    public readonly BeeDisk[] Disk = new BeeDisk[DRIVES];
    public readonly int[] HeadCyl = new int[DRIVES];
    public int Drive, Side; public bool Mfm = true;
    public int Track, Sector, Data, Cmd = 0xD0, Status;
    public bool Busy, Drq, Intrq;
    public string LastCmd = "";
    public int SectorsRead, SectorsWritten, LostData, Commands, Seeks;
    public readonly byte[] HeatR = new byte[200], HeatW = new byte[200];
    public Action<string> Log;
    public bool Fast = true;      // no rotational delay and quick steps (SD card / Gotek), or a real 300 rpm drive
    public DiskSound Snd;          // drive sounds (steps, head load, motor), if fitted
    int fastIdx;
    public bool Debug = Environment.GetEnvironmentVariable("UBEE_FDCDEBUG") == "1";

    readonly long cpuHz;
    readonly long rev;            // T-states per revolution
    long byteT;                   // T-states per byte
    long nextT = long.MaxValue;
    int state; const int S_IDLE = 0, S_STEP = 1, S_SETTLE = 2, S_SEARCH = 3, S_READ = 4, S_WRITEFIRST = 5, S_WRITE = 6, S_RADDR = 7, S_RTRACK = 8, S_WTRACKFIRST = 9, S_WTRACK = 10, S_DONE = 11, S_DELAY = 12;
    bool typeI = true; int stepDir = 1, stepsLeft; long searchUntil;
    byte[] buf; int pos, len; BeeSector cur;
    List<byte> wt;
    bool lostThis;

    public BeeFdc(long hz) { cpuHz = hz; rev = hz / 5; byteT = hz * 32 / 1000000; }

    public bool Active { get { return Busy; } }
    public long NextT { get { return nextT; } }
    BeeDisk D { get { return Disk[Drive]; } }

    public void Reset() { Busy = Drq = Intrq = false; state = S_IDLE; nextT = long.MaxValue; Status = 0; Track = 0; Sector = 1; typeI = true; Cmd = 0xD0; }

    long RotPos(long t) { return t % rev; }
    bool Index(long t) { return D != null && RotPos(t) < rev / 50; }   // a 4 ms index pulse

    int StepT { get { int[] ms = { 3, 6, 10, 15 }; return Fast ? 300 : (int)(cpuHz * ms[Cmd & 3] / 1000); } }

    // ---------------------------------------------------------------- ports
    public int ReadStatus(long t)
    {
        Sync(t);
        Intrq = false;
        if (typeI)
        {
            int s = Status & ~(0x80 | 0x40 | 0x04 | 0x02);
            if (D == null) s |= 0x80;
            if (D != null && D.WriteProtect) s |= 0x40;
            if (HeadCyl[Drive] == 0) s |= 0x04;
            if (Index(t)) s |= 0x02;
            return s | (Busy ? 1 : 0);
        }
        return (Status & ~3) | (Drq ? 2 : 0) | (Busy ? 1 : 0) | (D == null ? 0x80 : 0);
    }
    public int ReadData(long t) { Sync(t); Drq = false; return Data; }
    public void WriteData(int v, long t) { Sync(t); Data = v; Drq = false; }
    public void WriteTrack(int v, long t) { Sync(t); if (!Busy) Track = v; }
    public void WriteSector(int v, long t) { Sync(t); if (!Busy) Sector = v; }
    public int Port48 { get { return (Intrq || Drq) ? 0xFF : 0x7F; } }
    public void Latch(int v, long t) { Sync(t); Drive = v & 3; Side = (v >> 2) & 1; Mfm = (v & 8) != 0 || true; byteT = cpuHz * (Mfm ? 32 : 64) / 1000000; }

    public void Command(int v, long t)
    {
        Sync(t);
        if ((v & 0xF0) == 0xD0) { ForceInterrupt(v); return; }
        if (Busy) return;
        Cmd = v; Commands++; Intrq = false; Drq = false; lostThis = false;
        if (Snd != null) Snd.Command(t);
        if (Debug && Log != null) Log("FDC cmd " + v.ToString("X2") + " T" + Track + " S" + Sector + " cyl " + HeadCyl[Drive] + "/" + Side + " t " + t + " rot " + (RotPos(t) * 1000 / rev));
        Busy = true; Status = 0;
        int hi = v >> 4;
        if (hi < 8)
        {
            typeI = true;
            if (hi == 0) { LastCmd = "RESTORE"; stepsLeft = 255; stepDir = -1; Track = 0xFF; }
            else if (hi == 1) { LastCmd = "SEEK " + Data; stepDir = Data > Track ? 1 : -1; stepsLeft = Math.Abs(Data - Track); }
            else
            {
                if (hi >= 4 && hi <= 5) stepDir = 1; else if (hi >= 6) stepDir = -1;
                LastCmd = hi >= 6 ? "STEP OUT" : hi >= 4 ? "STEP IN" : "STEP";
                stepsLeft = 1;
            }
            Seeks++;
            state = S_STEP; nextT = t + 16;
            return;
        }
        typeI = false;
        if (D == null) { Finish(t, 0x80); return; }
        if (hi < 0xC)
        {
            bool write = hi >= 0xA;
            LastCmd = (write ? "WRITE" : "READ") + ((v & 0x10) != 0 ? " MULTI" : "") + " T" + Track + " S" + Sector;
            if (write && D.WriteProtect) { Finish(t, 0x40); return; }
            state = S_DELAY; nextT = t + ((v & 4) != 0 ? cpuHz * 15 / 1000 : 16);
            return;
        }
        if (hi == 0xC) { LastCmd = "READ ADDRESS"; state = S_DELAY; nextT = t + ((v & 4) != 0 ? cpuHz * 15 / 1000 : 16); return; }
        if (hi == 0xE) { LastCmd = "READ TRACK"; state = S_DELAY; nextT = t + 16; return; }
        LastCmd = "WRITE TRACK";
        if (D.WriteProtect) { Finish(t, 0x40); return; }
        state = S_DELAY; nextT = t + 16;
    }

    void ForceInterrupt(int v)
    {
        LastCmd = "FORCE INT";
        if (!Busy) { typeI = true; Status = 0; }
        Busy = false; Drq = false; state = S_IDLE; nextT = long.MaxValue;
        if ((v & 0x0F) != 0) Intrq = true;
    }

    void Finish(long t, int status)
    {
        Status = status | (lostThis ? 0x04 : 0);
        if (Snd != null) Snd.Busy(t);
        Busy = false; Drq = false; Intrq = true; state = S_IDLE; nextT = long.MaxValue;
    }

    // ---------------------------------------------------------------- the state machine
    public void Sync(long t)
    {
        while (nextT <= t)
        {
            long now = nextT;
            switch (state)
            {
                case S_STEP:
                    {
                        int hi = Cmd >> 4;
                        if (hi == 0)
                        {
                            if (HeadCyl[Drive] == 0) { Track = 0; state = S_SETTLE; nextT = now + 16; break; }
                            HeadCyl[Drive]--; if (Snd != null) Snd.Step(now); nextT = now + StepT; break;
                        }
                        if (stepsLeft <= 0) { state = S_SETTLE; nextT = now + 16; break; }
                        if (hi == 1) Track = (Track + stepDir) & 255;
                        else if ((Cmd & 0x10) != 0) Track = (Track + stepDir) & 255;
                        int was = HeadCyl[Drive];
                        HeadCyl[Drive] = Math.Max(0, Math.Min(83, HeadCyl[Drive] + stepDir));
                        if (Snd != null && HeadCyl[Drive] != was) Snd.Step(now);
                        stepsLeft--; nextT = now + StepT;
                        break;
                    }
                case S_SETTLE:
                    {
                        int st = 0x20;    // head loaded
                        if ((Cmd & 4) != 0)
                        {
                            // verify: an ID field on this cylinder must carry the track register's number
                            bool ok = D != null && D.HasTrack(HeadCyl[Drive], Side) && D.Track(HeadCyl[Drive], Side).Exists(x => x.C == Track);
                            if (!ok) st |= 0x10;
                            Finish(now, st);
                            HeatR[Math.Min(199, HeadCyl[Drive] * 2 + Side)] = 255;
                        }
                        else Finish(now, st);
                        break;
                    }
                case S_DELAY:
                    {
                        int hi = Cmd >> 4;
                        if (hi == 0xE) { BuildRawTrack(); state = S_RTRACK; nextT = NextIndex(now); break; }
                        if (hi == 0xF) { wt = new List<byte>(); Drq = true; state = S_WTRACKFIRST; nextT = NextIndex(now); break; }
                        state = hi == 0xC ? S_RADDR : S_SEARCH; searchUntil = now + rev * 5;
                        ScheduleNextId(now);
                        break;
                    }
                case S_SEARCH:
                    {
                        var sec = cur;
                        bool match = sec != null && sec.C == Track && sec.R == Sector && ((Cmd & 2) == 0 || sec.H == ((Cmd >> 3) & 1));
                        if (match)
                        {
                            bool write = (Cmd >> 4) >= 0xA;
                            if (sec.CrcError && !write) { Finish(now, 0x08); break; }
                            if (write)
                            {
                                buf = new byte[sec.Data.Length]; pos = 0; len = buf.Length;
                                Drq = true; state = S_WRITEFIRST; nextT = now + byteT * 22;   // the DRQ for the first byte comes before the data mark
                            }
                            else
                            {
                                buf = sec.Data; pos = 0; len = buf.Length;
                                state = S_READ; nextT = now + byteT * 44;
                            }
                            break;
                        }
                        if (now >= searchUntil) { Finish(now, 0x10); break; }
                        ScheduleNextId(now);
                        break;
                    }
                case S_READ:
                    {
                        if (pos < len)
                        {
                            if (Drq) { lostThis = true; LostData++; if (Debug && Log != null) Log("FDC lost data: byte " + pos + " of " + LastCmd); }
                            Data = buf[pos++]; Drq = true;
                            nextT = now + byteT;
                            break;
                        }
                        // CRC bytes
                        HeatR[Math.Min(199, HeadCyl[Drive] * 2 + Side)] = 255; SectorsRead++;
                        int rt = cur != null && cur.Deleted ? 0x20 : 0;
                        if ((Cmd & 0x10) != 0)
                        {
                            Sector = (Sector + 1) & 255; state = S_SEARCH; searchUntil = now + rev * 5;
                            ScheduleNextId(now + byteT * 2);
                            break;
                        }
                        Drq = false;
                        Finish(now + byteT * 2, rt);
                        break;
                    }
                case S_WRITEFIRST:
                    {
                        if (Drq) { Finish(now, 0x04); LostData++; break; }    // no first byte: the write never starts
                        state = S_WRITE; buf[pos++] = (byte)Data; Drq = true; nextT = now + byteT;
                        break;
                    }
                case S_WRITE:
                    {
                        if (pos < len)
                        {
                            if (Drq) { lostThis = true; LostData++; buf[pos++] = 0; }
                            else buf[pos++] = (byte)Data;
                            if (pos < len) Drq = true;
                            nextT = now + byteT;
                            break;
                        }
                        Array.Copy(buf, cur.Data, Math.Min(buf.Length, cur.Data.Length));
                        cur.Deleted = (Cmd & 1) != 0;
                        D.Dirty = true; D.Writes++; SectorsWritten++;
                        HeatW[Math.Min(199, HeadCyl[Drive] * 2 + Side)] = 255;
                        if ((Cmd & 0x10) != 0)
                        {
                            Sector = (Sector + 1) & 255; state = S_SEARCH; searchUntil = now + rev * 5;
                            ScheduleNextId(now + byteT * 2);
                            break;
                        }
                        Finish(now + byteT * 2, 0);
                        break;
                    }
                case S_RADDR:
                    {
                        if (cur == null) { if (now >= searchUntil) Finish(now, 0x10); else ScheduleNextId(now); break; }
                        buf = new byte[] { (byte)cur.C, (byte)cur.H, (byte)cur.R, (byte)cur.N, 0, 0 };
                        ushort crc = Crc(new byte[] { 0xA1, 0xA1, 0xA1, 0xFE, buf[0], buf[1], buf[2], buf[3] });
                        buf[4] = (byte)(crc >> 8); buf[5] = (byte)crc;
                        pos = 0; len = 6; Sector = cur.C;
                        state = S_RTRACK; nextT = now + byteT;
                        break;
                    }
                case S_RTRACK:
                    {
                        if (pos < len)
                        {
                            // bytes left unread here (a BIOS often takes only C H R N of an ID) are not flagged as lost data

                            Data = buf[pos++]; Drq = true; nextT = now + byteT;
                            break;
                        }
                        Drq = false; Finish(now, 0);
                        break;
                    }
                case S_WTRACKFIRST:
                    {
                        if (Drq) { Finish(now, 0x04); LostData++; break; }
                        state = S_WTRACK; wt.Add((byte)Data); Drq = true; nextT = now + byteT; pos = 1;
                        break;
                    }
                case S_WTRACK:
                    {
                        if (pos < rev / byteT)
                        {
                            if (Drq) { lostThis = true; LostData++; wt.Add(0x4E); } else wt.Add((byte)Data);
                            pos++; Drq = true; nextT = now + byteT;
                            break;
                        }
                        Drq = false;
                        ParseFormat(wt);
                        HeatW[Math.Min(199, HeadCyl[Drive] * 2 + Side)] = 255;
                        Finish(now, 0);
                        break;
                    }
                default: nextT = long.MaxValue; break;
            }
        }
    }

    long NextIndex(long t) { long p = RotPos(t); return t + (rev - p); }

    // the next ID field to pass under the head, and when
    void ScheduleNextId(long t)
    {
        var l = D != null && D.HasTrack(HeadCyl[Drive], Side) ? D.Track(HeadCyl[Drive], Side) : null;
        if (l == null || l.Count == 0) { cur = null; nextT = Math.Min(searchUntil, t + (Fast ? rev / 10 : rev)); if (nextT <= t) nextT = t + 1; return; }
        if (Fast)
        {
            // no rotational wait (an SD card or floppy emulator): the wanted sector's ID comes next, if it exists
            BeeSector want = (Cmd >> 4) == 0xC ? null : l.Find(x => x.C == Track && x.R == Sector && ((Cmd & 2) == 0 || x.H == ((Cmd >> 3) & 1)));
            if (want != null) { cur = want; nextT = t + byteT * 8; return; }
            fastIdx = (fastIdx + 1) % l.Count; cur = l[fastIdx];
            if (searchUntil - t > rev / 2) searchUntil = t + rev / 2;      // record not found after a short search
            nextT = t + byteT * 8;
            return;
        }
        long p = RotPos(t);
        long best = long.MaxValue; BeeSector bs = null;
        int n = l.Count;
        for (int i = 0; i < n; i++)
        {
            long at = rev / 40 + (rev * i) / n;          // ID fields spread evenly after the index gap
            long d = at - p; if (d <= 0) d += rev;
            if (d < best) { best = d; bs = l[i]; }
        }
        cur = bs; nextT = t + best;
    }

    void BuildRawTrack()
    {
        var o = new List<byte>();
        for (int i = 0; i < 80; i++) o.Add(0x4E);
        var l = D != null && D.HasTrack(HeadCyl[Drive], Side) ? D.Track(HeadCyl[Drive], Side) : new List<BeeSector>();
        foreach (var s in l)
        {
            for (int i = 0; i < 12; i++) o.Add(0); o.Add(0xA1); o.Add(0xA1); o.Add(0xA1); o.Add(0xFE);
            o.Add((byte)s.C); o.Add((byte)s.H); o.Add((byte)s.R); o.Add((byte)s.N); o.Add(0); o.Add(0);
            for (int i = 0; i < 22; i++) o.Add(0x4E); for (int i = 0; i < 12; i++) o.Add(0);
            o.Add(0xA1); o.Add(0xA1); o.Add(0xA1); o.Add((byte)(s.Deleted ? 0xF8 : 0xFB)); o.AddRange(s.Data); o.Add(0); o.Add(0);
            for (int i = 0; i < 40; i++) o.Add(0x4E);
        }
        while (o.Count < 6250) o.Add(0x4E);
        buf = o.ToArray(); pos = 0; len = Math.Min(buf.Length, (int)(rev / byteT));
    }

    void ParseFormat(List<byte> w)
    {
        var secs = new List<BeeSector>();
        int i = 0;
        while (i < w.Count - 8)
        {
            if (w[i] == 0xF5 && w[i + 1] == 0xF5 && w[i + 2] == 0xF5 && w[i + 3] == 0xFE)
            {
                int c = w[i + 4], h = w[i + 5], r = w[i + 6], n = w[i + 7] & 3;
                i += 8;
                int size = 128 << n;
                // the data field: F5 F5 F5 FB then the data
                int j = i;
                while (j < w.Count - 4 && !(w[j] == 0xF5 && w[j + 1] == 0xF5 && w[j + 2] == 0xF5 && (w[j + 3] == 0xFB || w[j + 3] == 0xF8))) j++;
                var data = new byte[size];
                bool del = j < w.Count - 4 && w[j + 3] == 0xF8;
                if (j < w.Count - 4) for (int k = 0; k < size && j + 4 + k < w.Count; k++) data[k] = w[j + 4 + k];
                secs.Add(new BeeSector { C = c, H = h, R = r, N = n, Data = data, Deleted = del });
                i = Math.Max(i, j + 4 + size);
            }
            else i++;
        }
        if (D != null) D.SetTrack(HeadCyl[Drive], Side, secs);
        if (Log != null) Log("FDC: formatted cylinder " + HeadCyl[Drive] + " side " + Side + ", " + secs.Count + " sectors");
    }

    static ushort Crc(byte[] b)
    {
        ushort crc = 0xFFFF;
        foreach (byte x in b)
        {
            crc ^= (ushort)(x << 8);
            for (int k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    public string StateName { get { string[] n = { "IDLE", "STEP", "SETTLE", "SEARCH", "READ", "WRITE WAIT", "WRITE", "READ ADDR", "TRANSFER", "FORMAT WAIT", "FORMAT", "DONE", "DELAY" }; return n[state]; } }
}
