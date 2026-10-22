using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Glassy.Core;

/// <summary>Per log file: how far it has been read, the last usage record seen (to skip repeats), and the tokens it
/// contributed per local hour ("2026-10-22T14"). Keeping contributions per file means a file that shrinks or is
/// rewritten can be re-read without double counting, and a file the tool deletes later keeps its history.</summary>
public sealed class AiFileState
{
    public string Provider { get; set; } = "";
    public long Offset { get; set; }
    public string LastKey { get; set; } = "";
    public Dictionary<string, long> Hours { get; set; } = new();
}

/// <summary>Tokens used per provider per local hour, built incrementally from the AI tools' own logs and saved next to
/// config.json, so a restart doesn't re-read gigabytes of Codex rollouts. "Tokens" means work done: fresh input plus
/// output - cache reads are left out (they dwarf everything else and cost far less).</summary>
public sealed class AiHistory
{
    public const string HourFormat = "yyyy-MM-dd'T'HH";
    const int Chunk = 4 << 20, MaxLine = 32 << 20;
    static readonly byte[] CodexMarker = Encoding.ASCII.GetBytes("\"token_count\""), ClaudeMarker = Encoding.ASCII.GetBytes("\"usage\"");

    public Dictionary<string, AiFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider -> local hour -> tokens, summed over every file ever read.</summary>
    public Dictionary<string, Dictionary<DateTime, long>> Totals()
    {
        var result = new Dictionary<string, Dictionary<DateTime, long>>();
        foreach (var f in Files.Values)
        {
            if (!result.TryGetValue(f.Provider, out var hours)) result[f.Provider] = hours = new();
            foreach (var (k, v) in f.Hours)
                if (DateTime.TryParseExact(k, HourFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var h)) hours[h] = hours.GetValueOrDefault(h) + v;
        }
        return result;
    }

    /// <summary>Reads whatever was appended to each file since last time. Returns true if anything changed.</summary>
    public bool Update(string provider, IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (var path in paths)
        {
            long len;
            try { len = new FileInfo(path).Length; } catch (IOException) { continue; }
            if (!Files.TryGetValue(path, out var st)) Files[path] = st = new AiFileState { Provider = provider };
            if (len < st.Offset) { st.Offset = 0; st.LastKey = ""; st.Hours.Clear(); changed = true; }   // rewritten: start over
            if (len == st.Offset) continue;
            try { Read(path, st); changed = true; } catch (IOException) { }
        }
        return changed;
    }

    /// <summary>Drops hours older than keepDays, and files that no longer exist and have nothing left to contribute.</summary>
    public void Prune(DateTime now, int keepDays)
    {
        string cutoff = now.AddDays(-keepDays).ToString(HourFormat, CultureInfo.InvariantCulture);
        foreach (var f in Files.Values) foreach (var k in f.Hours.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) f.Hours.Remove(k);
        foreach (var p in Files.Where(kv => kv.Value.Hours.Count == 0 && !File.Exists(kv.Key)).Select(kv => kv.Key).ToList()) Files.Remove(p);
    }

    /// <summary>Complete lines only: the offset stops after the last newline, so a line the tool is still writing is
    /// picked up whole next time. Lines are checked for the marker as raw bytes and only matching ones are decoded,
    /// which keeps a first pass over a multi-gigabyte rollout to seconds.</summary>
    void Read(string path, AiFileState st)
    {
        var marker = st.Provider == "codex" ? CodexMarker : ClaudeMarker;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(st.Offset, SeekOrigin.Begin);
        var buf = new byte[Chunk]; var carry = new MemoryStream(); bool skipping = false;
        long pos = st.Offset;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            int start = 0;
            while (true)
            {
                int nl = Array.IndexOf(buf, (byte)'\n', start, n - start);
                if (nl < 0)
                {
                    if (!skipping) { carry.Write(buf, start, n - start); if (carry.Length > MaxLine) { carry.SetLength(0); skipping = true; } }
                    break;
                }
                if (!skipping)
                {
                    ReadOnlySpan<byte> line;
                    if (carry.Length > 0) { carry.Write(buf, start, nl - start); line = carry.GetBuffer().AsSpan(0, (int)carry.Length); }
                    else line = buf.AsSpan(start, nl - start);
                    if (line.IndexOf(marker) >= 0) Add(st, Encoding.UTF8.GetString(line));
                }
                carry.SetLength(0); skipping = false;
                start = nl + 1;
                st.Offset = pos + start;
            }
            pos += n;
        }
    }

    void Add(AiFileState st, string line)
    {
        var rec = st.Provider == "codex" ? ParseCodexUsage(line) : ParseClaudeUsage(line);
        if (rec is not { } r || r.Key == st.LastKey) return;
        st.LastKey = r.Key;
        if (r.Tokens <= 0) return;
        string k = r.At.ToLocalTime().ToString(HourFormat, CultureInfo.InvariantCulture);
        st.Hours[k] = st.Hours.GetValueOrDefault(k) + r.Tokens;
    }

    /// <summary>A Codex token_count event: this turn's fresh input + output. Key is the running total, so the repeated
    /// events Codex writes for the same turn (a limits refresh, say) count once.</summary>
    public static (DateTimeOffset At, string Key, long Tokens)? ParseCodexUsage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var pl) || !pl.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
            if (!info.TryGetProperty("last_token_usage", out var last) || !info.TryGetProperty("total_token_usage", out var total)) return null;
            if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
            long fresh = Num(last, "input_tokens") - Num(last, "cached_input_tokens") + Num(last, "output_tokens");
            return (at, Num(total, "total_tokens").ToString(CultureInfo.InvariantCulture), Math.Max(0, fresh));
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { return null; }
    }

    /// <summary>A Claude Code assistant message: input + cache writes + output. Key is the message id - Claude Code
    /// writes one line per content block, each repeating the same message's usage.</summary>
    public static (DateTimeOffset At, string Key, long Tokens)? ParseClaudeUsage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") return null;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
            string id = msg.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "";
            return (at, id, Num(u, "input_tokens") + Num(u, "cache_creation_input_tokens") + Num(u, "output_tokens"));
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { return null; }
    }

    static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public static AiHistory Load(string path)
    {
        try { return JsonSerializer.Deserialize<AiHistory>(File.ReadAllText(path)) ?? new AiHistory(); }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) { return new AiHistory(); }
    }

    public void Save(string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
