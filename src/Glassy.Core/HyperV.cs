using System.Globalization;
using Microsoft.Win32;

namespace Glassy.Core;

/// <summary>State is the raw Msvm_ComputerSystem EnabledState code (see HyperVFormat.State); CpuPct is the average
/// across the VM's own virtual processors; MemoryMB is what the host has currently assigned it.</summary>
public sealed record HyperVVm(string Name, int State, double CpuPct, double MemoryMB, TimeSpan? Uptime);

public sealed class HyperVStatus
{
    /// <summary>False when nothing could be read at all - Note says why.</summary>
    public bool Available;
    /// <summary>False when the VM list came from performance counters only (WMI denied): only running VMs are known.</summary>
    public bool FullList;
    public string Note = "";
    public List<HyperVVm> Vms = new();
    /// <summary>Whole-host CPU including guests (the hypervisor's view) - the CPU panel on a Hyper-V host only sees the root partition.</summary>
    public double HostCpuPct = double.NaN;
}

public interface IHyperVProvider : IDisposable
{
    bool Installed { get; }
    HyperVStatus Sample();
}

public enum VmTone { Running, Off, Parked, Busy }
public enum HyperVRowKind { Header, Vm, Note }
public sealed record HyperVRow(HyperVRowKind Kind, string Text, HyperVVm Vm = null);

public static class HyperVFormat
{
    /// <summary>Msvm_ComputerSystem EnabledState codes to a short label and a colour family.</summary>
    public static (string Label, VmTone Tone) State(int code) => code switch
    {
        2 => ("Running", VmTone.Running),
        3 => ("Off", VmTone.Off),
        6 or 32769 => ("Saved", VmTone.Parked),
        9 or 32768 => ("Paused", VmTone.Parked),
        32770 => ("Starting", VmTone.Busy), 32771 => ("Checkpoint", VmTone.Busy), 32773 => ("Saving", VmTone.Busy),
        32774 => ("Stopping", VmTone.Busy), 32776 => ("Pausing", VmTone.Busy), 32777 => ("Resuming", VmTone.Busy),
        _ => ("Unknown", VmTone.Busy),
    };

    /// <summary>Rows under the Hyper-V graph - shared by Engine (height) and the renderer (drawing), like IisFormat.Rows.</summary>
    public static List<HyperVRow> Rows(PanelConfig pc, HyperVStatus s)
    {
        var rows = new List<HyperVRow>();
        if (s == null || !s.Available) { rows.Add(new HyperVRow(HyperVRowKind.Note, s == null || s.Note == "" ? "Waiting for Hyper-V data" : s.Note)); return rows; }
        var shown = s.Vms.Where(v => pc.HvShowOff || v.State == 2).ToList();
        int max = Math.Max(1, pc.HvMaxRows), running = s.Vms.Count(v => v.State == 2);
        string head = s.FullList ? $"Virtual machines  {running} of {s.Vms.Count} running" : $"Virtual machines  {running} running";
        if (shown.Count > max) head += $"  (showing {max})";
        rows.Add(new HyperVRow(HyperVRowKind.Header, head));
        if (shown.Count == 0) rows.Add(new HyperVRow(HyperVRowKind.Note, s.Vms.Count == 0 ? "No virtual machines" : "None running"));
        rows.AddRange(shown.Take(max).Select(v => new HyperVRow(HyperVRowKind.Vm, v.Name, v)));
        if (!s.FullList && s.Note != "") rows.Add(new HyperVRow(HyperVRowKind.Note, s.Note));
        return rows;
    }

    /// <summary>"VMName:Hv VP 3" -> "VMName" (a VM name may itself contain colons, so split at the last ":Hv VP").</summary>
    public static string VmOfProcessor(string instance)
    {
        int i = instance.LastIndexOf(":Hv VP", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? instance.Substring(0, i) : "";
    }

    /// <summary>Merges the WMI VM list (null when denied) with the per-VM counters. Without WMI the list is just the
    /// VMs the counters report, i.e. the running ones.</summary>
    public static HyperVStatus Merge(List<(string Name, int State, double UptimeMs)> wmi, string wmiError, Dictionary<string, double> vpRun, Dictionary<string, double> memMB, double hostCpu)
    {
        var cpu = vpRun.Where(kv => !kv.Key.StartsWith("_Total", StringComparison.OrdinalIgnoreCase))
                       .GroupBy(kv => VmOfProcessor(kv.Key), StringComparer.OrdinalIgnoreCase).Where(g => g.Key != "")
                       .ToDictionary(g => g.Key, g => g.Average(kv => kv.Value), StringComparer.OrdinalIgnoreCase);
        var s = new HyperVStatus { HostCpuPct = hostCpu };
        if (wmi != null)
        {
            s.FullList = true;
            s.Vms = wmi.Select(v => new HyperVVm(v.Name, v.State, v.State == 2 ? cpu.GetValueOrDefault(v.Name, double.NaN) : double.NaN,
                v.State == 2 ? memMB.GetValueOrDefault(v.Name, double.NaN) : double.NaN, v.UptimeMs > 0 ? TimeSpan.FromMilliseconds(v.UptimeMs) : null)).ToList();
        }
        else
        {
            s.Note = wmiError;
            var names = cpu.Keys.Concat(memMB.Keys.Where(k => !k.StartsWith("_Total", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase);
            s.Vms = names.Select(n => new HyperVVm(n, 2, cpu.GetValueOrDefault(n, double.NaN), memMB.GetValueOrDefault(n, double.NaN), null)).ToList();
        }
        s.Vms = s.Vms.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        s.Available = s.FullList || s.Vms.Count > 0 || !double.IsNaN(hostCpu);
        if (!s.Available && s.Note == "") s.Note = "Hyper-V data unavailable";
        return s;
    }
}

/// <summary>Late-bound WMI through the scripting COM API that ships with Windows - no System.Management package.
/// Uses Count/ItemIndex (rewindable result set) rather than COM enumeration.</summary>
public static class Wmi
{
    public static List<Dictionary<string, object>> Query(string ns, string wql, params string[] props)
    {
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new InvalidOperationException("WMI scripting is not available");
        dynamic locator = Activator.CreateInstance(type);
        dynamic service = locator.ConnectServer(".", ns);
        dynamic set = service.ExecQuery(wql);
        var rows = new List<Dictionary<string, object>>();
        int count = set.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic obj = set.ItemIndex(i);
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in props) row[p] = obj.Properties_.Item(p).Value;
            rows.Add(row);
        }
        return rows;   // ponytail: COM objects are left to the GC; fine at one query per 10 s, release explicitly if that rate ever rises
    }
}

/// <summary>Hyper-V host view. VM list, state and uptime from WMI (root\virtualization\v2), refreshed every 10 s on
/// a background thread because a WMI round trip is too slow for the UI tick; per-VM CPU, assigned memory and host
/// CPU from the hypervisor's performance counters every tick. WMI there needs admin or Hyper-V Administrators -
/// without it the panel still shows running VMs from the counters and says what's missing. Not verified against a
/// live Hyper-V host on the dev box (no Hyper-V installed there).</summary>
public sealed class PdhHyperVProvider : IHyperVProvider
{
    const string VpRun = @"\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time";
    const string Mem = @"\Hyper-V Dynamic Memory VM(*)\Physical Memory";
    const string Host = @"\Hyper-V Hypervisor Logical Processor(_Total)\% Total Run Time";
    IntPtr query, vp, mem, host;
    long nextOpenMs, nextWmiMs;
    volatile Task wmiTask;
    volatile List<(string, int, double)> wmiVms;
    volatile string wmiError = "Reading VM list...";
    string loggedWmiError = "";

    public bool Installed { get; } = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vmms") != null;

    void OpenCounters()
    {
        if (query != IntPtr.Zero || Environment.TickCount64 < nextOpenMs) return;
        nextOpenMs = Environment.TickCount64 + 30_000;
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
        // Each counter independently: a host without dynamic-memory VMs may lack one set without losing the others.
        uint e1 = Native.PdhAddEnglishCounterW(query, VpRun, IntPtr.Zero, out vp); if (e1 != 0) vp = IntPtr.Zero;
        uint e2 = Native.PdhAddEnglishCounterW(query, Mem, IntPtr.Zero, out mem); if (e2 != 0) mem = IntPtr.Zero;
        uint e3 = Native.PdhAddEnglishCounterW(query, Host, IntPtr.Zero, out host); if (e3 != 0) host = IntPtr.Zero;
        if (e1 != 0 || e2 != 0 || e3 != 0) AppLog.Write($"Hyper-V counters: vp=0x{e1:X8} mem=0x{e2:X8} host=0x{e3:X8}");
        Native.PdhCollectQueryData(query);
    }

    void StartWmiRefresh()
    {
        if (Environment.TickCount64 < nextWmiMs || wmiTask is { IsCompleted: false }) return;
        nextWmiMs = Environment.TickCount64 + 10_000;
        wmiTask = Task.Run(() =>
        {
            try
            {
                var rows = Wmi.Query(@"root\virtualization\v2", "SELECT Name, ElementName, EnabledState, OnTimeInMilliseconds FROM Msvm_ComputerSystem",
                    "Name", "ElementName", "EnabledState", "OnTimeInMilliseconds");
                // The host itself is also an Msvm_ComputerSystem; VMs are the ones whose Name is a GUID.
                wmiVms = rows.Where(r => Guid.TryParse(Convert.ToString(r["Name"], CultureInfo.InvariantCulture), out _))
                    .Select(r => (Convert.ToString(r["ElementName"], CultureInfo.InvariantCulture) ?? "",
                                  Convert.ToInt32(r["EnabledState"] ?? 0, CultureInfo.InvariantCulture),
                                  r["OnTimeInMilliseconds"] is { } on ? Convert.ToDouble(on, CultureInfo.InvariantCulture) : 0))
                    .ToList();
                wmiError = "";
            }
            catch (Exception ex)
            {
                wmiVms = null;
                bool denied = ex is UnauthorizedAccessException || (ex is System.Runtime.InteropServices.COMException c && (uint)c.HResult == 0x80041003);
                wmiError = denied ? "Off/saved VMs need Hyper-V Administrators" : "VM list unavailable (WMI)";
                string detail = $"Hyper-V WMI: {ex.GetType().Name} 0x{ex.HResult:X8}";
                if (detail != loggedWmiError) { AppLog.Write(detail); loggedWmiError = detail; }
            }
        });
    }

    double Single(IntPtr counter) =>
        counter != IntPtr.Zero && Native.PdhGetFormattedCounterValue(counter, Native.PDH_FMT_DOUBLE, out _, out var v) == 0 && v.Status <= Native.PDH_CSTATUS_NEW_DATA ? v.Value : double.NaN;

    public HyperVStatus Sample()
    {
        if (!Installed) return new HyperVStatus { Note = "Hyper-V not installed" };
        StartWmiRefresh();
        OpenCounters();
        var none = new Dictionary<string, double>();
        bool counters = query != IntPtr.Zero && Native.PdhCollectQueryData(query) == 0;
        return HyperVFormat.Merge(wmiVms, wmiError,
            counters && vp != IntPtr.Zero ? PdhInstances.Read(vp) : none,
            counters && mem != IntPtr.Zero ? PdhInstances.Read(mem) : none,
            counters ? Single(host) : double.NaN);
    }

    public void Dispose() { if (query != IntPtr.Zero) Native.PdhCloseQuery(query); query = IntPtr.Zero; }
}

public sealed class FakeHyperVProvider : IHyperVProvider
{
    public bool Installed { get; set; } = true;
    public HyperVStatus Value = new();
    public HyperVStatus Sample() => Value;
    public void Dispose() { }
}

/// <summary>Graph lines: host CPU (all partitions), then one CPU line per VM.</summary>
public sealed class HyperVSource : PerItemSource
{
    readonly Func<IHyperVProvider> provider;
    public HyperVStatus Status { get; private set; } = new();
    public HyperVSource(Func<IHyperVProvider> provider) { this.provider = provider; }
    public override PanelKind Kind => PanelKind.HyperV;
    public override float MinScale => 100;
    protected override string Prefix => "vm:";
    protected override IReadOnlyList<LineConfig> FixedLines() => new LineConfig[]
    {
        new() { Key = "host", Label = "Host CPU", Color = "#4DE1FF", Width = 1.8f, Glow = true, Fill = true },
    };
    protected override IEnumerable<string> Items => Status.Vms.Select(v => v.Name);
    public override bool Ready => Status.Available;
    public override int RowCount(PanelConfig cfg) => HyperVFormat.Rows(cfg, Status).Count;
    protected override void Refresh() => Status = provider().Sample();
    protected override float Value(string key)
    {
        if (key == "host") return (float)Status.HostCpuPct;
        var vm = Status.Vms.FirstOrDefault(v => ItemKey(v.Name) == key);
        return vm == null ? float.NaN : (float)vm.CpuPct;
    }
    public override string Readout(float[] latest) => !Status.Available ? Status.Note
        : double.IsNaN(Status.HostCpuPct) ? "" : "Host " + Fmt.Pct(Status.HostCpuPct);
    public override string Format(float v) => Fmt.Pct(v);
}
