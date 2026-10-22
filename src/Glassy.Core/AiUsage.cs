using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Glassy.Core;

/// <summary>One rate-limit window as the provider's own client last reported it. UsedPct is 0-100.</summary>
public sealed record AiWindow(string Label, double UsedPct, DateTimeOffset? ResetsAt);

public sealed class AiProviderStatus
{
    public string Id = "", Name = "";
    /// <summary>When the provider's client last reported its limits - the age shown under the name.</summary>
    public DateTimeOffset? ObservedAt;
    public string Plan = "";
    public List<AiWindow> Windows = new();
    /// <summary>Shown instead of the bars when there are no windows (nothing recorded yet, or no status line for Claude).</summary>
    public string Note = "";
}

public sealed class AiUsageStatus
{
    public List<AiProviderStatus> Providers = new();
    /// <summary>Provider -> local hour -> tokens, for the heatmap and Stats. Empty until the first log pass finishes.</summary>
    public Dictionary<string, Dictionary<DateTime, long>> Hours = new();
}

public interface IAiUsageProvider : IDisposable
{
    /// <summary>Whether any supported AI tool is installed here - the panel is only shown when this is true.</summary>
    bool Installed { get; }
    /// <summary>Latest known status; cheap - reading files happens off the UI thread.</summary>
    AiUsageStatus Sample();
}

public enum AiRowKind { Window, Note, Separator }
public sealed record AiRow(AiRowKind Kind, AiProviderStatus Provider = null, AiWindow Window = null, bool First = false);

public static class AiUsageFormat
{
    public const float SeparatorH = 5;

    /// <summary>Each provider's own brand colour: Claude's orange, OpenAI's white, Gemini's blue.</summary>
    public static string DefaultColor(string id) => id switch { "claude" => "#D97757", "codex" => "#ECECF1", "gemini" => "#4285F4", _ => "#B48CFF" };
    public static string Color(PanelConfig pc, string id) => pc.AiColors.TryGetValue(id, out var c) && c != "" ? c : DefaultColor(id);

    /// <summary>"5h" for 300 minutes, "7d" for 10080 - the two windows Codex and Claude both use today.</summary>
    public static string WindowLabel(double minutes) =>
        minutes >= 1440 && minutes % 1440 == 0 ? $"{minutes / 1440:0}d" : minutes >= 60 ? $"{minutes / 60:0.#}h" : $"{minutes:0}m";

    /// <summary>Claude's status line names its windows ("five_hour", "seven_day"); anything newer shows its own name.</summary>
    public static string ClaudeLabel(string key) => key switch
    {
        "five_hour" => "5h", "seven_day" => "7d",
        _ => key.Replace("seven_day_", "7d ").Replace("five_hour_", "5h ").Replace('_', ' '),
    };

    /// <summary>A window whose reset time has passed was reset by the provider since it was observed, so it reads 0%.</summary>
    public static double Effective(AiWindow w, DateTimeOffset now) => w.ResetsAt is { } r && r <= now ? 0 : w.UsedPct;

    public static string Reset(AiWindow w, DateTimeOffset now) =>
        w.ResetsAt is not { } r ? "" : r <= now ? "reset" : r.ToLocalTime().ToString("MMM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Age(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } t) return "";
        var a = now - t;
        return a.TotalMinutes < 1 ? "now" : a.TotalHours < 1 ? $"{a.TotalMinutes:0}m ago" : a.TotalDays < 1 ? $"{a.TotalHours:0}h ago" : $"{a.TotalDays:0}d ago";
    }

    /// <summary>The rows under the title, top to bottom - Engine sizes the panel from the same list. Each provider's
    /// windows (or a note) and a thin separator between providers.</summary>
    public static List<AiRow> Rows(PanelConfig pc, AiUsageStatus s)
    {
        var rows = new List<AiRow>();
        var shown = s.Providers.Where(p => !pc.AiHidden.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (shown.Count == 0) { rows.Add(new AiRow(AiRowKind.Note, new AiProviderStatus { Note = "Waiting for usage data" })); return rows; }
        foreach (var p in shown)
        {
            if (rows.Count > 0) rows.Add(new AiRow(AiRowKind.Separator));
            if (p.Windows.Count == 0) rows.Add(new AiRow(AiRowKind.Note, p, First: true));
            else rows.AddRange(p.Windows.Select((w, i) => new AiRow(AiRowKind.Window, p, w, i == 0)));
        }
        return rows;
    }

    public const float HeatGap = 2, HeatTop = 8;
    public static float HeatmapHeight(PanelConfig pc) => pc.AiHeatmap ? HeatTop + 7 * (Math.Clamp(pc.AiHeatCellH, 3, 30) + HeatGap) : 0;

    /// <summary>Week columns, weekday rows (Sunday on top), like a contribution graph: each cell's column and row,
    /// and how many columns there are. Today is always in the last column.</summary>
    public static (List<(int Col, int Row)> Pos, int Cols) HeatGrid(List<AiHeatCell> cells)
    {
        if (cells.Count == 0) return (new(), 0);
        DateTime WeekStart(DateTime d) => d.Date.AddDays(-(int)d.DayOfWeek);
        var first = WeekStart(cells[0].Day);
        var pos = cells.Select(c => ((int)((WeekStart(c.Day) - first).TotalDays / 7), (int)c.Day.DayOfWeek)).ToList();
        return (pos, pos[^1].Item1 + 1);
    }

    /// <summary>The Stats button in the panel's top-right corner, in widget DIPs - shared by the renderer and the click
    /// test. Moves left of the Settings gear when this panel is the topmost one, where the gear is drawn.</summary>
    public static (float X, float Y, float W, float H) StatsButton(float panelTop, float widgetWidth)
    {
        float r = widgetWidth - Engine.Margin - 10;
        if (panelTop <= Engine.Margin + 1) r = widgetWidth - Engine.GearMargin - Engine.GearSize - 4;
        return (r - 40, panelTop + 5, 40, 15);
    }

    public static float RowsHeight(List<AiRow> rows) => rows.Sum(r => r.Kind == AiRowKind.Separator ? SeparatorH : Engine.RowProc);

    /// <summary>One Codex rollout line, or null when it isn't a token_count event carrying rate limits. Codex writes the
    /// account's current limits on every turn (payload.rate_limits.primary = 5h, secondary = weekly).</summary>
    public static AiProviderStatus ParseCodexLine(string line)
    {
        if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var pl) || !pl.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) return null;
            var s = new AiProviderStatus { Id = "codex", Name = "Codex" };
            if (root.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) s.ObservedAt = at;
            if (rl.TryGetProperty("plan_type", out var plan) && plan.ValueKind == JsonValueKind.String) s.Plan = plan.GetString();
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("used_percent", out var used)) continue;
                double minutes = w.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number ? wm.GetDouble() : 0;
                s.Windows.Add(new AiWindow(minutes > 0 ? WindowLabel(minutes) : name, used.GetDouble(), Epoch(w, "resets_at")));
            }
            return s.Windows.Count > 0 ? s : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The cache a Claude Code status-line command writes from the rate_limits it is handed:
    /// {"written_utc": "...", "five_hour": {"used_percentage": 13, "resets_at": 1792694400}, "seven_day": {...}}.</summary>
    public static AiProviderStatus ParseClaudeCache(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = new AiProviderStatus { Id = "claude", Name = "Claude" };
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "written_utc" && DateTimeOffset.TryParse(prop.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) s.ObservedAt = at;
                else if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("used_percentage", out var used) && used.ValueKind == JsonValueKind.Number)
                    s.Windows.Add(new AiWindow(ClaudeLabel(prop.Name), used.GetDouble(), Epoch(prop.Value, "resets_at")));
            }
            // Shortest window first, so 5h sits above 7d like Codex.
            s.Windows = s.Windows.OrderBy(w => w.Label.StartsWith("5h", StringComparison.Ordinal) ? 0 : 1).ToList();
            return s;
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { return null; }
    }

    static DateTimeOffset? Epoch(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()) : null;

    /// <summary>The last rate-limit snapshot in the final maxBytes of a rollout. Reads only the tail: a long-running
    /// Codex thread's rollout can be gigabytes.</summary>
    public static AiProviderStatus CodexTail(string path, int maxBytes = 1 << 20)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        int n = 0; while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
        var lines = Encoding.UTF8.GetString(buf, 0, n).Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)   // a partial first line (tail started mid-line) is not valid JSON, so it is skipped by the parse
            if (ParseCodexLine(lines[i]) is { } s) return s;
        return null;
    }
}

/// <summary>Reads what the AI CLIs already write on this machine - no API keys, no network. Codex: the rate limits in
/// its newest session rollouts (CODEX_HOME, default ~/.codex). Claude Code: rate-limits-cache.json in its config folder
/// (CLAUDE_CONFIG_DIR, default ~/.claude), which a status-line command has to write - Claude Code only hands its limits
/// to the status line. Refreshed every 15 s on a background task.</summary>
public sealed class LocalAiUsageProvider : IAiUsageProvider
{
    readonly string codexHome, claudeHome, historyPath;
    AiHistory history;
    long nextHistoryMs;
    Dictionary<string, Dictionary<DateTime, long>> hours = new();
    volatile AiUsageStatus status = new();
    volatile Task refresh;
    long nextMs;
    string loggedError = "";

    public LocalAiUsageProvider(string historyPath)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".codex");
        claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(home, ".claude");
        Installed = Directory.Exists(codexHome) || Directory.Exists(claudeHome);
        this.historyPath = historyPath;
    }

    public bool Installed { get; }

    public AiUsageStatus Sample()
    {
        if (Environment.TickCount64 >= nextMs && refresh is not { IsCompleted: false })
        {
            nextMs = Environment.TickCount64 + 15_000;
            refresh = Task.Run(() =>
            {
                try { UpdateHistory(); status = Read(); }
                catch (Exception ex)   // last resort for a background task: log it; the next refresh tries again
                {
                    string d = "AI usage read failed: " + ex.Message;
                    if (d != loggedError) { AppLog.Write(d); loggedError = d; }
                }
            });
        }
        return status;
    }

    AiUsageStatus Read()
    {
        var s = new AiUsageStatus { Hours = hours };
        if (Directory.Exists(codexHome))
        {
            var codex = NewestRollouts().Select(f => AiUsageFormat.CodexTail(f)).FirstOrDefault(x => x != null)
                ?? new AiProviderStatus { Id = "codex", Name = "Codex", Note = "No usage recorded yet" };
            s.Providers.Add(codex);
        }
        if (Directory.Exists(claudeHome))
        {
            string cache = Path.Combine(claudeHome, "rate-limits-cache.json");
            var claude = File.Exists(cache) ? AiUsageFormat.ParseClaudeCache(File.ReadAllText(cache)) : null;
            s.Providers.Add(claude is { Windows.Count: > 0 } ? claude : new AiProviderStatus { Id = "claude", Name = "Claude", Note = "Needs a status line (see Settings)" });
        }
        return s;
    }

    /// <summary>Once a minute: read what the logs gained since last time and save the index if anything changed. The
    /// first pass on a machine with a long Codex history reads every rollout once; after that only new lines are read.</summary>
    void UpdateHistory()
    {
        if (Environment.TickCount64 < nextHistoryMs) return;
        nextHistoryMs = Environment.TickCount64 + 60_000;
        history ??= AiHistory.Load(historyPath);
        bool changed = false;
        string codexSessions = Path.Combine(codexHome, "sessions"), claudeProjects = Path.Combine(claudeHome, "projects");
        if (Directory.Exists(codexSessions)) changed |= history.Update("codex", Directory.EnumerateFiles(codexSessions, "rollout-*.jsonl", SearchOption.AllDirectories).ToList());
        if (Directory.Exists(claudeProjects)) changed |= history.Update("claude", Directory.EnumerateFiles(claudeProjects, "*.jsonl", SearchOption.AllDirectories).ToList());
        if (changed || hours.Count == 0)
        {
            history.Prune(DateTime.Now, 400);
            hours = history.Totals();
            if (changed) history.Save(historyPath);
        }
    }

    /// <summary>The few most recently written rollouts - the newest snapshot is in one of them.</summary>
    IEnumerable<string> NewestRollouts()
    {
        string dir = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(3).Select(f => f.FullName).ToList();
    }

    public void Dispose() { }
}

public sealed class FakeAiUsageProvider : IAiUsageProvider
{
    public bool Installed { get; set; } = true;
    public AiUsageStatus Value = new();
    public AiUsageStatus Sample() => Value;
    public void Dispose() { }
}
