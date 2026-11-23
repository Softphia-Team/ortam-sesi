using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OrtamSesi.UI;

/// <summary>Crescent icons are drawn in code (no external files). Created once per state.</summary>
internal static class TrayIcons
{
    public static readonly Color Moon = Color.FromArgb(255, 255, 214, 102);
    public static readonly Color MoonDim = Color.FromArgb(120, 255, 214, 102);
    public static readonly Color MoonGray = Color.FromArgb(255, 150, 156, 164);
    public static readonly Color Warn = Color.FromArgb(255, 255, 138, 61);

    public static Bitmap Draw(int size, Color moon, Color? dot)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = size * 0.06f, d = size - 2 * pad;

        using (var crescent = Crescent(pad, pad, d))
        {
            // Thin dark outline so it also stands out on a light taskbar
            using var edge = new Pen(Color.FromArgb(moon.A < 200 ? 70 : 140, 30, 34, 48), Math.Max(1f, size / 20f)) { LineJoin = LineJoin.Round };
            g.DrawPath(edge, crescent);
            using var brush = new SolidBrush(moon);
            g.FillPath(brush, crescent);
        }
        if (dot is Color c)
        {
            float r = size * 0.36f;
            using var b = new SolidBrush(c);
            using var p = new Pen(Color.FromArgb(160, 30, 34, 48), Math.Max(1f, size / 20f));
            g.FillEllipse(b, size - r - 0.5f, size - r - 0.5f, r, r);
            g.DrawEllipse(p, size - r - 0.5f, size - r - 0.5f, r, r);
        }
        return bmp;
    }

    /// <summary>Crescent: the part of the outer circle that lies outside a shifted second circle (a path made of two arcs).</summary>
    private static GraphicsPath Crescent(float x, float y, float d)
    {
        double r1 = d / 2, cx1 = x + r1, cy1 = y + r1;
        double r2 = d * 0.95 / 2, cx2 = x + d * 0.36 + r2, cy2 = y - d * 0.14 + r2;

        // Intersection points of the two circles
        double dx = cx2 - cx1, dy = cy2 - cy1, dist = Math.Sqrt(dx * dx + dy * dy);
        double a = (r1 * r1 - r2 * r2 + dist * dist) / (2 * dist);
        double h = Math.Sqrt(Math.Max(0, r1 * r1 - a * a));
        double mx = cx1 + a * dx / dist, my = cy1 + a * dy / dist;
        var p1 = (X: mx + h * dy / dist, Y: my - h * dx / dist);
        var p2 = (X: mx - h * dy / dist, Y: my + h * dx / dist);

        static double Ang(double px, double py, double cx, double cy) => Math.Atan2(py - cy, px - cx) * 180 / Math.PI;
        static bool Inside(double px, double py, double cx, double cy, double r) => (px - cx) * (px - cx) + (py - cy) * (py - cy) < r * r;
        static (double X, double Y) At(double cx, double cy, double r, double deg) =>
            (cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));

        // Outer arc: p1 → p2, in the direction that lies OUTSIDE the second circle
        double a1 = Ang(p1.X, p1.Y, cx1, cy1), a2 = Ang(p2.X, p2.Y, cx1, cy1);
        double sweepOuter = (a2 - a1 + 360) % 360;
        var mid = At(cx1, cy1, r1, a1 + sweepOuter / 2);
        if (Inside(mid.X, mid.Y, cx2, cy2, r2)) sweepOuter -= 360;

        // Inner arc: p2 → p1, in the direction that lies INSIDE the outer circle
        double b2 = Ang(p2.X, p2.Y, cx2, cy2), b1 = Ang(p1.X, p1.Y, cx2, cy2);
        double sweepInner = (b1 - b2 + 360) % 360;
        var mid2 = At(cx2, cy2, r2, b2 + sweepInner / 2);
        if (!Inside(mid2.X, mid2.Y, cx1, cy1, r1)) sweepInner -= 360;

        var path = new GraphicsPath();
        path.AddArc((float)(cx1 - r1), (float)(cy1 - r1), (float)(2 * r1), (float)(2 * r1), (float)a1, (float)sweepOuter);
        path.AddArc((float)(cx2 - r2), (float)(cy2 - r2), (float)(2 * r2), (float)(2 * r2), (float)b2, (float)sweepInner);
        path.CloseFigure();
        return path;
    }

    public static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    /// <summary>Writes a multi-size .ico file with PNG content (for the exe icon).</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var pngs = sizes.Select(s =>
        {
            using var b = Draw(s, Moon, null);
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
