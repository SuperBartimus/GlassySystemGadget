using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Glassy.Core;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using DwFontStyle = Vortice.DirectWrite.FontStyle;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace Glassy.App;

/// <summary>Draws the whole widget with Direct2D (GPU blur for glow) into a BGRA premultiplied surface, then reads it back for UpdateLayeredWindow.</summary>
public sealed class D2DRenderer : IDisposable
{
    readonly ID3D11Device d3d; readonly ID2D1Factory1 factory; readonly ID2D1Device dev; readonly ID2D1DeviceContext dc;
    readonly ID2D1Effect blur; readonly IDWriteFactory dw;
    readonly IDWriteTextFormat fTitle, fBody, fBodyR, fSmall, fSmallR, fTag, fWrap, fCenter;
    readonly Dictionary<string, float> widths = new();
    // Clipboard row content cache, keyed by ClipItem.Id (items are immutable once captured, so the cache never
    // goes stale except on a width/line-cap change, when the whole cache is cleared). Pruned each render pass to
    // whatever is currently in the store, so a deleted item's cached bitmap/layout is released promptly.
    readonly Dictionary<string, IDWriteTextLayout> clipTextCache = new();
    readonly Dictionary<string, ID2D1Bitmap> clipImageCache = new();
    readonly Dictionary<string, float> clipImageHeightCache = new();
    readonly Dictionary<string, FileRows> clipFilesCache = new();
    float clipCacheWidth = -1; int clipCacheMaxLines = -1;

    /// <summary>One Files-kind item's per-file wrapped layouts (each capped to its own share of the line budget),
    /// its type icon key, and how many files didn't fit at all.</summary>
    sealed class FileRows { public List<(IDWriteTextLayout layout, string iconKey)> Shown = new(); public int Hidden; public float Height; }
    readonly ID2D1StrokeStyle roundStroke, dashStroke;
    readonly Dictionary<uint, ID2D1SolidColorBrush> brushes = new();
    readonly Dictionary<string, (byte r, byte g, byte b)> parsed = new();
    readonly List<Vector2> pts = new();
    ID2D1Bitmap1 target, glowSrc, staging;
    public int PxW { get; private set; }
    public int PxH { get; private set; }
    float scale = 1, wDip, hDip;

    // Transient per-frame UI state the Clipboard panel needs but Engine/PanelRuntime deliberately don't carry
    // (scroll position and the search box are interaction state owned by MainWindow, not data).
    public float ClipScroll { get; set; }
    public bool ClipSearchVisible { get; set; }
    public string ClipSearchQuery { get; set; } = "";
    /// <summary>The Settings gear, top-left, overlaid on whatever panel happens to be first - only while the
    /// mouse is over the widget (MainWindow sets this from hover state), since the widget never becomes the
    /// active window and so has no title bar of its own to put a settings button on.</summary>
    public bool ShowSettingsGear { get; set; }

    /// <summary>The theme's text tint - was a fixed constant, now set once per frame from GlobalConfig.Theme.Text
    /// in Render(), same value used by every text/glyph brush in the frame.</summary>
    (byte r, byte g, byte b) TitleCol = (245, 225, 255);
    (byte r, byte g, byte b) borderCol = (210, 110, 255);
    (byte r, byte g, byte b) themeBgTop = (38, 14, 58), themeBgBottom = (12, 6, 24);

    /// <summary>"Hardware" normally; "WARP (software)" when no GPU device is available (for example some RDP sessions).</summary>
    public string DeviceKind { get; }

    public D2DRenderer(bool forceSoftware = false)
    {
        ID3D11Device device = null; string kind = "Hardware";
        if (!forceSoftware)
        {
            try { D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device).CheckError(); }
            catch (Exception) { device = null; }
        }
        if (device == null)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device).CheckError();
            kind = "WARP (software)";
        }
        d3d = device; DeviceKind = kind;
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        dev = factory.CreateDevice(dxgi);
        dc = dev.CreateDeviceContext(DeviceContextOptions.None);
        blur = new ID2D1Effect(dc.CreateEffect(EffectGuids.GaussianBlur));
        blur.SetValue((uint)GaussianBlurProperties.StandardDeviation, 4.5f);
        dw = DWrite.DWriteCreateFactory<IDWriteFactory>();
        IDWriteTextFormat F(float size, FontWeight w, TextAlignment a)
        {
            var f = dw.CreateTextFormat("Segoe UI", null, w, DwFontStyle.Normal, FontStretch.Normal, size);
            f.TextAlignment = a; f.WordWrapping = WordWrapping.NoWrap; return f;
        }
        fTitle = F(11.5f, FontWeight.Bold, TextAlignment.Leading);
        fBody = F(10f, FontWeight.Normal, TextAlignment.Leading); fBodyR = F(10f, FontWeight.Normal, TextAlignment.Trailing);
        fSmall = F(8.5f, FontWeight.Normal, TextAlignment.Leading); fSmallR = F(8.5f, FontWeight.Normal, TextAlignment.Trailing);
        fTag = F(8f, FontWeight.Normal, TextAlignment.Leading);
        fWrap = F(10f, FontWeight.Normal, TextAlignment.Leading);
        fWrap.WordWrapping = WordWrapping.Wrap;
        fWrap.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, dw.CreateEllipsisTrimmingSign(fWrap));
        fCenter = F(8.5f, FontWeight.Bold, TextAlignment.Center);
        roundStroke = factory.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        dashStroke = factory.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Dash });
    }

    /// <summary>(Re)creates the surfaces for a widget of the given size in DIPs at the given DPI scale.</summary>
    public void Resize(float widthDip, float heightDip, float dpiScale)
    {
        int w = (int)Math.Ceiling(widthDip * dpiScale), h = (int)Math.Ceiling(heightDip * dpiScale);
        if (target != null && w == PxW && h == PxH && dpiScale == scale) return;
        target?.Dispose(); glowSrc?.Dispose(); staging?.Dispose();
        PxW = w; PxH = h; scale = dpiScale; wDip = widthDip; hDip = heightDip;
        var fmt = new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied); float dpi = 96 * dpiScale;
        target = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(fmt, dpi, dpi, BitmapOptions.Target));
        glowSrc = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(fmt, dpi, dpi, BitmapOptions.Target));
        staging = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(fmt, dpi, dpi, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        blur.SetInput(0, glowSrc, true);
    }

    // ------------------------------------------------------------ brushes and colours
    ID2D1SolidColorBrush Br(byte r, byte g, byte b, float a = 1f)
    {
        uint key = (uint)(r << 24 | g << 16 | b << 8 | (byte)Math.Round(a * 255));
        if (!brushes.TryGetValue(key, out var br)) brushes[key] = br = dc.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a));
        return br;
    }
    ID2D1SolidColorBrush Br((byte r, byte g, byte b) c, float a = 1f) => Br(c.r, c.g, c.b, a);
    (byte r, byte g, byte b) Hex(string s)
    {
        if (parsed.TryGetValue(s, out var c)) return c;
        c = (255, 255, 255);
        if (s != null && s.Length == 7 && s[0] == '#' && int.TryParse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            c = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return parsed[s ?? ""] = c;
    }

    void Text(string s, IDWriteTextFormat f, float l, float t, float r, float b, ID2D1Brush br) =>
        dc.DrawText(s, f, new RawRectF(l, t, r, b), br, DrawTextOptions.Clip);

    // ------------------------------------------------------------ frame
    sealed class LineGeo { public PanelRuntime P; public LineConfig Cfg; public ID2D1PathGeometry Line, Area; public RawRectF Plot; }

    public void Render(Engine e, GlobalConfig g)
    {
        float W = g.Width; float op = (float)Math.Clamp(g.Opacity, 0.2, 1.0);
        TitleCol = Hex(g.Theme.Text); borderCol = Hex(g.Theme.Border);
        themeBgTop = Hex(g.Theme.BackgroundTop); themeBgBottom = Hex(g.Theme.BackgroundBottom);
        var geos = new List<LineGeo>();
        try
        {
            foreach (var p in e.Panels.Where(p => p.IsSeries)) BuildGeometry(p, W, geos);

            // Pass 1: wide glow strokes for lines that ask for it; blurred by the GPU below.
            bool anyGlow = geos.Any(x => x.Cfg.Glow && x.Line != null);
            if (anyGlow)
            {
                dc.Target = glowSrc; dc.BeginDraw(); dc.Clear(new Color4(0, 0, 0, 0));
                foreach (var x in geos.Where(x => x.Cfg.Glow && x.Line != null))
                {
                    dc.PushAxisAlignedClip(Grow(x.Plot, 6), AntialiasMode.Aliased);
                    dc.DrawGeometry(x.Line, Br(Hex(x.Cfg.Color), 0.95f), x.Cfg.Width * 2.6f, roundStroke);
                    dc.PopAxisAlignedClip();
                }
                dc.EndDraw();
            }

            // Pass 2: backgrounds, grid, fills, glow, crisp lines, text.
            dc.Target = target; dc.BeginDraw(); dc.Clear(new Color4(0, 0, 0, 0));
            foreach (var p in e.Panels) { Background(p, W, op); if (p.IsSeries) GridAndFill(p, W, geos); }
            if (anyGlow) dc.DrawImage(blur.Output, null, null, InterpolationMode.Linear, CompositeMode.SourceOver);
            foreach (var x in geos.Where(x => x.Line != null))
            {
                dc.PushAxisAlignedClip(Grow(x.Plot, 1), AntialiasMode.Aliased);
                dc.DrawGeometry(x.Line, Br(Lighten(Hex(x.Cfg.Color), x.Cfg.Glow ? 0.45f : 0f)), x.Cfg.Width, roundStroke);
                dc.PopAxisAlignedClip();
            }
            foreach (var p in e.Panels) Content(p, W, e);
            if (ShowSettingsGear) DrawSettingsGear(W);
            dc.EndDraw();
        }
        finally { foreach (var x in geos) { x.Line?.Dispose(); x.Area?.Dispose(); } }
    }

    static RawRectF Grow(RawRectF r, float d) => new(r.Left - d, r.Top - d, r.Right + d, r.Bottom + d);
    static (byte r, byte g, byte b) Lighten((byte r, byte g, byte b) c, float t) =>
        ((byte)(c.r + (255 - c.r) * t), (byte)(c.g + (255 - c.g) * t), (byte)(c.b + (255 - c.b) * t));

    static RawRectF PlotRect(PanelRuntime p, float W) =>
        new(Engine.Margin + 8, p.Top + p.PlotTopOffset, W - Engine.Margin - 8, p.Top + p.PlotTopOffset + p.PlotHeight);

    void BuildGeometry(PanelRuntime p, float W, List<LineGeo> outList)
    {
        var plot = PlotRect(p, W); int cells = p.Layout.HistCells + p.Layout.LiveCells;
        float cw = (plot.Right - plot.Left) / cells, ph = plot.Bottom - plot.Top; double max = Math.Max(1e-9, p.ScaleMax);
        for (int li = 0; li < p.Cfg.Lines.Count && li < p.Series.Length; li++)
        {
            var lc = p.Cfg.Lines[li]; if (!lc.Visible) continue;
            pts.Clear(); var s = p.Series[li];
            for (int i = 0; i < cells; i++)
            {
                float v = s.Cell(i); if (float.IsNaN(v)) continue;
                pts.Add(new Vector2(plot.Left + (i + 0.5f) * cw, plot.Bottom - (float)Math.Clamp(v / max, 0, 1) * ph));
            }
            var geo = new LineGeo { P = p, Cfg = lc, Plot = plot };
            if (pts.Count >= 2)
            {
                var arr = pts.ToArray();
                geo.Line = factory.CreatePathGeometry();
                using (var sink = geo.Line.Open()) { sink.BeginFigure(arr[0], FigureBegin.Hollow); sink.AddLines(arr.AsSpan(1).ToArray()); sink.EndFigure(FigureEnd.Open); sink.Close(); }
                if (lc.Fill)
                {
                    geo.Area = factory.CreatePathGeometry();
                    using var sink = geo.Area.Open();
                    sink.BeginFigure(new Vector2(arr[0].X, plot.Bottom), FigureBegin.Filled); sink.AddLines(arr);
                    sink.AddLine(new Vector2(arr[^1].X, plot.Bottom)); sink.EndFigure(FigureEnd.Closed); sink.Close();
                }
            }
            outList.Add(geo);
        }
    }

    // ------------------------------------------------------------ panel parts
    void Background(PanelRuntime p, float W, float op)
    {
        // A panel with its own SectionBgTop/Bottom set overrides the app-wide theme background for just this
        // section; "" (the default) inherits it. Border and text stay app-wide only - Bart only asked for a
        // per-section override on background.
        var (tr, tg, tb) = p.Cfg.SectionBgTop != "" ? Hex(p.Cfg.SectionBgTop) : themeBgTop;
        var (br2, bg2, bb2) = p.Cfg.SectionBgBottom != "" ? Hex(p.Cfg.SectionBgBottom) : themeBgBottom;
        var rr = new RoundedRectangle(new RawRectF(Engine.Margin, p.Top, W - Engine.Margin, p.Top + p.Height), 9, 9);
        using (var gs = dc.CreateGradientStopCollection(new[]
        {
            new GradientStop { Position = 0, Color = new Color4(tr / 255f, tg / 255f, tb / 255f, 0.94f * op) },
            new GradientStop { Position = 1, Color = new Color4(br2 / 255f, bg2 / 255f, bb2 / 255f, op) },
        }))
        using (var bg = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(0, p.Top), EndPoint = new Vector2(0, p.Top + p.Height) }, gs))
            dc.FillRoundedRectangle(rr, bg);
        dc.DrawRoundedRectangle(rr, Br(borderCol, 0.55f * Math.Min(1f, op + 0.2f)), 1.1f);
    }

    void GridAndFill(PanelRuntime p, float W, List<LineGeo> geos)
    {
        var plot = PlotRect(p, W); var grid = Br(220, 150, 255, 0.14f); float ph = plot.Bottom - plot.Top;
        for (int i = 0; i <= 4; i++) { float y = plot.Top + ph * i / 4f; dc.DrawLine(new Vector2(plot.Left, y), new Vector2(plot.Right, y), grid, 1f); }
        int cells = p.Layout.HistCells + p.Layout.LiveCells; float xd = plot.Left + (plot.Right - plot.Left) * p.Layout.HistCells / cells;
        dc.DrawLine(new Vector2(xd, plot.Top), new Vector2(xd, plot.Bottom), Br(255, 255, 255, 0.35f), 1f, dashStroke);
        foreach (var x in geos.Where(x => x.P == p && x.Area != null))
        {
            var c = Hex(x.Cfg.Color);
            using var gs = dc.CreateGradientStopCollection(new[]
            {
                new GradientStop { Position = 0, Color = new Color4(c.r / 255f, c.g / 255f, c.b / 255f, 0.34f) },
                new GradientStop { Position = 1, Color = new Color4(c.r / 255f, c.g / 255f, c.b / 255f, 0f) },
            });
            using var fill = dc.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = new Vector2(0, plot.Top), EndPoint = new Vector2(0, plot.Bottom) }, gs);
            dc.PushAxisAlignedClip(plot, AntialiasMode.Aliased); dc.FillGeometry(x.Area, fill); dc.PopAxisAlignedClip();
        }
    }

    static string TitleOf(PanelRuntime p) => p.Title != "" ? p.Title : p.Cfg.Kind switch
    {
        PanelKind.Cpu => "CPU", PanelKind.Ram => "Memory", PanelKind.Gpu => "GPU", PanelKind.Network => "Network", PanelKind.TopProcesses => "Top processes", _ => "",
    };

    float Measure(string s, IDWriteTextFormat f)
    {
        string key = s + "|" + f.FontSize;
        if (widths.TryGetValue(key, out var w)) return w;
        if (widths.Count > 400) widths.Clear();   // legend values change constantly; keep the cache bounded
        using var layout = dw.CreateTextLayout(s, f, 2000f, 30f);
        return widths[key] = layout.Metrics.Width;
    }

    List<(string text, (byte r, byte g, byte b) col, float w)> LegendItems(PanelRuntime p)
    {
        var items = new List<(string, (byte, byte, byte), float)>();
        for (int i = 0; i < p.Cfg.Lines.Count; i++)
        {
            var lc = p.Cfg.Lines[i]; if (!lc.Visible) continue;   // only lines that are switched on
            string t = lc.Label;
            if (p.Source.LegendWithValues && i < p.Latest.Length && !float.IsNaN(p.Latest[i])) t += " " + p.Source.Format(p.Latest[i]);
            items.Add((t, Hex(lc.Color), Measure(t, fSmall)));
        }
        return items;
    }

    void DrawLegend(List<(string text, (byte r, byte g, byte b) col, float w)> items, float x, float y, float maxRight)
    {
        var txt = Br(TitleCol, 0.75f);
        foreach (var it in items)
        {
            if (x + 12 + it.w > maxRight + 1) break;
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, y + 5.5f, x + 9, y + 8f), 1, 1), Br(it.col));
            Text(it.text, fSmall, x + 12, y + 1, x + 14 + it.w, y + 14, txt);
            x += 12 + it.w + 10;
        }
    }

    float Chip(string s, (byte r, byte g, byte b) c, float x, float y)
    {
        float w = Measure(s, fTag) + 10; var rr = new RoundedRectangle(new RawRectF(x, y, x + w, y + 13), 3, 3);
        dc.FillRoundedRectangle(rr, Br(c, 0.20f)); dc.DrawRoundedRectangle(rr, Br(c, 0.65f), 0.8f);
        Text(s, fTag, x + 5, y + 1, x + w, y + 13, Br(c));
        return x + w + 4;
    }

    void Content(PanelRuntime p, float W, Engine e)
    {
        float l = Engine.Margin + 10, r = W - Engine.Margin - 10, t = p.Top;
        var txt = Br(TitleCol, 0.95f); var dim = Br(TitleCol, 0.60f);
        if (p.Cfg.Kind == PanelKind.Drives) { DriveContent(p, W, l, r, t, txt, dim); return; }
        if (p.Cfg.Kind == PanelKind.Clipboard) { ClipboardContent(p, W, e, l, r, t, txt, dim); return; }
        if (p.Cfg.Kind == PanelKind.Battery) { BatteryContent(p, W, l, r, t, txt, dim); return; }
        switch (p.Cfg.Kind)
        {
            case PanelKind.Uptime:
                Text(p.Text, fBody, l, t + 5, r, t + 24, txt); return;
            case PanelKind.TopProcesses:
                Text(p.Cfg.SortByMemory ? "Top processes (memory)" : "Top processes (CPU)", fTitle, l, t + 4, r, t + 22, txt);
                Text(p.Cfg.SortByMemory ? "" : "CPU", fSmallR, r - 90, t + 8, r - 62, t + 22, dim); Text("Memory", fSmallR, r - 56, t + 8, r, t + 22, dim);
                for (int i = 0; i < p.Procs.Count; i++)
                {
                    var pr = p.Procs[i]; float y = t + Engine.Header + i * Engine.RowProc;
                    // Bar behind the row: length and colour follow the load (green -> yellow -> amber -> orange -> red at the full-scale value).
                    double pct = p.Cfg.SortByMemory ? 100.0 * pr.Mem / Math.Max(1, e.TotalPhysBytes) : pr.Cpu;
                    double frac = Math.Clamp(pct / Math.Max(0.1, p.Cfg.BarFullScale), 0, 1);
                    float bw = (float)frac * (r - l + 8);
                    if (bw >= 2) dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(l - 4, y + 1, l - 4 + bw, y + 14), 3, 3), Br(Fmt.LoadColor(frac), 0.42f));
                    string n = pr.Instances > 1 ? $"{pr.Name} ({pr.Instances})" : pr.Name;
                    Text(n, fBody, l, y, r - 96, y + 15, txt);
                    Text(pr.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%", fBodyR, r - 96, y, r - 58, y + 15, txt);
                    Text(Fmt.Bytes(pr.Mem), fBodyR, r - 58, y, r, y + 15, dim);
                }
                return;
        }
        // series panels: title + readout, optional legend row, axis label
        Text(TitleOf(p), fTitle, l, t + 4, l + 150, t + 22, txt);
        float titleEnd = l + Measure(TitleOf(p), fTitle) + 8;
        Text(p.Readout ?? "", fSmallR, Math.Max(titleEnd, l + 70), t + 7, r, t + 22, dim);
        if (p.LegendRow) DrawLegend(LegendItems(p), l, t + 21, r);
        var plot = PlotRect(p, W);
        Text(p.Source.Format((float)p.ScaleMax), fSmall, plot.Left + 2, plot.Top + 1, plot.Left + 90, plot.Top + 14, Br(TitleCol, 0.45f));
    }

    void DriveContent(PanelRuntime p, float W, float l, float r, float t, ID2D1Brush txt, ID2D1Brush dim)
    {
        var v = p.Volume; float x = l;
        Text(p.Title, fTitle, x, t + 3, x + 40, t + 20, txt); x += Measure(p.Title, fTitle) + 6;
        if (v.Label != "")
        {
            string lab = v.Label.Length > 16 ? v.Label.Substring(0, 15) + "..." : v.Label;
            Text(lab, fSmall, x, t + 6, x + 110, t + 20, dim); x += Measure(lab, fSmall) + 6;
        }
        var violet = ((byte)190, (byte)160, (byte)255);
        if (v.Kind == VolKind.Network) x = Chip("Network", ((byte)77, (byte)225, (byte)255), x, t + 5);
        else if (v.Kind == VolKind.Optical) x = Chip("CD/DVD", violet, x, t + 5);
        else if (v.Kind == VolKind.Removable) x = Chip("Removable", violet, x, t + 5);
        if (v.ReadOnly) x = Chip("Read-only", ((byte)255, (byte)200, (byte)87), x, t + 5);

        if (p.IsSeries)
        {
            var items = LegendItems(p); float total = items.Sum(i => 12 + i.w + 10) - 10;
            DrawLegend(items, Math.Max(x + 4, r - total), t + 4, r);
            var plot = PlotRect(p, W);
            Text(p.Source.Format((float)p.ScaleMax), fSmall, plot.Left + 2, plot.Top + 1, plot.Left + 90, plot.Top + 14, Br(TitleCol, 0.45f));
        }

        float by = p.Top + p.PlotTopOffset + (p.IsSeries ? p.PlotHeight + 4 : 0);
        if (p.Cfg.ShowSpaceBar)
        {
            float bl = l, br = r - 124, bt = by + 4;
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(bl, bt, br, bt + 8), 4, 4), Br(255, 255, 255, 0.10f));
            float fw = (float)Math.Clamp(v.UsedFraction, 0, 1) * (br - bl);
            // Read-only media (a full disc) is not a warning, so it gets a neutral colour instead of the load ramp.
            var barCol = v.ReadOnly ? ((byte)170, (byte)150, (byte)215) : Fmt.LoadColor(v.UsedFraction);
            if (fw > 1) dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(bl, bt, bl + fw, bt + 8), 4, 4), Br(barCol, v.ReadOnly ? 0.75f : 0.9f));
            Text($"{Fmt.Bytes(v.Free)} free of {Fmt.Bytes(v.Total)}", fSmallR, br + 6, by + 1, r, by + 15, dim);
        }
    }

    // ------------------------------------------------------------ Battery panel
    /// <summary>Draws its own graph line (bypassing the generic single-colour BuildGeometry pass - both of this
    /// panel's LineConfigs are Visible = false for exactly that reason) so each point can be coloured green while
    /// charging or by the yellow-orange-red discharge ramp while discharging, plus a battery-fullness glyph docked
    /// to whichever side Settings picked.</summary>
    void BatteryContent(PanelRuntime p, float W, float l, float r, float t, ID2D1Brush txt, ID2D1Brush dim)
    {
        Text("Battery", fTitle, l, t + 4, l + 90, t + 22, txt);
        Text(p.Readout ?? "", fSmallR, l + 80, t + 7, r, t + 22, dim);

        float latestPct = !float.IsNaN(p.Latest[0]) ? p.Latest[0] : (p.Latest.Length > 1 && !float.IsNaN(p.Latest[1]) ? p.Latest[1] : float.NaN);
        bool latestCharging = !float.IsNaN(p.Latest[0]);

        const float glyphW = 30;
        var full = PlotRect(p, W);
        bool left = p.Cfg.BatteryIconLeft;
        var plot = left ? new RawRectF(full.Left + glyphW + 8, full.Top, full.Right, full.Bottom) : new RawRectF(full.Left, full.Top, full.Right - glyphW - 8, full.Bottom);

        int cells = p.Layout.HistCells + p.Layout.LiveCells; float cw = (plot.Right - plot.Left) / cells, ph = plot.Bottom - plot.Top;
        Vector2? prev = null;
        for (int i = 0; i < cells; i++)
        {
            float vc = p.Series[0].Cell(i), vd = p.Series[1].Cell(i);
            bool have = !float.IsNaN(vc) || !float.IsNaN(vd);
            if (!have) { prev = null; continue; }
            bool charging = !float.IsNaN(vc); float v = charging ? vc : vd;
            var pt = new Vector2(plot.Left + (i + 0.5f) * cw, plot.Bottom - (float)Math.Clamp(v / 100.0, 0, 1) * ph);
            if (prev is { } pp)
            {
                var col = charging ? ((byte)124, (byte)255, (byte)107) : Fmt.DischargeColor(v);
                dc.DrawLine(pp, pt, Br(col), 2f, roundStroke);
            }
            prev = pt;
        }

        DrawBatteryGlyph(left ? full.Left : full.Right - glyphW, full.Top, glyphW, full.Bottom - full.Top, latestPct, latestCharging, txt);
    }

    /// <summary>A drawn battery silhouette (body + terminal nub), filled bottom-up to the charge percentage and
    /// tinted the same way as the graph line, with the percentage centred inside it.</summary>
    void DrawBatteryGlyph(float x, float y, float w, float h, float percent, bool charging, ID2D1Brush txt)
    {
        if (float.IsNaN(percent)) percent = 0;
        float nub = w * 0.4f, nubH = 4f, bodyTop = y + nubH + 1;
        dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x + (w - nub) / 2, y, x + (w + nub) / 2, bodyTop), 1.5f, 1.5f), Br(TitleCol, 0.55f));
        var body = new RoundedRectangle(new RawRectF(x, bodyTop, x + w, y + h), 4, 4);
        dc.DrawRoundedRectangle(body, Br(TitleCol, 0.6f), 1.6f);
        float pad = 3, innerH = (y + h) - bodyTop - pad * 2, fillH = innerH * (float)Math.Clamp(percent / 100.0, 0, 1);
        var fillCol = charging ? ((byte)124, (byte)255, (byte)107) : Fmt.DischargeColor(percent);
        if (fillH > 1)
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x + pad, y + h - pad - fillH, x + w - pad, y + h - pad), 2.5f, 2.5f), Br(fillCol, 0.85f));
        Text($"{percent:0}%", fCenter, x, (bodyTop + y + h) / 2 - 6, x + w, (bodyTop + y + h) / 2 + 8, txt);
    }

    // ------------------------------------------------------------ Clipboard panel
    static string Age(DateTime createdUtc)
    {
        var s = DateTime.UtcNow - createdUtc;
        return s.TotalMinutes < 1 ? "just now" : s.TotalHours < 1 ? $"{(int)s.TotalMinutes}m ago" : s.TotalDays < 1 ? $"{(int)s.TotalHours}h ago" : $"{(int)s.TotalDays}d ago";
    }

    /// <summary>The items a search currently matches (or everything, if the query is blank) - the same list drawing and hit-testing must agree on.</summary>
    public static IReadOnlyList<ClipItem> ClipVisibleItems(Engine e, string query) =>
        string.IsNullOrWhiteSpace(query) ? e.Clipboard.Items : e.Clipboard.Search(query).ToList();

    /// <summary>Each item's current row height (DIPs), for the given content width and line cap. MainWindow calls
    /// this for hit-testing/scrolling with the exact same numbers the last Render() drew, since it reads (and
    /// lazily fills) the same per-item cache Render() uses - drawing and hit-testing can never disagree.</summary>
    public float[] ClipRowHeights(IReadOnlyList<ClipItem> items, ClipboardStore store, float bodyWidth, int maxLines)
    {
        EnsureClipCacheFresh(bodyWidth, maxLines);
        var heights = new float[items.Count];
        for (int i = 0; i < items.Count; i++) heights[i] = RowContentHeight(items[i], store, bodyWidth, maxLines);
        return heights;
    }

    void EnsureClipCacheFresh(float width, int maxLines)
    {
        if (width == clipCacheWidth && maxLines == clipCacheMaxLines) return;
        foreach (var l in clipTextCache.Values) l.Dispose(); clipTextCache.Clear();
        foreach (var b in clipImageCache.Values) b?.Dispose(); clipImageCache.Clear(); clipImageHeightCache.Clear();
        foreach (var fr in clipFilesCache.Values) foreach (var (layout, _) in fr.Shown) layout.Dispose(); clipFilesCache.Clear();
        clipCacheWidth = width; clipCacheMaxLines = maxLines;
    }

    /// <summary>Drops cached content for any item no longer in the store, so a deleted item's cached bitmap/layout is released promptly.</summary>
    void PruneClipCache(IReadOnlyList<ClipItem> allItems)
    {
        if (clipTextCache.Count == 0 && clipImageCache.Count == 0 && clipFilesCache.Count == 0) return;
        var live = new HashSet<string>(allItems.Select(i => i.Id));
        foreach (var id in clipTextCache.Keys.Where(id => !live.Contains(id)).ToList()) { clipTextCache[id].Dispose(); clipTextCache.Remove(id); }
        foreach (var id in clipImageCache.Keys.Where(id => !live.Contains(id)).ToList()) { clipImageCache[id]?.Dispose(); clipImageCache.Remove(id); clipImageHeightCache.Remove(id); }
        foreach (var id in clipFilesCache.Keys.Where(id => !live.Contains(id)).ToList()) { foreach (var (layout, _) in clipFilesCache[id].Shown) layout.Dispose(); clipFilesCache.Remove(id); }
    }

    float RowContentHeight(ClipItem item, ClipboardStore store, float width, int maxLines) => item.Kind switch
    {
        ClipKind.Text or ClipKind.RichText => ClipboardLayout.RowHeightForLines(Math.Clamp((int)(GetOrBuildTextLayout(item, width, maxLines)?.Metrics.LineCount ?? 1), 1, maxLines)),
        ClipKind.Image => ClipboardLayout.RowHeightForImage(GetOrBuildImageThumb(item, store, width, maxLines).height),
        _ => GetOrBuildFileRows(item, width, maxLines).Height,
    };

    void ClipboardContent(PanelRuntime p, float W, Engine e, float l, float r, float t, ID2D1Brush txt, ID2D1Brush dim)
    {
        var store = e.Clipboard;
        Text("Clipboard", fTitle, l, t + 4, l + 90, t + 22, txt);
        Text($"{store.Items.Count} items", fSmallR, l + 80, t + 7, r - Engine.ClipIcon - 6, t + 22, dim);
        GlyphSearch(r - Engine.ClipIcon, t + 5, ClipSearchVisible ? Br(TitleCol, 0.95f) : dim);

        var items = ClipVisibleItems(e, ClipSearchQuery);
        int maxLines = Math.Max(1, p.Cfg.ClipMaxPreviewLines);
        float bodyWidth = ClipboardLayout.BodyWidth(W);
        PruneClipCache(store.Items);
        var heights = ClipRowHeights(items, store, bodyWidth, maxLines);

        float headerBottom = t + Engine.ClipHeader;
        float listTop = headerBottom + (ClipSearchVisible ? Engine.ClipHeader : 0);
        float listBottom = t + p.Height;
        dc.PushAxisAlignedClip(new RawRectF(l - 6, listTop, r + 6, listBottom), AntialiasMode.Aliased);
        float acc = 0;
        for (int i = 0; i < items.Count; i++)
        {
            float rowTop = listTop + acc - ClipScroll;
            if (rowTop + heights[i] >= listTop && rowTop <= listBottom) DrawClipRow(items[i], store, l, r, rowTop, heights[i], bodyWidth, maxLines, txt, dim);
            acc += heights[i];
            if (rowTop > listBottom) break;   // rows are drawn top-to-bottom; once we're past the visible area, the rest are too
        }
        dc.PopAxisAlignedClip();
        if (items.Count == 0)
            Text(ClipSearchQuery != "" ? "No matches." : "Nothing copied yet.", fBody, l, listTop + 6, r, listTop + 24, dim);

        int barW = p.Cfg.ClipScrollBarWidth;
        if (barW > 0)
        {
            var (thumbTop, thumbH) = ClipboardLayout.ScrollThumb(p.Height, ClipSearchVisible, heights, ClipScroll);
            if (thumbH > 0)
            {
                float bx = r + 3;
                dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(bx, listTop, bx + barW, listBottom), barW / 2f, barW / 2f), Br(255, 255, 255, 0.06f));
                dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(bx, listTop + thumbTop, bx + barW, listTop + thumbTop + thumbH), barW / 2f, barW / 2f), Br(TitleCol, 0.35f));
            }
        }
    }

    static readonly (byte r, byte g, byte b) ClipSelectedCol = (77, 225, 255);

    void DrawClipRow(ClipItem item, ClipboardStore store, float l, float r, float rowTop, float rowHeight, float bodyWidth, int maxLines, ID2D1Brush txt, ID2D1Brush dim)
    {
        if (store.SelectedId != "" && item.Id == store.SelectedId)
        {
            // The one row that's actually on the OS clipboard right now - a persistent marker, not a hover state.
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(l - 4, rowTop, r, rowTop + rowHeight - 1), 4, 4), Br(ClipSelectedCol, 0.07f));
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(l - 4, rowTop + 2, l - 1, rowTop + rowHeight - 3), 1.5f, 1.5f), Br(ClipSelectedCol, 0.9f));
        }
        var g = ClipboardLayout.Row(l, r, rowTop);
        DrawTypeBadge(g.TypeIconX, g.IconY, item, dim);
        Text(item.TypeLabel + "  " + Age(item.CreatedUtc), fSmall, g.TextLeft, rowTop + 2, g.OpenX - 6, rowTop + 14, dim);
        GlyphOpen(g.OpenX, g.IconY, dim);
        GlyphPin(g.PinX, g.IconY, item.Pinned, item.Pinned ? Br(255, 214, 79) : dim);
        GlyphDelete(g.DeleteX, g.IconY, Br(255, 130, 140, 0.75f));

        float bodyTop = ClipboardLayout.BodyTop(rowTop);
        switch (item.Kind)
        {
            case ClipKind.Text: case ClipKind.RichText:
                var layout = GetOrBuildTextLayout(item, bodyWidth, maxLines);
                if (layout != null) dc.DrawTextLayout(new Vector2(g.TextLeft, bodyTop), layout, txt);
                break;
            case ClipKind.Image:
                var (bmp, h) = GetOrBuildImageThumb(item, store, bodyWidth, maxLines);
                if (bmp != null)
                {
                    float bw = bmp.Size.Width;
                    dc.DrawBitmap(bmp, new RawRectF(g.TextLeft, bodyTop, g.TextLeft + bw, bodyTop + h), 1f, BitmapInterpolationMode.Linear, null);
                }
                else Text("(preview unavailable)", fSmall, g.TextLeft, bodyTop, g.TextRight, bodyTop + Engine.ClipLineH, dim);
                break;
            default:
                var fr = GetOrBuildFileRows(item, bodyWidth, maxLines);
                float fy = bodyTop; float textX = g.TextLeft + Engine.ClipFileIcon + 4;
                foreach (var (fileLayout, iconKey) in fr.Shown)
                {
                    DrawFileGlyph(g.TextLeft, fy + 1, iconKey);
                    dc.DrawTextLayout(new Vector2(textX, fy), fileLayout, txt);
                    fy += Math.Max(Engine.ClipLineH, fileLayout.Metrics.Height);
                }
                if (fr.Hidden > 0) Text($"+ {fr.Hidden} more", fSmall, g.TextLeft, fy, g.TextRight, fy + Engine.ClipLineH, dim);
                break;
        }
        dc.DrawLine(new Vector2(l, rowTop + rowHeight - 1), new Vector2(r, rowTop + rowHeight - 1), Br(255, 255, 255, 0.06f), 1f);
    }

    /// <summary>Text/RichText only: a word-wrapped layout capped at maxLines (DirectWrite trims the last line with
    /// an ellipsis if it overflows). Cached per item; LineCount from this same bounded layout is what determines
    /// the row's height, so the two can never disagree.</summary>
    IDWriteTextLayout GetOrBuildTextLayout(ClipItem item, float width, int maxLines)
    {
        if (clipTextCache.TryGetValue(item.Id, out var cached)) return cached;
        float capHeight = maxLines * Engine.ClipLineH;
        IDWriteTextLayout layout;
        try { layout = dw.CreateTextLayout(item.PlainText, fWrap, Math.Max(10, width), capHeight); }
        catch (ExternalException ex) { AppLog.Write("clipboard text layout failed for item " + item.Id + ": " + ex.Message); return null; }
        clipTextCache[item.Id] = layout;
        return layout;
    }

    /// <summary>Image only: a scaled thumbnail decoded from the stored PNG blob, cached per item. Uses
    /// CopyFromMemory into a bitmap created empty - a different, more established Direct2D API than the
    /// create-with-initial-data constructor that crashed for row type icons (see DrawTypeBadge); verified safe by
    /// dedicated headless checks before being wired in here.</summary>
    (ID2D1Bitmap bmp, float height) GetOrBuildImageThumb(ClipItem item, ClipboardStore store, float maxWidth, int maxLines)
    {
        if (clipImageCache.TryGetValue(item.Id, out var cachedBmp) && clipImageHeightCache.TryGetValue(item.Id, out var cachedH)) return (cachedBmp, cachedH);
        float capHeight = maxLines * Engine.ClipLineH;
        try
        {
            var bytes = store.ReadBlobBytes(item);
            using var ms = new MemoryStream(bytes);
            using var src = new Bitmap(ms);
            float scale = Math.Min(1f, Math.Min(maxWidth / src.Width, capHeight / src.Height));
            int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
            var (bmp, _) = BuildBitmapFromGdi(src, w, h);
            clipImageCache[item.Id] = bmp; clipImageHeightCache[item.Id] = h;
            return (bmp, h);
        }
        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is ExternalException || ex is OutOfMemoryException)
        {
            AppLog.Write("clipboard image thumbnail failed for item " + item.Id + ": " + ex.Message);
            clipImageCache[item.Id] = null; clipImageHeightCache[item.Id] = Engine.ClipLineH;
            return (null, Engine.ClipLineH);
        }
    }

    /// <summary>Scales `src` to w x h, uploads it as a premultiplied ID2D1Bitmap via CopyFromMemory (byte[] overload
    /// - no unsafe pointer in this code at all).</summary>
    (ID2D1Bitmap, int) BuildBitmapFromGdi(Image src, int w, int h)
    {
        using var thumb = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumb)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, w, h); }
        var data = thumb.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        byte[] buf;
        try
        {
            buf = new byte[w * h * 4];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int i = 0; i < w * h; i++)
            {
                byte a = buf[i * 4 + 3];
                buf[i * 4] = (byte)(buf[i * 4] * a / 255); buf[i * 4 + 1] = (byte)(buf[i * 4 + 1] * a / 255); buf[i * 4 + 2] = (byte)(buf[i * 4 + 2] * a / 255);
            }
        }
        finally { thumb.UnlockBits(data); }
        var fmt = new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
        var bmp = dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(fmt, 96, 96, BitmapOptions.None));
        bmp.CopyFromMemory(buf, (uint)(w * 4));
        return (bmp, h);
    }

    /// <summary>Files only: each file path gets its own word-wrapped layout (so a long path wraps instead of
    /// clipping), drawn with its real file-type icon. Files are shown in order until the panel's line budget runs
    /// out; anything after that is dropped and counted into Hidden rather than measured.</summary>
    FileRows GetOrBuildFileRows(ClipItem item, float bodyWidth, int maxLines)
    {
        if (clipFilesCache.TryGetValue(item.Id, out var cached)) return cached;
        var result = BuildFileRows(item, bodyWidth, maxLines, maxLines);
        if (result.Hidden > 0 && maxLines > 1)   // reserve one line for "+N more" and remeasure, now that we know it is needed
        {
            foreach (var (layout, _) in result.Shown) layout.Dispose();
            result = BuildFileRows(item, bodyWidth, maxLines, maxLines - 1);
        }
        clipFilesCache[item.Id] = result;
        return result;
    }

    FileRows BuildFileRows(ClipItem item, float bodyWidth, int maxLines, int lineBudget)
    {
        float textWidth = Math.Max(10, bodyWidth - Engine.ClipFileIcon - 4);
        var fr = new FileRows(); int used = 0;
        foreach (var path in item.Files)
        {
            if (used >= lineBudget) { fr.Hidden++; continue; }
            IDWriteTextLayout layout;
            try { layout = dw.CreateTextLayout(path, fWrap, textWidth, (lineBudget - used) * Engine.ClipLineH); }
            catch (ExternalException ex) { AppLog.Write("clipboard file path layout failed: " + ex.Message); fr.Hidden++; continue; }
            used += Math.Clamp((int)layout.Metrics.LineCount, 1, lineBudget - used);
            string ext = Path.GetExtension(path);
            fr.Shown.Add((layout, ext == "" ? "\0dir" : ext.ToLowerInvariant()));
        }
        fr.Height = ClipboardLayout.RowHeightForLines(Math.Max(1, used + (fr.Hidden > 0 ? 1 : 0)));
        return fr;
    }

    // ponytail: retried real per-extension icon extraction here with a different, more established D2D upload API
    // (CopyFromMemory, proven safe by the image-thumbnail feature above) instead of the create-with-pointer
    // overload that crashed the first attempt (see the comment on DrawTypeBadge). It crashed again, with the same
    // fatal/uncatchable signature, on an extension the first attempt never exercised - which rules out the D2D
    // upload step as the cause and points at the SHGetFileInfo/Icon.FromHandle/DestroyIcon chain itself being
    // unsafe in this app's context, regardless of what happens to the icon afterward. Not retrying a third way;
    // a drawn glyph, tinted by a small extension->colour table, gives some of the same visual variety with no
    // Windows icon interop at all.
    static readonly (byte r, byte g, byte b) FileGlyphNeutral = (190, 160, 255);
    static (byte r, byte g, byte b) FileGlyphColor(string ext) => ext switch
    {
        ".doc" or ".docx" or ".rtf" => (90, 150, 255),
        ".xls" or ".xlsx" or ".csv" => (110, 220, 130),
        ".ppt" or ".pptx" => (255, 140, 90),
        ".pdf" => (255, 100, 100),
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => (255, 200, 90),
        ".zip" or ".rar" or ".7z" => (200, 170, 120),
        ".txt" or ".md" or ".log" => (190, 190, 200),
        _ => FileGlyphNeutral,
    };

    /// <summary>A small drawn file/folder glyph per line, tinted by extension - no Windows icon interop.</summary>
    void DrawFileGlyph(float x, float y, string extKey)
    {
        bool isDir = extKey == "\0dir";
        var c = isDir ? FileGlyphNeutral : FileGlyphColor(extKey);
        var br = Br(c, 0.85f);
        float s = Engine.ClipFileIcon;
        if (isDir)
        {
            dc.DrawLine(new Vector2(x + 1, y + 3.5f), new Vector2(x + 4.5f, y + 3.5f), br, 1f);
            dc.DrawLine(new Vector2(x + 4.5f, y + 3.5f), new Vector2(x + 6, y + 2f), br, 1f);
            dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + 1, y + 3.5f, x + s - 1, y + s - 1), 1, 1), br, 1f);
        }
        else
        {
            dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + 1.5f, y + 0.5f, x + s - 1.5f, y + s - 0.5f), 1, 1), br, 1f);
            for (int i = 0; i < 2; i++) { float ly = y + 3.5f + i * 3f; dc.DrawLine(new Vector2(x + 3.5f, ly), new Vector2(x + s - 3.5f, ly), br, 0.9f); }
        }
    }

    // Small drawn glyphs for the row actions - the widget's whole style is Direct2D primitives, so these match
    // rather than importing icon fonts/resources. The type icon (below) is the one place a real OS icon is used.
    void GlyphOpen(float cx, float cy, ID2D1Brush br)
    {
        float s = Engine.ClipIcon;
        dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(cx + 2, cy + 2, cx + s - 2, cy + s - 2), 2, 2), br, 1f);
        dc.DrawLine(new Vector2(cx + 5, cy + s - 5), new Vector2(cx + s - 5, cy + 5), br, 1.3f);
        dc.DrawLine(new Vector2(cx + s - 9, cy + 5), new Vector2(cx + s - 5, cy + 5), br, 1.3f);
        dc.DrawLine(new Vector2(cx + s - 5, cy + 5), new Vector2(cx + s - 5, cy + 9), br, 1.3f);
    }
    /// <summary>A map-pin / teardrop: rounded top tapering to a point at the bottom. Deliberately not a circle with
    /// a diagonal line - that reads as the magnifying-glass search icon at this size, not as "pin".</summary>
    void GlyphPin(float cx, float cy, bool filled, ID2D1Brush br)
    {
        float mid = cx + Engine.ClipIcon / 2f, top = cy + 2.5f, rad = 3.3f;
        using var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(new Vector2(mid - rad, top + rad), FigureBegin.Filled);
            sink.AddArc(new ArcSegment { Point = new Vector2(mid + rad, top + rad), Size = new Vortice.Mathematics.Size(rad, rad), SweepDirection = SweepDirection.Clockwise, ArcSize = ArcSize.Large });
            sink.AddLine(new Vector2(mid, top + rad * 2 + 4.5f));   // the point at the bottom
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        if (filled) dc.FillGeometry(geo, br); else dc.DrawGeometry(geo, br, 1.3f);
    }
    void GlyphDelete(float cx, float cy, ID2D1Brush br)
    {
        float s = Engine.ClipIcon, pad = 4;
        dc.DrawLine(new Vector2(cx + pad, cy + pad), new Vector2(cx + s - pad, cy + s - pad), br, 1.4f);
        dc.DrawLine(new Vector2(cx + s - pad, cy + pad), new Vector2(cx + pad, cy + s - pad), br, 1.4f);
    }
    void GlyphSearch(float cx, float cy, ID2D1Brush br)
    {
        var center = new Vector2(cx + 6, cy + 6);
        dc.DrawEllipse(new Ellipse(center, 4.5f, 4.5f), br, 1.3f);
        dc.DrawLine(new Vector2(cx + 9.5f, cy + 9.5f), new Vector2(cx + 13, cy + 13), br, 1.5f);
    }

    static readonly (byte r, byte g, byte b) GearAccent = (255, 90, 220);

    /// <summary>A solid, filled gear/sprocket (thick radial teeth + filled body), overlaid at the widget's
    /// top-right corner - a backing plate keeps it legible over whatever panel content happens to be underneath.
    /// Solid and accent-coloured on purpose (Bart: "make it bigger... colored/solid to make it more noticeable"),
    /// unlike every other glyph in the app, which are all thin outlines - this one is a primary action, not a
    /// passive status indicator, and needs to read as a button even glanced at quickly.</summary>
    void DrawSettingsGear(float W)
    {
        float size = Engine.GearSize;
        float x = W - Engine.GearMargin - size, y = Engine.GearMargin;
        float cx = x + size / 2, cy = y + size / 2, rOuter = size / 2, rBody = rOuter - 7f;
        dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x - 4, y - 4, x + size + 4, y + size + 4), 8, 8), Br(20, 10, 35, 0.75f));
        var br = Br(GearAccent);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            var inner = new Vector2(cx + (float)(Math.Cos(a) * (rBody - 1)), cy + (float)(Math.Sin(a) * (rBody - 1)));
            var outer = new Vector2(cx + (float)(Math.Cos(a) * rOuter), cy + (float)(Math.Sin(a) * rOuter));
            dc.DrawLine(inner, outer, br, 4f, roundStroke);
        }
        dc.FillEllipse(new Ellipse(new Vector2(cx, cy), rBody, rBody), br);
    }

    // ponytail: a real per-extension OS icon (SHGetFileInfo -> Icon.FromHandle -> GDI+ -> premultiply -> ID2D1Bitmap
    // with an initial-data pointer) reliably produced a fatal, uncatchable native crash here, confirmed by
    // marker-instrumented bisection of every step, including after fixing a real double-free of the icon handle
    // found along the way. CreateBitmap-with-initial-data is a code path nothing else in this renderer uses -
    // every other bitmap here is created empty (CreateBitmap(size, IntPtr.Zero, ...)) and drawn into, never
    // constructed from a raw pointer. A drawn vector badge is strictly safer (no GDI+/icon interop at all) and
    // matches the pin/open/delete glyphs already drawn this way. Upgrade path, if ever revisited: render the icon
    // to a WIC bitmap and hand Direct2D an IWICBitmapSource instead of a raw byte pointer.
    void DrawTypeBadge(float x, float y, ClipItem item, ID2D1Brush br)
    {
        float s = Engine.ClipIcon;
        switch (item.Kind)
        {
            case ClipKind.Text: case ClipKind.RichText:
                dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + 2, y + 1, x + s - 2, y + s - 1), 1.5f, 1.5f), br, 1f);
                for (int i = 0; i < 3; i++) { float ly = y + 4 + i * 3.5f; dc.DrawLine(new Vector2(x + 4, ly), new Vector2(x + s - 4 - (i == 2 ? 3 : 0), ly), br, 1f); }
                break;
            case ClipKind.Image:
                dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + 1, y + 2, x + s - 1, y + s - 2), 1.5f, 1.5f), br, 1f);
                dc.FillEllipse(new Ellipse(new Vector2(x + 5.5f, y + 6), 1.6f, 1.6f), br);
                dc.DrawLine(new Vector2(x + 3, y + s - 4), new Vector2(x + 7, y + 7.5f), br, 1f);
                dc.DrawLine(new Vector2(x + 7, y + 7.5f), new Vector2(x + s - 3, y + s - 4), br, 1f);
                break;
            default:   // Files: a small folder
                dc.DrawLine(new Vector2(x + 2, y + 5), new Vector2(x + 6, y + 5), br, 1f);
                dc.DrawLine(new Vector2(x + 6, y + 5), new Vector2(x + 8, y + 3.5f), br, 1f);
                dc.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x + 2, y + 5, x + s - 2, y + s - 2), 1, 1), br, 1f);
                break;
        }
    }

    // ------------------------------------------------------------ readback
    /// <summary>Copies the rendered frame (premultiplied BGRA, PxW*4 bytes per row) to dst.</summary>
    public unsafe void CopyTo(IntPtr dst)
    {
        staging.CopyFromBitmap(target);
        var map = staging.Map(MapOptions.Read);
        try { for (int y = 0; y < PxH; y++) Buffer.MemoryCopy((byte*)map.Bits + (long)y * map.Pitch, (byte*)dst + (long)y * PxW * 4, PxW * 4, PxW * 4); }
        finally { staging.Unmap(); }
    }

    public void Dispose()
    {
        foreach (var b in brushes.Values) b.Dispose();
        foreach (var l in clipTextCache.Values) l.Dispose();
        foreach (var b in clipImageCache.Values) b?.Dispose();
        foreach (var fr in clipFilesCache.Values) foreach (var (layout, _) in fr.Shown) layout.Dispose();
        target?.Dispose(); glowSrc?.Dispose(); staging?.Dispose(); blur.Dispose();
        fTitle.Dispose(); fBody.Dispose(); fBodyR.Dispose(); fSmall.Dispose(); fSmallR.Dispose(); fTag.Dispose(); fWrap.Dispose(); fCenter.Dispose(); roundStroke.Dispose(); dashStroke.Dispose();
        dw.Dispose(); dc.Dispose(); dev.Dispose(); factory.Dispose(); d3d.Dispose();
    }
}
