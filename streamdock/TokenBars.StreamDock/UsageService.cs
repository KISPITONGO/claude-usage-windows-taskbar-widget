using ClaudeUsageWidgetProvider;

namespace TokenBars.StreamDock;

/// <summary>One rate-limit window, ready to draw.</summary>
internal sealed record Window(string Name, double Percent, DateTimeOffset ResetsAt, TimeSpan Length)
{
    /// <summary>Fraction of the window already elapsed (0..1), used for the pace marker.</summary>
    public double Elapsed
    {
        get
        {
            if (Length <= TimeSpan.Zero || ResetsAt == DateTimeOffset.UnixEpoch) return -1;
            var left = ResetsAt - DateTimeOffset.UtcNow;
            return Math.Clamp(1 - left / Length, 0, 1);
        }
    }
}

internal sealed record ServiceSnapshot(
    bool IsClaude,
    Window? Session,
    Window? Week,
    TokenTotals? Tokens,
    string? Error,
    DateTimeOffset UpdatedAt)
{
    public bool HasData => Session != null || Week != null;
}

/// <summary>
/// Polls Claude and Codex in the background and keeps the latest snapshot of each.
/// One instance serves every key, so adding more keys does not add more API calls.
/// </summary>
internal sealed class UsageService
{
    // Each Claude poll is a 1-token request, so stay a bit gentler than the widget.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TokenInterval = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeApiClient? _claude;
    private ClaudeApiClient? _codex;
    private DateTimeOffset _tokensAt = DateTimeOffset.MinValue;
    private TokenTotals? _claudeTokens, _codexTokens;

    public ServiceSnapshot Claude { get; private set; } = Empty(true);
    public ServiceSnapshot Codex { get; private set; } = Empty(false);

    public event Action? Changed;

    private static ServiceSnapshot Empty(bool claude) =>
        new(claude, null, null, null, null, DateTimeOffset.MinValue);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(force: false);
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { }
        }
    }

    public async Task RefreshAsync(bool force)
    {
        if (!await _gate.WaitAsync(0)) return; // a refresh is already running
        try
        {
            EnsureClients();

            if (force || DateTimeOffset.UtcNow - _tokensAt > TokenInterval)
            {
                await Task.Run(() =>
                {
                    try { _claudeTokens = LocalTokenCounter.CountClaude(); } catch (Exception ex) { Log.Write("tokens claude: " + ex.Message); }
                    try { _codexTokens = LocalTokenCounter.CountCodex(); } catch (Exception ex) { Log.Write("tokens codex: " + ex.Message); }
                });
                _tokensAt = DateTimeOffset.UtcNow;
            }

            var claudeTask = Fetch(_claude, true, force);
            var codexTask = Fetch(_codex, false, force);
            Claude = await claudeTask;
            Codex = await codexTask;
        }
        catch (Exception ex)
        {
            Log.Write("refresh: " + ex);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    private void EnsureClients()
    {
        if (_claude != null && _codex != null) return;
        List<AccountInfo> accounts;
        try { accounts = CredentialStore.LoadAllAccounts(); }
        catch (Exception ex) { Log.Write("accounts: " + ex.Message); return; }

        _claude ??= accounts.FirstOrDefault(a => a.Service == ServiceType.Claude) is { } c ? new ClaudeApiClient(c) : null;
        _codex ??= accounts.FirstOrDefault(a => a.Service == ServiceType.Codex) is { } x ? new ClaudeApiClient(x) : null;
    }

    private async Task<ServiceSnapshot> Fetch(ClaudeApiClient? client, bool isClaude, bool force)
    {
        var tokens = isClaude ? _claudeTokens : _codexTokens;
        var previous = isClaude ? Claude : Codex;
        if (client == null)
            return previous with { Tokens = tokens, Error = "Sin sesión" };

        UsageData? data;
        try { data = await client.GetUsageAsync(force); }
        catch (Exception ex) { data = null; Log.Write((isClaude ? "claude: " : "codex: ") + ex.Message); }

        if (data == null)
        {
            if (client.LastError != null) Log.Write((isClaude ? "claude: " : "codex: ") + client.LastError);
            return previous with { Tokens = tokens, Error = previous.HasData ? null : "Sin datos" };
        }

        var prefix = isClaude ? "unified-" : "session-";
        var windows = data.Limits
            .Where(l => l.Label.StartsWith(prefix))
            .Select(ToWindow)
            .OrderBy(w => w.Length)
            .ToList();
        if (windows.Count == 0) windows = data.Limits.Take(2).Select(ToWindow).OrderBy(w => w.Length).ToList();

        return new ServiceSnapshot(isClaude,
            windows.ElementAtOrDefault(0),
            windows.Count > 1 ? windows[^1] : null,
            tokens, null, DateTimeOffset.UtcNow);
    }

    private static Window ToWindow(RateLimitEntry e)
    {
        var len = ParseLength(e.Label);
        var name = len.TotalHours switch
        {
            <= 0 => e.Label,
            < 24 => $"{len.TotalHours:0} h",
            >= 6.5 * 24 and <= 7.5 * 24 => "Semana",
            _ => $"{len.TotalDays:0} d"
        };
        return new Window(name, Math.Clamp(e.Utilization, 0, 999), e.ResetsAt, len);
    }

    /// <summary>"unified-5h" → 5 h, "session-7d" → 7 days.</summary>
    internal static TimeSpan ParseLength(string label)
    {
        var suffix = label[(label.LastIndexOf('-') + 1)..];
        if (suffix.Length < 2 || !long.TryParse(suffix[..^1], out var n)) return TimeSpan.Zero;
        return suffix[^1] switch
        {
            'd' => TimeSpan.FromDays(n),
            'h' => TimeSpan.FromHours(n),
            'm' => TimeSpan.FromMinutes(n),
            's' => TimeSpan.FromSeconds(n),
            _ => TimeSpan.Zero
        };
    }
}
