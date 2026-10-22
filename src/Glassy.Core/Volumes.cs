using System.Text;

namespace Glassy.Core;

public enum VolKind { Fixed, Removable, Optical, Network }

/// <summary>One mounted drive letter with media present.</summary>
public sealed class VolumeRow
{
    public string Letter = "";            // "C" (no colon)
    public string Label = "";
    public VolKind Kind;
    public bool ReadOnly;
    public long Total, Free;
    /// <summary>Network shares have no local disk counters, so no activity graph.</summary>
    public bool HasActivity => Kind != VolKind.Network;
    public double UsedFraction => Total <= 0 ? 0 : 1.0 - (double)Free / Total;
    public string Key => $"{Letter}|{Kind}|{ReadOnly}|{Label}";
}

/// <summary>Columns of one compact Drives row, between the panel's inner edges l and r: the letter, the space bar, the
/// activity graph (with its rates under it), then the volume label when at least MinLabel is left over.</summary>
public readonly record struct DriveRowLayout(float BarL, float BarR, float GraphL, float GraphR, float LabelL)
{
    public const float RowH = 22, Pad = 3, LetterW = 22, Gap = 6, MinLabel = 36, GraphTop = 2, GraphH = 8;

    public static DriveRowLayout For(float l, float r)
    {
        float x = l + LetterW, avail = r - x;
        float bar = avail * 0.45f, graph = avail * 0.30f;
        if (r - (x + bar + Gap + graph + Gap) < MinLabel) { bar = (avail - Gap) * 0.6f; graph = avail - Gap - bar; }   // too narrow: no label column
        float gl = x + bar + Gap, gr = gl + graph;
        return new(x, x + bar, gl, gr, Math.Min(r, gr + Gap));
    }
}

public interface IVolumeProvider : IDisposable
{
    /// <summary>Current drives, ordered by letter. The array is replaced (never mutated) when anything changes.</summary>
    VolumeRow[] Snapshot { get; }
}

/// <summary>
/// Polls drives on background threads: local drives every 3 s, network drives every 10 s on their own thread, because an
/// unreachable network share can block a query for many seconds and must never stall the UI or the local drives.
/// </summary>
public sealed class VolumeWatcher : IVolumeProvider
{
    readonly object gate = new();
    readonly ManualResetEvent stop = new(false);
    VolumeRow[] local = Array.Empty<VolumeRow>(), remote = Array.Empty<VolumeRow>();
    volatile VolumeRow[] merged = Array.Empty<VolumeRow>();
    public VolumeRow[] Snapshot => merged;

    public VolumeWatcher()
    {
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS);   // no "There is no disk in the drive" dialogs
        local = Scan(false); Merge();                          // synchronous first pass: local drives are fast
        Start(false, 3000); Start(true, 10000);
    }

    void Start(bool network, int periodMs)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var rows = Scan(network);
                    lock (gate) { if (network) remote = rows; else local = rows; Merge(); }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* drive vanished mid-query; next pass fixes it */ }
                if (stop.WaitOne(periodMs)) return;
            }
        }) { IsBackground = true, Name = network ? "Glassy network drives" : "Glassy local drives", Priority = ThreadPriority.BelowNormal }.Start();
    }

    void Merge()
    {
        var m = local.Concat(remote).OrderBy(v => v.Letter, StringComparer.Ordinal).ToArray();
        if (!m.Select(v => v.Key + v.Free).SequenceEqual(merged.Select(v => v.Key + v.Free))) merged = m;
    }

    /// <summary>Drives of one class (network or not) that currently have media and answer a space query.</summary>
    public static VolumeRow[] Scan(bool network)
    {
        var rows = new List<VolumeRow>();
        foreach (var root in Environment.GetLogicalDrives())
        {
            uint type = Native.GetDriveTypeW(root);
            if ((type == 4) != network) continue;
            VolKind? kind = type switch { 2 => VolKind.Removable, 3 or 6 => VolKind.Fixed, 4 => VolKind.Network, 5 => VolKind.Optical, _ => null };
            if (kind == null) continue;
            if (!Native.GetDiskFreeSpaceExW(root, out _, out ulong total, out ulong free) || total == 0) continue;   // no media / unreachable
            var label = new StringBuilder(261); var fs = new StringBuilder(261); uint flags = 0;
            Native.GetVolumeInformationW(root, label, 261, out _, out _, out flags, fs, 261);
            rows.Add(new VolumeRow
            {
                Letter = root.Substring(0, 1).ToUpperInvariant(), Label = label.ToString(), Kind = kind.Value,
                ReadOnly = (flags & Native.FILE_READ_ONLY_VOLUME) != 0, Total = (long)total, Free = (long)free,
            });
        }
        return rows.ToArray();
    }

    public void Dispose() => stop.Set();
}
