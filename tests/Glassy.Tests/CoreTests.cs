using System.Diagnostics;
using System.Text;
using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class BufferTests
{
    [Fact]
    public void Ring_wraps_and_indexes_oldest_first()
    {
        var r = new FloatRing(3);
        for (int i = 1; i <= 5; i++) r.Push(i);
        Assert.Equal(3, r.Count);
        Assert.Equal(new float[] { 3, 4, 5 }, new[] { r[0], r[1], r[2] });
    }

    [Fact]
    public void Layout_splits_80_20_and_derives_ratios()
    {
        var t = new TimeLayout(324, 80, 30, 6 * 3600, 500);
        Assert.Equal(259, t.HistCells); Assert.Equal(65, t.LiveCells);
        Assert.Equal(1, t.TicksPerLive);                // 30 s / 65 cells = 0.46 s -> one tick per live pixel
        Assert.InRange(t.TicksPerHist, 160, 170);       // 21600 s / 259 cells / 0.5 s = 166.8
    }

    [Fact]
    public void Live_cell_is_mean_of_its_ticks_and_history_cell_is_mean_or_peak()
    {
        var t = new TimeLayout(20, 50, 10, 20, 500);    // 10 live cells over 10 s -> 2 ticks/cell; 10 hist cells over 20 s -> 4 ticks/cell
        Assert.Equal(2, t.TicksPerLive); Assert.Equal(4, t.TicksPerHist);
        var avg = new SeriesBuffer(t, HistoryMode.Average); var peak = new SeriesBuffer(t, HistoryMode.Peak);
        foreach (var v in new float[] { 10, 30, 20, 80 }) { avg.Push(v); peak.Push(v); }
        Assert.Equal(2, avg.Live.Count); Assert.Equal(20f, avg.Live[0]); Assert.Equal(50f, avg.Live[1]);
        Assert.Equal(35f, avg.Hist[0]);      // mean of 10,30,20,80
        Assert.Equal(80f, peak.Hist[0]);     // peak survives; the mean would hide it
    }

    [Fact]
    public void NaN_samples_are_skipped_and_cells_right_align()
    {
        var t = new TimeLayout(20, 50, 10, 20, 500);
        var s = new SeriesBuffer(t, HistoryMode.Average);
        s.Push(float.NaN); s.Push(float.NaN);
        Assert.Equal(0, s.Live.Count);
        s.Push(5); s.Push(7);
        Assert.Equal(6f, s.Cell(19));                       // newest live cell is the rightmost cell
        Assert.True(float.IsNaN(s.Cell(0)));                // nothing yet at the far left
        Assert.True(float.IsNaN(s.Cell(9)));                // history empty
    }
}

public class ConfigTests
{
    static string Tmp() => Path.Combine(Path.GetTempPath(), "glassy-test-" + Guid.NewGuid().ToString("N"), "config.json");

    [Fact]
    public void Roundtrip_keeps_order_enums_and_lines_without_bom()
    {
        var path = Tmp(); var cfg = AppConfig.CreateDefault();
        cfg.Panels.Reverse(); cfg.Global.Mode = WindowMode.TopMost; cfg.Panels[0].Lines.Add(new LineConfig { Key = "x", Color = "#123456" });
        ConfigStore.Save(cfg, path);
        var raw = File.ReadAllBytes(path);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "UTF-8 BOM found");
        Assert.Contains("\"TopMost\"", Encoding.UTF8.GetString(raw));   // enums are strings, not numbers
        var back = ConfigStore.Load(path);
        Assert.Equal(cfg.Panels.Select(p => p.Kind), back.Panels.Select(p => p.Kind));
        Assert.Equal(WindowMode.TopMost, back.Global.Mode);
        Assert.Equal("#123456", back.Panels[0].Lines[0].Color);
    }

    [Fact]
    public void Corrupt_file_is_kept_as_bad_and_defaults_returned()
    {
        var path = Tmp(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var cfg = ConfigStore.Load(path);
        Assert.Equal(AppConfig.CreateDefault().Panels.Count, cfg.Panels.Count);
        Assert.True(File.Exists(path + ".bad")); Assert.False(File.Exists(path));
    }

    [Fact]
    public void Missing_file_gives_defaults() =>
        Assert.NotEmpty(ConfigStore.Load(Tmp()).Panels);
}

public class FormatTests
{
    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(1.2, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(130, 200)]
    public void NiceCeil_rounds_up(double v, double expected) => Assert.Equal(expected, Fmt.NiceCeil(v));

    [Fact]
    public void NiceCeilUnits_is_round_in_the_displayed_unit()
    {
        Assert.Equal(2.5 * 1048576, Fmt.NiceCeilUnits(2.2 * 1048576, 1024));   // 2.5 MB, not 2.38 MB
        Assert.Equal(500_000, Fmt.NiceCeilUnits(353_000, 1000));
        Assert.True(Fmt.NiceCeilUnits(700, 1024) >= 700);
    }

    [Fact]
    public void Bits_and_bytes_format() { Assert.Equal("25 Mbps", Fmt.Bits(25_000_000)); Assert.Equal("1.5 MB", Fmt.Bytes(1.5 * 1024 * 1024)); }
}

public class SourceSmokeTests
{
    [Fact]
    public void Cpu_has_total_plus_one_line_per_thread_and_rises_under_load()
    {
        using var cpu = new CpuSource();
        Assert.Equal(1 + Environment.ProcessorCount, cpu.DefaultLines().Count);
        var v = new float[cpu.DefaultLines().Count];
        Thread.Sleep(300); cpu.Sample(v);
        float idle = v[0];
        var stop = false; var burners = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() => { while (!Volatile.Read(ref stop)) { } }) { IsBackground = true }).ToList();
        burners.ForEach(b => b.Start()); Thread.Sleep(700); cpu.Sample(v); Volatile.Write(ref stop, true);
        Assert.InRange(v[0], 60, 100);              // every thread busy
        Assert.All(v, x => Assert.InRange(x, 0, 100));
        Assert.True(v[0] > idle);
    }

    [Fact]
    public void Cpu_labels_distinguish_P_and_E_threads_on_hybrid_parts()
    {
        using var cpu = new CpuSource(); var lines = cpu.DefaultLines();
        Assert.Equal(lines.Count, lines.Select(l => l.Label).Distinct().Count());   // labels are unique
    }

    [Fact]
    public void Ram_reports_percentages()
    {
        var r = new RamSource(); var v = new float[2]; r.Sample(v);
        Assert.InRange(v[0], 1, 100); Assert.InRange(v[1], 1, 100); Assert.Contains("/", r.Readout(v));
    }

    [Fact]
    public void Process_parser_offsets_match_this_process()
    {
        using var ps = new ProcessSampler(); var me = Process.GetCurrentProcess(); me.Refresh();
        var priv = ps.ProbePrivateWs(me.Id); Assert.NotNull(priv);
        // A wrong field (hard-fault count, thread count...) is a small number; a real private working set of a .NET host is many MB.
        Assert.InRange(priv.Value, 3L * 1024 * 1024, (long)(me.WorkingSet64 * 1.15));
        var rows = ps.Sample(500, false);
        Assert.Contains(rows, r => r.Name.Contains("testhost", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("dotnet", StringComparison.OrdinalIgnoreCase));
        Assert.All(rows, r => Assert.True(r.Cpu >= 0 && r.Mem >= 0));
    }

    [Fact]
    public void Process_cpu_delta_sees_a_busy_thread()
    {
        using var ps = new ProcessSampler(); ps.Sample(1, false);
        var end = Stopwatch.StartNew(); while (end.ElapsedMilliseconds < 800) { }     // burn one core in this process
        var rows = ps.Sample(500, false);
        var mine = rows.First(r => r.Name.Contains("testhost", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("dotnet", StringComparison.OrdinalIgnoreCase));
        Assert.True(mine.Cpu > 1.5, "expected ~4% of a 24-thread box, got " + mine.Cpu);   // 1 of 24 threads = 4.2%
    }

    [Fact]
    public void NetHub_updates_and_reports_nonnegative_rates()
    {
        using var n = new NetHub(); n.Update(); Thread.Sleep(300); n.Update();
        Assert.True(n.Combined.down >= 0 && n.Combined.up >= 0);
        Assert.NotNull(n.Adapters);
    }

    [Fact]
    public void DriveActivityHub_reads_real_counters_for_a_local_drive()
    {
        var c = VolumeWatcher.Scan(false).FirstOrDefault(v => v.Kind == VolKind.Fixed); if (c == null) return;
        using var h = new DriveActivityHub(); h.Sync(new[] { c.Letter }); h.Update(); Thread.Sleep(300); h.Update();
        Assert.True(h.TryGet(c.Letter, out float r, out float w));
        Assert.False(float.IsNaN(r)); Assert.False(float.IsNaN(w));
        h.Sync(Array.Empty<string>());
        Assert.False(h.TryGet(c.Letter, out _, out _));   // removed drive: counters released
    }

    [Fact]
    public void Gpu_reports_when_nvml_present()
    {
        using var g = new GpuSource(); if (!g.Available) return;   // machines without an NVIDIA driver skip
        var v = new float[4]; g.Sample(v);
        Assert.InRange(v[0], 0, 100); Assert.InRange(v[1], 0.01, 100);
    }

    [Fact]
    public void Volume_scan_finds_a_fixed_drive_with_space_data()
    {
        var v = VolumeWatcher.Scan(false).First(x => x.Kind == VolKind.Fixed);
        Assert.InRange(v.Total, 1, long.MaxValue); Assert.InRange(v.Free, 0, v.Total); Assert.False(v.ReadOnly);
    }
}

public class EngineTests
{
    [Fact]
    public void Default_config_ticks_and_reconciles_lines()
    {
        var cfg = AppConfig.CreateDefault(); using var e = new Engine(cfg);
        for (int i = 0; i < 3; i++) { e.Tick(); Thread.Sleep(120); }
        var cpu = e.Panels.First(p => p.Cfg.Kind == PanelKind.Cpu);
        Assert.Equal(1 + Environment.ProcessorCount, cpu.Cfg.Lines.Count);
        Assert.Equal(cpu.Cfg.Lines.Count, cpu.Series.Length);
        Assert.True(e.TotalHeight > 100);
        Assert.Contains(e.Panels, p => p.Cfg.Kind == PanelKind.Drives && p.Volume != null);
        Assert.StartsWith("Uptime", e.Panels.First(p => p.Cfg.Kind == PanelKind.Uptime).Text);
    }

    [Fact]
    public void Changing_durations_rebuilds_buffers_but_unrelated_edits_keep_history()
    {
        var cfg = AppConfig.CreateDefault(); using var e = new Engine(cfg);
        var ram = e.Panels.First(p => p.Cfg.Kind == PanelKind.Ram);
        e.Tick(); e.Tick();
        var before = ram.Series[0];
        cfg.Panels.First(p => p.Kind == PanelKind.Ram).Lines[0].Color = "#00FF00";
        e.Apply(cfg);
        Assert.Same(before, e.Panels.First(p => p.Cfg.Kind == PanelKind.Ram).Series[0]);      // colour edit: history kept
        cfg.Panels.First(p => p.Kind == PanelKind.Ram).LiveSeconds = 90;
        e.Apply(cfg);
        Assert.NotSame(before, e.Panels.First(p => p.Cfg.Kind == PanelKind.Ram).Series[0]);   // timing edit: rebuilt
    }

    [Fact]
    public void Duplicate_panel_kinds_in_a_hand_edited_config_are_ignored()
    {
        var cfg = AppConfig.CreateDefault(); cfg.Panels.Add(new PanelConfig { Kind = PanelKind.Cpu });
        using var e = new Engine(cfg);
        Assert.Single(e.Panels, p => p.Cfg.Kind == PanelKind.Cpu);
    }

    [Fact]
    public void Disabled_and_reordered_panels_follow_config()
    {
        var cfg = AppConfig.CreateDefault(); cfg.Panels.First(p => p.Kind == PanelKind.Gpu).Enabled = false;
        var first = cfg.Panels[0]; cfg.Panels.RemoveAt(0); cfg.Panels.Add(first);
        using var e = new Engine(cfg);
        Assert.DoesNotContain(e.Panels, p => p.Cfg.Kind == PanelKind.Gpu);
        Assert.Equal(PanelKind.Cpu, e.Panels.Last().Cfg.Kind);
        Assert.True(e.Panels[0].Top < e.Panels[1].Top);
    }
}

public class LabelAndStartupTests
{
    [Fact]
    public void Hybrid_core_numbers_are_consecutive_within_each_class()
    {
        using var cpu = new CpuSource(); var labels = cpu.DefaultLines().Skip(1).Select(l => l.Label).ToList();
        if (!labels.Any(l => l.StartsWith("E"))) return;   // non-hybrid CPU: labels are "CPU n"
        var p = labels.Where(l => l.StartsWith("P")).Select(l => int.Parse(l.Substring(1, l.Length - 2))).Distinct().OrderBy(x => x).ToList();
        var e = labels.Where(l => l.StartsWith("E")).Select(l => int.Parse(l.Substring(1))).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(0, p.Count), p);      // 0,1,2... not 0,2,4...
        Assert.Equal(Enumerable.Range(0, e.Count), e);
    }

    [Fact]
    public void Launcher_quotes_paths_and_refuses_injection()
    {
        var vbs = Startup.BuildLauncher(@"C:\Program Files\dotnet\dotnet.exe", @"D:\My Apps\Glassy.App.dll");
        var expected = "CreateObject(\"WScript.Shell\").Run \"\"\"" + @"C:\Program Files\dotnet\dotnet.exe" + "\"\" \"\"" + @"D:\My Apps\Glassy.App.dll" + "\"\"\", 0, False" + Environment.NewLine;
        Assert.Equal(expected, vbs);
        Assert.Throws<ArgumentException>(() => Startup.BuildLauncher(@"C:\x" + "\"; evil", @"D:\a.dll"));
        Assert.Throws<ArgumentException>(() => Startup.BuildLauncher(@"C:\x.exe", @"D:\a.dll" + "\r\nWScript.Quit"));
    }

    [Fact]
    public void Autostart_registry_value_is_written_and_removed()
    {
        const string name = "GlassySystemGadget.UnitTest";
        var launcher = Path.Combine(Path.GetTempPath(), "glassy-test-" + Guid.NewGuid().ToString("N"), "launch.vbs");
        try
        {
            Assert.False(Startup.IsEnabled(name));
            Startup.Set(true, @"D:\Glassy.App.dll", name, launcher);
            Assert.True(Startup.IsEnabled(name)); Assert.True(File.Exists(launcher));
            Startup.Set(false, @"D:\Glassy.App.dll", name, launcher);
            Assert.False(Startup.IsEnabled(name));
        }
        finally { Startup.Set(false, "", name, launcher); }   // never leave a test value in the user's Run key
    }
}

sealed class FakeVolumes : IVolumeProvider
{
    public VolumeRow[] Snapshot { get; set; } = Array.Empty<VolumeRow>();
    public void Dispose() { }
}

sealed class FakeNet : INetProvider
{
    public IReadOnlyList<string> Adapters { get; set; } = Array.Empty<string>();
    public List<string> Tracked = new();
    public (float down, float up) Combined => (0, 0);
    public bool TryGet(string adapter, out float down, out float up) { down = up = 0; return false; }
    public void Track(IEnumerable<string> names) => Tracked = names.ToList();
    public void Update() { }
    public void Dispose() { }
}

public class DriveSectionTests
{
    static VolumeRow Vol(string letter, VolKind kind, bool ro = false, string label = "") =>
        new() { Letter = letter, Kind = kind, ReadOnly = ro, Label = label, Total = 1000, Free = 400 };

    static AppConfig DrivesOnly() { var c = AppConfig.CreateDefault(); c.Panels = new List<PanelConfig> { AppConfig.DrivesPanel(true) }; return c; }

    [Fact]
    public void One_section_per_drive_with_tags_and_no_activity_graph_for_network_drives()
    {
        var vols = new FakeVolumes { Snapshot = new[] { Vol("C", VolKind.Fixed), Vol("D", VolKind.Optical, ro: true), Vol("N", VolKind.Network) } };
        using var e = new Engine(DrivesOnly(), vols, new FakeNet());
        Assert.Equal(new[] { "C", "D", "N" }, e.Panels.Select(p => p.Sub));
        Assert.True(e.Panels[1].Volume.ReadOnly); Assert.Equal(VolKind.Optical, e.Panels[1].Volume.Kind);
        Assert.NotNull(e.Panels[0].Source); Assert.Null(e.Panels[2].Source);           // network share: space bar only
        Assert.True(e.Panels[2].Height < e.Panels[0].Height);                          // and therefore a shorter section
        Assert.Equal(Engine.DriveHeader + 38 + 4 + Engine.DriveBar + 4, e.Panels[0].Height);
        Assert.Equal(e.Panels[0].Top + e.Panels[0].Height + Engine.SectionGap, e.Panels[1].Top);   // sections of one group sit close together
    }

    [Fact]
    public void A_drive_without_activity_counters_gets_a_bar_but_no_graph()
    {
        var vols = new FakeVolumes { Snapshot = new[] { Vol("C", VolKind.Fixed), Vol("1", VolKind.Optical) } };   // no LogicalDisk instance named "1:"
        using var e = new Engine(DrivesOnly(), vols, new FakeNet());
        for (int i = 0; i < 8; i++) { e.Tick(); Thread.Sleep(60); }                     // a few empty samples mark the drive as having no data
        Assert.NotNull(e.Panels[0].Source); Assert.Null(e.Panels[1].Source);
        Assert.True(e.Panels[1].Height < e.Panels[0].Height);
    }

    [Fact]
    public void Plugging_in_and_removing_a_drive_changes_the_layout_and_keeps_other_history()
    {
        var vols = new FakeVolumes { Snapshot = new[] { Vol("C", VolKind.Fixed) } };
        using var e = new Engine(DrivesOnly(), vols, new FakeNet());
        e.Tick(); var cSeries = e.Panels[0].Series[0]; int v0 = e.LayoutVersion; float h0 = e.TotalHeight;
        vols.Snapshot = new[] { Vol("C", VolKind.Fixed), Vol("E", VolKind.Removable, label: "USB") };   // stick inserted
        e.Tick();
        Assert.Equal(2, e.Panels.Count); Assert.True(e.LayoutVersion > v0); Assert.True(e.TotalHeight > h0);
        Assert.Same(cSeries, e.Panels[0].Series[0]);                                   // C keeps its history
        int v1 = e.LayoutVersion;
        vols.Snapshot = new[] { Vol("C", VolKind.Fixed) };                             // stick removed
        e.Tick();
        Assert.Single(e.Panels); Assert.True(e.LayoutVersion > v1);
    }

    [Fact]
    public void Free_space_changes_update_the_section_without_a_layout_change()
    {
        var vols = new FakeVolumes { Snapshot = new[] { Vol("C", VolKind.Fixed) } };
        using var e = new Engine(DrivesOnly(), vols, new FakeNet()); e.Tick(); int v = e.LayoutVersion;
        var c = Vol("C", VolKind.Fixed); c.Free = 100; vols.Snapshot = new[] { c };
        e.Tick();
        Assert.Equal(v, e.LayoutVersion); Assert.Equal(100, e.Panels[0].Volume.Free);
    }

    [Fact]
    public void Hidden_drives_get_no_section_and_the_activity_toggle_removes_the_graph()
    {
        var cfg = DrivesOnly(); var vols = new FakeVolumes { Snapshot = new[] { Vol("C", VolKind.Fixed), Vol("D", VolKind.Fixed) } };
        cfg.Panels[0].HiddenDrives.Add("d");                                            // case-insensitive
        using var e = new Engine(cfg, vols, new FakeNet());
        Assert.Equal(new[] { "C" }, e.Panels.Select(p => p.Sub));
        cfg.Panels[0].ShowActivity = false; e.Apply(cfg);
        Assert.Null(e.Panels[0].Source);
        Assert.Equal(Engine.DriveHeader + Engine.DriveBar + 4, e.Panels[0].Height);
    }
}

public class NetworkSectionTests
{
    static AppConfig NetOnly(Action<PanelConfig> tweak)
    {
        var c = AppConfig.CreateDefault(); c.Panels = c.Panels.Where(p => p.Kind == PanelKind.Network).ToList(); tweak(c.Panels[0]); return c;
    }

    [Fact]
    public void Combined_mode_is_one_graph_and_separate_mode_is_one_graph_per_selected_adapter()
    {
        var net = new FakeNet { Adapters = new[] { "Ethernet", "Wi-Fi", "vEthernet (Default Switch)" } };
        using (var e = new Engine(NetOnly(_ => { }), new FakeVolumes(), net)) Assert.Single(e.Panels);
        using var e2 = new Engine(NetOnly(p => { p.SeparateAdapters = true; p.Adapters = new List<string> { "Ethernet", "Wi-Fi" }; }), new FakeVolumes(), net);
        Assert.Equal(new[] { "Ethernet", "Wi-Fi" }, e2.Panels.Select(p => p.Title));
        Assert.Equal(new[] { "Ethernet", "Wi-Fi" }, net.Tracked);
    }

    [Fact]
    public void An_adapter_that_disappears_loses_its_section_and_one_that_appears_gains_one()
    {
        var net = new FakeNet { Adapters = new[] { "Ethernet", "USB LAN" } };
        var cfg = NetOnly(p => { p.SeparateAdapters = true; p.Adapters = new List<string> { "Ethernet", "USB LAN" }; });
        using var e = new Engine(cfg, new FakeVolumes(), net); Assert.Equal(2, e.Panels.Count);
        net.Adapters = new[] { "Ethernet" }; e.Tick(); Assert.Single(e.Panels);
        net.Adapters = new[] { "Ethernet", "USB LAN" }; e.Tick(); Assert.Equal(2, e.Panels.Count);
    }
}

public class ColourAndMigrationTests
{
    [Fact]
    public void Load_colour_ramp_runs_green_to_red_with_orange_at_90_percent()
    {
        Assert.Equal(((byte)124, (byte)255, (byte)107), Fmt.LoadColor(0));
        Assert.Equal(((byte)255, (byte)120, (byte)40), Fmt.LoadColor(0.9));       // orange
        Assert.Equal(((byte)255, (byte)60, (byte)80), Fmt.LoadColor(1.0));        // red
        Assert.Equal(Fmt.LoadColor(1.0), Fmt.LoadColor(7));                       // clamped
        double prev = 999; foreach (var f in new[] { 0.5, 0.6, 0.75, 0.9, 1.0 }) { var g = Fmt.LoadColor(f).g; Assert.True(g <= prev); prev = g; }   // green drains as load rises
    }

    [Fact]
    public void Old_config_with_separate_disk_panels_migrates_to_one_drives_panel_in_place()
    {
        var path = Path.Combine(Path.GetTempPath(), "glassy-test-" + Guid.NewGuid().ToString("N"), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"Global\": {}, \"Panels\": [ { \"Kind\": \"Cpu\" }, { \"Kind\": \"DiskIo\", \"Enabled\": false }, { \"Kind\": \"DiskSpace\" }, { \"Kind\": \"Uptime\" } ] }");
        var cfg = ConfigStore.Load(path);
        Assert.Equal(new[] { PanelKind.Cpu, PanelKind.Drives, PanelKind.Uptime, PanelKind.Clipboard, PanelKind.Battery }, cfg.Panels.Select(p => p.Kind));
        Assert.True(cfg.Panels[1].Enabled);                                       // DiskSpace was enabled, so the merged panel is
        Assert.False(File.Exists(path + ".bad"));                                 // migrated, not discarded
    }

    [Fact]
    public void Old_config_that_predates_the_battery_panel_still_gains_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "glassy-test-" + Guid.NewGuid().ToString("N"), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"Global\": {}, \"Panels\": [ { \"Kind\": \"Cpu\" }, { \"Kind\": \"Drives\" }, { \"Kind\": \"Clipboard\" } ] }");
        var cfg = ConfigStore.Load(path);
        var battery = cfg.Panels.Single(p => p.Kind == PanelKind.Battery);
        Assert.True(battery.Enabled);
    }

    [Fact]
    public void Discharge_color_ramp_runs_yellow_to_red_with_no_green()
    {
        Assert.Equal(((byte)255, (byte)214, (byte)79), Fmt.DischargeColor(100));   // full: yellow
        Assert.Equal(((byte)255, (byte)168, (byte)60), Fmt.DischargeColor(50));    // half: orange
        Assert.Equal(((byte)255, (byte)60, (byte)80), Fmt.DischargeColor(0));      // empty: red
        for (double pct = 0; pct <= 100; pct += 5) Assert.Equal(255, Fmt.DischargeColor(pct).r);   // never green
    }
}

public class BatteryTests
{
    static AppConfig BatteryOnly() { var c = AppConfig.CreateDefault(); c.Panels = new List<PanelConfig> { AppConfig.BatteryPanel() }; return c; }

    [Fact]
    public void No_battery_detected_means_no_panel_even_though_it_is_enabled()
    {
        var battery = new FakeBatteryProvider { Value = null };
        using var e = new Engine(BatteryOnly(), battery: battery);
        Assert.Empty(e.Panels);
    }

    [Fact]
    public void A_detected_battery_appears_and_samples_charge_and_discharge_into_separate_lines()
    {
        var battery = new FakeBatteryProvider { Value = new BatteryStatus { Present = true, Charging = true, OnAc = true, Percent = 62, TimeRemaining = TimeSpan.FromMinutes(40) } };
        using var e = new Engine(BatteryOnly(), battery: battery);
        var p = Assert.Single(e.Panels);
        Assert.Equal(PanelKind.Battery, p.Cfg.Kind);
        e.Tick();
        Assert.Equal(62f, p.Latest[0]);            // charging -> line 0
        Assert.True(float.IsNaN(p.Latest[1]));      // discharge line untouched
        Assert.Contains("Charging", p.Readout);
        Assert.Contains("to full", p.Readout);

        battery.Value = new BatteryStatus { Present = true, Charging = false, OnAc = false, Percent = 58, TimeRemaining = TimeSpan.FromHours(2) };
        e.Tick();
        Assert.True(float.IsNaN(p.Latest[0]));
        Assert.Equal(58f, p.Latest[1]);
        Assert.Contains("On battery", p.Readout);
        Assert.Contains("remaining", p.Readout);
    }

    [Fact]
    public void Battery_appearing_or_vanishing_live_changes_the_panel_set()
    {
        var battery = new FakeBatteryProvider { Value = null };
        using var e = new Engine(BatteryOnly(), battery: battery);
        e.Tick(); Assert.Empty(e.Panels);
        battery.Value = new BatteryStatus { Present = true, Percent = 80 };
        e.Tick(); Assert.Single(e.Panels);
        battery.Value = null;
        e.Tick(); Assert.Empty(e.Panels);
    }

    [Fact]
    public void Real_provider_returns_null_or_a_sane_reading_without_throwing()
    {
        using var p = new SystemBatteryProvider();
        var s = p.Snapshot;   // this dev machine is a desktop: expected to be null, but must not throw either way
        if (s is { } v) Assert.InRange(v.Percent, 0, 100);
    }
}
