using System.Text.Json;

namespace ClaudeUsageWidgetProvider;

public sealed class TokenTotals
{
    public long Today { get; set; }
    public long Last7Days { get; set; }
}

/// <summary>
/// Counts tokens from the local logs written by Claude Code (~/.claude/projects/**/*.jsonl)
/// and Codex CLI (~/.codex/sessions/**/*.jsonl). Only usage that went through those CLIs on
/// this machine is visible here; web/app usage is only reflected in the rate-limit bars.
/// </summary>
internal static class LocalTokenCounter
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    internal static TokenTotals CountClaude(string? root = null, DateTimeOffset? now = null)
    {
        root ??= Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            "projects");
        var clock = now ?? DateTimeOffset.Now;
        var totals = new TokenTotals();
        // The same assistant message can be logged several times (streaming, resumed sessions).
        var seen = new HashSet<string>();

        foreach (var line in ReadRecentLines(root, clock))
        {
            if (!line.Contains("\"usage\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (!r.TryGetProperty("message", out var msg) ||
                    msg.ValueKind != JsonValueKind.Object ||
                    !msg.TryGetProperty("usage", out var usage) ||
                    !TryGetTimestamp(r, out var ts))
                    continue;

                var id = msg.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                var req = r.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
                if (id != null && !seen.Add(id + ":" + req)) continue;

                long tokens = GetLong(usage, "input_tokens") + GetLong(usage, "output_tokens")
                            + GetLong(usage, "cache_creation_input_tokens") + GetLong(usage, "cache_read_input_tokens");
                Add(totals, ts, tokens, clock);
            }
            catch (JsonException) { }
        }
        return totals;
    }

    internal static TokenTotals CountCodex(string? root = null, DateTimeOffset? now = null)
    {
        root ??= Path.Combine(
            Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            "sessions");
        var clock = now ?? DateTimeOffset.Now;
        var totals = new TokenTotals();

        foreach (var file in RecentFiles(root, clock))
        {
            // token_count events carry a running session total; count the increase between events.
            long previous = 0;
            foreach (var line in ReadLines(file))
            {
                if (!line.Contains("\"token_count\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("payload", out var payload) ||
                        payload.ValueKind != JsonValueKind.Object ||
                        !payload.TryGetProperty("info", out var info) ||
                        info.ValueKind != JsonValueKind.Object ||
                        !info.TryGetProperty("total_token_usage", out var total) ||
                        !TryGetTimestamp(r, out var ts))
                        continue;

                    long current = GetLong(total, "total_tokens");
                    if (current > previous)
                        Add(totals, ts, current - previous, clock);
                    previous = current;
                }
                catch (JsonException) { }
            }
        }
        return totals;
    }

    private static void Add(TokenTotals totals, DateTimeOffset ts, long tokens, DateTimeOffset now)
    {
        var local = ts.ToLocalTime();
        if (now - local > Window || tokens <= 0) return;
        totals.Last7Days += tokens;
        if (local.Date == now.Date) totals.Today += tokens;
    }

    private static IEnumerable<string> RecentFiles(string root, DateTimeOffset now)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
        catch { yield break; }
        var cutoff = now.UtcDateTime - Window - TimeSpan.FromDays(1);
        foreach (var f in files)
        {
            DateTime modified;
            try { modified = File.GetLastWriteTimeUtc(f); } catch { continue; }
            if (modified >= cutoff) yield return f;
        }
    }

    private static IEnumerable<string> ReadRecentLines(string root, DateTimeOffset now) =>
        RecentFiles(root, now).SelectMany(ReadLines);

    private static IEnumerable<string> ReadLines(string path)
    {
        StreamReader reader;
        try
        {
            // The CLIs keep these files open while writing.
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            reader = new StreamReader(fs);
        }
        catch { yield break; }

        using (reader)
        {
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); } catch { yield break; }
                if (line == null) yield break;
                yield return line;
            }
        }
    }

    private static bool TryGetTimestamp(JsonElement r, out DateTimeOffset ts)
    {
        ts = default;
        return r.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal, out ts);
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    internal static string Format(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString()
    };
}
