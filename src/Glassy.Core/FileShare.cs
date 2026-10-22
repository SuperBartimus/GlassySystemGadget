using Microsoft.Win32;

namespace Glassy.Core;

/// <summary>Free/Total are NaN when the share's path can't be measured (a missing folder, an unmounted volume).</summary>
public sealed record SmbShare(string Name, string Path, double OpenFiles, double ReadBps, double WriteBps, double Free, double Total)
{
    public double Bps => ReadBps + WriteBps;
}

public enum FileShareRowKind { Header, Share, Note }
public sealed record FileShareRow(FileShareRowKind Kind, string Text, SmbShare Share = null);

public sealed class FileShareStatus
{
    /// <summary>False when the SMB counters can't be read (Server service stopped) - Note says why.</summary>
    public bool Running;
    public string Note = "";
    public List<SmbShare> Shares = new();
    public double Sessions = double.NaN, FilesOpen = double.NaN, ReadBps, WriteBps;
}

public interface IFileShareProvider : IDisposable
{
    /// <summary>Whether this machine shares any folders - the panel is only ever shown when this is true.</summary>
    bool Installed { get; }
    FileShareStatus Sample();
}

public static class FileShareFormat
{
    /// <summary>The rows drawn under the throughput graph; Engine sizes the panel from the same list. Shares past
    /// FsMaxRows fold into the header ("Shares  8 of 12").</summary>
    public static List<FileShareRow> Rows(PanelConfig pc, FileShareStatus s)
    {
        var rows = new List<FileShareRow>();
        if (s == null || !s.Running) { rows.Add(new FileShareRow(FileShareRowKind.Note, s == null || s.Note == "" ? "Waiting for SMB counters" : s.Note)); return rows; }
        int max = Math.Max(1, pc.FsMaxRows), n = s.Shares.Count;
        rows.Add(new FileShareRow(FileShareRowKind.Header, n > max ? $"Shares  {max} of {n}" : $"Shares  {n}"));
        if (n == 0) rows.Add(new FileShareRow(FileShareRowKind.Note, "No shared folders"));
        rows.AddRange(s.Shares.Take(max).Select(x => new FileShareRow(FileShareRowKind.Share, x.Name, x)));
        return rows;
    }

    /// <summary>"\\*\Data" (or "\\HOST\Data" for a scoped share) -> "Data".</summary>
    public static string ShareOfInstance(string instance) => instance.Substring(instance.LastIndexOf('\\') + 1);

    /// <summary>Share name -> path from the LanmanServer\Shares registry values ("Path=D:\Data" inside a multi-string).
    /// Only shares someone created live there - the automatic admin shares (C$, ADMIN$, IPC$) don't.</summary>
    public static Dictionary<string, string> RegistryShares(RegistryKey key)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key == null) return result;
        foreach (var name in key.GetValueNames())
            result[name] = (key.GetValue(name) as string[] ?? Array.Empty<string>())
                .FirstOrDefault(x => x.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))?.Substring(5) ?? "";
        return result;
    }

    /// <summary>One status from the share list and the per-share counter readings (keyed by share name as the counters
    /// spell it - see PdhInstances.CounterName).</summary>
    public static FileShareStatus Merge(Dictionary<string, string> shares, Dictionary<string, (double Free, double Total)> space,
        Dictionary<string, double> open, Dictionary<string, double> read, Dictionary<string, double> write, double sessions, double filesOpen)
    {
        var byShare = (Dictionary<string, double> d) => d
            .GroupBy(kv => ShareOfInstance(kv.Key), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.OrdinalIgnoreCase);
        var o = byShare(open); var r = byShare(read); var w = byShare(write);
        var s = new FileShareStatus { Running = true, Sessions = sessions, FilesOpen = filesOpen };
        // The registry also keeps shares whose folder is gone, which the server isn't actually sharing; the counters list
        // every active share (at 0 when idle), so they decide - unless they list nothing at all.
        s.Shares = shares.Where(kv => o.Count == 0 || o.ContainsKey(PdhInstances.CounterName(kv.Key)))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv =>
        {
            string c = PdhInstances.CounterName(kv.Key);
            var (free, total) = space.TryGetValue(kv.Key, out var sp) ? sp : (double.NaN, double.NaN);
            return new SmbShare(kv.Key, kv.Value, o.GetValueOrDefault(c), r.GetValueOrDefault(c), w.GetValueOrDefault(c), free, total);
        }).ToList();
        s.ReadBps = s.Shares.Sum(x => x.ReadBps); s.WriteBps = s.Shares.Sum(x => x.WriteBps);
        return s;
    }
}

/// <summary>Shares from the registry, activity from the "SMB Server Shares" and "Server" performance counters - no WMI
/// and no admin rights expected (the dev box runs as admin, so non-admin access is unverified). The share list and free
/// space are re-read every 10 s so a new share or a filling disk shows up without a restart.</summary>
public sealed class PdhFileShareProvider : IFileShareProvider
{
    const string SharesKey = @"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares";
    const string Open = @"\SMB Server Shares(*)\Current Open File Count", Read = @"\SMB Server Shares(*)\Read Bytes/sec",
        Write = @"\SMB Server Shares(*)\Write Bytes/sec", Sessions = @"\Server\Server Sessions", Files = @"\Server\Files Open";
    IntPtr query, open, read, write, sessions, files;
    long nextOpenMs, nextListMs;
    uint lastError;
    Dictionary<string, string> shares = new();
    Dictionary<string, (double, double)> space = new();

    public bool Installed { get; } = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer") != null
        && FileShareFormat.RegistryShares(Registry.LocalMachine.OpenSubKey(SharesKey)).Count > 0;

    bool OpenQuery()
    {
        if (query != IntPtr.Zero) return true;
        if (Environment.TickCount64 < nextOpenMs) return false;
        nextOpenMs = Environment.TickCount64 + 30_000;
        uint err = Native.PdhOpenQueryW(null, IntPtr.Zero, out query);
        if (err == 0) err = Native.PdhAddEnglishCounterW(query, Open, IntPtr.Zero, out open);
        if (err == 0) err = Native.PdhAddEnglishCounterW(query, Read, IntPtr.Zero, out read);
        if (err == 0) err = Native.PdhAddEnglishCounterW(query, Write, IntPtr.Zero, out write);
        // The two totals are optional: without them the header just leaves sessions/open files out.
        if (err == 0 && Native.PdhAddEnglishCounterW(query, Sessions, IntPtr.Zero, out sessions) != 0) sessions = IntPtr.Zero;
        if (err == 0 && Native.PdhAddEnglishCounterW(query, Files, IntPtr.Zero, out files) != 0) files = IntPtr.Zero;
        if (err == 0) err = Native.PdhCollectQueryData(query);
        if (err != 0)
        {
            if (err != lastError) AppLog.Write($"File share counters unavailable: PDH 0x{err:X8}");
            lastError = err; Close(); return false;
        }
        lastError = 0;
        return true;
    }

    void Close() { if (query != IntPtr.Zero) Native.PdhCloseQuery(query); query = open = read = write = sessions = files = IntPtr.Zero; }

    void RefreshList()
    {
        if (Environment.TickCount64 < nextListMs) return;
        nextListMs = Environment.TickCount64 + 10_000;
        using (var key = Registry.LocalMachine.OpenSubKey(SharesKey)) shares = FileShareFormat.RegistryShares(key);
        var sp = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in shares)
            if (path != "" && Native.GetDiskFreeSpaceExW(path, out ulong avail, out ulong total, out _) && total > 0) sp[name] = (avail, total);
        space = sp;
    }

    public FileShareStatus Sample()
    {
        RefreshList();
        if (!OpenQuery() || Native.PdhCollectQueryData(query) != 0)
        {
            Close();
            return new FileShareStatus { Note = lastError != 0 ? $"SMB counters unreadable (PDH 0x{lastError:X8})" : "Server service not running" };
        }
        return FileShareFormat.Merge(shares, space, PdhInstances.Read(open), PdhInstances.Read(read), PdhInstances.Read(write),
            PdhInstances.Single(sessions), PdhInstances.Single(files));
    }

    public void Dispose() => Close();
}

public sealed class FakeFileShareProvider : IFileShareProvider
{
    public bool Installed { get; set; } = true;
    public FileShareStatus Value = new();
    public FileShareStatus Sample() => Value;
    public void Dispose() { }
}

/// <summary>Graph lines: total read and write bytes/sec, then one throughput (read + write) line per share.</summary>
public sealed class FileShareSource : PerItemSource
{
    readonly Func<IFileShareProvider> provider;
    public FileShareStatus Status { get; private set; } = new();
    public FileShareSource(Func<IFileShareProvider> provider) { this.provider = provider; }
    public override PanelKind Kind => PanelKind.FileShare;
    public override float MinScale => 1024 * 1024;   // 1 MB/s
    public override double UnitBase => 1024;
    protected override string Prefix => "share:";
    protected override IReadOnlyList<LineConfig> FixedLines() => new LineConfig[]
    {
        new() { Key = "read", Label = "Read", Color = "#7CFF6B", Width = 1.8f, Glow = true, Fill = true },
        new() { Key = "write", Label = "Write", Color = "#FFB463", Width = 1.5f },
    };
    protected override IEnumerable<string> Items => Status.Shares.Select(s => s.Name);
    public override bool Ready => Status.Running;
    public override int RowCount(PanelConfig cfg) => FileShareFormat.Rows(cfg, Status).Count;
    protected override void Refresh() => Status = provider().Sample();
    protected override float Value(string key)
    {
        if (!Status.Running) return float.NaN;
        if (key == "read") return (float)Status.ReadBps;
        if (key == "write") return (float)Status.WriteBps;
        var share = Status.Shares.FirstOrDefault(s => ItemKey(s.Name) == key);
        return share == null ? float.NaN : (float)share.Bps;
    }
    public override string Format(float v) => Fmt.Bytes(v) + "/s";
    public override string Readout(float[] latest)
    {
        if (!Status.Running) return Status.Note;
        var parts = new List<string>();
        if (!double.IsNaN(Status.Sessions)) parts.Add($"{Status.Sessions:0} sessions");
        if (!double.IsNaN(Status.FilesOpen)) parts.Add($"{Status.FilesOpen:0} open");
        return parts.Count > 0 ? string.Join("  ", parts) : $"{Status.Shares.Count} shares";
    }
}
