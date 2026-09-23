using System.Runtime.InteropServices;

namespace Glassy.Core;

/// <summary>Everything the renderer needs for one drawn section (a whole panel, one drive, or one network adapter).</summary>
public sealed class PanelRuntime
{
    public PanelConfig Cfg = null!;
    public string Sub = "";                           // drive letter or adapter name; "" for ordinary panels
    public string Title = "";                         // "" = renderer default for the kind
    public IPanelSource Source;                       // null for list/text panels and drives without an activity graph
    public SeriesBuffer[] Series = Array.Empty<SeriesBuffer>();
    public float[] Latest = Array.Empty<float>();
    public double ScaleMax = 100;
    public TimeLayout Layout;
    public float Top, Height, PlotTopOffset, PlotHeight;   // DIPs, set by Engine
    public bool LegendRow;                            // legend on its own row under the title
    public List<ProcRow> Procs = new();
    public VolumeRow Volume;                          // drive sections
    public string Text = "";
    public string Readout = "";
    internal string TimingKey = "";
    public bool IsSeries => Source != null;
}

/// <summary>Owns the data sources and per-section buffers; the UI calls Apply on config changes and Tick on a timer.</summary>
public sealed class Engine : IDisposable
{
    public const float PanelGap = 6, SectionGap = 3, Margin = 4, RowProc = 15, Header = 26, LegendRowH = 12, DriveHeader = 22, DriveBar = 16;
    public const float ClipHeader = 26, ClipIcon = 16, ClipIconTopPad = 3, ClipMinHeight = 90;
    /// <summary>Row layout, top to bottom: the icon strip (badge + type/age text + open/pin/delete, ClipIconTopPad
    /// to ClipBodyTop), then the wrapped-text/file-list/image body (ClipLineH per line, or an image's scaled
    /// height), then ClipBodyBottomPad. Rows are variable height - a short clip takes less space, a long one grows
    /// to the panel's line cap - but every row uses these same numbers, so drawing and hit-testing agree exactly.</summary>
    public const float ClipLineH = 13.5f, ClipBodyTop = 22, ClipBodyBottomPad = 4;
    /// <summary>The small per-file type icon drawn in front of each file path in a Files row's body.</summary>
    public const float ClipFileIcon = 12;
    public AppConfig Config { get; private set; } = null!;
    public List<PanelRuntime> Panels { get; } = new();
    public float TotalHeight { get; private set; }
    /// <summary>Increments whenever the set or size of sections changes (config edit, drive or adapter added/removed).</summary>
    public int LayoutVersion { get; private set; }
    public long TotalPhysBytes { get; private set; }

    readonly Dictionary<PanelKind, IPanelSource> sources = new();
    readonly Dictionary<(PanelKind, string), PanelRuntime> runtimes = new();
    IVolumeProvider volumes; readonly bool ownsVolumes;
    INetProvider net; readonly bool ownsNet;
    IBatteryProvider battery; readonly bool ownsBattery;
    DriveActivityHub activity;
    ProcessSampler procs;
    readonly ClipboardStore clipboard;
    string layoutSig = "";
    long lastProc, lastSlow;

    /// <summary>The Clipboard folder that belongs next to a given config.json - so a test or a --config override
    /// gets its own isolated clipboard history instead of silently sharing the real one in %AppData%.</summary>
    public static string ClipboardDirFor(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "Clipboard");
    public static string DefaultClipboardDir => ClipboardDirFor(ConfigStore.DefaultPath);

    public Engine(AppConfig cfg, IVolumeProvider volumes = null, INetProvider net = null, ClipboardStore clipboard = null, string configPath = null, IBatteryProvider battery = null)
    {
        this.volumes = volumes; ownsVolumes = volumes == null;
        this.net = net; ownsNet = net == null;
        this.battery = battery; ownsBattery = battery == null;
        this.clipboard = clipboard ?? new ClipboardStore(ClipboardDirFor(configPath ?? ConfigStore.DefaultPath));
        Apply(cfg);
    }

    IVolumeProvider Volumes => volumes ??= new VolumeWatcher();
    INetProvider Net => net ??= new NetHub();
    IBatteryProvider Battery => battery ??= new SystemBatteryProvider();
    DriveActivityHub Activity => activity ??= new DriveActivityHub();
    /// <summary>The clipboard history store; App reads/mutates it directly (add on capture, delete/pin/search from the UI).</summary>
    public ClipboardStore Clipboard => clipboard;

    IPanelSource SourceFor(PanelKind k)
    {
        if (sources.TryGetValue(k, out var s)) return s;
        s = k switch { PanelKind.Cpu => new CpuSource(), PanelKind.Ram => new RamSource(), PanelKind.Gpu => new GpuSource(), PanelKind.Battery => new BatterySource(() => Battery.Snapshot), _ => null };
        if (s != null) sources[k] = s;
        return s;
    }

    /// <summary>Adds missing lines (first run, CPU thread count changed) and drops lines a source no longer produces; keeps user styling.</summary>
    public static void ReconcileLines(PanelConfig p, IPanelSource src)
    {
        var defaults = src.DefaultLines();
        var byKey = p.Lines.ToDictionary(l => l.Key);
        p.Lines = defaults.Select(d => byKey.TryGetValue(d.Key, out var have) ? have : d).ToList();
    }

    static bool HasLegendRow(PanelKind k) => k == PanelKind.Ram || k == PanelKind.Gpu || k == PanelKind.Network;

    IEnumerable<VolumeRow> VisibleVolumes(PanelConfig pc) =>
        Volumes.Snapshot.Where(v => !pc.HiddenDrives.Contains(v.Letter, StringComparer.OrdinalIgnoreCase));

    List<string> SelectedAdapters(PanelConfig pc) =>
        pc.SeparateAdapters ? pc.Adapters.Where(a => Net.Adapters.Contains(a)).Distinct().ToList() : null;

    /// <summary>Battery presence, not a user preference: the panel is enabled in config but only ever shown when
    /// hardware actually reports one (this dev machine, a desktop, never will).</summary>
    bool BatteryPresent => Battery.Snapshot?.Present == true;

    /// <summary>Everything dynamic that affects layout: which drives (and their tags), which adapters, and whether a battery are present.</summary>
    string Signature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var pc in Config.Panels.Where(p => p.Enabled))
        {
            if (pc.Kind == PanelKind.Drives) sb.Append("D:").AppendJoin(';', VisibleVolumes(pc).Select(v => v.Key + (Activity.Has(v.Letter) ? "+" : "-"))).Append('|');
            else if (pc.Kind == PanelKind.Network && pc.SeparateAdapters) sb.Append("N:").AppendJoin(';', SelectedAdapters(pc)).Append('|');
            else if (pc.Kind == PanelKind.Battery) sb.Append("B:").Append(BatteryPresent).Append('|');
        }
        return sb.ToString();
    }

    public void Apply(AppConfig cfg) { Config = cfg; Rebuild(); }

    void Rebuild()
    {
        Panels.Clear();
        var mem = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (Native.GlobalMemoryStatusEx(ref mem)) TotalPhysBytes = (long)mem.TotalPhys;
        int plotCells = (int)(Config.Global.Width - 2 * Margin - 16);
        var live = new HashSet<(PanelKind, string)>(); var adapters = new List<string>();
        float y = Margin;

        var drivesCfg = Config.Panels.FirstOrDefault(p => p.Enabled && p.Kind == PanelKind.Drives);
        if (drivesCfg != null) Activity.Sync(drivesCfg.ShowActivity ? VisibleVolumes(drivesCfg).Where(v => v.HasActivity).Select(v => v.Letter).ToList() : new List<string>());
        else if (activity != null) activity.Sync(Array.Empty<string>());

        bool batteryPresent = BatteryPresent;
        foreach (var pc in Config.Panels.Where(p => p.Enabled && (p.Kind != PanelKind.Battery || batteryPresent)).GroupBy(p => p.Kind).Select(g => g.First()))   // one panel per kind; a hand-edited duplicate is ignored
        {
            var subs = new List<(string sub, string title, VolumeRow vol)>();
            if (pc.Kind == PanelKind.Drives) subs.AddRange(VisibleVolumes(pc).Select(v => (v.Letter, v.Letter + ":", v)));
            else if (pc.Kind == PanelKind.Network && pc.SeparateAdapters) subs.AddRange(SelectedAdapters(pc).Select(a => (a, a, (VolumeRow)null)));
            else subs.Add(("", "", null));

            for (int i = 0; i < subs.Count; i++)
            {
                var (sub, title, vol) = subs[i];
                if (!runtimes.TryGetValue((pc.Kind, sub), out var rt)) runtimes[(pc.Kind, sub)] = rt = new PanelRuntime();
                rt.Cfg = pc; rt.Sub = sub; rt.Title = title; rt.Volume = vol;
                Configure(rt, plotCells);
                rt.Top = y; y += rt.Height + (i < subs.Count - 1 ? SectionGap : PanelGap);
                Panels.Add(rt); live.Add((pc.Kind, sub));
                if (pc.Kind == PanelKind.Network && sub != "") adapters.Add(sub);
            }
        }
        foreach (var dead in runtimes.Keys.Where(k => !live.Contains(k)).ToList()) runtimes.Remove(dead);   // a removed USB stick or adapter releases its history
        if (net != null || adapters.Count > 0) Net.Track(adapters);

        TotalHeight = Math.Max(20, y - PanelGap + Margin);
        layoutSig = Signature();
        LayoutVersion++;
        lastSlow = 0; lastProc = 0;   // refresh list panels immediately
    }

    void Configure(PanelRuntime rt, int plotCells)
    {
        var pc = rt.Cfg; var kind = pc.Kind;
        rt.LegendRow = HasLegendRow(kind);
        switch (kind)
        {
            case PanelKind.Cpu: case PanelKind.Ram: case PanelKind.Gpu: case PanelKind.Battery: rt.Source = SourceFor(kind); break;
            case PanelKind.Network:
                if (rt.Source is not NetView nv || nv.Adapter != (rt.Sub == "" ? null : rt.Sub)) rt.Source = new NetView(Net, rt.Sub == "" ? null : rt.Sub);
                break;
            case PanelKind.Drives:
                bool act = pc.ShowActivity && rt.Volume.HasActivity && Activity.Has(rt.Sub);   // no counter (e.g. CD drive) = no graph
                if (!act) rt.Source = null;
                else if (rt.Source is not DriveView dv || dv.Letter != rt.Sub) rt.Source = new DriveView(Activity, rt.Sub);
                break;
            default: rt.Source = null; break;
        }

        if (rt.Source != null)
        {
            ReconcileLines(pc, rt.Source); rt.Source.Configure(pc);
            var t = new TimeLayout(plotCells, pc.SplitPercent, pc.LiveSeconds, pc.HistorySeconds, Config.Global.TickMs);
            string key = $"{plotCells}|{Config.Global.TickMs}|{pc.LiveSeconds}|{pc.HistorySeconds}|{pc.SplitPercent}|{pc.History}|{pc.Lines.Count}";
            if (key != rt.TimingKey)
            {
                rt.TimingKey = key; rt.Layout = t;
                rt.Series = pc.Lines.Select(_ => new SeriesBuffer(t, pc.History)).ToArray();
                rt.Latest = Enumerable.Repeat(float.NaN, pc.Lines.Count).ToArray();
            }
        }

        if (kind == PanelKind.Drives)
        {
            bool act = rt.Source != null;
            rt.PlotTopOffset = DriveHeader; rt.PlotHeight = pc.Height;
            rt.Height = Math.Max(26, DriveHeader + (act ? pc.Height + 4 : 0) + (pc.ShowSpaceBar ? DriveBar : 0) + 4);
        }
        else if (rt.Source != null)
        {
            rt.PlotTopOffset = Header - 2 + (rt.LegendRow ? LegendRowH : 0);
            rt.Height = pc.Height; rt.PlotHeight = Math.Max(10, pc.Height - rt.PlotTopOffset - 8);
        }
        else if (kind == PanelKind.TopProcesses) rt.Height = Header + pc.Count * RowProc + 6;
        else if (kind == PanelKind.Clipboard) rt.Height = Math.Max(ClipMinHeight, pc.Height);
        else rt.Height = 28;
    }

    /// <summary>One sampling step. Cheap; list panels refresh at their own slower cadence.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        bool doProc = now - lastProc >= 2000, doSlow = now - lastSlow >= 30000;

        if (net != null) net.Update();   // may add/remove adapters, so before the layout check
        if (Signature() != layoutSig) Rebuild();
        else if (volumes != null)
            foreach (var p in Panels.Where(p => p.Volume != null)) p.Volume = volumes.Snapshot.FirstOrDefault(v => v.Letter == p.Sub) ?? p.Volume;   // free-space changes

        if (activity != null) activity.Update();
        foreach (var p in Panels)
        {
            if (p.IsSeries)
            {
                var vals = new float[p.Series.Length];
                p.Source.Sample(vals);
                for (int i = 0; i < vals.Length && i < p.Series.Length; i++) p.Series[i].Push(vals[i]);
                p.Latest = vals; p.Readout = p.Source.Readout(vals);
                p.ScaleMax = p.Cfg.Scale == ScaleMode.Fixed ? Math.Max(1, p.Cfg.FixedMax) : AutoScale(p);
            }
            else if (p.Cfg.Kind == PanelKind.TopProcesses && doProc)
                p.Procs = (procs ??= new ProcessSampler()).Sample(p.Cfg.Count, p.Cfg.SortByMemory);
            else if (p.Cfg.Kind == PanelKind.Uptime && (doSlow || p.Text == ""))
                p.Text = "Uptime  " + Fmt.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
        }
        if (doProc) lastProc = now; if (doSlow) lastSlow = now;
    }

    /// <summary>Fills every graph with synthetic waves. Only used by the headless --shot mode to check layout and drawing.</summary>
    public void DemoFill()
    {
        var rng = new Random(11);
        foreach (var p in Panels.Where(p => p.Cfg.Kind == PanelKind.Battery))
        {
            // Battery's two lines are mutually NaN-gated (see BatterySource), so it gets its own realistic
            // discharge curve here instead of the generic simultaneous-sine-wave fill below.
            int n = p.Layout.HistCells * p.Layout.TicksPerHist + p.Layout.LiveCells * p.Layout.TicksPerLive;
            for (int i = 0; i < n; i++) p.Series[1].Push((float)Math.Clamp(90 - 35.0 * i / Math.Max(1, n - 1), 0, 100));
            p.Latest = new[] { float.NaN, p.Series[1].Latest };
            p.Readout = Fmt.Pct(p.Series[1].Latest) + "  On battery  " + Fmt.Uptime(TimeSpan.FromMinutes(96));
            p.ScaleMax = 100;
        }
        foreach (var p in Panels.Where(p => p.IsSeries && p.Cfg.Kind != PanelKind.Battery))
        {
            double top = p.Cfg.Scale == ScaleMode.Fixed ? p.Cfg.FixedMax : p.Source.MinScale * 20;
            int n = p.Layout.HistCells * p.Layout.TicksPerHist + p.Layout.LiveCells * p.Layout.TicksPerLive;
            for (int li = 0; li < p.Series.Length; li++)
            {
                double amp = li == 0 ? 0.45 : 0.15 + 0.3 * rng.NextDouble(), ph = rng.NextDouble() * 6;
                for (int i = 0; i < n; i++)
                {
                    double wave = 0.5 + 0.5 * Math.Sin(i / (p.Layout.TicksPerHist * 9.0) + ph) * Math.Sin(i / (p.Layout.TicksPerHist * 3.1 + 7) + li);
                    double v = top * (0.06 + amp * wave + 0.05 * rng.NextDouble());
                    if (rng.NextDouble() < 0.0006) v += top * 0.35;
                    p.Series[li].Push((float)Math.Clamp(v, 0, top));
                }
            }
            p.ScaleMax = p.Cfg.Scale == ScaleMode.Fixed ? p.Cfg.FixedMax : AutoScale(p);
        }
    }

    static double AutoScale(PanelRuntime p)
    {
        float max = 0;
        for (int i = 0; i < p.Series.Length; i++) if (p.Cfg.Lines[i].Visible) max = Math.Max(max, p.Series[i].MaxDisplayed());
        return Math.Max(p.Source.MinScale, Fmt.NiceCeilUnits(max * 1.05, p.Source.UnitBase));
    }

    /// <summary>Drives currently mounted, for the Settings page (regardless of what is hidden).</summary>
    public IReadOnlyList<VolumeRow> CurrentVolumes => Volumes.Snapshot;
    /// <summary>Network adapters currently present, for the Settings page.</summary>
    public IReadOnlyList<string> CurrentAdapters { get { Net.Update(); return Net.Adapters; } }

    public void Dispose()
    {
        foreach (var s in sources.Values) s.Dispose();
        procs?.Dispose(); activity?.Dispose();
        if (ownsVolumes) volumes?.Dispose();
        if (ownsNet) net?.Dispose();
        if (ownsBattery) battery?.Dispose();
    }
}
