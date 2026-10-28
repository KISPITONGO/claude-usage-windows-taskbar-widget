using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageWidgetProvider;

namespace TokenBars.StreamDock;

internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenBars", "streamdock.log");
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

internal static class Program
{
    private const string Prefix = "com.tokenbars.streamdock.";

    private sealed class Key
    {
        public required string Action;
        public KeyView View;
        public string? LastImage;
    }

    private static readonly ConcurrentDictionary<string, Key> Keys = new();
    private static readonly UsageService Usage = new();
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    private static ClientWebSocket? _socket;

    public static async Task<int> Main(string[] args)
    {
        // Developer helpers: render sample keys / the action icons to a folder.
        if (args.Length >= 2 && args[0] == "--preview") return Preview(args[1], live: false);
        if (args.Length >= 2 && args[0] == "--preview-live") return Preview(args[1], live: true);
        if (args.Length >= 2 && args[0] == "--icons") return Icons(args[1]);

        string? port = null, uuid = null, registerEvent = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "-port": port = args[i + 1]; break;
                case "-pluginUUID": uuid = args[i + 1]; break;
                case "-registerEvent": registerEvent = args[i + 1]; break;
            }
        }
        if (port == null || uuid == null || registerEvent == null)
        {
            Log.Write("Faltan argumentos: " + string.Join(' ', args));
            return 1;
        }

        Log.Write($"Iniciando plugin (puerto {port})");
        using var cts = new CancellationTokenSource();
        Usage.Changed += () => _ = RedrawAllAsync();
        _ = Usage.RunAsync(cts.Token);
        _ = TickAsync(cts.Token);

        try
        {
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token);
            await SendAsync(new JsonObject { ["event"] = registerEvent, ["uuid"] = uuid });
            await ReceiveLoopAsync(_socket, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Write("WebSocket: " + ex.Message);
        }
        cts.Cancel();
        Log.Write("Plugin cerrado");
        return 0;
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try { await HandleAsync(JsonNode.Parse(text)); }
            catch (Exception ex) { Log.Write("Evento inválido: " + ex.Message); }
        }
    }

    private static async Task HandleAsync(JsonNode? msg)
    {
        var ev = msg?["event"]?.GetValue<string>();
        var context = msg?["context"]?.GetValue<string>();
        var action = msg?["action"]?.GetValue<string>();
        if (ev == null || context == null) return;

        switch (ev)
        {
            case "willAppear":
            {
                var view = msg?["payload"]?["settings"]?["view"]?.GetValue<int?>() ?? 0;
                var key = Keys.AddOrUpdate(context,
                    _ => new Key { Action = action ?? "", View = (KeyView)Math.Clamp(view, 0, 3) },
                    (_, k) => { k.LastImage = null; return k; });
                await DrawAsync(context, key);
                break;
            }
            case "willDisappear":
                Keys.TryRemove(context, out _);
                break;
            case "dialRotate":
            {
                // Knob: turn to change the view of every Claude/Codex key at once.
                var ticks = msg?["payload"]?["ticks"]?.GetValue<int>() ?? 0;
                if (ticks == 0) break;
                foreach (var (ctx, k) in Keys)
                {
                    if (!IsServiceKey(k)) continue;
                    k.View = (KeyView)((((int)k.View + Math.Sign(ticks)) % 4 + 4) % 4);
                    await SaveViewAsync(ctx, k);
                }
                await RedrawAllAsync();
                break;
            }
            case "dialDown":
                _ = Usage.RefreshAsync(force: true);
                break;
            case "keyDown":
            {
                if (!Keys.TryGetValue(context, out var key)) break;
                if (IsServiceKey(key))
                {
                    key.View = (KeyView)(((int)key.View + 1) % 4);
                    await SaveViewAsync(context, key);
                    await DrawAsync(context, key);
                }
                // Any press also asks for fresh numbers.
                _ = Usage.RefreshAsync(force: true);
                break;
            }
        }
    }

    private static bool IsServiceKey(Key key) =>
        key.Action.EndsWith(".claude") || key.Action.EndsWith(".codex");

    private static Task SaveViewAsync(string context, Key key) => SendAsync(new JsonObject
    {
        ["event"] = "setSettings",
        ["context"] = context,
        ["payload"] = new JsonObject { ["view"] = (int)key.View }
    });

    /// <summary>Redraw once a minute so the reset countdowns and pace markers keep moving.</summary>
    private static async Task TickAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
            await RedrawAllAsync();
        }
    }

    private static async Task RedrawAllAsync()
    {
        foreach (var (context, key) in Keys)
            await DrawAsync(context, key);
    }

    private static async Task DrawAsync(string context, Key key)
    {
        if (key.Action.EndsWith(".knob")) return; // knobs on the AKP03 have no screen
        byte[] png;
        try { png = Render(key.Action, key.View, Usage.Claude, Usage.Codex); }
        catch (Exception ex) { Log.Write("Render: " + ex.Message); return; }

        var image = "data:image/png;base64," + Convert.ToBase64String(png);
        if (image == key.LastImage) return; // nothing changed, spare the USB link
        key.LastImage = image;
        await SendAsync(new JsonObject
        {
            ["event"] = "setImage",
            ["context"] = context,
            ["payload"] = new JsonObject { ["image"] = image, ["target"] = 0 }
        });
    }

    private static byte[] Render(string action, KeyView view, ServiceSnapshot claude, ServiceSnapshot codex) =>
        action.Replace(Prefix, "") switch
        {
            "claude" => KeyRenderer.Service(claude, view),
            "codex" => KeyRenderer.Service(codex, view),
            "claude5h" => KeyRenderer.Service(claude, KeyView.Session),
            "claudeweek" => KeyRenderer.Service(claude, KeyView.Week),
            "codex5h" => KeyRenderer.Service(codex, KeyView.Session),
            "codexweek" => KeyRenderer.Service(codex, KeyView.Week),
            "tokens" => KeyRenderer.Tokens(claude, codex),
            "tokens7d" => KeyRenderer.Tokens(claude, codex, week: true),
            "resets" => KeyRenderer.Resets(claude, codex),
            _ => KeyRenderer.Combined(claude, codex)
        };

    private static async Task SendAsync(JsonObject payload)
    {
        var ws = _socket;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await SendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { Log.Write("Envío: " + ex.Message); }
        finally { SendLock.Release(); }
    }

    // ---------- developer helpers ----------

    private static int Preview(string dir, bool live)
    {
        Directory.CreateDirectory(dir);
        ServiceSnapshot claude, codex;
        if (live)
        {
            Usage.RefreshAsync(force: true).GetAwaiter().GetResult();
            claude = Usage.Claude;
            codex = Usage.Codex;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            claude = new ServiceSnapshot(true,
                new Window("5 h", 42, now.AddHours(2.2), TimeSpan.FromHours(5)),
                new Window("Semana", 78, now.AddDays(2.4), TimeSpan.FromDays(7)),
                new TokenTotals { Today = 1_840_000, Last7Days = 23_500_000 }, null, now);
            codex = new ServiceSnapshot(false,
                new Window("5 h", 93, now.AddMinutes(38), TimeSpan.FromHours(5)),
                new Window("Semana", 27, now.AddDays(5.1), TimeSpan.FromDays(7)),
                new TokenTotals { Today = 312_000, Last7Days = 4_100_000 }, null, now);
        }

        foreach (var view in Enum.GetValues<KeyView>())
        {
            File.WriteAllBytes(Path.Combine(dir, $"claude-{view}.png"), KeyRenderer.Service(claude, view));
            File.WriteAllBytes(Path.Combine(dir, $"codex-{view}.png"), KeyRenderer.Service(codex, view));
        }
        File.WriteAllBytes(Path.Combine(dir, "combined.png"), KeyRenderer.Combined(claude, codex));
        File.WriteAllBytes(Path.Combine(dir, "tokens.png"), KeyRenderer.Tokens(claude, codex));
        File.WriteAllBytes(Path.Combine(dir, "tokens7d.png"), KeyRenderer.Tokens(claude, codex, week: true));
        File.WriteAllBytes(Path.Combine(dir, "resets.png"), KeyRenderer.Resets(claude, codex));
        File.WriteAllBytes(Path.Combine(dir, "claude-error.png"),
            KeyRenderer.Service(new ServiceSnapshot(true, null, null, null, "Sin sesión", DateTimeOffset.UtcNow), KeyView.Both));
        Console.WriteLine("Vistas previas en " + dir);
        return 0;
    }

    private static int Icons(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, claude) in new[] { ("claude", (bool?)true), ("codex", false), ("both", null) })
        {
            File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), KeyRenderer.Icon(claude, 72));
            File.WriteAllBytes(Path.Combine(dir, $"{name}@2x.png"), KeyRenderer.Icon(claude, 144));
        }
        foreach (var (name, word) in new[] { ("today", "hoy"), ("week", "7d") })
        {
            File.WriteAllBytes(Path.Combine(dir, $"{name}.png"), KeyRenderer.WordIcon(word, 72));
            File.WriteAllBytes(Path.Combine(dir, $"{name}@2x.png"), KeyRenderer.WordIcon(word, 144));
        }
        File.WriteAllBytes(Path.Combine(dir, "clock.png"), KeyRenderer.ClockIcon(72));
        File.WriteAllBytes(Path.Combine(dir, "clock@2x.png"), KeyRenderer.ClockIcon(144));
        File.WriteAllBytes(Path.Combine(dir, "knob.png"), KeyRenderer.KnobIcon(72));
        File.WriteAllBytes(Path.Combine(dir, "knob@2x.png"), KeyRenderer.KnobIcon(144));
        return 0;
    }
}
