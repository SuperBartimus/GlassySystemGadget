using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glassy.Core;

/// <summary>The tray icon picture: a small dark glass tile with a scrolling CPU line, so it visibly moves while the widget
/// is alive (a hung UI thread freezes it). Encoded as an in-memory .ico so the Icon owns its handle - no GetHicon or
/// DestroyIcon, the handle chain that crashed the clipboard file icons.</summary>
public static class TrayArt
{
    /// <summary>values: CPU percent, oldest first; NaN leaves a gap. Scaled to the window's own peak (at least 5%) so idle
    /// noise still moves.</summary>
    public static Bitmap Draw(int size, IReadOnlyList<float> values)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
        using (var path = Rounded(rect, size / 5f))
        {
            using (var bg = new LinearGradientBrush(rect, Color.FromArgb(58, 40, 88), Color.FromArgb(24, 16, 40), 90f)) g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(220, 190, 140, 255), Math.Max(1f, size / 16f));
            g.DrawPath(border, path);
        }

        float pad = size / 7f, w = size - 2 * pad, h = size - 2 * pad;
        float top = Math.Max(5f, values.Where(v => !float.IsNaN(v)).DefaultIfEmpty(0).Max());
        var pts = new List<PointF>();
        for (int i = 0; i < values.Count; i++)
        {
            if (float.IsNaN(values[i])) continue;
            pts.Add(new PointF(pad + w * i / Math.Max(1, values.Count - 1), pad + h * (1 - Math.Clamp(values[i] / top, 0, 1))));
        }
        if (pts.Count >= 2)
        {
            var area = pts.Append(new PointF(pts[^1].X, pad + h)).Append(new PointF(pts[0].X, pad + h)).ToArray();
            using (var fill = new SolidBrush(Color.FromArgb(90, 255, 60, 230))) g.FillPolygon(fill, area);
            using var line = new Pen(Color.FromArgb(255, 255, 110, 240), Math.Max(1.2f, size / 12f)) { LineJoin = LineJoin.Round };
            g.DrawLines(line, pts.ToArray());
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    /// <summary>A one-image .ico (32-bit BMP entry: header, bottom-up BGRA rows, empty AND mask) holding the bitmap.</summary>
    public static byte[] Ico(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, pix = w * h * 4, mask = (w + 31) / 32 * 4 * h;
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)1);                                   // ICONDIR
        bw.Write((byte)(w >= 256 ? 0 : w)); bw.Write((byte)(h >= 256 ? 0 : h)); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((short)1); bw.Write((short)32); bw.Write(40 + pix + mask); bw.Write(22);             // ICONDIRENTRY
        bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);         // BITMAPINFOHEADER (height covers the mask too)
        bw.Write(0); bw.Write(pix + mask); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--) { Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length); bw.Write(row); }
        }
        finally { bmp.UnlockBits(data); }
        bw.Write(new byte[mask]);
        bw.Flush(); return ms.ToArray();
    }
}
