namespace Glassy.Core;

/// <summary>Fixed-capacity ring; index 0 is the oldest value.</summary>
public sealed class FloatRing
{
    readonly float[] a; int head; public int Count { get; private set; }
    public FloatRing(int capacity) { a = new float[Math.Max(1, capacity)]; }
    public int Capacity => a.Length;
    public void Push(float v)
    {
        a[head] = v; head = (head + 1) % a.Length;
        if (Count < a.Length) Count++;
    }
    public float this[int i] => a[(head - Count + i + a.Length * 2) % a.Length];
}

/// <summary>Cell counts and sampling ratios for one graph; one cell = one drawn pixel column.</summary>
public readonly struct TimeLayout
{
    public int HistCells { get; }
    public int LiveCells { get; }
    public int TicksPerLive { get; }
    public int TicksPerHist { get; }

    public TimeLayout(int plotCells, int splitPercent, double liveSeconds, double historySeconds, int tickMs)
    {
        plotCells = Math.Max(10, plotCells);
        var split = Math.Clamp(splitPercent, 10, 95);
        HistCells = Math.Clamp((int)Math.Round(plotCells * split / 100.0), 1, plotCells - 1);
        LiveCells = plotCells - HistCells;
        double tick = Math.Max(50, tickMs) / 1000.0;
        TicksPerLive = Math.Max(1, (int)Math.Round(liveSeconds / LiveCells / tick));
        TicksPerHist = Math.Max(1, (int)Math.Round(historySeconds / HistCells / tick));
    }
}

/// <summary>
/// One graph line: a live ring (each cell = mean of TicksPerLive samples) and a history ring
/// (each cell = mean or peak of TicksPerHist samples). NaN samples are skipped.
/// </summary>
public sealed class SeriesBuffer
{
    public FloatRing Live { get; }
    public FloatRing Hist { get; }
    readonly int liveN, histN; readonly bool peak;
    double liveSum, histAcc; int liveCount, histCount;
    public float Latest { get; private set; } = float.NaN;

    public SeriesBuffer(TimeLayout t, HistoryMode mode)
    {
        Live = new FloatRing(t.LiveCells); Hist = new FloatRing(t.HistCells);
        liveN = t.TicksPerLive; histN = t.TicksPerHist; peak = mode == HistoryMode.Peak;
    }

    public void Push(float v)
    {
        if (float.IsNaN(v)) return;
        Latest = v;
        liveSum += v; if (++liveCount >= liveN) { Live.Push((float)(liveSum / liveCount)); liveSum = 0; liveCount = 0; }
        histAcc = peak ? (histCount == 0 ? v : Math.Max(histAcc, v)) : histAcc + v;
        if (++histCount >= histN) { Hist.Push((float)(peak ? histAcc : histAcc / histCount)); histAcc = 0; histCount = 0; }
    }

    /// <summary>Value of display cell i (0 = leftmost); NaN where no data yet. Newest data is right-aligned in each half.</summary>
    public float Cell(int i)
    {
        int hc = Hist.Capacity;
        if (i < hc) { int k = i - (hc - Hist.Count); return k >= 0 ? Hist[k] : float.NaN; }
        int lc = Live.Capacity; int j = i - hc - (lc - Live.Count);
        return j >= 0 ? Live[j] : float.NaN;
    }

    public float MaxDisplayed()
    {
        float m = 0;
        for (int i = 0; i < Hist.Count; i++) m = Math.Max(m, Hist[i]);
        for (int i = 0; i < Live.Count; i++) m = Math.Max(m, Live[i]);
        return m;
    }
}
