using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace Glassy.Core;

/// <summary>A time-series data source: one value per line per tick.</summary>
public interface IPanelSource : IDisposable
{
    PanelKind Kind { get; }
    /// <summary>Lines (with default styling) this source produces, in Sample() order.</summary>
    IReadOnlyList<LineConfig> DefaultLines();
    /// <summary>Applies panel-specific settings (adapter, ...). Called whenever config changes.</summary>
    void Configure(PanelConfig cfg);
    /// <summary>Fills one value per line; NaN when unavailable.</summary>
    void Sample(float[] into);
    /// <summary>Header text for the panel, from the latest values.</summary>
    string Readout(float[] latest);
    /// <summary>Formats an axis/current value (units differ per source).</summary>
    string Format(float v);
    /// <summary>Smallest automatic scale ceiling.</summary>
    float MinScale { get; }
    /// <summary>1000 for bit rates and percentages, 1024 for byte counts; keeps auto-scale ceilings round in the units shown.</summary>
    double UnitBase { get; }
    /// <summary>True when the legend should show each line's current value (network, drives) instead of a separate readout.</summary>
    bool LegendWithValues { get; }
}

public static class Fmt
{
    public static string Bytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return (i == 0 ? b.ToString("0", CultureInfo.InvariantCulture) : b.ToString(b >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)) + " " + u[i];
    }
    public static string Bits(double bps)
    {
        string[] u = { "bps", "Kbps", "Mbps", "Gbps" }; int i = 0;
        while (bps >= 1000 && i < u.Length - 1) { bps /= 1000; i++; }
        return bps.ToString(bps >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + u[i];
    }
    public static string Pct(double v) => v.ToString("0", CultureInfo.InvariantCulture) + "%";
    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";

    /// <summary>Like NiceCeil, but rounds within the unit (KB, MB...) implied by unitBase, so 2.1 MB becomes 2.5 MB rather than 2.38 MB.</summary>
    public static double NiceCeilUnits(double v, double unitBase)
    {
        if (v <= 0) return 1;
        double unit = Math.Pow(unitBase, Math.Max(0, Math.Floor(Math.Log(v) / Math.Log(unitBase))));
        return NiceCeil(v / unit) * unit;
    }

    /// <summary>Rounds up to 1, 2, 2.5, 5 or 10 times a power of ten.</summary>
    public static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v))), m = v / p;
        double n = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
        return n * p;
    }

    static readonly (double at, int r, int g, int b)[] LoadStops =
    {
        (0.00, 124, 255, 107), (0.50, 255, 214, 79), (0.75, 255, 168, 60), (0.90, 255, 120, 40), (1.00, 255, 60, 80),
    };

    /// <summary>Load colour ramp: green at 0, yellow at 50%, amber at 75%, orange at 90%, red at 100%.</summary>
    public static (byte r, byte g, byte b) LoadColor(double fraction)
    {
        double f = Math.Clamp(fraction, 0, 1);
        for (int i = 1; i < LoadStops.Length; i++)
        {
            if (f > LoadStops[i].at) continue;
            var (a0, r0, g0, b0) = LoadStops[i - 1]; var (a1, r1, g1, b1) = LoadStops[i]; double t = (f - a0) / (a1 - a0);
            return ((byte)(r0 + (r1 - r0) * t), (byte)(g0 + (g1 - g0) * t), (byte)(b0 + (b1 - b0) * t));
        }
        return (255, 60, 80);
    }

    static readonly (double at, int r, int g, int b)[] DischargeStops =
    {
        (0.00, 255, 214, 79), (0.50, 255, 168, 60), (1.00, 255, 60, 80),
    };

    /// <summary>Discharge colour ramp: yellow at full, orange at half, red at empty. Deliberately excludes green
    /// (LoadColor's zero-stop), since green is reserved for "charging" on the battery graph.</summary>
    public static (byte r, byte g, byte b) DischargeColor(double percent)
    {
        double f = 1 - Math.Clamp(percent / 100.0, 0, 1);
        for (int i = 1; i < DischargeStops.Length; i++)
        {
            if (f > DischargeStops[i].at) continue;
            var (a0, r0, g0, b0) = DischargeStops[i - 1]; var (a1, r1, g1, b1) = DischargeStops[i]; double t = (f - a0) / (a1 - a0);
            return ((byte)(r0 + (r1 - r0) * t), (byte)(g0 + (g1 - g0) * t), (byte)(b0 + (b1 - b0) * t));
        }
        return (255, 60, 80);
    }

    public static string Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c; double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0); else if (h < 120) (r, g, b) = (x, c, 0); else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c); else if (h < 300) (r, g, b) = (x, 0, c); else (r, g, b) = (c, 0, x);
        return $"#{(int)((r + m) * 255):X2}{(int)((g + m) * 255):X2}{(int)((b + m) * 255):X2}";
    }
}

// ---------------------------------------------------------------- CPU
public sealed class CpuSource : IPanelSource
{
    readonly int n; readonly IntPtr buf; readonly Native.ProcPerf[] prev, cur;
    readonly string[] labels; readonly bool[] efficiency; readonly bool hybrid;
    public PanelKind Kind => PanelKind.Cpu;
    public float MinScale => 100;
    public double UnitBase => 1000;
    public bool LegendWithValues => false;
    public int ThreadCount => n;

    public CpuSource()
    {
        n = Environment.ProcessorCount;
        buf = Marshal.AllocHGlobal(n * Marshal.SizeOf<Native.ProcPerf>());
        prev = new Native.ProcPerf[n]; cur = new Native.ProcPerf[n];
        (labels, efficiency, hybrid) = DescribeThreads(n);
        Read(prev);
    }

    /// <summary>Names logical processors (P0a/P0b = the two threads of P-core 0, E0 = E-core 0) from the CPU-set table.</summary>
    static (string[], bool[], bool) DescribeThreads(int n)
    {
        var labels = new string[n]; var eff = new bool[n];
        for (int i = 0; i < n; i++) labels[i] = "CPU " + i;
        Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint need, IntPtr.Zero, 0);
        if (need == 0) return (labels, eff, false);
        IntPtr p = Marshal.AllocHGlobal((int)need);
        try
        {
            if (!Native.GetSystemCpuSetInformation(p, need, out uint got, IntPtr.Zero, 0)) return (labels, eff, false);
            var rows = new List<(int lp, int core, int cls)>();
            for (int off = 0; off < got;)
            {
                int size = Marshal.ReadInt32(p, off), type = Marshal.ReadInt32(p, off + 4);
                if (size <= 0) break;
                if (type == 0) rows.Add((Marshal.ReadByte(p, off + 14), Marshal.ReadByte(p, off + 15), Marshal.ReadByte(p, off + 18)));
                off += size;
            }
            int maxCls = rows.Count == 0 ? 0 : rows.Max(r => r.cls), minCls = rows.Count == 0 ? 0 : rows.Min(r => r.cls);
            bool hybrid = maxCls != minCls;
            foreach (var r in rows.OrderBy(r => r.lp))
            {
                if (r.lp >= n) continue;
                bool isE = hybrid && r.cls == minCls; eff[r.lp] = isE;
                int smt = rows.Count(o => o.core == r.core && o.cls == r.cls && o.lp < r.lp);
                // Windows numbers a core by its first logical processor (0, 2, 4...); rank it within its class instead (0, 1, 2...).
                int rank = rows.Where(o => o.cls == r.cls).Select(o => o.core).Distinct().OrderBy(c => c).ToList().IndexOf(r.core);
                labels[r.lp] = isE ? $"E{rank}" : hybrid ? $"P{rank}{(char)('a' + smt)}" : $"CPU {r.lp}";
            }
            return (labels, eff, hybrid);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    void Read(Native.ProcPerf[] into)
    {
        int size = Marshal.SizeOf<Native.ProcPerf>();
        if (Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation, buf, n * size, out _) != 0) return;
        for (int i = 0; i < n; i++) into[i] = Marshal.PtrToStructure<Native.ProcPerf>(buf + i * size);
    }

    public IReadOnlyList<LineConfig> DefaultLines()
    {
        var l = new List<LineConfig> { new() { Key = "total", Label = "Total", Color = "#FF3CE6", Width = 2.2f, Glow = true, Fill = true } };
        int p = 0, e = 0, pTotal = efficiency.Count(x => !x), eTotal = n - pTotal;
        for (int i = 0; i < n; i++)
        {
            bool isE = efficiency[i]; int k = isE ? e++ : p++; int of = isE ? eTotal : pTotal;
            double t = of <= 1 ? 0 : (double)k / (of - 1);
            l.Add(new LineConfig
            {
                Key = "cpu" + i, Label = labels[i], Width = 1f,
                Color = isE ? Fmt.Hsv(190 + 40 * t, 0.65, 1) : Fmt.Hsv(15 + 45 * t, 0.75, 1),
            });
        }
        return l;
    }

    public void Configure(PanelConfig cfg) { }

    public void Sample(float[] into)
    {
        Read(cur); double tIdle = 0, tAll = 0;
        for (int i = 0; i < n; i++)
        {
            double idle = cur[i].Idle - prev[i].Idle, all = (cur[i].Kernel - prev[i].Kernel) + (cur[i].User - prev[i].User);
            tIdle += idle; tAll += all;
            into[1 + i] = all <= 0 ? 0 : (float)Math.Clamp(100 * (1 - idle / all), 0, 100);
            prev[i] = cur[i];
        }
        into[0] = tAll <= 0 ? 0 : (float)Math.Clamp(100 * (1 - tIdle / tAll), 0, 100);
    }

    public string Readout(float[] l) => float.IsNaN(l[0]) ? "" : Fmt.Pct(l[0]) + "  " + n + " threads";
    public string Format(float v) => Fmt.Pct(v);
    public void Dispose() => Marshal.FreeHGlobal(buf);
}

// ---------------------------------------------------------------- RAM
public sealed class RamSource : IPanelSource
{
    public PanelKind Kind => PanelKind.Ram;
    public float MinScale => 100;
    public double UnitBase => 1000;
    public bool LegendWithValues => false;
    string readout = "";
    public IReadOnlyList<LineConfig> DefaultLines() => new LineConfig[]
    {
        new() { Key = "used", Label = "In use", Color = "#7CFF6B", Width = 2f, Glow = true, Fill = true },
        new() { Key = "commit", Label = "Committed", Color = "#FFC857", Width = 1.2f },
    };
    public void Configure(PanelConfig cfg) { }
    public void Sample(float[] into)
    {
        var s = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (!Native.GlobalMemoryStatusEx(ref s)) { into[0] = into[1] = float.NaN; return; }
        into[0] = s.MemoryLoad;
        into[1] = s.TotalPageFile == 0 ? 0 : (float)(100.0 * (s.TotalPageFile - s.AvailPageFile) / s.TotalPageFile);
        readout = $"{Fmt.Bytes(s.TotalPhys - s.AvailPhys)} / {Fmt.Bytes(s.TotalPhys)}";
    }
    public string Readout(float[] l) => readout;
    public string Format(float v) => Fmt.Pct(v);
    public void Dispose() { }
}

// ---------------------------------------------------------------- GPU (NVIDIA via NVML)
public sealed class GpuSource : IPanelSource
{
    IntPtr dev; string name = ""; string readout = "";
    public bool Available { get; }
    public PanelKind Kind => PanelKind.Gpu;
    public float MinScale => 100;
    public double UnitBase => 1000;
    public bool LegendWithValues => false;

    public GpuSource()
    {
        try
        {
            if (Native.nvmlInit_v2() != 0 || Native.nvmlDeviceGetHandleByIndex_v2(0, out dev) != 0) return;
            var sb = new StringBuilder(96); if (Native.nvmlDeviceGetName(dev, sb, 96) == 0) name = sb.ToString().Replace("NVIDIA ", "").Replace("GeForce ", "");
            Available = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { Available = false; }
    }

    public IReadOnlyList<LineConfig> DefaultLines() => new LineConfig[]
    {
        new() { Key = "gpu", Label = "Core", Color = "#4DA6FF", Width = 2f, Glow = true, Fill = true },
        new() { Key = "vram", Label = "VRAM", Color = "#FF7AD9", Width = 1.5f },
        new() { Key = "enc", Label = "Video encode", Color = "#FFD166", Width = 1f, Visible = false },
        new() { Key = "dec", Label = "Video decode", Color = "#06D6A0", Width = 1f, Visible = false },
    };
    public void Configure(PanelConfig cfg) { }

    public void Sample(float[] into)
    {
        Array.Fill(into, float.NaN);
        if (!Available) { readout = "No NVIDIA GPU / NVML"; return; }
        if (Native.nvmlDeviceGetUtilizationRates(dev, out var u) == 0) into[0] = u.Gpu;
        string vram = "";
        if (Native.nvmlDeviceGetMemoryInfo(dev, out var m) == 0 && m.Total > 0) { into[1] = 100f * m.Used / m.Total; vram = $"  VRAM {Fmt.Bytes(m.Used)}/{Fmt.Bytes(m.Total)}"; }
        if (Native.nvmlDeviceGetEncoderUtilization(dev, out uint e, out _) == 0) into[2] = e;
        if (Native.nvmlDeviceGetDecoderUtilization(dev, out uint d, out _) == 0) into[3] = d;
        string extra = "";
        if (Native.nvmlDeviceGetPowerUsage(dev, out uint mw) == 0) extra += $"  {mw / 1000.0:0} W";
        if (Native.nvmlDeviceGetClockInfo(dev, 0, out uint mhz) == 0) extra += $"  {mhz} MHz";
        readout = (extra + vram).Trim();
    }
    public string Readout(float[] l) => readout;
    public string Format(float v) => Fmt.Pct(v);
    public void Dispose() { }
}

// ---------------------------------------------------------------- Battery
/// <summary>Two mutually-exclusive lines (percent while charging, percent while discharging - the other is NaN
/// and SeriesBuffer.Push skips NaN) so the renderer can tell, per history cell, which colour the graph should be
/// without a separate ring: whichever of the pair is non-NaN. Both lines default to invisible - the renderer draws
/// the graph itself, coloured per point, rather than through the generic single-colour-per-line pipeline.</summary>
public sealed class BatterySource : IPanelSource
{
    readonly Func<BatteryStatus?> read;
    string readout = "";
    public BatterySource(Func<BatteryStatus?> read) { this.read = read; }
    public PanelKind Kind => PanelKind.Battery;
    public float MinScale => 100;
    public double UnitBase => 1000;
    public bool LegendWithValues => false;
    public IReadOnlyList<LineConfig> DefaultLines() => new LineConfig[]
    {
        new() { Key = "charge", Label = "Charging", Color = "#7CFF6B", Width = 2f, Visible = false },
        new() { Key = "discharge", Label = "Discharging", Color = "#FFA83C", Width = 2f, Visible = false },
    };
    public void Configure(PanelConfig cfg) { }
    public void Sample(float[] into)
    {
        var s = read();
        if (s == null || !s.Value.Present) { into[0] = into[1] = float.NaN; readout = ""; return; }
        var v = s.Value;
        into[0] = v.Charging ? (float)v.Percent : float.NaN;
        into[1] = !v.Charging ? (float)v.Percent : float.NaN;
        string state = v.Charging ? "Charging" : v.OnAc ? "On AC" : "On battery";
        string time = v.TimeRemaining is { } t ? "  " + Fmt.Uptime(t) + (v.Charging ? " to full" : " remaining") : "";
        readout = Fmt.Pct(v.Percent) + "  " + state + time;
    }
    public string Readout(float[] l) => readout;
    public string Format(float v) => Fmt.Pct(v);
    public void Dispose() { }
}

// ---------------------------------------------------------------- Non-series data
public sealed class ProcRow { public string Name = ""; public double Cpu; public long Mem; public int Instances; }

/// <summary>Per-process CPU and memory from one NtQuerySystemInformation call (no per-process handles, so no access-denied gaps).</summary>
public sealed class ProcessSampler : IDisposable
{
    // x64 SYSTEM_PROCESS_INFORMATION field offsets.
    const int OffNext = 0, OffPrivWs = 8, OffCreate = 32, OffUser = 40, OffKernel = 48, OffNameLen = 56, OffNamePtr = 64, OffPid = 80;
    IntPtr buf = Marshal.AllocHGlobal(1 << 20); int cap = 1 << 20;
    Dictionary<(long pid, long create), long> prev = new(); long prevTicks;

    /// <summary>Queries the full process snapshot into buf, growing it as needed. The process list can grow between
    /// calls (and has, on this dev machine, past the original 1 MB guess), so every caller must retry through this,
    /// not just Sample() - a one-shot query that returns null on STATUS_INFO_LENGTH_MISMATCH silently "loses" real
    /// processes once the system has enough of them, exactly the failure ProbePrivateWs had.</summary>
    void Query()
    {
        int ret;
        while (Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, cap, out ret) == Native.STATUS_INFO_LENGTH_MISMATCH)
        { Marshal.FreeHGlobal(buf); cap = Math.Max(cap * 2, ret + (1 << 16)); buf = Marshal.AllocHGlobal(cap); }
    }

    public List<ProcRow> Sample(int top, bool byMemory)
    {
        Query();
        long now = Stopwatch.GetTimestamp(); double dt100ns = prevTicks == 0 ? 0 : (now - prevTicks) / (double)Stopwatch.Frequency * 1e7;
        var cur = new Dictionary<(long, long), long>(prev.Count + 16); var byName = new Dictionary<string, ProcRow>(StringComparer.OrdinalIgnoreCase);
        for (int off = 0; ;)
        {
            long pid = Marshal.ReadIntPtr(buf, off + OffPid).ToInt64();
            if (pid != 0)
            {
                long create = Marshal.ReadInt64(buf, off + OffCreate), cpu = Marshal.ReadInt64(buf, off + OffUser) + Marshal.ReadInt64(buf, off + OffKernel);
                cur[(pid, create)] = cpu;
                double pct = dt100ns > 0 && prev.TryGetValue((pid, create), out long p0) ? Math.Max(0, cpu - p0) / dt100ns / Environment.ProcessorCount * 100 : 0;
                int len = (ushort)Marshal.ReadInt16(buf, off + OffNameLen);
                string name = len == 0 ? (pid == 4 ? "System" : "?") : Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, off + OffNamePtr), len / 2);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (!byName.TryGetValue(name, out var row)) byName[name] = row = new ProcRow { Name = name };
                row.Cpu += pct; row.Mem += Marshal.ReadInt64(buf, off + OffPrivWs); row.Instances++;
            }
            int next = Marshal.ReadInt32(buf, off + OffNext); if (next == 0) break; off += next;
        }
        prev = cur; prevTicks = now;
        return (byMemory ? byName.Values.OrderByDescending(r => r.Mem) : byName.Values.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.Mem)).Take(top).ToList();
    }

    /// <summary>Test hook: private working set of one process id.</summary>
    internal long? ProbePrivateWs(int pidWanted)
    {
        Query();
        for (int off = 0; ;)
        {
            if (Marshal.ReadIntPtr(buf, off + OffPid).ToInt64() == pidWanted) return Marshal.ReadInt64(buf, off + OffPrivWs);
            int next = Marshal.ReadInt32(buf, off + OffNext); if (next == 0) return null; off += next;
        }
    }
    public void Dispose() { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
}
