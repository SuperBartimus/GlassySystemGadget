using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Glassy.Core;

// ---------------------------------------------------------------- Network
public interface INetProvider : IDisposable
{
    /// <summary>Every adapter that could be shown (loopback and tunnels excluded), by name.</summary>
    IReadOnlyList<string> Adapters { get; }
    /// <summary>Bits per second summed over the physical adapters that are up.</summary>
    (float down, float up) Combined { get; }
    /// <summary>Rates for a tracked adapter; false when the adapter is down or not tracked.</summary>
    bool TryGet(string adapter, out float down, out float up);
    /// <summary>Adapters to report individually (in addition to the combined figure).</summary>
    void Track(IEnumerable<string> names);
    void Update();
}

public sealed class NetHub : INetProvider
{
    readonly Dictionary<string, (long rx, long tx)> prev = new();
    Dictionary<string, (float d, float u)> rates = new();
    HashSet<string> tracked = new();
    List<NetworkInterface> nics = new(); DateTime nicsAt = DateTime.MinValue;
    long prevTicks; float combDown, combUp;
    public IReadOnlyList<string> Adapters { get; private set; } = Array.Empty<string>();
    public (float down, float up) Combined => (combDown, combUp);
    public void Track(IEnumerable<string> names) => tracked = new HashSet<string>(names);
    public bool TryGet(string adapter, out float down, out float up)
    {
        if (rates.TryGetValue(adapter, out var r)) { down = r.d; up = r.u; return true; }
        down = up = 0; return false;
    }

    static bool IsCandidate(NetworkInterface n) => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel;

    /// <summary>Virtual switches, WSL and VM adapters double-count traffic that a physical adapter already carries.</summary>
    static bool IsVirtual(NetworkInterface n)
    {
        var d = n.Description + " " + n.Name;
        return new[] { "vEthernet", "Hyper-V", "Virtual", "WSL", "VMware", "VirtualBox", "Loopback" }.Any(k => d.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    void Refresh()
    {
        if (DateTime.UtcNow - nicsAt < TimeSpan.FromSeconds(30)) return;
        nicsAt = DateTime.UtcNow;
        nics = NetworkInterface.GetAllNetworkInterfaces().Where(IsCandidate).ToList();
        var names = nics.Select(n => n.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.SequenceEqual(Adapters)) Adapters = names;
    }

    public void Update()
    {
        Refresh();
        long now = Stopwatch.GetTimestamp(); double dt = prevTicks == 0 ? 0 : (now - prevTicks) / (double)Stopwatch.Frequency;
        var fresh = new Dictionary<string, (float, float)>(); float cd = 0, cu = 0;
        foreach (var n in nics)
        {
            if (n.OperationalStatus != OperationalStatus.Up) { prev.Remove(n.Name); continue; }
            bool physical = !IsVirtual(n), want = tracked.Contains(n.Name);
            if (!physical && !want) continue;
            try
            {
                var s = n.GetIPStatistics(); long rx = s.BytesReceived, tx = s.BytesSent; float d = 0, u = 0;
                if (dt > 0 && prev.TryGetValue(n.Name, out var p) && rx >= p.rx && tx >= p.tx) { d = (float)((rx - p.rx) * 8 / dt); u = (float)((tx - p.tx) * 8 / dt); }
                prev[n.Name] = (rx, tx);
                if (physical) { cd += d; cu += u; }
                if (want) fresh[n.Name] = (d, u);
            }
            catch (NetworkInformationException) { nicsAt = DateTime.MinValue; }
        }
        rates = fresh; combDown = cd; combUp = cu; prevTicks = now;
    }
    public void Dispose() { }
}

/// <summary>One graph's view of the network hub: the combined figure (adapter == null) or a single adapter.</summary>
public sealed class NetView : IPanelSource
{
    readonly INetProvider hub; readonly string adapter;
    public NetView(INetProvider hub, string adapter) { this.hub = hub; this.adapter = adapter; }
    public string Adapter => adapter;
    public PanelKind Kind => PanelKind.Network;
    public float MinScale => 1_000_000;   // 1 Mbps
    public double UnitBase => 1000;
    public bool LegendWithValues => true;
    public IReadOnlyList<LineConfig> DefaultLines() => new LineConfig[]
    {
        new() { Key = "down", Label = "Download", Color = "#4DE1FF", Width = 1.8f, Glow = true, Fill = true },
        new() { Key = "up", Label = "Upload", Color = "#FF3CE6", Width = 1.8f, Glow = true },
    };
    public void Configure(PanelConfig cfg) { }
    public void Sample(float[] into)
    {
        if (hub == null) { into[0] = into[1] = float.NaN; return; }
        if (adapter == null) { var c = hub.Combined; into[0] = c.down; into[1] = c.up; }
        else { hub.TryGet(adapter, out float d, out float u); into[0] = d; into[1] = u; }   // a down adapter reads as zero
    }
    public string Readout(float[] latest) => "";
    public string Format(float v) => Fmt.Bits(v);
    public void Dispose() { }
}

// ---------------------------------------------------------------- Per-drive activity (PDH LogicalDisk)
public sealed class DriveActivityHub : IDisposable
{
    IntPtr query; readonly Dictionary<string, (IntPtr read, IntPtr write)> counters = new(); readonly Dictionary<string, (float r, float w)> values = new();
    readonly Dictionary<string, int> emptyStreak = new();   // consecutive samples with no data
    const int DeadAfter = 4;

    public DriveActivityHub()
    {
        try { if (Native.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero; }
        catch (DllNotFoundException) { query = IntPtr.Zero; }
    }

    /// <summary>Makes the set of counters match the given drive letters (no colon). Letters without a LogicalDisk instance are skipped.</summary>
    public void Sync(IEnumerable<string> letters)
    {
        if (query == IntPtr.Zero) return;
        var want = new HashSet<string>(letters, StringComparer.OrdinalIgnoreCase);
        foreach (var l in counters.Keys.Where(l => !want.Contains(l)).ToList())
        {
            Native.PdhRemoveCounter(counters[l].read); Native.PdhRemoveCounter(counters[l].write); counters.Remove(l); values.Remove(l); emptyStreak.Remove(l);
        }
        foreach (var l in want.Where(l => !counters.ContainsKey(l)))
        {
            if (Native.PdhAddEnglishCounterW(query, $@"\LogicalDisk({l}:)\Disk Read Bytes/sec", IntPtr.Zero, out var r) != 0) continue;
            if (Native.PdhAddEnglishCounterW(query, $@"\LogicalDisk({l}:)\Disk Write Bytes/sec", IntPtr.Zero, out var w) != 0) { Native.PdhRemoveCounter(r); continue; }
            counters[l] = (r, w); emptyStreak[l] = 0;
        }
    }

    static float One(IntPtr c) =>
        Native.PdhGetFormattedCounterValue(c, Native.PDH_FMT_DOUBLE, out _, out var v) == 0 && v.Status == 0 ? (float)Math.Max(0, v.Value) : float.NaN;

    public void Update()
    {
        if (query == IntPtr.Zero || counters.Count == 0) return;
        if (Native.PdhCollectQueryData(query) != 0) return;
        foreach (var kv in counters)
        {
            float r = One(kv.Value.read), w = One(kv.Value.write); values[kv.Key] = (r, w);
            emptyStreak[kv.Key] = float.IsNaN(r) && float.IsNaN(w) ? emptyStreak[kv.Key] + 1 : 0;
        }
    }

    /// <summary>
    /// True while this drive letter is producing activity data. Windows accepts a counter path even for a drive that has no counters
    /// (CD drives, some volumes), so a drive is judged by its data: a few empty samples in a row and it counts as having none.
    /// </summary>
    public bool Has(string letter) => counters.ContainsKey(letter) && emptyStreak[letter] < DeadAfter;

    public bool TryGet(string letter, out float read, out float write)
    {
        if (values.TryGetValue(letter, out var v)) { read = v.r; write = v.w; return true; }
        read = write = float.NaN; return false;
    }

    public void Dispose() { if (query != IntPtr.Zero) { Native.PdhCloseQuery(query); query = IntPtr.Zero; } }
}

public sealed class DriveView : IPanelSource
{
    readonly DriveActivityHub hub; readonly string letter;
    public DriveView(DriveActivityHub hub, string letter) { this.hub = hub; this.letter = letter; }
    public string Letter => letter;
    public PanelKind Kind => PanelKind.Drives;
    public float MinScale => 1024 * 1024;   // 1 MB/s
    public double UnitBase => 1024;
    public bool LegendWithValues => true;
    public IReadOnlyList<LineConfig> DefaultLines() => new LineConfig[]
    {
        new() { Key = "read", Label = "Read", Color = "#7CFF6B", Width = 1.5f, Glow = true, Fill = true },
        new() { Key = "write", Label = "Write", Color = "#FFC857", Width = 1.5f, Glow = true },
    };
    public void Configure(PanelConfig cfg) { }
    public void Sample(float[] into)
    {
        if (hub != null && hub.TryGet(letter, out float r, out float w)) { into[0] = r; into[1] = w; } else { into[0] = into[1] = float.NaN; }
    }
    public string Readout(float[] latest) => "";
    public string Format(float v) => Fmt.Bytes(v) + "/s";
    public void Dispose() { }
}
