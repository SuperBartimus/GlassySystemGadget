using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class HyperVTests
{
    static Engine HvEngine(FakeHyperVProvider hv, PanelConfig p = null)
    {
        var c = AppConfig.CreateDefault();
        c.Panels = new List<PanelConfig> { p ?? AppConfig.HyperVPanel() };
        return new Engine(c, clipboard: new ClipboardStore(Path.Combine(Path.GetTempPath(), "glassy-hv-" + Guid.NewGuid().ToString("N"))), hyperV: hv);
    }

    static HyperVStatus Host(params (string name, int state)[] vms) => new()
    {
        Available = true, FullList = true, HostCpuPct = 20,
        Vms = vms.Select(v => new HyperVVm(v.name, v.state, v.state == 2 ? 10 : double.NaN, 1024, null)).ToList(),
    };

    [Fact]
    public void Panel_is_only_shown_where_the_Hyper_V_role_is_installed()
    {
        using (var e = HvEngine(new FakeHyperVProvider { Installed = false })) Assert.Empty(e.Panels);
        using (var e = HvEngine(new FakeHyperVProvider())) Assert.Single(e.Panels, p => p.Cfg.Kind == PanelKind.HyperV);
    }

    [Fact]
    public void Processor_instance_names_map_back_to_their_VM_even_when_the_name_has_colons()
    {
        Assert.Equal("web01", HyperVFormat.VmOfProcessor("web01:Hv VP 0"));
        Assert.Equal("lab:old", HyperVFormat.VmOfProcessor("lab:old:Hv VP 12"));
        Assert.Equal("", HyperVFormat.VmOfProcessor("_Total"));
    }

    [Fact]
    public void With_WMI_every_VM_is_listed_and_CPU_is_the_average_of_its_processors()
    {
        var wmi = new List<(string, int, double)> { ("web01", 2, 3_600_000), ("lab", 3, 0) };
        var vp = new Dictionary<string, double> { ["web01:Hv VP 0"] = 10, ["web01:Hv VP 1"] = 30, ["_Total"] = 99 };
        var s = HyperVFormat.Merge(wmi, "", vp, new Dictionary<string, double> { ["web01"] = 4096 }, 25);
        Assert.True(s.FullList);
        Assert.Equal(new[] { "lab", "web01" }, s.Vms.Select(v => v.Name));
        var web = s.Vms[1];
        Assert.Equal(20, web.CpuPct); Assert.Equal(4096, web.MemoryMB); Assert.Equal(TimeSpan.FromHours(1), web.Uptime);
        Assert.True(double.IsNaN(s.Vms[0].CpuPct));                         // an off VM has no CPU reading
    }

    [Fact]
    public void Without_WMI_the_running_VMs_still_come_from_the_counters_and_the_row_list_says_why_others_are_missing()
    {
        var vp = new Dictionary<string, double> { ["web01:Hv VP 0"] = 12 };
        var s = HyperVFormat.Merge(null, "Off/saved VMs need Hyper-V Administrators", vp, new Dictionary<string, double>(), double.NaN);
        Assert.True(s.Available); Assert.False(s.FullList);
        Assert.Equal(new[] { "web01" }, s.Vms.Select(v => v.Name));
        var rows = HyperVFormat.Rows(AppConfig.HyperVPanel(), s);
        Assert.Equal("Off/saved VMs need Hyper-V Administrators", rows[^1].Text);
    }

    [Fact]
    public void Off_VMs_can_be_hidden_and_the_row_cap_is_reported_in_the_header()
    {
        var pc = AppConfig.HyperVPanel(); pc.HvMaxRows = 1;
        var rows = HyperVFormat.Rows(pc, Host(("a", 2), ("b", 2), ("c", 3)));
        Assert.Equal(new[] { "Virtual machines  2 of 3 running  (showing 1)", "a" }, rows.Select(r => r.Text));
        pc.HvShowOff = false; pc.HvMaxRows = 10;
        Assert.DoesNotContain(HyperVFormat.Rows(pc, Host(("a", 2), ("c", 3))), r => r.Text == "c");
    }

    [Fact]
    public void A_VM_appearing_adds_its_line_without_wiping_the_other_lines_history()
    {
        var fake = new FakeHyperVProvider { Value = Host(("a", 2)) };
        using var e = HvEngine(fake);
        e.Tick(); e.Tick();
        var p = e.Panels[0];
        Assert.Equal(new[] { "host", "vm:a" }, p.Cfg.Lines.Select(l => l.Key));
        var aBuffer = p.Series[1];
        fake.Value = Host(("a", 2), ("b", 2));
        e.Tick(); e.Tick();
        p = e.Panels[0];
        Assert.Equal(new[] { "host", "vm:a", "vm:b" }, p.Cfg.Lines.Select(l => l.Key));
        Assert.Same(aBuffer, p.Series[1]);
        Assert.Equal(p.Height, Engine.Header - 2 + Engine.LegendRowH + p.Cfg.Height + 6 + 3 * Engine.RowProc + 4);   // header + 2 VM rows
    }

    [Fact]
    public void A_per_VM_line_colour_survives_a_restart_before_the_first_reading()
    {
        var pc = AppConfig.HyperVPanel();
        pc.Lines = new List<LineConfig> { new() { Key = "host", Label = "Host CPU" }, new() { Key = "vm:a", Label = "a", Color = "#123456" } };
        using var e = HvEngine(new FakeHyperVProvider { Value = new HyperVStatus() }, pc);   // nothing read yet
        Assert.Equal("#123456", e.Panels[0].Cfg.Lines.Single(l => l.Key == "vm:a").Color);
    }

    [Fact]
    public void Two_VMs_with_the_same_name_do_not_break_the_line_list()
    {
        using var e = HvEngine(new FakeHyperVProvider { Value = Host(("dup", 2), ("dup", 3)) });
        e.Tick(); e.Tick();
        Assert.Single(e.Panels[0].Cfg.Lines, l => l.Key == "vm:dup");
    }

    /// <summary>Exercises the late-bound WMI plumbing on a class every machine has (the dev box has no Hyper-V).</summary>
    [Fact]
    public void Wmi_query_reads_properties_of_a_real_instance()
    {
        int pid = Environment.ProcessId;
        var rows = Wmi.Query(@"root\cimv2", $"SELECT Name, ProcessId FROM Win32_Process WHERE ProcessId = {pid}", "Name", "ProcessId");
        Assert.Equal(pid, Convert.ToInt32(Assert.Single(rows)["ProcessId"]));
    }
}
