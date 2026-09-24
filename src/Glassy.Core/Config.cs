using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glassy.Core;

/// <summary>Bottom = behind every other window, which is the "sits on the desktop" behaviour.
/// Reparenting under the shell was tried (three placements) and is not composited on Windows 11 build 26200.</summary>
public enum WindowMode { Normal, TopMost, Bottom }
public enum DockEdge { None, Left, Right }
public enum ProcPriority { Idle, BelowNormal }
public enum HistoryMode { Average, Peak }
public enum ScaleMode { Auto, Fixed }
/// <summary>DiskIo and DiskSpace are legacy: they exist only so old config files still load, and are merged into Drives on load.</summary>
public enum PanelKind { Cpu, Ram, Gpu, Network, DiskIo, DiskSpace, TopProcesses, Uptime, Drives, Clipboard, Battery }

public sealed class LineConfig
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#FF3CE6";
    public float Width { get; set; } = 1.5f;
    public bool Visible { get; set; } = true;
    public bool Glow { get; set; }
    public bool Fill { get; set; }
}

public sealed class PanelConfig
{
    public PanelKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public int Height { get; set; } = 90;
    /// <summary>Time shown by the right-hand (live) part of the graph.</summary>
    public double LiveSeconds { get; set; } = 30;
    /// <summary>Time shown by the left-hand (history) part of the graph.</summary>
    public double HistorySeconds { get; set; } = 6 * 3600;
    public int SplitPercent { get; set; } = 80;
    public HistoryMode History { get; set; } = HistoryMode.Average;
    public ScaleMode Scale { get; set; } = ScaleMode.Fixed;
    public double FixedMax { get; set; } = 100;
    public List<LineConfig> Lines { get; set; } = new();
    // Network
    public bool SeparateAdapters { get; set; }                 // false = one combined graph of all physical adapters
    public List<string> Adapters { get; set; } = new();        // when SeparateAdapters: one graph per adapter listed here
    // Drives (one section per mounted drive: activity graph + space bar)
    public List<string> HiddenDrives { get; set; } = new();    // drive letters the user turned off
    public bool ShowActivity { get; set; } = true;
    public bool ShowSpaceBar { get; set; } = true;
    // Top processes
    public int Count { get; set; } = 5;
    public bool SortByMemory { get; set; }
    /// <summary>Percent (of total CPU, or of physical RAM when sorting by memory) at which a row's bar is full and red.</summary>
    public double BarFullScale { get; set; } = 25;
    // Clipboard
    public int ClipMaxItems { get; set; } = 200;
    public long ClipMaxImageBytes { get; set; } = 5 * 1024 * 1024;
    public bool ClipPlaySound { get; set; } = true;
    /// <summary>"" plays a system sound; otherwise the path to a .wav file.</summary>
    public string ClipSoundFile { get; set; } = "";
    public bool ClipRestoreOnStartup { get; set; }
    public bool ClipSearchExpanded { get; set; }
    /// <summary>Max lines (or the image-thumbnail height equivalent) a single row grows to before it stops - a short clip takes less space.</summary>
    public int ClipMaxPreviewLines { get; set; } = 11;
    /// <summary>0 hides it entirely.</summary>
    public int ClipScrollBarWidth { get; set; } = 5;
    /// <summary>When on (default), a source marking its content "CanIncludeInClipboardHistory=0" is skipped, same
    /// as the more deliberate ExcludeClipboardContentFromMonitorProcessing flag - most password managers set one
    /// of the two. rdpclip.exe (RDP's clipboard bridge) sets this same flag to 0 on everything it bridges, so
    /// turning this off is what makes clipboard sync work over an RDP session, at the cost of that safety net for
    /// any app that only sets this flag (not the other one) to mark content private.</summary>
    public bool ClipHonorHistoryFlag { get; set; } = true;
    // Battery
    public bool BatteryIconLeft { get; set; } = true;
}

public sealed class GlobalConfig
{
    public WindowMode Mode { get; set; } = WindowMode.Normal;   // visible on first launch; Bottom is one Settings choice away
    public DockEdge Dock { get; set; } = DockEdge.None;
    public int Monitor { get; set; }
    public int X { get; set; } = 40;
    public int Y { get; set; } = 40;
    public bool LockPosition { get; set; }
    public int Width { get; set; } = 340;
    /// <summary>Glass panel opacity, 0.2 - 1.0.</summary>
    public double Opacity { get; set; } = 0.85;
    public bool ClickThrough { get; set; }
    public int TickMs { get; set; } = 500;
    public ProcPriority Priority { get; set; } = ProcPriority.BelowNormal;
    public bool EcoQos { get; set; } = true;
    public bool TrimMemory { get; set; } = true;
    public bool StartWithWindows { get; set; }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public GlobalConfig Global { get; set; } = new();
    /// <summary>Top-to-bottom order of the list is the on-screen order.</summary>
    public List<PanelConfig> Panels { get; set; } = new();

    /// <summary>Height is the activity graph height of each drive section; the section adds its header and space bar around it.</summary>
    public static PanelConfig DrivesPanel(bool enabled) =>
        new() { Kind = PanelKind.Drives, Enabled = enabled, Height = 38, Scale = ScaleMode.Auto, History = HistoryMode.Peak };

    /// <summary>Height is the total scrollable pane height (title bar and search row included).</summary>
    public static PanelConfig ClipboardPanel(bool enabled) => new() { Kind = PanelKind.Clipboard, Enabled = enabled, Height = 220 };

    /// <summary>Always enabled - visibility is decided purely by whether Engine detects a battery, not a user
    /// preference (there is deliberately no "Show this panel" checkbox for it in Settings).</summary>
    public static PanelConfig BatteryPanel() => new() { Kind = PanelKind.Battery, Enabled = true, Height = 90, Scale = ScaleMode.Fixed, FixedMax = 100 };

    public static AppConfig CreateDefault()
    {
        PanelConfig P(PanelKind k, int h, ScaleMode s, double max = 100, HistoryMode hm = HistoryMode.Average) =>
            new PanelConfig { Kind = k, Height = h, Scale = s, FixedMax = max, History = hm };
        return new AppConfig
        {
            Panels =
            {
                P(PanelKind.Cpu, 130, ScaleMode.Fixed),
                P(PanelKind.Ram, 92, ScaleMode.Fixed),
                P(PanelKind.Gpu, 112, ScaleMode.Fixed),
                P(PanelKind.Network, 102, ScaleMode.Auto, 0, HistoryMode.Peak),
                DrivesPanel(true),
                P(PanelKind.TopProcesses, 0, ScaleMode.Fixed),
                P(PanelKind.Uptime, 0, ScaleMode.Fixed),
                ClipboardPanel(true),
                BatteryPanel(),
            }
        };
    }
}

public static class ConfigStore
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassySystemGadget", "config.json");

    /// <summary>Loads the config; a missing file gives defaults, a corrupt file is kept as .bad and replaced by defaults.</summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return AppConfig.CreateDefault();
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllBytes(path), Opts);
            if (cfg?.Global == null || cfg.Panels == null || cfg.Panels.Count == 0) throw new JsonException("empty config");
            Migrate(cfg);
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is NotSupportedException)
        {
            try { File.Move(path, path + ".bad", true); } catch (IOException) { /* best effort */ }
            return AppConfig.CreateDefault();
        }
    }

    /// <summary>Brings an older saved config up to the current panel set. Each step is independent - one not applying never skips another.</summary>
    public static void Migrate(AppConfig cfg)
    {
        // Old configs had separate Disk activity and Disk space panels; they are now one Drives panel at the position of the first.
        bool Old(PanelConfig p) => p.Kind == PanelKind.DiskIo || p.Kind == PanelKind.DiskSpace;
        int first = cfg.Panels.FindIndex(Old);
        if (first >= 0)
        {
            bool enabled = cfg.Panels.Where(Old).Any(p => p.Enabled);
            cfg.Panels.RemoveAll(Old);
            if (!cfg.Panels.Any(p => p.Kind == PanelKind.Drives)) cfg.Panels.Insert(Math.Min(first, cfg.Panels.Count), AppConfig.DrivesPanel(enabled));
        }
        // A config saved before the Clipboard panel existed: add it at the end, enabled, rather than silently omitting a feature the user asked for.
        if (!cfg.Panels.Any(p => p.Kind == PanelKind.Clipboard)) cfg.Panels.Add(AppConfig.ClipboardPanel(true));
        // Same for Battery - it has no "enabled" preference, only hardware detection, but still needs to exist in
        // the list (for its Settings page and panel order) even for a config saved before it existed.
        if (!cfg.Panels.Any(p => p.Kind == PanelKind.Battery)) cfg.Panels.Add(AppConfig.BatteryPanel());
    }

    /// <summary>Atomic save (temp file + replace); UTF-8 without BOM.</summary>
    public static void Save(AppConfig cfg, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(cfg, Opts));
        File.Move(tmp, path, true);
    }
}
