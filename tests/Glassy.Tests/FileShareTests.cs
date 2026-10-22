using Glassy.Core;
using Microsoft.Win32;
using Xunit;

namespace Glassy.Tests;

public class FileShareTests
{
    static Engine FsEngine(FakeFileShareProvider fs, PanelConfig p = null)
    {
        var c = AppConfig.CreateDefault();
        c.Panels = new List<PanelConfig> { p ?? AppConfig.FileSharePanel() };
        return new Engine(c, clipboard: new ClipboardStore(Path.Combine(Path.GetTempPath(), "glassy-fs-" + Guid.NewGuid().ToString("N"))), shares: fs);
    }

    static FileShareStatus Server(int n) => new()
    {
        Running = true,
        Shares = Enumerable.Range(1, n).Select(i => new SmbShare("share" + i, @"D:\s" + i, i, i * 1000, i * 500, 50, 100)).ToList(),
    };

    [Fact]
    public void Panel_is_only_shown_where_folders_are_shared()
    {
        using (var e = FsEngine(new FakeFileShareProvider { Installed = false })) Assert.Empty(e.Panels);
        using (var e = FsEngine(new FakeFileShareProvider())) Assert.Single(e.Panels, p => p.Cfg.Kind == PanelKind.FileShare);
    }

    [Fact]
    public void Rows_fold_past_the_cap_into_the_header()
    {
        var pc = AppConfig.FileSharePanel(); pc.FsMaxRows = 2;
        Assert.Equal(new[] { "Shares  2 of 3", "share1", "share2" }, FileShareFormat.Rows(pc, Server(3)).Select(r => r.Text));
        Assert.Equal("Server service not running", Assert.Single(FileShareFormat.Rows(pc, new FileShareStatus { Note = "Server service not running" })).Text);
    }

    [Fact]
    public void Counters_match_shares_by_their_counter_spelling_and_inactive_or_admin_shares_are_left_out()
    {
        var shares = new Dictionary<string, string> { ["Data (old)"] = @"D:\Old", ["Media"] = @"E:\Media", ["Gone"] = @"Z:\Missing" };   // Gone: still in the registry, not shared
        var open = new Dictionary<string, double> { [@"\*\data [old]"] = 3, [@"\*\media"] = 1, [@"\*\c$"] = 9, ["_Total"] = 13 };
        var read = new Dictionary<string, double> { [@"\*\media"] = 2000, [@"\SRV1\media"] = 500 };   // a scoped share instance adds to the same share
        var s = FileShareFormat.Merge(shares, new() { ["Media"] = (25.0, 100.0) }, open, read, new(), 4, 13);
        Assert.Equal(new[] { "Data (old)", "Media" }, s.Shares.Select(x => x.Name));
        Assert.Equal(3, s.Shares[0].OpenFiles);
        Assert.True(double.IsNaN(s.Shares[0].Total));                                   // no free-space reading for that path
        Assert.Equal((1.0, 2500.0, 25.0), (s.Shares[1].OpenFiles, s.Shares[1].ReadBps, s.Shares[1].Free));
        Assert.Equal(2500, s.ReadBps);                                                   // totals are the listed shares only, not C$
    }

    [Fact]
    public void Share_paths_are_read_from_the_registry_values()
    {
        string sub = @"Software\GlassyTest-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(sub))
            {
                k.SetValue("Media", new[] { "CSCFlags=0", "MaxUses=4294967295", @"Path=E:\Media", "Permissions=0", "Type=0" }, RegistryValueKind.MultiString);
                k.SetValue("Odd", new[] { "Type=0" }, RegistryValueKind.MultiString);
            }
            using var key = Registry.CurrentUser.OpenSubKey(sub);
            var shares = FileShareFormat.RegistryShares(key);
            Assert.Equal(@"E:\Media", shares["media"]);
            Assert.Equal("", shares["Odd"]);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sub, false); }
    }

    [Fact]
    public void Each_share_gets_its_own_throughput_line()
    {
        using var e = FsEngine(new FakeFileShareProvider { Value = Server(2) });
        e.Tick(); e.Tick();
        var p = e.Panels[0];
        Assert.Equal(new[] { "read", "write", "share:share1", "share:share2" }, p.Cfg.Lines.Select(l => l.Key));
        Assert.Equal(new[] { 1500f, 3000f }, p.Latest.Skip(2));                          // read + write per share
        Assert.Equal(p.Height, Engine.Header - 2 + Engine.LegendRowH + p.Cfg.Height + 6 + 3 * Engine.RowProc + 4);
    }

    [Theory]
    [InlineData("v0.9.0", "0.8.1.0", true)]
    [InlineData("v0.8.1", "0.8.1.0", false)]
    [InlineData("v0.8.0", "0.8.1.0", false)]
    [InlineData("v0.10.0", "0.9.0.0", true)]                                             // numeric, not string, comparison
    [InlineData("nightly", "0.8.1.0", false)]
    public void Update_check_compares_versions_numerically(string tag, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewer(Updates.ParseTag(tag), Version.Parse(current)));
}
