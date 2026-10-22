using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Glassy.Core;

namespace Glassy.App;

/// <summary>The AI usage panel's Stats flyout: personality, today and the 7-day average, the provider split, records
/// and the hour-of-day pattern. Owner-drawn to match the widget; closes when it loses focus or on Esc. Recomputed
/// from the panel's latest history every 15 s while open.</summary>
public sealed class AiStatsForm : Form
{
    static readonly Color Bg = Color.FromArgb(30, 22, 44), Card = Color.FromArgb(44, 33, 64), Fg = Color.FromArgb(235, 225, 250), Dim = Color.FromArgb(165, 150, 190);
    readonly Func<PanelRuntime> panel;
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 15_000 };
    readonly Font title = new("Segoe UI", 12f, FontStyle.Bold), head = new("Segoe UI", 8f, FontStyle.Bold), body = new("Segoe UI", 9f),
        big = new("Segoe UI", 11f, FontStyle.Bold), small = new("Segoe UI", 7.5f), emoji = new("Segoe UI Emoji", 20f), emojiSmall = new("Segoe UI Emoji", 9f);
    AiStatsResult stats;

    public AiStatsForm(Func<PanelRuntime> panel)
    {
        this.panel = panel;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        BackColor = Bg; DoubleBuffered = true; KeyPreview = true;
        ClientSize = new Size(Px(300), Px(452));
        Recompute();
        refresh.Tick += (_, _) => { Recompute(); Invalidate(); };
        refresh.Start();
        Deactivate += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    void Recompute() => stats = AiStats.Compute(panel()?.Ai.Hours ?? new(), DateTime.Now);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refresh.Dispose(); title.Dispose(); head.Dispose(); body.Dispose(); big.Dispose(); small.Dispose(); emoji.Dispose(); emojiSmall.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Beside the widget, level with the AI usage panel: toward the middle of the screen (left of a widget on
    /// the right half, right of one on the left half), or the other side if that one has no room.</summary>
    public void PlaceBeside(Rectangle widget, int panelTopPx)
    {
        var area = Screen.FromRectangle(widget).WorkingArea;
        int x = AiUsageFormat.FlyoutX(widget.Left, widget.Right, Width, area.Left, area.Right, Px(6));
        int y = Math.Clamp(widget.Top + panelTopPx, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
        float w = 300, l = 14, r = w - 14, y = 12;
        using var fg = new SolidBrush(Fg); using var dim = new SolidBrush(Dim); using var card = new SolidBrush(Card);
        using (var border = new Pen(Color.FromArgb(120, 80, 170))) g.DrawRectangle(border, 0, 0, w - 1, 452 - 1);
        g.DrawString("AI stats", title, fg, l, y); y += 30;
        var pc = panel()?.Cfg;
        Color ProviderColor(string id) => ColorTranslator.FromHtml(pc != null ? AiUsageFormat.Color(pc, id) : AiUsageFormat.DefaultColor(id));

        // Personality card
        Fill(g, card, l - 4, y, r - l + 8, 94, 8);
        using (var accent = new SolidBrush(Color.FromArgb(180, 140, 255))) g.FillRectangle(accent, l - 4, y, 3, 94);
        var p = stats.Personality;
        g.DrawString("USAGE PERSONALITY", head, dim, l + 4, y + 6);
        g.DrawString(p?.Glyph ?? "", emoji, fg, l + 2, y + 20);
        g.DrawString(p?.Name ?? "No data yet", big, fg, l + 44, y + 22);
        g.DrawString(p?.Blurb ?? "Usage shows up after the first log pass.", small, dim, new RectangleF(l + 44, y + 42, r - l - 46, 16));
        if (stats.Secondary is { } s)
        {
            g.DrawString(s.Glyph, emojiSmall, fg, l + 4, y + 60);
            g.DrawString($"{s.Name} - {s.Blurb}", small, dim, new RectangleF(l + 24, y + 62, r - l - 26, 28));
        }
        y += 104;

        // Summary + split
        g.DrawString("LAST 7 DAYS", head, dim, l, y); y += 16;
        Line(g, "Today", AiStats.Tokens(stats.Today) + " tokens", l, r, ref y, fg, dim);
        Line(g, "Daily average", AiStats.Tokens(stats.Avg7) + " tokens", l, r, ref y, fg, dim);
        if (stats.Split.Count > 0)
        {
            float x = l, bw = r - l;
            foreach (var (prov, share) in stats.Split)
            {
                using var b = new SolidBrush(ProviderColor(prov));
                g.FillRectangle(b, x, y + 3, (float)(bw * share), 6); x += (float)(bw * share);
            }
            y += 12;
            g.DrawString(string.Join("   ", stats.Split.Select(x => $"{AiStats.ProviderName(x.Provider)} {x.Share:P0}")), small, dim, l, y); y += 16;
        }
        y += 6;

        // Records
        g.DrawString("RECORDS", head, dim, l, y); y += 16;
        Line(g, "Best day", stats.BestDay is { } bd ? $"{AiStats.Tokens(bd.Tokens)}  {bd.Day:ddd, MMM d}" : "-", l, r, ref y, fg, dim);
        Line(g, "Lightest day", stats.LightestDay is { } ld ? $"{AiStats.Tokens(ld.Tokens)}  {ld.Day:ddd, MMM d}" : "-", l, r, ref y, fg, dim);
        Line(g, "Active streak", stats.ActiveStreak == 1 ? "1 day" : $"{stats.ActiveStreak} days", l, r, ref y, fg, dim);
        Line(g, "Best streak", stats.BestStreak == 1 ? "1 day" : $"{stats.BestStreak} days", l, r, ref y, fg, dim);
        y += 8;

        // Hour-of-day pattern, last 30 days, current hour outlined
        g.DrawString("HOURLY PATTERN (30 DAYS)", head, dim, l, y); y += 18;
        float ph = 60, colW = (r - l) / 24f; long max = Math.Max(1, stats.HourOfDay.Max());
        using (var empty = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
        using (var bar = new SolidBrush(Color.FromArgb(180, 140, 255)))
        using (var now = new Pen(Fg, 1.2f))
            for (int h = 0; h < 24; h++)
            {
                float bx = l + h * colW + 1, bwid = colW - 2, bh = (float)(ph * stats.HourOfDay[h] / max);
                g.FillRectangle(empty, bx, y, bwid, ph);
                if (bh > 0) g.FillRectangle(bar, bx, y + ph - bh, bwid, bh);
                if (h == DateTime.Now.Hour) g.DrawRectangle(now, bx - 0.5f, y - 0.5f, bwid + 1, ph + 1);
            }
        y += ph + 4;
        foreach (var (h, lab) in new[] { (0, "12AM"), (6, "6AM"), (12, "12PM"), (18, "6PM"), (23, "11PM") })
        {
            var sz = g.MeasureString(lab, small);
            g.DrawString(lab, small, dim, Math.Clamp(l + h * colW + colW / 2 - sz.Width / 2, l - 4, r - sz.Width + 4), y);
        }
    }



    void Line(Graphics g, string label, string value, float l, float r, ref float y, Brush fg, Brush dim)
    {
        g.DrawString(label, body, dim, l, y);
        var sz = g.MeasureString(value, body);
        g.DrawString(value, body, fg, r - sz.Width, y);
        y += 19;
    }

    static void Fill(Graphics g, Brush b, float x, float y, float w, float h, float rad)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, rad, rad, 180, 90); path.AddArc(x + w - rad, y, rad, rad, 270, 90);
        path.AddArc(x + w - rad, y + h - rad, rad, rad, 0, 90); path.AddArc(x, y + h - rad, rad, rad, 90, 90);
        path.CloseFigure(); g.FillPath(b, path);
    }
}
