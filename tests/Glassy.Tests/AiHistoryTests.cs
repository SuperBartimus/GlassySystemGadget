using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class AiHistoryTests
{
    static string TempFile(string name) { var d = Path.Combine(Path.GetTempPath(), "glassy-aih-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return Path.Combine(d, name); }

    static string Claude(string id, string ts, int input, int write, int output, int read = 0) =>
        $"{{\"type\":\"assistant\",\"timestamp\":\"{ts}\",\"message\":{{\"id\":\"{id}\",\"usage\":{{\"input_tokens\":{input},\"cache_creation_input_tokens\":{write},\"cache_read_input_tokens\":{read},\"output_tokens\":{output}}}}}}}";

    static string Codex(string ts, long total, int input, int cached, int output) =>
        $"{{\"timestamp\":\"{ts}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":{total}}}," +
        $"\"last_token_usage\":{{\"input_tokens\":{input},\"cached_input_tokens\":{cached},\"output_tokens\":{output}}}}},\"rate_limits\":null}}}}";

    static long Sum(AiHistory h, string p) => h.Totals().GetValueOrDefault(p)?.Values.Sum() ?? 0;

    [Fact]
    public void Claude_repeated_message_lines_count_once_and_cache_reads_are_left_out()
    {
        string f = TempFile("s.jsonl");
        File.WriteAllLines(f, new[]
        {
            "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}",
            Claude("m1", "2026-10-22T14:00:00Z", 10, 100, 5, read: 99999), Claude("m1", "2026-10-22T14:00:01Z", 10, 100, 5, read: 99999),
            Claude("m2", "2026-10-22T15:00:00Z", 1, 0, 2),
        });
        var h = new AiHistory();
        Assert.True(h.Update("claude", new[] { f }));
        Assert.Equal(115 + 3, Sum(h, "claude"));
        Assert.False(h.Update("claude", new[] { f }));                                       // nothing new: no re-read, no double count
    }

    [Fact]
    public void A_line_still_being_written_is_counted_once_it_is_complete()
    {
        string f = TempFile("s.jsonl");
        string line = Claude("m1", "2026-10-22T14:00:00Z", 1, 0, 1);
        File.WriteAllText(f, line.Substring(0, 30));
        var h = new AiHistory(); h.Update("claude", new[] { f });
        Assert.Equal(0, Sum(h, "claude"));
        File.WriteAllText(f, line + "\n");
        h.Update("claude", new[] { f });
        Assert.Equal(2, Sum(h, "claude"));
    }

    [Fact]
    public void Codex_counts_fresh_input_plus_output_and_skips_repeated_turn_events()
    {
        string f = TempFile("rollout-a.jsonl");
        File.WriteAllLines(f, new[]
        {
            Codex("2026-10-22T14:00:00Z", 1000, 900, 800, 50),       // 100 fresh + 50 out
            Codex("2026-10-22T14:00:05Z", 1000, 900, 800, 50),       // same running total: the same turn again
            "{\"timestamp\":\"2026-10-22T14:01:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":null}}",
            Codex("2026-10-22T14:02:00Z", 2000, 500, 0, 10),
        });
        var h = new AiHistory(); h.Update("codex", new[] { f });
        Assert.Equal(150 + 510, Sum(h, "codex"));
    }

    [Fact]
    public void A_rewritten_file_is_recounted_not_added_twice_and_history_survives_a_restart()
    {
        string f = TempFile("s.jsonl");
        File.WriteAllLines(f, new[] { Claude("m1", "2026-10-22T14:00:00Z", 100, 0, 0), Claude("m2", "2026-10-22T14:00:00Z", 100, 0, 0) });
        var h = new AiHistory(); h.Update("claude", new[] { f });
        File.WriteAllLines(f, new[] { Claude("m9", "2026-10-22T14:00:00Z", 7, 0, 0) });     // shorter than what was read
        h.Update("claude", new[] { f });
        Assert.Equal(7, Sum(h, "claude"));
        string idx = TempFile("ai-history.json"); h.Save(idx);
        var back = AiHistory.Load(idx);
        Assert.Equal(7, Sum(back, "claude"));
        Assert.False(back.Update("claude", new[] { f }));                                    // offset survived too
        File.Delete(f);
        back.Prune(new DateTime(2026, 10, 23), 400);
        Assert.Equal(7, Sum(back, "claude"));                                                 // a deleted log keeps its history
    }

    [Fact]
    public void Hours_are_bucketed_in_local_time()
    {
        string f = TempFile("s.jsonl");
        var at = new DateTimeOffset(2026, 10, 22, 23, 30, 0, TimeSpan.Zero);
        File.WriteAllLines(f, new[] { Claude("m1", at.ToString("o"), 1, 0, 0) });
        var h = new AiHistory(); h.Update("claude", new[] { f });
        var local = at.ToLocalTime().DateTime;
        Assert.Equal(new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0), Assert.Single(h.Totals()["claude"]).Key);
    }
}

public class AiStatsTests
{
    static readonly DateTime Now = new(2026, 10, 22, 15, 0, 0);   // a Thursday

    static Dictionary<string, Dictionary<DateTime, long>> H(params (string p, int daysAgo, int hour, long tokens)[] xs)
    {
        var d = new Dictionary<string, Dictionary<DateTime, long>>();
        foreach (var (p, ago, hour, t) in xs) { if (!d.TryGetValue(p, out var m)) d[p] = m = new(); m[Now.Date.AddDays(-ago).AddHours(hour)] = t; }
        return d;
    }

    [Fact]
    public void Heatmap_squares_take_the_busiest_provider_relative_to_its_own_peak()
    {
        // Codex peaks at 1000 and Claude at 10; the day before today Claude is at its peak while Codex is at 10%.
        var cells = AiStats.Heatmap(H(("codex", 0, 10, 1000), ("codex", 1, 10, 100), ("claude", 1, 10, 10)), Now, 7);
        Assert.Equal(7, cells.Count); Assert.Equal(Now.Date, cells[^1].Day);
        Assert.Equal(("claude", 1.0), (cells[^2].Provider, cells[^2].Level));
        Assert.Equal(("codex", 1.0), (cells[^1].Provider, cells[^1].Level));
        Assert.Equal(("", 0.0), (cells[0].Provider, cells[0].Level));
        Assert.Equal(new[] { 0, 1, 1, 2, 4 }, new[] { 0, 0.01, 0.25, 0.26, 1 }.Select(AiStats.Step));
    }

    [Fact]
    public void Heatmap_grid_puts_today_in_the_last_column_on_its_weekday_row()
    {
        var cells = AiStats.Heatmap(new(), Now, 70);
        var (pos, cols) = AiUsageFormat.HeatGrid(cells);
        Assert.Equal(cols - 1, pos[^1].Col); Assert.Equal((int)DayOfWeek.Thursday, pos[^1].Row);
        Assert.Equal(11, cols);                                                               // 70 days from a Friday to a Thursday spans 11 weeks
    }

    [Fact]
    public void Records_and_streaks()
    {
        var s = AiStats.Compute(H(("codex", 0, 10, 50), ("codex", 1, 10, 500), ("claude", 2, 10, 5), ("codex", 5, 10, 70), ("codex", 6, 10, 70)), Now);
        Assert.Equal(50, s.Today);
        Assert.Equal((Now.Date.AddDays(-1), 500L), s.BestDay);
        Assert.Equal((Now.Date.AddDays(-2), 5L), s.LightestDay);
        Assert.Equal(3, s.ActiveStreak); Assert.Equal(3, s.BestStreak);
        Assert.Equal((50 + 500 + 5 + 70 + 70) / 7, s.Avg7);
        Assert.Equal(695, s.HourOfDay[10]);
    }

    [Fact]
    public void A_streak_is_still_ongoing_before_todays_first_use()
    {
        Assert.Equal(2, AiStats.Compute(H(("codex", 1, 10, 1), ("codex", 2, 10, 1)), Now).ActiveStreak);
    }

    [Fact]
    public void Personalities_follow_when_the_work_happens()
    {
        Assert.Equal("Hibernating", AiStats.Compute(new(), Now).Personality.Name);
        Assert.Equal("Night Owl", AiStats.Compute(H(("codex", 1, 23, 100), ("codex", 2, 1, 100), ("codex", 3, 14, 50)), Now).Personality.Name);
        var office = AiStats.Compute(H(("claude", 0, 9, 10), ("claude", 0, 11, 10), ("claude", 1, 13, 10), ("claude", 2, 15, 10), ("codex", 2, 16, 15)), Now);
        Assert.Equal("Nine-to-Fiver", office.Personality.Name);
        Assert.Equal("Two-Timer", office.Secondary.Name);
    }
}
