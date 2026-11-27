// uBonPC floppy disk images: Microbee raw formats (DS80 / DS8, DS40, SS80, DS82, DS84, DS8B) and CPCEMU DSK /
// Extended DSK, held as tracks of sectors with their real ID fields. Also a CP/M disk builder that makes a bootable
// DS80 disk from the Premium Plus system tracks and a set of files, so a .COM can be run directly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class BeeSector
{
    public int C, H, R, N = 2;
    public byte[] Data;
    public bool Deleted, CrcError;
}

public sealed class RawFormat
{
    public string Name; public int Cyls, Heads, Spt = 10, Size = 512, SysSecOfs = 1, DataSecOfs = 1, SysTrackSides;
    public RawFormat(string n, int cyl, int heads, int sysTs, int dataOfs) { Name = n; Cyls = cyl; Heads = heads; SysTrackSides = sysTs; DataSecOfs = dataOfs; }
    public int Bytes { get { return Cyls * Heads * Spt * Size; } }
}

public sealed class BeeDisk
{
    public string Path, Name, Format;
    public int Cylinders, Heads;
    public bool WriteProtect, Dirty; public int Writes;
    public RawFormat Raw;      // null = DSK
    readonly Dictionary<int, List<BeeSector>> tracks = new Dictionary<int, List<BeeSector>>();

    public static readonly RawFormat[] RawFormats = {
        new RawFormat("DS80", 80, 2, 4, 21),     // Microbee modular / Premium Plus: 4 system track-sides, data sectors 21-30
        new RawFormat("DS40", 40, 2, 2, 1),
        new RawFormat("SS80", 80, 1, 2, 1),
        new RawFormat("DS82", 80, 2, 2, 1),
        new RawFormat("DS84", 80, 2, 2, 1),
        new RawFormat("DS8B", 80, 2, 4, 1),
    };

    public List<BeeSector> Track(int c, int h) { List<BeeSector> l; if (!tracks.TryGetValue(c * 2 + h, out l)) { l = new List<BeeSector>(); tracks[c * 2 + h] = l; } return l; }
    public bool HasTrack(int c, int h) { List<BeeSector> l; return tracks.TryGetValue(c * 2 + h, out l) && l.Count > 0; }

    public static bool IsDiskName(string p)
    {
        string e = System.IO.Path.GetExtension(p).ToLowerInvariant();
        return e == ".dsk" || e == ".ds80" || e == ".ds8" || e == ".d80" || e == ".ds40" || e == ".ds4" || e == ".d40" || e == ".ss80" || e == ".s80"
            || e == ".ds82" || e == ".d82" || e == ".ds84" || e == ".d84" || e == ".ds8b" || e == ".d8b" || e == ".img" || e == ".raw";
    }

    static RawFormat RawFor(string path, long len)
    {
        string e = System.IO.Path.GetExtension(path).ToLowerInvariant().TrimStart('.');
        switch (e)
        {
            case "ds80": case "ds8": case "d80": return RawFormats[0];
            case "ds40": case "ds4": case "d40": return RawFormats[1];
            case "ss80": case "s80": return RawFormats[2];
            case "ds82": case "d82": return RawFormats[3];
            case "ds84": case "d84": return RawFormats[4];
            case "ds8b": case "d8b": return RawFormats[5];
        }
        return len == 409600 ? RawFormats[1] : RawFormats[0];
    }

    public static BeeDisk Load(string path)
    {
        var b = File.ReadAllBytes(path);
        var d = new BeeDisk { Path = path, Name = System.IO.Path.GetFileName(path) };
        string hdr = Encoding.ASCII.GetString(b, 0, Math.Min(34, b.Length));
        if (hdr.StartsWith("MV - CPC") || hdr.StartsWith("EXTENDED")) d.LoadDsk(b, hdr.StartsWith("EXTENDED"));
        else d.LoadRaw(b, RawFor(path, b.Length));
        try { d.WriteProtect = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; } catch { }
        return d;
    }

    void LoadRaw(byte[] b, RawFormat f)
    {
        Raw = f; Format = "RAW " + f.Name; Cylinders = f.Cyls; Heads = f.Heads;
        int o = 0;
        for (int c = 0; c < f.Cyls; c++)
            for (int h = 0; h < f.Heads; h++)
            {
                int ts = c * f.Heads + h;
                int ofs = ts < f.SysTrackSides ? f.SysSecOfs : f.DataSecOfs;
                var l = Track(c, h);
                for (int s = 0; s < f.Spt; s++)
                {
                    var data = new byte[f.Size];
                    if (o < b.Length) Array.Copy(b, o, data, 0, Math.Min(f.Size, b.Length - o)); else for (int i = 0; i < f.Size; i++) data[i] = 0xE5;
                    o += f.Size;
                    l.Add(new BeeSector { C = c, H = h, R = s + ofs, N = 2, Data = data });
                }
            }
    }

    void LoadDsk(byte[] b, bool ext)
    {
        Raw = null; Format = ext ? "EXTENDED DSK" : "DSK";
        int ntr = b[0x30], nsd = b[0x31];
        Cylinders = ntr; Heads = nsd;
        int pos = 0x100;
        for (int t = 0; t < ntr * nsd; t++)
        {
            int tsize = ext ? b[0x34 + t] * 256 : b[0x32] | (b[0x33] << 8);
            if (tsize == 0) continue;
            if (pos + 0x100 > b.Length) break;
            if (Encoding.ASCII.GetString(b, pos, 10) != "Track-Info") { pos += tsize; continue; }
            int c = b[pos + 0x10], h = b[pos + 0x11], n = b[pos + 0x14], ns = b[pos + 0x15];
            int dp = pos + 0x100;
            var l = Track(c, h);
            for (int s = 0; s < ns; s++)
            {
                int si = pos + 0x18 + s * 8;
                int sc = b[si], sh = b[si + 1], sr = b[si + 2], sn = b[si + 3], st1 = b[si + 4], st2 = b[si + 5];
                int len = ext ? b[si + 6] | (b[si + 7] << 8) : 128 << Math.Min(n, 6);
                int real = 128 << Math.Min(sn, 6);
                var data = new byte[real];
                if (dp + Math.Min(len, real) <= b.Length) Array.Copy(b, dp, data, 0, Math.Min(len, real));
                dp += len;
                l.Add(new BeeSector { C = sc, H = sh, R = sr, N = sn, Data = data, Deleted = (st2 & 0x40) != 0, CrcError = (st1 & 0x20) != 0 && (st2 & 0x20) != 0 });
            }
            pos += tsize;
        }
    }

    public void Save() { Save(Path); }
    public void Save(string path)
    {
        if (Raw != null && System.IO.Path.GetExtension(path).ToLowerInvariant() != ".dsk") File.WriteAllBytes(path, ToRaw(Raw));
        else File.WriteAllBytes(path, ToDsk());
        Path = path; Dirty = false;
    }

    public byte[] ToRaw(RawFormat f)
    {
        var o = new byte[f.Bytes];
        for (int i = 0; i < o.Length; i++) o[i] = 0xE5;
        int p = 0;
        for (int c = 0; c < f.Cyls; c++)
            for (int h = 0; h < f.Heads; h++)
            {
                int ts = c * f.Heads + h;
                int ofs = ts < f.SysTrackSides ? f.SysSecOfs : f.DataSecOfs;
                var l = Track(c, h);
                for (int s = 0; s < f.Spt; s++)
                {
                    var sec = l.Find(x => x.R == s + ofs) ?? l.Find(x => x.R == s + (ofs == 1 ? 21 : 1));
                    if (sec != null) Array.Copy(sec.Data, 0, o, p, Math.Min(f.Size, sec.Data.Length));
                    p += f.Size;
                }
            }
        return o;
    }

    byte[] ToDsk()
    {
        var ms = new MemoryStream();
        var hdr = new byte[256];
        Encoding.ASCII.GetBytes("EXTENDED CPC DSK File\r\nDisk-Info\r\n").CopyTo(hdr, 0);
        Encoding.ASCII.GetBytes("uBonPC        ").CopyTo(hdr, 0x22);
        int ncyl = Cylinders, nh = Heads;
        foreach (var k in tracks.Keys) { if (tracks[k].Count == 0) continue; ncyl = Math.Max(ncyl, k / 2 + 1); nh = Math.Max(nh, (k & 1) + 1); }
        hdr[0x30] = (byte)ncyl; hdr[0x31] = (byte)nh;
        var blocks = new List<byte[]>();
        for (int c = 0; c < ncyl; c++)
            for (int h = 0; h < nh; h++)
            {
                var l = HasTrack(c, h) ? Track(c, h) : new List<BeeSector>();
                if (l.Count == 0) { hdr[0x34 + c * nh + h] = 0; blocks.Add(null); continue; }
                var tb = new MemoryStream();
                var ti = new byte[256];
                Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(ti, 0);
                ti[0x10] = (byte)c; ti[0x11] = (byte)h; ti[0x14] = (byte)l[0].N; ti[0x15] = (byte)l.Count; ti[0x16] = 0x4E; ti[0x17] = 0xE5;
                for (int s = 0; s < l.Count && s < 29; s++)
                {
                    var sc = l[s]; int si = 0x18 + s * 8;
                    ti[si] = (byte)sc.C; ti[si + 1] = (byte)sc.H; ti[si + 2] = (byte)sc.R; ti[si + 3] = (byte)sc.N;
                    ti[si + 4] = (byte)(sc.CrcError ? 0x20 : 0); ti[si + 5] = (byte)((sc.Deleted ? 0x40 : 0) | (sc.CrcError ? 0x20 : 0));
                    ti[si + 6] = (byte)(sc.Data.Length & 255); ti[si + 7] = (byte)(sc.Data.Length >> 8);
                }
                tb.Write(ti, 0, 256);
                foreach (var sc in l) tb.Write(sc.Data, 0, sc.Data.Length);
                while (tb.Length % 256 != 0) tb.WriteByte(0);
                hdr[0x34 + c * nh + h] = (byte)(tb.Length / 256);
                blocks.Add(tb.ToArray());
            }
        ms.Write(hdr, 0, 256);
        foreach (var bl in blocks) if (bl != null) ms.Write(bl, 0, bl.Length);
        return ms.ToArray();
    }

    /// <summary>A track rewritten by Write Track (format): its new sectors.</summary>
    public void SetTrack(int c, int h, List<BeeSector> secs) { tracks[c * 2 + h] = secs; Dirty = true; Writes++; }

    // ---------------------------------------------------------------- CP/M (Premium Plus DS80: 4K blocks, 3:1 skew)
    static readonly int[] Skew = { 1, 4, 7, 0, 3, 6, 9, 2, 5, 8 };
    const int TRACK = 5120, DIRSTART = 4 * TRACK, BLOCK = 4096, DIRENT = 128;

    /// <summary>Reads a logical (unskewed) 512-byte sector of the DS80 CP/M filesystem area. lsec counts from track-side 0.</summary>
    byte[] LogicalSector(int lsec)
    {
        int ts = lsec / 10, s = lsec % 10;
        int c = ts / 2, h = ts % 2;
        int phys = Skew[s];
        int ofs = ts < 4 ? 1 : 21;
        var sec = Track(c, h).Find(x => x.R == phys + ofs) ?? Track(c, h).Find(x => x.R == phys + 1) ?? Track(c, h).Find(x => x.R == phys + 21);
        return sec != null ? sec.Data : null;
    }

    public List<string> Directory()
    {
        var o = new List<string>();
        if (Raw == null || Raw.Name != "DS80") { o.Add("(directory listing is for DS80 disks)"); return o; }
        var names = new SortedDictionary<string, int>();
        for (int e = 0; e < DIRENT; e++)
        {
            int lsec = DIRSTART / 512 + (e * 32) / 512;
            var d = LogicalSector(lsec); if (d == null) continue;
            int off = (e * 32) % 512;
            if (d[off] > 15) continue;
            string n = Encoding.ASCII.GetString(d, off + 1, 8).TrimEnd() + "." + Encoding.ASCII.GetString(d, off + 9, 3).TrimEnd();
            var sb = new StringBuilder(); foreach (char ch in n) sb.Append((char)(ch & 0x7F)); n = sb.ToString();
            int recs = d[off + 15] + (((d[off + 12] & 31) + (d[off + 14] << 5)) % 4) * 128;
            int ov; names.TryGetValue(n, out ov); names[n] = ov + recs;
        }
        foreach (var kv in names) o.Add(string.Format("{0,-12} {1,6} bytes", kv.Key, kv.Value * 128));
        return o;
    }

    /// <summary>Builds a bootable DS80 CP/M disk: the system tracks (system\boot_tracks.bin) and the given files.</summary>
    public static BeeDisk BuildCpm(string path, byte[] systemTracks, List<KeyValuePair<string, byte[]>> files)
    {
        var lg = new byte[819200];
        for (int i = 0; i < lg.Length; i++) lg[i] = 0xE5;
        Array.Copy(systemTracks, 0, lg, 0, Math.Min(DIRSTART, systemTracks.Length));
        int blk = 1;                  // block 0 = the directory (128 entries x 32 bytes = 4K)
        var entries = new List<byte[]>();
        foreach (var f in files)
        {
            string nm = f.Key.ToUpperInvariant(); string stem = System.IO.Path.GetFileNameWithoutExtension(nm), ext = System.IO.Path.GetExtension(nm).TrimStart('.');
            if (stem.Length > 8) stem = stem.Substring(0, 8); if (ext.Length > 3) ext = ext.Substring(0, 3);
            var data = f.Value;
            int records = (data.Length + 127) / 128;
            int nblocks = (data.Length + BLOCK - 1) / BLOCK;
            if (blk + nblocks > 195) throw new Exception("disk full (" + nm + ")");
            int done = 0, extent = 0;
            do
            {
                var e = new byte[32];
                Encoding.ASCII.GetBytes((stem.PadRight(8) + ext.PadRight(3))).CopyTo(e, 1);
                int recHere = Math.Min(records - done, 512);              // 16 blocks x 4K = 4 logical extents of 128 records
                int lastExt = extent * 4 + (recHere > 0 ? (recHere - 1) / 128 : 0);
                e[12] = (byte)(lastExt & 31); e[14] = (byte)(lastExt >> 5);
                e[15] = (byte)(recHere - ((recHere > 0 ? (recHere - 1) / 128 : 0) * 128));
                int bl = (recHere * 128 + BLOCK - 1) / BLOCK;
                for (int i = 0; i < bl; i++) e[16 + i] = (byte)(blk + i);
                int byteOfs = done * 128;
                Array.Copy(data, byteOfs, lg, DIRSTART + blk * BLOCK, Math.Min(data.Length - byteOfs, bl * BLOCK));
                blk += bl; done += recHere; extent++;
                entries.Add(e);
            } while (done < records);
        }
        if (entries.Count > DIRENT) throw new Exception("too many directory entries");
        for (int i = 0; i < entries.Count; i++) Array.Copy(entries[i], 0, lg, DIRSTART + i * 32, 32);
        // skew the data area into physical order. The system tracks are not skewed: boot_tracks.bin is a copy of a real
        // disk's first two cylinders, already in physical order (skewing them scrambled the boot sector, and the boot
        // ROM jumped into garbage and fell into its monitor)
        var img = new byte[lg.Length];
        Array.Copy(lg, img, DIRSTART);
        for (int t = DIRSTART / TRACK; t < lg.Length / TRACK; t++)
            for (int s = 0; s < 10; s++)
                Array.Copy(lg, t * TRACK + s * 512, img, t * TRACK + Skew[s] * 512, 512);
        var d = new BeeDisk { Path = path, Name = System.IO.Path.GetFileName(path) };
        d.LoadRaw(img, RawFormats[0]);
        return d;
    }

    public static BeeDisk Blank(string path, RawFormat f)
    {
        var img = new byte[f.Bytes]; for (int i = 0; i < img.Length; i++) img[i] = 0xE5;
        var d = new BeeDisk { Path = path, Name = System.IO.Path.GetFileName(path) };
        d.LoadRaw(img, f);
        return d;
    }
}
