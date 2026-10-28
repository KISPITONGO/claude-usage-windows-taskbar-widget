using ClaudeUsageWidgetProvider;
using SkiaSharp;

namespace TokenBars.StreamDock;

/// <summary>What a service key shows. Pressing the key moves to the next view.</summary>
internal enum KeyView { Both = 0, Session = 1, Week = 2, Tokens = 3 }

/// <summary>Draws the key images (144×144 PNG, Stream Dock scales them to the key).</summary>
internal static class KeyRenderer
{
    public const int Size = 144;

    private static readonly SKColor Background = SKColor.Parse("#17171C");
    private static readonly SKColor Track = SKColor.Parse("#2C2C34");
    private static readonly SKColor Text = SKColor.Parse("#F2F2F5");
    private static readonly SKColor Muted = SKColor.Parse("#9A9AA6");
    private static readonly SKColor ClaudeColor = SKColor.Parse("#D97757");
    private static readonly SKColor CodexColor = SKColor.Parse("#10A37F");
    private static readonly SKColor Amber = SKColor.Parse("#F5A524");
    private static readonly SKColor Red = SKColor.Parse("#E5484D");

    private static readonly SKTypeface Bold = Typeface(SKFontStyle.Bold);
    private static readonly SKTypeface Regular = Typeface(SKFontStyle.Normal);

    private static SKTypeface Typeface(SKFontStyle style)
    {
        foreach (var family in new[] { "Segoe UI", "Segoe UI Variable", "Arial", "DejaVu Sans" })
        {
            var tf = SKTypeface.FromFamilyName(family, style);
            if (tf != null && tf.FamilyName == family) return tf;
        }
        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }

    public static SKColor Accent(bool claude) => claude ? ClaudeColor : CodexColor;

    /// <summary>Orange/green normally, amber from 75 %, red from 90 %.</summary>
    public static SKColor BarColor(bool claude, double percent) =>
        percent >= 90 ? Red : percent >= 75 ? Amber : Accent(claude);

    // ---------- public entry points ----------

    public static byte[] Service(ServiceSnapshot s, KeyView view) => Draw(c =>
    {
        Header(c, s.IsClaude);

        if (view == KeyView.Tokens)
        {
            TokensBody(c, s.IsClaude, s.Tokens, top: 44);
            return;
        }
        if (s.Error != null && !s.HasData)
        {
            Message(c, s.Error);
            return;
        }
        if (!s.HasData)
        {
            Message(c, "Cargando…");
            return;
        }

        switch (view)
        {
            case KeyView.Session: BigGauge(c, s.IsClaude, s.Session); break;
            case KeyView.Week: BigGauge(c, s.IsClaude, s.Week); break;
            default:
                BarBlock(c, s.IsClaude, s.Session, top: 40);
                BarBlock(c, s.IsClaude, s.Week, top: 92);
                break;
        }
    });

    /// <summary>Both services on one key: four thin bars.</summary>
    public static byte[] Combined(ServiceSnapshot claude, ServiceSnapshot codex) => Draw(c =>
    {
        float y = 8;
        foreach (var s in new[] { claude, codex })
        {
            using var name = new SKPaint { Typeface = Bold, TextSize = 17, Color = Accent(s.IsClaude), IsAntialias = true };
            c.DrawText(s.IsClaude ? "Claude" : "Codex", 10, y + 15, name);
            if (!s.HasData)
            {
                using var p = new SKPaint { Typeface = Regular, TextSize = 14, Color = Muted, IsAntialias = true };
                c.DrawText(s.Error?.Split('\n')[0] ?? "Cargando…", 10, y + 40, p);
                y += 68;
                continue;
            }
            MiniRow(c, s.IsClaude, s.Session, y + 22);
            MiniRow(c, s.IsClaude, s.Week, y + 44);
            y += 68;
        }
    });

    /// <summary>Local token counts (Claude Code + Codex CLI) for today and the last 7 days.</summary>
    public static byte[] Tokens(ServiceSnapshot claude, ServiceSnapshot codex) => Draw(c =>
    {
        using var title = new SKPaint { Typeface = Bold, TextSize = 17, Color = Text, IsAntialias = true };
        c.DrawText("Tokens hoy", 10, 24, title);

        float y = 32;
        foreach (var s in new[] { claude, codex })
        {
            using var big = new SKPaint { Typeface = Bold, TextSize = 25, Color = Text, IsAntialias = true };
            c.DrawText(s.Tokens != null ? LocalTokenCounter.Format(s.Tokens.Today) : "–", 10, y + 26, big);
            using var name = new SKPaint { Typeface = Bold, TextSize = 14, Color = Accent(s.IsClaude), IsAntialias = true, TextAlign = SKTextAlign.Right };
            c.DrawText(s.IsClaude ? "Claude" : "Codex", Size - 10, y + 25, name);
            using var week = new SKPaint { Typeface = Regular, TextSize = 13, Color = Muted, IsAntialias = true };
            c.DrawText(s.Tokens != null ? "7 días: " + LocalTokenCounter.Format(s.Tokens.Last7Days) : "", 10, y + 44, week);
            y += 56;
        }
    });

    /// <summary>Static icon used in the action list before the plugin draws anything.</summary>
    public static byte[] Icon(bool? claude, int size)
    {
        var info = new SKImageInfo(size, size);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        float k = size / 72f;
        using var bg = new SKPaint { Color = Background, IsAntialias = true };
        c.DrawRoundRect(new SKRect(0, 0, size, size), 14 * k, 14 * k, bg);
        var colors = claude switch
        {
            true => new[] { ClaudeColor, ClaudeColor },
            false => new[] { CodexColor, CodexColor },
            null => new[] { ClaudeColor, CodexColor }
        };
        float[] fill = { 0.62f, 0.38f };
        for (int i = 0; i < 2; i++)
        {
            var y = (24 + i * 18) * k;
            using var t = new SKPaint { Color = Track, IsAntialias = true };
            c.DrawRoundRect(new SKRect(12 * k, y, 60 * k, y + 10 * k), 5 * k, 5 * k, t);
            using var f = new SKPaint { Color = colors[i], IsAntialias = true };
            c.DrawRoundRect(new SKRect(12 * k, y, (12 + 48 * fill[i]) * k, y + 10 * k), 5 * k, 5 * k, f);
        }
        return Encode(surface);
    }

    // ---------- pieces ----------

    private static byte[] Draw(Action<SKCanvas> paint)
    {
        var info = new SKImageInfo(Size, Size);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(Background);
        paint(c);
        return Encode(surface);
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Header(SKCanvas c, bool claude)
    {
        using var dot = new SKPaint { Color = Accent(claude), IsAntialias = true };
        c.DrawCircle(16, 20, 6, dot);
        using var name = new SKPaint { Typeface = Bold, TextSize = 19, Color = Accent(claude), IsAntialias = true };
        c.DrawText(claude ? "Claude" : "Codex", 28, 27, name);
    }

    /// <summary>Label + percent on one line, the bar, and the reset countdown below.</summary>
    private static void BarBlock(SKCanvas c, bool claude, Window? w, float top)
    {
        using var label = new SKPaint { Typeface = Regular, TextSize = 14, Color = Muted, IsAntialias = true };
        c.DrawText(w?.Name ?? "–", 10, top + 13, label);

        using var pct = new SKPaint { Typeface = Bold, TextSize = 20, Color = w != null ? BarColor(claude, w.Percent) : Muted, IsAntialias = true, TextAlign = SKTextAlign.Right };
        c.DrawText(w != null ? $"{w.Percent:0}%" : "–", Size - 10, top + 15, pct);

        Bar(c, claude, w, new SKRect(10, top + 22, Size - 10, top + 34));

        if (w != null)
        {
            using var reset = new SKPaint { Typeface = Regular, TextSize = 12, Color = Muted, IsAntialias = true };
            c.DrawText("reinicia " + Countdown(w.ResetsAt), 10, top + 48, reset);
        }
    }

    private static void MiniRow(SKCanvas c, bool claude, Window? w, float top)
    {
        using var label = new SKPaint { Typeface = Regular, TextSize = 12, Color = Muted, IsAntialias = true };
        c.DrawText(w == null ? "–" : w.Name == "Semana" ? "7 d" : w.Name, 10, top + 13, label);
        Bar(c, claude, w, new SKRect(40, top + 3, 98, top + 13));
        using var pct = new SKPaint { Typeface = Bold, TextSize = 15, Color = w != null ? BarColor(claude, w.Percent) : Muted, IsAntialias = true, TextAlign = SKTextAlign.Right };
        c.DrawText(w != null ? $"{w.Percent:0}%" : "–", Size - 8, top + 14, pct);
    }

    private static void Bar(SKCanvas c, bool claude, Window? w, SKRect r)
    {
        float radius = r.Height / 2;
        using var track = new SKPaint { Color = Track, IsAntialias = true };
        c.DrawRoundRect(r, radius, radius, track);
        if (w == null) return;

        var frac = (float)Math.Clamp(w.Percent / 100, 0, 1);
        if (frac > 0)
        {
            var fillRect = new SKRect(r.Left, r.Top, r.Left + Math.Max(r.Height, r.Width * frac), r.Bottom);
            using var fill = new SKPaint { Color = BarColor(claude, w.Percent), IsAntialias = true };
            c.DrawRoundRect(fillRect, radius, radius, fill);
        }

        // Pace marker: where the bar would be if usage were spread evenly over the window.
        var elapsed = w.Elapsed;
        if (elapsed >= 0)
        {
            var x = r.Left + r.Width * (float)elapsed;
            using var tick = new SKPaint { Color = Text.WithAlpha(210), StrokeWidth = 2, IsAntialias = true };
            c.DrawLine(x, r.Top - 3, x, r.Bottom + 3, tick);
        }
    }

    /// <summary>One window filling the key: ring with the percent in the middle.</summary>
    private static void BigGauge(SKCanvas c, bool claude, Window? w)
    {
        var center = new SKPoint(Size / 2f, 82);
        const float radius = 40;
        var rect = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);

        using var track = new SKPaint { Color = Track, Style = SKPaintStyle.Stroke, StrokeWidth = 11, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawArc(rect, 135, 270, false, track);

        if (w != null)
        {
            var frac = (float)Math.Clamp(w.Percent / 100, 0, 1);
            using var arc = new SKPaint { Color = BarColor(claude, w.Percent), Style = SKPaintStyle.Stroke, StrokeWidth = 11, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            if (frac > 0) c.DrawArc(rect, 135, 270 * frac, false, arc);

            var elapsed = w.Elapsed;
            if (elapsed >= 0)
            {
                var angle = (135 + 270 * elapsed) * Math.PI / 180;
                var inner = new SKPoint(center.X + (float)Math.Cos(angle) * (radius - 9), center.Y + (float)Math.Sin(angle) * (radius - 9));
                var outer = new SKPoint(center.X + (float)Math.Cos(angle) * (radius + 9), center.Y + (float)Math.Sin(angle) * (radius + 9));
                using var tick = new SKPaint { Color = Text.WithAlpha(210), StrokeWidth = 2.5f, IsAntialias = true };
                c.DrawLine(inner, outer, tick);
            }
        }

        using var pct = new SKPaint { Typeface = Bold, TextSize = 28, Color = Text, IsAntialias = true, TextAlign = SKTextAlign.Center };
        c.DrawText(w != null ? $"{w.Percent:0}%" : "–", center.X, center.Y + 6, pct);
        using var name = new SKPaint { Typeface = Regular, TextSize = 13, Color = Muted, IsAntialias = true, TextAlign = SKTextAlign.Center };
        c.DrawText(w?.Name.ToLowerInvariant() ?? "", center.X, center.Y + 24, name);

        if (w != null)
        {
            using var reset = new SKPaint { Typeface = Regular, TextSize = 13, Color = Muted, IsAntialias = true, TextAlign = SKTextAlign.Center };
            c.DrawText("reinicia " + Countdown(w.ResetsAt), center.X, Size - 6, reset);
        }
    }

    private static void TokensBody(SKCanvas c, bool claude, TokenTotals? t, float top)
    {
        using var label = new SKPaint { Typeface = Regular, TextSize = 14, Color = Muted, IsAntialias = true };
        using var big = new SKPaint { Typeface = Bold, TextSize = 30, Color = Text, IsAntialias = true };
        using var mid = new SKPaint { Typeface = Bold, TextSize = 22, Color = Text, IsAntialias = true };
        c.DrawText("hoy", 10, top + 12, label);
        c.DrawText(t != null ? LocalTokenCounter.Format(t.Today) : "–", 10, top + 44, big);
        c.DrawText("7 días", 10, top + 66, label);
        c.DrawText(t != null ? LocalTokenCounter.Format(t.Last7Days) : "–", 10, top + 92, mid);
    }

    private static void Message(SKCanvas c, string text)
    {
        using var p = new SKPaint { Typeface = Regular, TextSize = 16, Color = Muted, IsAntialias = true, TextAlign = SKTextAlign.Center };
        var lines = text.Split('\n');
        float y = 84 - (lines.Length - 1) * 10;
        foreach (var line in lines)
        {
            c.DrawText(line, Size / 2f, y, p);
            y += 21;
        }
    }

    internal static string Countdown(DateTimeOffset resetsAt)
    {
        var left = resetsAt - DateTimeOffset.UtcNow;
        if (resetsAt == DateTimeOffset.UnixEpoch || left <= TimeSpan.Zero) return "ahora";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays} d {left.Hours} h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours} h {left.Minutes:00} m";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} m";
    }
}
