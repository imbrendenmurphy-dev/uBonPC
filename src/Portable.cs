// The bits of uBonPC's shared code that differ between Windows, Linux and macOS: file-name case and PNG writing.
// Everything here works on .NET Framework, Mono and modern .NET (taken from VZonPC's Portable.cs).
using System;
using System.IO;
using System.IO.Compression;

public static class Portable
{
    public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
    public static readonly bool IsMac = !IsWindows && Directory.Exists("/System/Library");

    /// <summary>dir/name, matching the name's case loosely on case-sensitive file systems (so a ROM or disk named in
    /// upper case is still found as lower case on Linux, as it would be on Windows).</summary>
    public static string Find(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        if (IsWindows || File.Exists(p) || !Directory.Exists(dir)) return p;
        try
        {
            foreach (var f in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch (Exception) { }
        return p;
    }

    // ---------------------------------------------------------------- PNG
    static uint[] crcTable;
    static uint Crc(byte[] b, int o, int n, uint c)
    {
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint v = i; for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320 ^ (v >> 1) : v >> 1; t[i] = v; }
            crcTable = t;
        }
        for (int i = 0; i < n; i++) c = crcTable[(c ^ b[o + i]) & 0xFF] ^ (c >> 8);
        return c;
    }
    static void BE(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var all = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) all[i] = (byte)type[i];
        Array.Copy(data, 0, all, 4, data.Length);
        BE(s, (uint)data.Length); s.Write(all, 0, all.Length); BE(s, Crc(all, 0, all.Length, 0xFFFFFFFF) ^ 0xFFFFFFFF);
    }

    /// <summary>Writes 0xRRGGBB pixels as a 24-bit PNG, each pixel scaled up to scale x scale.</summary>
    public static void SavePng(string fn, int[] src, int w, int h, int scale)
    {
        if (scale < 1) scale = 1;
        int W = w * scale, H = h * scale;
        var raw = new byte[(W * 3 + 1) * H];
        int o = 0;
        for (int y = 0; y < H; y++)
        {
            raw[o++] = 0;                         // filter: none
            int row = (y / scale) * w;
            for (int x = 0; x < W; x++) { int c = src[row + x / scale]; raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c; }
        }
        var z = new MemoryStream();
        z.WriteByte(0x78); z.WriteByte(0x9C);     // zlib header
        using (var d = new DeflateStream(z, CompressionMode.Compress, true)) d.Write(raw, 0, raw.Length);
        uint a = 1, b = 0;                        // Adler-32 of the uncompressed data
        for (int i = 0; i < raw.Length; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
        BE(z, (b << 16) | a);

        string dir = Path.GetDirectoryName(Path.GetFullPath(fn)); if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        using (var f = File.Create(fn))
        {
            f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new MemoryStream(); BE(ihdr, (uint)W); BE(ihdr, (uint)H); ihdr.Write(new byte[] { 8, 2, 0, 0, 0 }, 0, 5);
            Chunk(f, "IHDR", ihdr.ToArray());
            Chunk(f, "IDAT", z.ToArray());
            Chunk(f, "IEND", new byte[0]);
        }
    }
}
