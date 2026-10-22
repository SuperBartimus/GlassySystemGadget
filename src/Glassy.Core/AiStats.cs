using System.Globalization;

namespace Glassy.Core;

/// <summary>One heatmap square: a day, how busy it was against each provider's own busiest day in range (0-1), and
/// which provider was busiest that day (its colour). Providers are compared by share of their own peak, since Codex
/// and Claude count tokens on very different scales.</summary>
public sealed record AiHeatCell(DateTime Day, double Level, string Provider);

public sealed record AiPersonality(string Name, string Blurb, string Glyph);

public sealed class AiStatsResult
{
    public long Today, Avg7;
    public (DateTime Day, long Tokens)? BestDay, LightestDay;
    public int ActiveStreak, BestStreak;
    /// <summary>Tokens per hour of day (0-23) over the last 30 days.</summary>
    public long[] HourOfDay = new long[24];
    /// <summary>Provider -> share of the last 7 days' tokens (0-1), biggest first.</summary>
    public List<(string Provider, double Share)> Split = new();
    public AiPersonality Personality, Secondary;
}

public static class AiStats
{
    public static Dictionary<DateTime, long> Daily(Dictionary<DateTime, long> hours) =>
        hours.GroupBy(kv => kv.Key.Date).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

    /// <summary>The last `days` days ending today, oldest first.</summary>
    public static List<AiHeatCell> Heatmap(Dictionary<string, Dictionary<DateTime, long>> hours, DateTime today, int days)
    {
        today = today.Date;
        var daily = hours.ToDictionary(kv => kv.Key, kv => Daily(kv.Value));
        var peak = daily.ToDictionary(kv => kv.Key, kv => (double)kv.Value.Where(d => d.Key > today.AddDays(-days) && d.Key <= today).Select(d => d.Value).DefaultIfEmpty(0).Max());
        var cells = new List<AiHeatCell>();
        for (int i = days - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var best = daily.Select(kv => (p: kv.Key, level: peak[kv.Key] > 0 ? kv.Value.GetValueOrDefault(d) / peak[kv.Key] : 0))
                .OrderByDescending(x => x.level).FirstOrDefault();
            cells.Add(new AiHeatCell(d, best.level, best.level > 0 ? best.p : ""));
        }
        return cells;
    }

    /// <summary>GitHub-style intensity steps: 0 for an idle day, then 1-4 by quarter of the provider's peak.</summary>
    public static int Step(double level) => level <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(level * 4), 1, 4);

    public static AiStatsResult Compute(Dictionary<string, Dictionary<DateTime, long>> hours, DateTime now)
    {
        var today = now.Date;
        var all = new Dictionary<DateTime, long>();
        foreach (var p in hours.Values) foreach (var (h, v) in p) all[h] = all.GetValueOrDefault(h) + v;
        var daily = Daily(all);
        var r = new AiStatsResult { Today = daily.GetValueOrDefault(today) };
        r.Avg7 = Enumerable.Range(0, 7).Sum(i => daily.GetValueOrDefault(today.AddDays(-i))) / 7;
        var used = daily.Where(d => d.Value > 0).ToList();
        if (used.Count > 0)
        {
            var best = used.OrderByDescending(d => d.Value).First(); r.BestDay = (best.Key, best.Value);
            var light = used.OrderBy(d => d.Value).First(); r.LightestDay = (light.Key, light.Value);
        }
        // Today still counts as part of an ongoing streak before its first use.
        var day = daily.GetValueOrDefault(today) > 0 ? today : today.AddDays(-1);
        while (daily.GetValueOrDefault(day) > 0) { r.ActiveStreak++; day = day.AddDays(-1); }
        int run = 0;
        foreach (var d in used.Select(x => x.Key).OrderBy(x => x))
        {
            run = daily.GetValueOrDefault(d.AddDays(-1)) > 0 ? run + 1 : 1;
            r.BestStreak = Math.Max(r.BestStreak, run);
        }
        foreach (var (h, v) in all) if (h >= today.AddDays(-29)) r.HourOfDay[h.Hour] += v;

        var week = hours.ToDictionary(kv => kv.Key, kv => kv.Value.Where(x => x.Key >= today.AddDays(-6)).Sum(x => x.Value));
        long weekTotal = week.Values.Sum();
        if (weekTotal > 0) r.Split = week.Where(kv => kv.Value > 0).Select(kv => (kv.Key, (double)kv.Value / weekTotal)).OrderByDescending(x => x.Item2).ToList();
        (r.Personality, r.Secondary) = Personalities(all, daily, r, today);
        return r;
    }

    public static string ProviderName(string id) => id switch { "codex" => "Codex", "claude" => "Claude", "gemini" => "Gemini", _ => id };

    /// <summary>A playful read of the last 7 days: the primary is how and when the work happens, the secondary a
    /// notable extra. First matching rule wins in each list.</summary>
    static (AiPersonality, AiPersonality) Personalities(Dictionary<DateTime, long> all, Dictionary<DateTime, long> daily, AiStatsResult r, DateTime today)
    {
        var week = all.Where(kv => kv.Key >= today.AddDays(-6) && kv.Value > 0).ToList();
        long total = week.Sum(kv => kv.Value);
        if (total == 0) return (new AiPersonality("Hibernating", "Nothing in the last 7 days. The models miss you.", "\U0001F43B"), null);

        double Share(Func<DateTime, bool> when) => (double)week.Where(kv => when(kv.Key)).Sum(kv => kv.Value) / total;
        double night = Share(h => h.Hour >= 22 || h.Hour < 5), early = Share(h => h.Hour >= 5 && h.Hour < 9);
        double weekend = Share(h => h.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        double office = Share(h => h.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && h.Hour >= 9 && h.Hour < 17);
        int topN = Math.Max(1, (int)Math.Ceiling(week.Count * 0.1));
        double burst = (double)week.Select(kv => kv.Value).OrderByDescending(v => v).Take(topN).Sum() / total;
        int activeDays = week.Select(kv => kv.Key.Date).Distinct().Count();
        double hoursPerDay = (double)week.Count / activeDays;

        AiPersonality primary =
            night >= 0.4 ? new("Night Owl", "Does the best work after dark.", "\U0001F319") :
            early >= 0.35 ? new("Early Bird", "Gets the tokens in before the coffee's cold.", "\U0001F426") :
            weekend >= 0.6 ? new("Weekend Warrior", "Saves the big builds for the weekend.", "\U0001F6E1") :
            burst >= 0.5 && week.Count >= 4 ? new("Sprint User", "Fast bursts of focused work.", char.ConvertFromUtf32(0x26A1)) :
            activeDays >= 6 && hoursPerDay >= 6 ? new("Marathoner", "Long sessions, day after day.", "\U0001F3C3") :
            office >= 0.7 ? new("Nine-to-Fiver", "Keeps the AI on business hours.", "\U0001F4BC") :
            activeDays <= 2 ? new("Dabbler", "Drops in now and then.", "\U0001F331") :
            new("Steady Drip", "A little every day, never a flood.", "\U0001F4A7");

        long before = Enumerable.Range(7, 56).Sum(i => daily.GetValueOrDefault(today.AddDays(-i)));
        int distinctHours = week.Select(kv => kv.Key.Hour).Distinct().Count();
        bool idleBefore = Enumerable.Range(1, 3).All(i => daily.GetValueOrDefault(today.AddDays(-i)) == 0);
        var split = r.Split;
        AiPersonality secondary =
            before > 0 && total >= 2.0 * before / 8 ? new("Token Furnace", "Burning more than twice the usual this week.", "\U0001F525") :
            distinctHours >= 16 ? new("Busy Bee", "Active at all hours - always something on the go.", "\U0001F41D") :
            split.Count >= 2 && split[1].Share >= 0.25 ? new("Two-Timer", $"Splits the work between {ProviderName(split[0].Provider)} and {ProviderName(split[1].Provider)}.", char.ConvertFromUtf32(0x2696)) :
            r.ActiveStreak >= 5 ? new("Streaker", $"{r.ActiveStreak} days running.", "\U0001F3C5") :
            daily.GetValueOrDefault(today) > 0 && idleBefore ? new("Comeback Kid", "Back after a few quiet days.", "\U0001F44B") :
            split.Count >= 1 && split[0].Share >= 0.9 ? new("Loyalist", $"Sticks with {ProviderName(split[0].Provider)}.", "\U0001F3AF") :
            null;
        return (primary, secondary);
    }

    /// <summary>"1.2M", "350k", "820".</summary>
    public static string Tokens(double v) =>
        v >= 1e9 ? (v / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B" :
        v >= 1e6 ? (v / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M" :
        v >= 1e3 ? (v / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k" : v.ToString("0", CultureInfo.InvariantCulture);
}
