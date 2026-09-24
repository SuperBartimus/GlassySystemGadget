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
        if (args.Contains("--selftest")) return SelfTest(cfgPath);
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
    static int SelfTest(string cfgPath)
    {
        var cfg = ConfigStore.Load(cfgPath);
        using var engine = new Engine(cfg, configPath: cfgPath);
        for (int i = 0; i < 4; i++) { engine.Tick(); Thread.Sleep(cfg.Global.TickMs); }
        using var form = new SettingsForm(cfg, engine, () => { });
        int pages = form.BuildAllPages();
        Console.WriteLine($"settings pages built: {pages}; sections: {engine.Panels.Count}; drives: {engine.CurrentVolumes.Count}; adapters: {engine.CurrentAdapters.Count}");
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

    /// <summary>Headless check: render one frame to a PNG (composited over a gradient so the glass is visible) and exit.</summary>
    static int Shot(string cfgPath, string png, bool demo, float scale)
    {
        var cfg = ConfigStore.Load(cfgPath);
        // This dev machine is a desktop with no real battery; --demo fakes one present so the panel (and its
        // hardware-detection gate) can still be checked headlessly.
        var demoBattery = demo ? new FakeBatteryProvider { Value = new BatteryStatus { Present = true, Charging = false, OnAc = false, Percent = 55, TimeRemaining = TimeSpan.FromHours(1.6) } } : null;
        using var engine = new Engine(cfg, configPath: cfgPath, battery: demoBattery); using var r = new D2DRenderer(ForceWarp);
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
        return 0;
    }
}
