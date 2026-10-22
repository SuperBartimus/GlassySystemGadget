using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Glassy.Core;

namespace Glassy.App;

static class Program
{
    static bool ForceWarp;   // --warp: test the software-rendering fallback
    static string Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    [STAThread]
    static int Main(string[] args)
    {
        ForceWarp = args.Contains("--warp");
        string cfgPath = Arg(args, "--config") ?? ConfigStore.DefaultPath;
        string shot = Arg(args, "--shot");
        if (args.Contains("--selftest")) return SelfTest(cfgPath, Arg(args, "--page"), shot);
        if (Arg(args, "--soak") is string soak) return Soak(cfgPath, int.Parse(soak));
        if (shot != null) return Shot(cfgPath, shot, args.Contains("--demo"), float.TryParse(Arg(args, "--scale"), out var sc) ? sc : 1f);

        using var mutex = new Mutex(true, "Local\\GlassySystemGadget.Instance", out bool first);
        using var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\GlassySystemGadget.Settings");
        if (!first) { Win32.AllowSetForegroundWindow(-1); openSettings.Set(); return 0; }   // -1 = ASFW_ANY: a second launch hands its foreground right to the running widget, which opens Settings

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);   // an always-on widget must not pop a modal error box
        Application.ThreadException += (_, e) => AppLog.Write("unhandled UI exception: " + e.Exception);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var win = new MainWindow(ConfigStore.Load(cfgPath), cfgPath);
        var reg = ThreadPool.RegisterWaitForSingleObject(openSettings, (_, _) => { try { win.BeginInvoke(win.OpenSettings); } catch (InvalidOperationException) { } }, null, -1, false);
        Application.Run(win);
        reg.Unregister(null);
        return 0;
    }

    /// <summary>Headless check: builds every Settings page against live data (no window is shown) and reports any exception.</summary>
    static int SelfTest(string cfgPath, string page = null, string png = null)
    {
        var cfg = ConfigStore.Load(cfgPath);
        using var engine = new Engine(cfg, configPath: cfgPath);
        for (int i = 0; i < 4; i++) { engine.Tick(); Thread.Sleep(cfg.Global.TickMs); }
        using var form = new SettingsForm(cfg, engine, () => { });
        int pages = form.BuildAllPages();
        Console.WriteLine($"settings pages built: {pages}; sections: {engine.Panels.Count}; drives: {engine.CurrentVolumes.Count}; adapters: {engine.CurrentAdapters.Count}");
        if (page != null && png != null) { form.SavePage(page, png); Console.WriteLine($"page {page} -> {png}"); }   // --selftest --page Updates --shot x.png
        return 0;
    }

    /// <summary>Headless leak check: runs the sampling + drawing loop without a window and prints managed vs process memory.</summary>
    static int Soak(string cfgPath, int seconds)
    {
        var cfg = ConfigStore.Load(cfgPath);
        using var engine = new Engine(cfg, configPath: cfgPath); using var r = new D2DRenderer(ForceWarp);
        r.Resize(cfg.Global.Width, engine.TotalHeight, 1f);
        IntPtr buf = Marshal.AllocHGlobal(r.PxW * r.PxH * 4);
        var me = System.Diagnostics.Process.GetCurrentProcess(); var sw = System.Diagnostics.Stopwatch.StartNew(); long next = 0, trimAt = 30000;
        Console.WriteLine("t_s  managed_MB  gen0 gen2  private_MB  ws_MB");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            engine.Tick(); r.Render(engine, cfg.Global); r.CopyTo(buf);
            if (sw.ElapsedMilliseconds >= next)
            {
                me.Refresh(); next += 10000;
                Console.WriteLine($"{sw.Elapsed.TotalSeconds,3:0}  {GC.GetTotalMemory(false) / 1048576.0,9:0.0}  {GC.CollectionCount(0),4} {GC.CollectionCount(2),4}  {me.PrivateMemorySize64 / 1048576.0,9:0.0}  {me.WorkingSet64 / 1048576.0,6:0.0}");
            }
            if (sw.ElapsedMilliseconds > trimAt) { MainWindow.TrimMemory(); trimAt += 30000; }
            Thread.Sleep(cfg.Global.TickMs);
        }
        me.Refresh(); Console.WriteLine($"renderer={r.DeviceKind}; CPU {me.TotalProcessorTime.TotalMilliseconds / sw.ElapsedMilliseconds / Environment.ProcessorCount * 100:0.00}% of whole system, {me.TotalProcessorTime.TotalMilliseconds / sw.ElapsedMilliseconds * 100:0.0}% of one thread");
        Console.WriteLine($"after forced GC: managed {GC.GetTotalMemory(true) / 1048576.0:0.0} MB, private {me.PrivateMemorySize64 / 1048576.0:0.0} MB");
        Marshal.FreeHGlobal(buf); return 0;
    }

    /// <summary>Only used by --demo --shot, so a screenshot of the Clipboard panel has something to show. Never touches the real OS clipboard.</summary>
    static void DemoFillClipboard(Engine engine)
    {
        var store = engine.Clipboard;
        string longText = string.Join(" ", Enumerable.Range(1, 200).Select(i => "word" + i)) + " - a long copied paragraph that should wrap across several lines and, if it runs past the line cap, end in an ellipsis rather than just being cut off silently without any sign that more text follows.";
        store.Add(ClipboardClassifier.Classify(new System.Windows.Forms.DataObject(System.Windows.Forms.DataFormats.UnicodeText, longText), 1_000_000), 200);
        var html = new System.Windows.Forms.DataObject();
        html.SetData(System.Windows.Forms.DataFormats.Html, "<b>Glassy System Gadget</b> - build log");
        html.SetData(System.Windows.Forms.DataFormats.UnicodeText, "Glassy System Gadget - build log");
        store.Add(ClipboardClassifier.Classify(html, 1_000_000), 200);
        var files = Enumerable.Range(1, 3).Select(i => $@"C:\Users\Example\Documents\report-{i}.docx")
            .Append(@"\\HomeNAS\Downloads\Quarterly Reports\2026\Q3\draft-with-a-genuinely-long-filename-to-check-word-wrap.xlsx")
            .Append(@"C:\Users\Example\Pictures\photo.jpg").ToArray();
        store.Add(ClipboardClassifier.Classify(new System.Windows.Forms.DataObject(System.Windows.Forms.DataFormats.FileDrop, files), 1_000_000), 200);
        using (var img = new Bitmap(220, 140))
        {
            using (var g = Graphics.FromImage(img))
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, img.Width, img.Height), Color.FromArgb(255, 90, 60, 200), Color.FromArgb(255, 60, 200, 190), 40f))
            { g.FillRectangle(br, 0, 0, img.Width, img.Height); g.FillEllipse(Brushes.White, 60, 40, 60, 60); }
            var imgData = new System.Windows.Forms.DataObject(); imgData.SetImage(img);
            store.Add(ClipboardClassifier.Classify(imgData, 1_000_000), 200);
        }
        if (store.Items.Count > 0) store.TogglePin(store.Items.Last().Id);
    }

    /// <summary>Only used by --demo --shot: a fixed fake forecast (no network call) so the Weather panel - disabled
    /// by default, since it's the app's only feature needing an outbound call - has something to show headlessly.</summary>
    /// <summary>70 days of made-up hourly token counts (fixed seed): Claude most weekdays in office hours, Codex in
    /// evening bursts, a few idle days - enough for the heatmap and Stats to show every colour and step.</summary>
    static Dictionary<string, Dictionary<DateTime, long>> DemoAiHours()
    {
        var rng = new Random(7); var codex = new Dictionary<DateTime, long>(); var claude = new Dictionary<DateTime, long>();
        for (int d = 0; d < 70; d++)
        {
            var day = DateTime.Today.AddDays(-d);
            if (rng.NextDouble() < 0.18) continue;
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            for (int h = 9; h < 17; h++) if (!weekend && rng.NextDouble() < 0.6) claude[day.AddHours(h)] = rng.Next(20_000, 400_000);
            for (int h = 19; h < 23; h++) if (rng.NextDouble() < 0.35) codex[day.AddHours(h)] = rng.Next(50_000, 900_000);
        }
        return new() { ["codex"] = codex, ["claude"] = claude };
    }

    static IWeatherProvider DemoWeather()
    {
        var snap = new WeatherSnapshot
        {
            LocationName = "Austin, TX", TempC = 23, FeelsLikeC = 25, HumidityPct = 54, WindKmh = 14, WindGustKmh = 22,
            PrecipChance = 20, Condition = WeatherCondition.PartlyCloudy, FetchedAtUtc = DateTime.UtcNow,
        };
        var now = DateTime.Now;
        var hourlyConds = new[] { WeatherCondition.PartlyCloudy, WeatherCondition.Clear, WeatherCondition.Rain, WeatherCondition.Thunderstorm };
        for (int i = 0; i < 4; i++) snap.Hourly.Add(new WeatherPoint { Time = now.AddHours(2 * i), TempC = 23 - i, TempLowC = double.NaN, PrecipChance = i * 20, Condition = hourlyConds[i] });
        var dailyConds = new[] { WeatherCondition.Overcast, WeatherCondition.Snow, WeatherCondition.Clear };
        for (int i = 0; i < 3; i++) snap.Daily.Add(new WeatherPoint { Time = now.AddDays(i + 1), TempC = 20 - i, TempLowC = 10 - i, PrecipChance = i * 15, Condition = dailyConds[i] });
        snap.Alerts.Add(new WeatherAlert { Event = "Severe Thunderstorm Warning", Severity = "Severe", Headline = "demo alert" });
        return new FakeWeatherProvider { Snapshot = snap };
    }

    /// <summary>Headless check: render one frame to a PNG (composited over a gradient so the glass is visible) and exit.</summary>
    static int Shot(string cfgPath, string png, bool demo, float scale)
    {
        var cfg = ConfigStore.Load(cfgPath);
        // This dev machine is a desktop with no real battery; --demo fakes one present so the panel (and its
        // hardware-detection gate) can still be checked headlessly.
        var demoBattery = demo ? new FakeBatteryProvider { Value = new BatteryStatus { Present = true, Charging = false, OnAc = false, Percent = 55, TimeRemaining = TimeSpan.FromHours(1.6) } } : null;
        var demoWeather = demo ? DemoWeather() : null;
        if (demo && cfg.Panels.FirstOrDefault(x => x.Kind == PanelKind.Weather) is { } wp) { wp.Enabled = true; wp.WeatherResolvedName = "Austin, TX"; }
        // Same for IIS: the dev box has no IIS role, so --demo fakes a small web server.
        var demoIis = demo ? new FakeIisProvider { Value = new IisStatus
        {
            Running = true, TotalConnections = 14, TotalRequestsPerSec = 23.5,
            Sites = { new IisSite("Default Web Site", 3, 4.5), new IisSite("intranet", 9, 17), new IisSite("api", 2, 2) },
            Pools = { new IisPool("DefaultAppPool", 3), new IisPool("intranet", 3), new IisPool("legacy", 5) },
        } } : null;
        var demoHyperV = demo ? new FakeHyperVProvider { Value = new HyperVStatus
        {
            Available = true, FullList = true, HostCpuPct = 34,
            Vms =
            {
                new HyperVVm("dc01", 2, 6, 2048, TimeSpan.FromDays(41.3)), new HyperVVm("sql01", 2, 47, 16384, TimeSpan.FromDays(12.2)),
                new HyperVVm("web01", 2, 18, 4096, TimeSpan.FromHours(30)), new HyperVVm("lab-win11", 6, double.NaN, double.NaN, null),
                new HyperVVm("old-backup", 3, double.NaN, double.NaN, null),
            },
        } } : null;
        const double GB = 1024.0 * 1024 * 1024;
        var demoShares = demo ? new FakeFileShareProvider { Value = new FileShareStatus
        {
            Running = true, Sessions = 7, FilesOpen = 23, ReadBps = 41.5e6, WriteBps = 6.2e6,
            Shares =
            {
                new SmbShare("Media", @"D:\Media", 4, 38e6, 0, 610 * GB, 3700 * GB), new SmbShare("Projects", @"E:\Projects", 17, 3.4e6, 6.1e6, 120 * GB, 950 * GB),
                new SmbShare("Scans", @"E:\Scans", 2, 0.1e6, 0.1e6, 120 * GB, 950 * GB), new SmbShare("Users$", @"F:\Users", 0, 0, 0, 1800 * GB, 1900 * GB),
            },
        } } : null;
        var nowDemo = DateTimeOffset.Now;
        var demoAi = demo ? new FakeAiUsageProvider { Value = new AiUsageStatus
        {
            Providers =
            {
                new AiProviderStatus { Id = "codex", Name = "Codex", Plan = "plus", ObservedAt = nowDemo.AddMinutes(-3), Windows =
                    { new AiWindow("5h", 33, nowDemo.AddHours(3.4)), new AiWindow("7d", 17, nowDemo.AddDays(3.6)) } },
                new AiProviderStatus { Id = "claude", Name = "Claude", ObservedAt = nowDemo.AddSeconds(-20), Windows =
                    { new AiWindow("5h", 57, nowDemo.AddHours(4.6)), new AiWindow("7d", 84, nowDemo.AddDays(1.9)) } },
            },
            Hours = DemoAiHours(),
        } } : null;
        // Fake drives, so a shot never shows this machine's own drive labels.
        const long Gib = 1L << 30;
        var demoVolumes = demo ? new DemoVolumes { Snapshot = new[]
        {
            new VolumeRow { Letter = "C", Label = "System", Kind = VolKind.Fixed, Total = 1000 * Gib, Free = 412 * Gib },
            new VolumeRow { Letter = "D", Label = "Data", Kind = VolKind.Fixed, Total = 4000 * Gib, Free = 1150 * Gib },
            new VolumeRow { Letter = "E", Label = "Games", Kind = VolKind.Fixed, Total = 2000 * Gib, Free = 140 * Gib },
            new VolumeRow { Letter = "M", Label = "Media", Kind = VolKind.Network, Total = 8000 * Gib, Free = 4400 * Gib },
            new VolumeRow { Letter = "U", Label = "USB stick", Kind = VolKind.Removable, Total = 64 * Gib, Free = 51 * Gib },
        } } : null;
        using var engine = new Engine(cfg, demoVolumes, configPath: cfgPath, battery: demoBattery, weather: demoWeather, iis: demoIis, hyperV: demoHyperV, shares: demoShares, ai: demoAi, demoDriveGraphs: demo); using var r = new D2DRenderer(ForceWarp);
        for (int i = 0; i < 6; i++) { engine.Tick(); Thread.Sleep(cfg.Global.TickMs); }
        if (demo) { engine.DemoFill(); DemoFillClipboard(engine); r.ShowSettingsGear = true; }   // showcase the hover-only Settings gear in demo shots
        r.Resize(cfg.Global.Width, engine.TotalHeight, scale); r.Render(engine, cfg.Global);
        IntPtr buf = Marshal.AllocHGlobal(r.PxW * r.PxH * 4);
        try
        {
            r.CopyTo(buf);
            using var back = new Bitmap(r.PxW + 40, r.PxH + 40, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(back))
            {
                using var br = new LinearGradientBrush(new Rectangle(0, 0, back.Width, back.Height), Color.FromArgb(60, 90, 140), Color.FromArgb(150, 120, 80), 45f);
                g.FillRectangle(br, 0, 0, back.Width, back.Height);
                using var fg = new Bitmap(r.PxW, r.PxH, r.PxW * 4, PixelFormat.Format32bppPArgb, buf);
                g.DrawImage(fg, 20, 20);
            }
            back.Save(png, ImageFormat.Png);
        }
        finally { Marshal.FreeHGlobal(buf); }
        Console.Error.WriteLine($"shot {r.PxW}x{r.PxH}px -> {png}");
        // The AI Stats flyout too, beside it (never shown on screen): its owner-drawn page is checked the same way.
        if (engine.Panels.FirstOrDefault(x => x.Cfg.Kind == PanelKind.AiUsage) is { } ai)
        {
            using var form = new AiStatsForm(() => ai);
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            string statsPng = Path.ChangeExtension(png, null) + "-stats.png";
            bmp.Save(statsPng, ImageFormat.Png);
            Console.Error.WriteLine($"stats -> {statsPng}");
        }
        return 0;
    }
}

sealed class DemoVolumes : IVolumeProvider
{
    public VolumeRow[] Snapshot { get; set; } = Array.Empty<VolumeRow>();
    public void Dispose() { }
}
