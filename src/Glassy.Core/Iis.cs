using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glassy.Core;

public sealed record IisSite(string Name, double Connections, double RequestsPerSec);
/// <summary>State is the raw APP_POOL_WAS "Current Application Pool State" code - see IisFormat.PoolState.</summary>
public sealed record IisPool(string Name, int State);

public enum IisRowKind { Header, Site, Pool, Note }
public sealed record IisRow(IisRowKind Kind, string Text, IisSite Site = null, IisPool Pool = null);

public sealed class IisStatus
{
    /// <summary>False when W3SVC is stopped or its counters can't be read - Note says which.</summary>
    public bool Running;
    public string Note = "";
    public List<IisSite> Sites = new();
    public List<IisPool> Pools = new();
    public double TotalConnections, TotalRequestsPerSec;
}

public interface IIisProvider : IDisposable
{
    /// <summary>Whether the IIS role is installed at all - the panel is only ever shown when this is true.</summary>
    bool Installed { get; }
    /// <summary>Reads fresh values. Rate counters need two reads, so the first one after start reports 0 req/s.</summary>
    IisStatus Sample();
}

public static class IisFormat
{
    /// <summary>APP_POOL_WAS state codes (1-7) to a short label and whether it's healthy (running), stopped, or in between.</summary>
    public static (string Label, int Health) PoolState(int code) => code switch
    {
        3 => ("Running", 1),
        5 => ("Stopped", -1),
        1 => ("Uninitialized", 0), 2 => ("Starting", 0), 4 => ("Stopping", 0), 6 => ("Shutting down", 0), 7 => ("Deleting", 0),
        _ => ("Unknown", 0),
    };

    /// <summary>The rows drawn under the IIS graph, top to bottom - Engine sizes the panel from the count and the
    /// renderer draws exactly these, so the two can't disagree. Overflow past IisMaxRows is folded into the list's
    /// header ("Sites  8 of 12") rather than costing another row.</summary>
    public static List<IisRow> Rows(PanelConfig pc, IisStatus s)
    {
        var rows = new List<IisRow>();
        if (s == null || !s.Running) { rows.Add(new IisRow(IisRowKind.Note, s == null || s.Note == "" ? "Waiting for IIS counters" : s.Note)); return rows; }
        int max = Math.Max(1, pc.IisMaxRows);
        string Head(string name, int n) => n > max ? $"{name}  {max} of {n}" : $"{name}  {n}";
        if (pc.IisShowSites)
        {
            rows.Add(new IisRow(IisRowKind.Header, Head("Sites", s.Sites.Count)));
            if (s.Sites.Count == 0) rows.Add(new IisRow(IisRowKind.Note, "No started sites"));
            rows.AddRange(s.Sites.Take(max).Select(x => new IisRow(IisRowKind.Site, x.Name, Site: x)));
        }
        if (pc.IisShowPools)
        {
            rows.Add(new IisRow(IisRowKind.Header, Head("App pools", s.Pools.Count)));
            if (s.Pools.Count == 0) rows.Add(new IisRow(IisRowKind.Note, "No app pools reported"));
            rows.AddRange(s.Pools.Take(max).Select(x => new IisRow(IisRowKind.Pool, x.Name, Pool: x)));
        }
        return rows;
    }

    /// <summary>Counter instance names to show: drops the _Total aggregate, sorted by name so rows don't jump around.</summary>
    public static List<string> Instances(IEnumerable<string> names) =>
        names.Where(n => !n.StartsWith("_Total", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Reads every instance of one wildcard PDH counter ("\Web Service(*)\Current Connections") in one call.</summary>
public static class PdhInstances
{
    /// <summary>The name a thing goes by in counter instance names: PDH rewrites "(" ")" as "[" "]" and "#" "/" "\" as "_",
    /// so a VM "Helvm (Ubuntu)" is "Helvm [Ubuntu]" there and would never match the WMI or registry name as-is.</summary>
    public static string CounterName(string name) =>
        name.Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('/', '_').Replace('\\', '_');

    /// <summary>One non-wildcard counter's value, or NaN when the counter is missing or has no valid reading yet.</summary>
    public static double Single(IntPtr counter) =>
        counter != IntPtr.Zero && Native.PdhGetFormattedCounterValue(counter, Native.PDH_FMT_DOUBLE, out _, out var v) == 0 && v.Status <= Native.PDH_CSTATUS_NEW_DATA ? v.Value : double.NaN;

    public static Dictionary<string, double> Read(IntPtr counter)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        uint size = 0, count = 0;
        if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero) != Native.PDH_MORE_DATA || size == 0) return result;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(counter, Native.PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return result;
            int stride = Marshal.SizeOf<Native.PdhItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Native.PdhItem>(buf + i * stride);
                if (item.Value.Status > Native.PDH_CSTATUS_NEW_DATA) continue;   // not valid (yet) - e.g. a rate counter's first read
                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                result[name] = Math.Max(0, item.Value.Value);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }
}

/// <summary>IIS via its own performance counters - readable without admin, unlike applicationHost.config or the
/// WebAdministration WMI namespace. Sites come from "Web Service" instances (only sites W3SVC has started) and pools
/// from "APP_POOL_WAS". Not verified against a live IIS server on the dev box (no IIS installed there).</summary>
public sealed class PdhIisProvider : IIisProvider
{
    const string Conn = @"\Web Service(*)\Current Connections", Req = @"\Web Service(*)\Total Method Requests/sec", Pool = @"\APP_POOL_WAS(*)\Current Application Pool State";
    IntPtr query, conn, req, pool;
    long nextOpenMs;
    uint lastError;

    public bool Installed { get; } = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W3SVC") != null;

    bool Open()
    {
        if (query != IntPtr.Zero) return true;
        if (Environment.TickCount64 < nextOpenMs) return false;
        nextOpenMs = Environment.TickCount64 + 30_000;   // W3SVC stopped (no counters yet): retry twice a minute, not every tick
        uint err = Native.PdhOpenQueryW(null, IntPtr.Zero, out query);
        if (err == 0) err = Native.PdhAddEnglishCounterW(query, Conn, IntPtr.Zero, out conn);
        if (err == 0) err = Native.PdhAddEnglishCounterW(query, Req, IntPtr.Zero, out req);
        if (err == 0) err = Native.PdhAddEnglishCounterW(query, Pool, IntPtr.Zero, out pool);
        if (err == 0) err = Native.PdhCollectQueryData(query);
        if (err != 0)
        {
            if (err != lastError) AppLog.Write($"IIS counters unavailable: PDH 0x{err:X8}");   // once per distinct error, for diagnosing a real server
            lastError = err; Close(); return false;
        }
        lastError = 0;
        return true;
    }

    void Close() { if (query != IntPtr.Zero) Native.PdhCloseQuery(query); query = conn = req = pool = IntPtr.Zero; }

    public IisStatus Sample()
    {
        var s = new IisStatus();
        if (!Installed) { s.Note = "IIS not installed"; return s; }
        if (!Open() || Native.PdhCollectQueryData(query) != 0)
        {
            Close(); s.Note = lastError != 0 ? $"IIS counters unreadable (PDH 0x{lastError:X8})" : "W3SVC not running"; return s;
        }
        var c = PdhInstances.Read(conn); var r = PdhInstances.Read(req); var p = PdhInstances.Read(pool);
        if (c.Count == 0 && p.Count == 0) { s.Note = "W3SVC not running"; return s; }
        s.Running = true;
        s.Sites = IisFormat.Instances(c.Keys).Select(n => new IisSite(n, c[n], r.GetValueOrDefault(n))).ToList();
        s.Pools = IisFormat.Instances(p.Keys).Select(n => new IisPool(n, (int)p[n])).ToList();
        s.TotalConnections = c.GetValueOrDefault("_Total", s.Sites.Sum(x => x.Connections));
        s.TotalRequestsPerSec = r.GetValueOrDefault("_Total", s.Sites.Sum(x => x.RequestsPerSec));
        return s;
    }

    public void Dispose() => Close();
}

/// <summary>Fixed reading for tests and --demo (the dev box has no IIS).</summary>
public sealed class FakeIisProvider : IIisProvider
{
    public bool Installed { get; set; } = true;
    public IisStatus Value = new();
    public IisStatus Sample() => Value;
    public void Dispose() { }
}

/// <summary>Graph lines: total requests/sec, total connections, then one requests/sec line per site.
/// The site/pool rows under the graph come from Status.</summary>
public sealed class IisSource : PerItemSource
{
    readonly Func<IIisProvider> provider;
    public IisStatus Status { get; private set; } = new();
    public IisSource(Func<IIisProvider> provider) { this.provider = provider; }
    public override PanelKind Kind => PanelKind.Iis;
    public override float MinScale => 10;
    protected override string Prefix => "site:";
    protected override IReadOnlyList<LineConfig> FixedLines() => new LineConfig[]
    {
        new() { Key = "req", Label = "Req/s", Color = "#4DE1FF", Width = 1.8f, Glow = true, Fill = true },
        new() { Key = "conn", Label = "Connections", Color = "#FFB463", Width = 1.5f },
    };
    protected override IEnumerable<string> Items => Status.Sites.Select(s => s.Name);
    public override bool Ready => Status.Running;
    public override int RowCount(PanelConfig cfg) => IisFormat.Rows(cfg, Status).Count;
    protected override void Refresh() => Status = provider().Sample();
    protected override float Value(string key)
    {
        if (!Status.Running) return float.NaN;
        if (key == "req") return (float)Status.TotalRequestsPerSec;
        if (key == "conn") return (float)Status.TotalConnections;
        var site = Status.Sites.FirstOrDefault(s => ItemKey(s.Name) == key);
        return site == null ? float.NaN : (float)site.RequestsPerSec;
    }
    public override string Readout(float[] latest) => Status.Running ? $"{Status.Sites.Count} sites  {Status.Pools.Count} pools" : Status.Note;
}
