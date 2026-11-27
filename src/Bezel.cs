// uBonPC monitor bezel for full screen, after the owner's microbee MONITOR: a rounded, slightly bulging CRT face (a
// superellipse) whose unlit phosphor shows as a faint grey sheen, darker towards the edges, with a soft reflection at
// the top left; a black recess round the glass; the charcoal front with its pale case rim; "microbee MONITOR" under the
// glass at the right. The static parts are drawn once per layout, in plain code (the same on every platform); each frame only the glass pixels are
// recomputed (picture x brightness + sheen), straight into a buffer the size of the area, blitted 1:1.
using System;
using System.Collections.Generic;

public sealed class Bezel
{
    int tw, th, sw, sh;                 // target area, source picture size the layout was made for
    int[] outBuf;                       // the composed area (bezel + glass)
    int[] gIdx, gSrc;                   // glass pixels: where in outBuf, which source pixel (-1 = black)
    byte[] gMul, gAdd;                  // per glass pixel: brightness (0-255 of the picture) and sheen added
    int gCount;
    public int PicX, PicY, PicW, PicH;  // where the picture lands in the area

    /// <summary>The area (tw x th) with the monitor front drawn round a w x h picture.</summary>
    public int[] Compose(int[] px, int w, int h, int areaW, int areaH)
    {
        if (areaW != tw || areaH != th || w != sw || h != sh || outBuf == null) Layout(w, h, areaW, areaH);
        var o = outBuf; int n = gCount;
        var idx = gIdx; var src = gSrc; var mul = gMul; var add = gAdd;
        for (int k = 0; k < n; k++)
        {
            int s = src[k], c = s >= 0 ? px[s] : 0;
            int m = mul[k], a = add[k];
            int r = (((c >> 16) & 255) * m >> 8) + a, g = (((c >> 8) & 255) * m >> 8) + a, b = ((c & 255) * m >> 8) + a;
            if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255;
            o[idx[k]] = (r << 16) | (g << 8) | b;
        }
        return o;
    }

    void Layout(int w, int h, int areaW, int areaH)
    {
        tw = areaW; th = areaH; sw = w; sh = h;
        // proportions from the photos: glass a little bigger than the picture area, a narrow surround at the sides and
        // top, a deeper chin for the label
        const double GL = 1.035, SIDE = 0.075, TOP = 0.075, CHIN = 0.15;
        const double FIT = 0.955;                                          // leave room for the case rim and a little black round it
        double s = FIT * Math.Min(tw / (w * GL * (1 + 2 * SIDE)), th / (h * GL * (1 + TOP + CHIN)));
        if (s >= 1 && Math.Floor(s) / s >= 0.93) s = Math.Floor(s);       // whole pixels when it costs little
        int pw = (int)Math.Round(w * s), ph = (int)Math.Round(h * s);
        double gw = pw * GL, gh = ph * GL;
        double bw = gw * (1 + 2 * SIDE), bh = gh * (1 + TOP + CHIN);
        double bx = (tw - bw) / 2, by = (th - bh) / 2;
        double gcx = bx + bw / 2, gcy = by + gh * TOP + gh / 2;
        PicX = (int)Math.Round(gcx - pw / 2.0); PicY = (int)Math.Round(gcy - ph / 2.0); PicW = pw; PicH = ph;

        // ---- the static front, in plain code (no GDI+, so it is the same on Windows, Linux and macOS): the pale case
        // rim, the charcoal front with a faint lit edge, and the label
        outBuf = new int[tw * th];
        double r = bh * 0.045;
        double rx0 = bx - bw * 0.008, ry0 = by - bh * 0.008, rw = bw * 1.016, rh = bh * 1.016;
        int fy0 = Math.Max(0, (int)ry0 - 2), fy1 = Math.Min(th, (int)(ry0 + rh) + 3), fx0 = Math.Max(0, (int)rx0 - 2), fx1 = Math.Min(tw, (int)(rx0 + rw) + 3);
        for (int y = fy0; y < fy1; y++)
            for (int x = fx0; x < fx1; x++)
            {
                double cx = x + 0.5, cy = y + 0.5;
                double aRim = Cover(cx, cy, rx0, ry0, rw, rh, r * 1.15);
                if (aRim <= 0) continue;
                int c = Mix(0, Grad(0x9C9992, 0x555450, (cy - ry0) / rh), aRim);
                double sd = Sdf(cx, cy, bx, by, bw, bh, r);
                double aBody = Math.Max(0, Math.Min(1, 0.5 - sd));
                if (aBody > 0)
                {
                    c = Mix(c, Grad(0x2A2C30, 0x17181B, (cy - by) / bh), aBody);
                    double e = Math.Max(0, 1 - Math.Abs(sd + 2.25) / 0.75);            // a 1.5-pixel line just inside the edge
                    if (e > 0) c = Mix(c, 0x606268, e * 70 / 255.0);
                }
                outBuf[y * tw + x] = c;
            }
        // the label, under the right of the glass, from the mask in Label (rendered once from Arial: "microbee" bold
        // and "MONITOR" at 0.78 the size), scaled by area sampling
        double fs = Math.Max(9, gh * 0.032), k = fs / LabelFs;
        double lx = gcx + gw / 2 - LabelTextW * k, ly = gcy + gh / 2 + gh * 0.05;
        var mask = LabelMask();
        int lx0 = Math.Max(0, (int)Math.Floor(lx)), lx1 = Math.Min(tw, (int)Math.Ceiling(lx + LabelW * k));
        int ly0 = Math.Max(0, (int)Math.Floor(ly)), ly1 = Math.Min(th, (int)Math.Ceiling(ly + LabelH * k));
        for (int y = ly0; y < ly1; y++)
            for (int x = lx0; x < lx1; x++)
            {
                double a = 0;
                for (int sy2 = 0; sy2 < 4; sy2++)
                    for (int sx2 = 0; sx2 < 4; sx2++)
                    {
                        double mx = (x + (sx2 + 0.5) / 4 - lx) / k, my = (y + (sy2 + 0.5) / 4 - ly) / k;
                        a += Sample(mask, mx - 0.5, my - 0.5);
                    }
                a /= 16 * 255.0;
                if (a > 0.003) outBuf[y * tw + x] = Mix(outBuf[y * tw + x], 0xA4A6AA, a);
            }
        // ---- the recess and the glass, per pixel: a superellipse |u|^N + |v|^N <= 1
        const double N = 5.0, LIP = 0.022;
        double ga = gw / 2, gb = gh / 2;
        var li = new List<int>(); var ls = new List<int>(); var lm = new List<byte>(); var la = new List<byte>();
        int y0 = Math.Max(0, (int)(gcy - gb * (1 + LIP * 2)) - 2), y1 = Math.Min(th, (int)(gcy + gb * (1 + LIP * 2)) + 2);
        int x0 = Math.Max(0, (int)(gcx - ga * (1 + LIP * 2)) - 2), x1 = Math.Min(tw, (int)(gcx + ga * (1 + LIP * 2)) + 2);
        double lipN = Math.Pow(1 + LIP, N), lip2N = Math.Pow(1 + LIP * 2, N);
        for (int y = y0; y < y1; y++)
        {
            double v = Math.Abs((y + 0.5 - gcy) / gb), vn = Math.Pow(v, N);
            int sy = (int)Math.Floor((y + 0.5 - PicY) / s);
            for (int x = x0; x < x1; x++)
            {
                double u = Math.Abs((x + 0.5 - gcx) / ga), q = Math.Pow(u, N) + vn;
                int o = y * tw + x;
                if (q > lip2N) continue;
                if (q > lipN)
                {
                    // the bevelled edge of the recess: a thin lighter line catching the light, fading into the front
                    double t = (Math.Pow(q, 1 / N) - (1 + LIP)) / LIP;      // 0 inner .. 1 outer
                    int baseC = outBuf[o], lit = (int)(26 * (1 - t));
                    outBuf[o] = Mix(baseC, 0x0A0A0B, 1 - t) + ((lit << 16) | (lit << 8) | lit);
                    continue;
                }
                if (q > 1)
                {
                    outBuf[o] = 0x070708;                                  // the recess itself
                    continue;
                }
                // the glass: brightness falls off towards the edge, the unlit phosphor is a faint grey, and there is a
                // soft window reflection at the upper left
                double rr = Math.Pow(q, 2.0 / N);                           // ~ radius^2, 0 centre .. 1 edge
                double mulD = 1.0 - 0.28 * rr * rr;
                double hx = (x + 0.5 - (gcx - ga * 0.42)) / (ga * 0.55), hy = (y + 0.5 - (gcy - gb * 0.52)) / (gb * 0.42);
                double hl = Math.Exp(-(hx * hx + hy * hy) * 1.6);
                double edge = Math.Max(0, (Math.Pow(q, 1 / N) - 0.93) / 0.07);   // a thin glint just inside the rim
                double addD = 22 * (1 - 0.6 * rr) + 16 * hl + 10 * edge * edge;
                int sx = (int)Math.Floor((x + 0.5 - PicX) / s);
                int sidx = (sx >= 0 && sx < w && sy >= 0 && sy < h) ? sy * w + sx : -1;
                li.Add(o); ls.Add(sidx); lm.Add((byte)Math.Max(0, Math.Min(255, (int)(mulD * 256 - 1)))); la.Add((byte)Math.Max(0, Math.Min(255, (int)addD)));
            }
        }
        gIdx = li.ToArray(); gSrc = ls.ToArray(); gMul = lm.ToArray(); gAdd = la.ToArray(); gCount = gIdx.Length;
    }

    static int Mix(int a, int b, double t)
    {
        if (t < 0) t = 0; if (t > 1) t = 1;
        int r = (int)(((a >> 16) & 255) * (1 - t) + ((b >> 16) & 255) * t), g = (int)(((a >> 8) & 255) * (1 - t) + ((b >> 8) & 255) * t), bl = (int)((a & 255) * (1 - t) + (b & 255) * t);
        return (r << 16) | (g << 8) | bl;
    }

    static int Grad(int top, int bottom, double t) { return Mix(top, bottom, t); }

    /// <summary>Signed distance from (x, y) to a rounded rectangle (negative inside).</summary>
    static double Sdf(double x, double y, double rx, double ry, double rw, double rh, double rad)
    {
        double hx = rw / 2, hy = rh / 2, qx = Math.Abs(x - (rx + hx)) - (hx - rad), qy = Math.Abs(y - (ry + hy)) - (hy - rad);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - rad;
    }
    static double Cover(double x, double y, double rx, double ry, double rw, double rh, double rad) { return Math.Max(0, Math.Min(1, 0.5 - Sdf(x, y, rx, ry, rw, rh, rad))); }

    // ---- the label: "microbee MONITOR" as an 8-bit coverage mask, LabelW x LabelH, drawn at LabelFs pixels
    const int LabelW = 545, LabelH = 74; const double LabelFs = 64, LabelTextW = 542.6687;
    static byte[] labelMask;
    static byte[] LabelMask()
    {
        if (labelMask != null) return labelMask;
        var o = new byte[LabelW * LabelH];
        using (var z = new System.IO.Compression.DeflateStream(new System.IO.MemoryStream(Convert.FromBase64String(LabelData)), System.IO.Compression.CompressionMode.Decompress))
        {
            int n = 0, got; while (n < o.Length && (got = z.Read(o, n, o.Length - n)) > 0) n += got;
        }
        return labelMask = o;
    }
    static double Sample(byte[] m, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y); double fx = x - x0, fy = y - y0;
        return (1 - fx) * (1 - fy) * Px(m, x0, y0) + fx * (1 - fy) * Px(m, x0 + 1, y0) + (1 - fx) * fy * Px(m, x0, y0 + 1) + fx * fy * Px(m, x0 + 1, y0 + 1);
    }
    static int Px(byte[] m, int x, int y) { return x < 0 || y < 0 || x >= LabelW || y >= LabelH ? 0 : m[y * LabelW + x]; }
    const string LabelData = "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";
}