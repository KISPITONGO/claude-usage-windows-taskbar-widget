using ClaudeUsageWidgetProvider;
using SkiaSharp;

namespace TokenBars.StreamDock;

/// <summary>What a service key shows. Pressing the key (or turning a knob) moves to the next view.</summary>
internal enum KeyView { Both = 0, Session = 1, Week = 2, Tokens = 3 }

/// <summary>
/// Draws the key images. They are drawn at 144×144 and Stream Dock scales them down;
/// the AKP03 keys are only 60×60 px, so nothing is smaller than ~20 px here (≈8 px on the key).
/// </summary>
internal static class KeyRenderer
{
    public const int Size = 144;

    private static readonly SKColor Background = SKColor.Parse("#111114");
    private static readonly SKColor Track = SKColor.Parse("#2E2E37");
    private static readonly SKColor Text = SKColor.Parse("#F4F4F7");
    private static readonly SKColor Muted = SKColor.Parse("#A3A3AF");
    private static readonly SKColor ClaudeColor = SKColor.Parse("#D97757");
    private static readonly SKColor CodexColor = SKColor.Parse("#10A37F");
    private static readonly SKColor Amber = SKColor.Parse("#F5A524");
    private static readonly SKColor Red = SKColor.Parse("#E5484D");

    private static readonly SKTypeface Bold = Typeface(SKFontStyle.Bold);

    private static SKTypeface Typeface(SKFontStyle style)
    {
        foreach (var family in new[] { "Segoe UI", "Arial", "DejaVu Sans" })
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
        var name = s.IsClaude ? "Claude" : "Codex";
        Label(c, name, 8, 26, 24, Accent(s.IsClaude));

        if (view == KeyView.Tokens)
        {
            Label(c, Tok(s.Tokens?.Today), Size / 2f, 78, 44, Text, SKTextAlign.Center);
            Label(c, "tokens hoy", Size / 2f, 102, 20, Muted, SKTextAlign.Center);
            Label(c, "7d " + Tok(s.Tokens?.Last7Days), Size / 2f, 134, 24, Muted, SKTextAlign.Center);
            return;
        }
        if (!s.HasData)
        {
            Message(c, s.Error ?? "Cargando");
            return;
        }

        switch (view)
        {
            case KeyView.Session:
                Gauge(c, s.IsClaude, s.Session, "5h");
                break;
            case KeyView.Week:
                Gauge(c, s.IsClaude, s.Week, "sem");
                break;
            default:
                Row(c, s.IsClaude, "5h", s.Session, top: 36);
                Row(c, s.IsClaude, "sem", s.Week, top: 90);
                break;
        }
    });

    /// <summary>Both services on one key: four fat bars, labelled in the service colour.</summary>
    public static byte[] Combined(ServiceSnapshot claude, ServiceSnapshot codex) => Draw(c =>
    {
        float y = 6;
        foreach (var s in new[] { claude, codex })
        {
            FatBar(c, s.IsClaude, "5h", s.Session, y);
            FatBar(c, s.IsClaude, "7d", s.Week, y + 33);
            y += 72;
        }
    });

    /// <summary>Local token counts for today (Claude Code + Codex CLI).</summary>
    public static byte[] Tokens(ServiceSnapshot claude, ServiceSnapshot codex) => Draw(c =>
    {
        Label(c, "tokens hoy", Size / 2f, 26, 22, Muted, SKTextAlign.Center);
        Label(c, Tok(claude.Tokens?.Today), Size / 2f, 78, 40, ClaudeColor, SKTextAlign.Center);
        Label(c, Tok(codex.Tokens?.Today), Size / 2f, 128, 40, CodexColor, SKTextAlign.Center);
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
            var y = (22 + i * 18) * k;
            using var t = new SKPaint { Color = Track, IsAntialias = true };
            c.DrawRoundRect(new SKRect(12 * k, y, 60 * k, y + 12 * k), 6 * k, 6 * k, t);
            using var f = new SKPaint { Color = colors[i], IsAntialias = true };
            c.DrawRoundRect(new SKRect(12 * k, y, (12 + 48 * fill[i]) * k, y + 12 * k), 6 * k, 6 * k, f);
        }
        return Encode(surface);
    }

    /// <summary>Icon for the knob action: a dial with an arrow.</summary>
    public static byte[] KnobIcon(int size)
    {
        var info = new SKImageInfo(size, size);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        float k = size / 72f;
        using var bg = new SKPaint { Color = Background, IsAntialias = true };
        c.DrawRoundRect(new SKRect(0, 0, size, size), 14 * k, 14 * k, bg);
        var rect = new SKRect(18 * k, 18 * k, 54 * k, 54 * k);
        using var a = new SKPaint { Color = ClaudeColor, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * k, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawArc(rect, 200, 140, false, a);
        using var b = new SKPaint { Color = CodexColor, Style = SKPaintStyle.Stroke, StrokeWidth = 6 * k, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawArc(rect, 20, 140, false, b);
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

    private static void Label(SKCanvas c, string text, float x, float y, float size, SKColor color,
                              SKTextAlign align = SKTextAlign.Left)
    {
        using var p = new SKPaint { Typeface = Bold, TextSize = size, Color = color, IsAntialias = true, TextAlign = align };
        c.DrawText(text, x, y, p);
    }

    /// <summary>Window label on the left, big percent on the right, fat bar underneath.</summary>
    private static void Row(SKCanvas c, bool claude, string label, Window? w, float top)
    {
        Label(c, label, 8, top + 24, 22, Muted);
        Label(c, Pct(w), Size - 8, top + 28, 32, w != null ? BarColor(claude, w.Percent) : Muted, SKTextAlign.Right);
        Bar(c, claude, w, new SKRect(8, top + 36, Size - 8, top + 52));
    }

    /// <summary>One row of the combined key: service-coloured tag, bar with the percent inside.</summary>
    private static void FatBar(SKCanvas c, bool claude, string tag, Window? w, float top)
    {
        Label(c, tag, 17, top + 22, 19, Accent(claude), SKTextAlign.Center);
        var r = new SKRect(36, top, Size - 6, top + 29);
        Bar(c, claude, w, r, paceMarker: false);
        using var p = new SKPaint { Typeface = Bold, TextSize = 24, Color = Text, IsAntialias = true, TextAlign = SKTextAlign.Right };
        using var shadow = new SKPaint { Typeface = Bold, TextSize = 24, Color = SKColors.Black.WithAlpha(150), IsAntialias = true, TextAlign = SKTextAlign.Right };
        c.DrawText(Pct(w), r.Right - 7, top + 23, shadow);
        c.DrawText(Pct(w), r.Right - 8, top + 22, p);
    }

    private static void Bar(SKCanvas c, bool claude, Window? w, SKRect r, bool paceMarker = true)
    {
        float radius = Math.Min(8, r.Height / 2);
        using var track = new SKPaint { Color = Track, IsAntialias = true };
        c.DrawRoundRect(r, radius, radius, track);
        if (w == null) return;

        var frac = (float)Math.Clamp(w.Percent / 100, 0, 1);
        if (frac > 0)
        {
            var fillRect = new SKRect(r.Left, r.Top, r.Left + Math.Max(radius * 2, r.Width * frac), r.Bottom);
            using var fill = new SKPaint { Color = BarColor(claude, w.Percent), IsAntialias = true };
            c.DrawRoundRect(fillRect, radius, radius, fill);
        }

        // Pace marker: where the bar would be if usage were spread evenly over the window.
        var elapsed = w.Elapsed;
        if (paceMarker && elapsed >= 0)
        {
            var x = r.Left + r.Width * (float)elapsed;
            using var tick = new SKPaint { Color = Text, StrokeWidth = 4, IsAntialias = true };
            c.DrawLine(x, r.Top - 3, x, r.Bottom + 3, tick);
        }
    }

    /// <summary>One window filling the key: ring with the percent inside, reset time in the gap.</summary>
    private static void Gauge(SKCanvas c, bool claude, Window? w, string label)
    {
        var center = new SKPoint(Size / 2f, 86);
        const float radius = 42;
        const float stroke = 15;
        var rect = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);

        using var track = new SKPaint { Color = Track, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawArc(rect, 135, 270, false, track);

        if (w != null)
        {
            var frac = (float)Math.Clamp(w.Percent / 100, 0, 1);
            using var arc = new SKPaint { Color = BarColor(claude, w.Percent), Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            if (frac > 0) c.DrawArc(rect, 135, 270 * frac, false, arc);

            var elapsed = w.Elapsed;
            if (elapsed >= 0)
            {
                var angle = (135 + 270 * elapsed) * Math.PI / 180;
                var inner = new SKPoint(center.X + (float)Math.Cos(angle) * (radius - 8), center.Y + (float)Math.Sin(angle) * (radius - 8));
                var outer = new SKPoint(center.X + (float)Math.Cos(angle) * (radius + 11), center.Y + (float)Math.Sin(angle) * (radius + 11));
                using var tick = new SKPaint { Color = Text, StrokeWidth = 4, IsAntialias = true };
                c.DrawLine(inner, outer, tick);
            }
        }

        Label(c, Pct(w), center.X, center.Y + 6, 28, Text, SKTextAlign.Center);
        Label(c, label, center.X, center.Y + 27, 19, Muted, SKTextAlign.Center);
        if (w != null)
            Label(c, Countdown(w.ResetsAt), center.X, Size - 4, 21, Muted, SKTextAlign.Center);
    }

    private static void Message(SKCanvas c, string text)
    {
        var lines = text.Split(' ', '\n');
        float y = 88 - (lines.Length - 1) * 14;
        foreach (var line in lines)
        {
            Label(c, line, Size / 2f, y, 26, Muted, SKTextAlign.Center);
            y += 30;
        }
    }

    private static string Pct(Window? w) => w != null ? $"{w.Percent:0}%" : "–";

    private static string Tok(long? tokens) => tokens is { } t ? LocalTokenCounter.Format(t) : "–";

    /// <summary>Compact time until reset: "38m", "2h11m", "2d9h".</summary>
    internal static string Countdown(DateTimeOffset resetsAt)
    {
        var left = resetsAt - DateTimeOffset.UtcNow;
        if (resetsAt == DateTimeOffset.UnixEpoch || left <= TimeSpan.Zero) return "ahora";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d{left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h{left.Minutes:00}m";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }
}
