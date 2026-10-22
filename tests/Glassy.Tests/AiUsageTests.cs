using System.Text;
using Glassy.Core;
using Xunit;

namespace Glassy.Tests;

public class AiUsageTests
{
    // Shape copied from a real Codex rollout token_count event (values changed).
    const string CodexLine = "{\"timestamp\":\"2026-10-22T14:04:26.123Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":5}}," +
        "\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":24.0,\"window_minutes\":300,\"resets_at\":1792694875}," +
        "\"secondary\":{\"used_percent\":16.0,\"window_minutes\":10080,\"resets_at\":1793143040},\"plan_type\":\"plus\"}}}";

    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "glassy-ai-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Codex_token_count_line_gives_both_windows_with_labels_and_reset_times()
    {
        var s = AiUsageFormat.ParseCodexLine(CodexLine);
        Assert.Equal(new[] { ("5h", 24.0), ("7d", 16.0) }, s.Windows.Select(w => (w.Label, w.UsedPct)));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792694875), s.Windows[0].ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 4, 26, 123, TimeSpan.Zero), s.ObservedAt);
        Assert.Equal("plus", s.Plan);
        Assert.Null(AiUsageFormat.ParseCodexLine("{\"payload\":{\"type\":\"token_count\",\"rate_limits\":null}}"));
        Assert.Null(AiUsageFormat.ParseCodexLine("{\"type\":\"response_item\",\"payload\":{\"text\":\"talking about rate_limits\"}}"));
        Assert.Null(AiUsageFormat.ParseCodexLine("{\"rate_limits\": trunc"));
    }

    [Fact]
    public void Only_the_tail_of_a_big_rollout_is_read_and_the_last_snapshot_wins()
    {
        string f = Path.Combine(TempDir(), "rollout-x.jsonl");
        var sb = new StringBuilder();
        sb.Append(CodexLine.Replace("24.0", "90.0")).Append('\n');                        // older snapshot, outside the tail
        sb.Append(new string('x', 5000)).Append('\n');
        sb.Append(CodexLine).Append('\n');
        sb.Append("{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\"}}\n");     // later lines without limits
        File.WriteAllText(f, sb.ToString());
        Assert.Equal(24.0, AiUsageFormat.CodexTail(f, maxBytes: 2000).Windows[0].UsedPct);
        Assert.Equal(24.0, AiUsageFormat.CodexTail(f).Windows[0].UsedPct);                // both in range: the later one wins
        Assert.Null(AiUsageFormat.CodexTail(f, maxBytes: 100));                           // tail too short to hold one: nothing, not a half line
    }

    [Fact]
    public void Claude_status_line_cache_gives_5h_above_7d()
    {
        var s = AiUsageFormat.ParseClaudeCache("{\"written_utc\":\"2026-10-22T14:43:53.993Z\",\"seven_day\":{\"used_percentage\":19,\"resets_at\":1793012400}," +
            "\"five_hour\":{\"used_percentage\":13,\"resets_at\":1792694400}}");
        Assert.Equal(new[] { ("5h", 13.0), ("7d", 19.0) }, s.Windows.Select(w => (w.Label, w.UsedPct)));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 43, 53, 993, TimeSpan.Zero), s.ObservedAt);
        Assert.Null(AiUsageFormat.ParseClaudeCache("not json"));
    }

    [Fact]
    public void A_window_past_its_reset_time_reads_zero()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var past = new AiWindow("5h", 57, now.AddMinutes(-1)); var future = new AiWindow("7d", 57, now.AddHours(1));
        Assert.Equal(0, AiUsageFormat.Effective(past, now)); Assert.Equal("reset", AiUsageFormat.Reset(past, now));
        Assert.Equal(57, AiUsageFormat.Effective(future, now));
    }

    [Fact]
    public void Rows_separate_providers_and_hidden_ones_are_left_out()
    {
        var s = new AiUsageStatus { Providers =
        {
            new AiProviderStatus { Id = "codex", Windows = { new AiWindow("5h", 1, null), new AiWindow("7d", 2, null) } },
            new AiProviderStatus { Id = "claude", Note = "Needs a status line (see Settings)" },
        } };
        var pc = AppConfig.AiUsagePanel();
        Assert.Equal(new[] { AiRowKind.Window, AiRowKind.Window, AiRowKind.Separator, AiRowKind.Note }, AiUsageFormat.Rows(pc, s).Select(r => r.Kind));
        pc.AiHidden.Add("CODEX");
        Assert.Equal(new[] { AiRowKind.Note }, AiUsageFormat.Rows(pc, s).Select(r => r.Kind));
    }

    [Theory]
    [InlineData(1500, 1840, 1194)]   // widget on the right half (e.g. docked right): flyout to its left
    [InlineData(40, 380, 386)]       // widget on the left half: flyout to its right
    [InlineData(1700, 1910, 1394)]   // right half, room only on the left
    [InlineData(5, 345, 351)]        // left half at the very edge: still to the right
    public void Stats_flyout_opens_beside_the_widget_toward_the_middle_of_the_screen(int wl, int wr, int expectedX) =>
        Assert.Equal(expectedX, AiUsageFormat.FlyoutX(wl, wr, 300, 0, 1920, 6));

    [Fact]
    public void Stats_flyout_flips_sides_when_the_preferred_one_has_no_room()
    {
        // A wide widget on a small screen: centre right of middle, but no room on the left - goes right, kept on screen.
        Assert.Equal(700, AiUsageFormat.FlyoutX(250, 800, 300, 0, 1000, 6));
        // Centre left of middle, but no room on the right - goes left, kept on screen.
        Assert.Equal(0, AiUsageFormat.FlyoutX(200, 760, 300, 0, 1000, 6));
        // A second monitor to the right: the widget is right of the virtual desktop's middle but on the left half of its
        // own screen, so the flyout goes right.
        Assert.Equal(2686, AiUsageFormat.FlyoutX(2340, 2680, 300, 1920, 3840, 6));
    }

    [Fact]
    public void Panel_shows_only_where_an_AI_tool_is_installed_and_grows_with_its_rows()
    {
        var c = AppConfig.CreateDefault(); c.Panels = new List<PanelConfig> { AppConfig.AiUsagePanel() };
        var store = new ClipboardStore(TempDir());
        using (var e = new Engine(c, clipboard: store, ai: new FakeAiUsageProvider { Installed = false })) Assert.Empty(e.Panels);
        var fake = new FakeAiUsageProvider();
        using var e2 = new Engine(c, clipboard: store, ai: fake);
        float waiting = e2.Panels[0].Height;
        fake.Value = new AiUsageStatus { Providers = { new AiProviderStatus { Id = "codex", Windows = { new AiWindow("5h", 1, null), new AiWindow("7d", 2, null) } } } };
        e2.Tick(); e2.Tick();
        Assert.Equal(waiting + Engine.RowProc, e2.Panels[0].Height);
    }
}
