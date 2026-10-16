using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class IisTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "glassy-iis-" + Guid.NewGuid().ToString("N"));

    static Engine IisEngine(FakeIisProvider iis, PanelConfig p = null)
    {
        var c = AppConfig.CreateDefault();
        c.Panels = new List<PanelConfig> { p ?? AppConfig.IisPanel() };
        return new Engine(c, clipboard: new ClipboardStore(TempDir()), iis: iis);
    }

    static IisStatus Server(int sites, int pools) => new()
    {
        Running = true,
        Sites = Enumerable.Range(1, sites).Select(i => new IisSite("site" + i, i, i * 2)).ToList(),
        Pools = Enumerable.Range(1, pools).Select(i => new IisPool("pool" + i, 3)).ToList(),
    };

    [Fact]
    public void Panel_is_only_shown_where_the_IIS_role_is_installed()
    {
        using (var e = IisEngine(new FakeIisProvider { Installed = false })) Assert.Empty(e.Panels);
        using (var e = IisEngine(new FakeIisProvider { Installed = true })) Assert.Single(e.Panels, p => p.Cfg.Kind == PanelKind.Iis);
    }

    [Fact]
    public void Panel_grows_by_one_row_per_site_and_pool_once_counters_arrive()
    {
        var fake = new FakeIisProvider();
        using var e = IisEngine(fake);
        float before = e.Panels[0].Height;                       // nothing sampled yet: one "waiting" note row
        fake.Value = Server(sites: 3, pools: 2);
        e.Tick(); e.Tick();                                      // first tick samples, second sees the new row count and relayouts
        var p = e.Panels[0];
        Assert.Equal(2 + 3 + 2, IisFormat.Rows(p.Cfg, ((IisSource)p.Source).Status).Count);   // two headers, three sites, two pools
        Assert.Equal(before + 6 * Engine.RowProc, p.Height);
    }

    [Fact]
    public void Lists_past_the_row_cap_fold_into_their_header_and_toggles_hide_a_list()
    {
        var pc = AppConfig.IisPanel(); pc.IisMaxRows = 2;
        var rows = IisFormat.Rows(pc, Server(sites: 5, pools: 1));
        Assert.Equal(new[] { "Sites  2 of 5", "site1", "site2", "App pools  1", "pool1" }, rows.Select(r => r.Text));
        pc.IisShowSites = false;
        Assert.DoesNotContain(IisFormat.Rows(pc, Server(5, 1)), r => r.Kind == IisRowKind.Site);
    }

    [Fact]
    public void A_stopped_service_shows_one_note_row()
    {
        var rows = IisFormat.Rows(AppConfig.IisPanel(), new IisStatus { Running = false, Note = "W3SVC not running" });
        Assert.Equal("W3SVC not running", Assert.Single(rows).Text);
    }

    [Fact]
    public void Total_instance_is_dropped_and_names_are_sorted()
    {
        Assert.Equal(new[] { "api", "Default Web Site" }, IisFormat.Instances(new[] { "_Total", "Default Web Site", "api" }));
    }

    [Theory]
    [InlineData(3, "Running", 1)]
    [InlineData(5, "Stopped", -1)]
    [InlineData(2, "Starting", 0)]
    [InlineData(99, "Unknown", 0)]
    public void Pool_state_codes_map_to_label_and_health(int code, string label, int health) =>
        Assert.Equal((label, health), IisFormat.PoolState(code));

    /// <summary>Exercises the real PdhGetFormattedCounterArrayW marshaling on a counter every machine has, since
    /// the dev box has no IIS counters to read.</summary>
    [Fact]
    public void Wildcard_counter_read_returns_every_instance()
    {
        Assert.Equal(0u, Native.PdhOpenQueryW(null, IntPtr.Zero, out var q));
        try
        {
            Assert.Equal(0u, Native.PdhAddEnglishCounterW(q, @"\Processor(*)\% Processor Time", IntPtr.Zero, out var c));
            Native.PdhCollectQueryData(q); Thread.Sleep(200); Native.PdhCollectQueryData(q);
            var values = PdhInstances.Read(c);
            Assert.Contains("_Total", values.Keys);
            Assert.Equal(Environment.ProcessorCount, values.Keys.Count(k => k != "_Total"));
            Assert.All(values.Values, v => Assert.InRange(v, 0, 100));
        }
        finally { Native.PdhCloseQuery(q); }
    }

    [Fact]
    public void A_config_from_a_newer_build_loses_only_the_panels_it_does_not_know()
    {
        var path = Path.Combine(TempDir(), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"Global\": { \"Width\": 300 }, \"Panels\": [ { \"Kind\": \"Cpu\" }, { \"Kind\": \"SomeFuturePanel\" }, { \"Kind\": \"Uptime\" } ] }");
        var cfg = ConfigStore.Load(path);
        Assert.Equal(300, cfg.Global.Width);                                  // the user's settings survived
        Assert.Equal(new[] { PanelKind.Cpu, PanelKind.Uptime }, cfg.Panels.Take(2).Select(p => p.Kind));
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Each_site_gets_its_own_requests_line()
    {
        var fake = new FakeIisProvider { Value = Server(sites: 2, pools: 1) };
        using var e = IisEngine(fake);
        e.Tick(); e.Tick();
        var p = e.Panels[0];
        Assert.Equal(new[] { "req", "conn", "site:site1", "site:site2" }, p.Cfg.Lines.Select(l => l.Key));
        Assert.Equal(new[] { 2f, 4f }, p.Latest.Skip(2));                    // each site's own req/s
    }
}
