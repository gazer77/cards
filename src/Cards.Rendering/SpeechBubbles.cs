using SkiaSharp;

namespace Cards.Rendering;

/// <summary>
/// Timed messages shown next to the player they are about.
///
/// The messages themselves are the engine's status line, which describes one player's
/// action ("North bids 3"). Showing them in a single bar at the edge of the table makes
/// the reader work out who each line refers to; anchoring them to a seat says it
/// directly, which matters most in a four-player game where three of the seats are
/// acting on their own.
/// </summary>
internal sealed class SpeechBubbles
{
    /// <summary>How long a bubble stays fully visible before fading.</summary>
    private const float HoldMs = 2600f;
    private const float FadeMs = 700f;

    /// <summary>
    /// One live bubble per player. A seat that acts twice in quick succession replaces
    /// its own message rather than stacking, so a fast run of turns cannot bury the
    /// table under text.
    /// </summary>
    private readonly Dictionary<string, (string Text, long Start)> _bubbles = [];

    public bool Any => _bubbles.Count > 0;

    public void Post(string playerId, string text, long nowMs)
        => _bubbles[playerId] = (text, nowMs);

    public void Clear() => _bubbles.Clear();

    /// <summary>Drops bubbles whose fade has finished. Returns true if any were removed.</summary>
    public bool Expire(long nowMs)
    {
        var done = _bubbles
            .Where(kv => nowMs - kv.Value.Start > HoldMs + FadeMs)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in done) _bubbles.Remove(id);
        return done.Count > 0;
    }

    /// <summary>
    /// Draws each live bubble against its player's zone.
    /// <paramref name="anchorFor"/> supplies the seat's bounds, or null if that player
    /// has nothing on the table to point at — in which case the message is skipped
    /// rather than drawn somewhere arbitrary.
    /// </summary>
    public void Draw(SKCanvas canvas, SKImageInfo info, long nowMs, Func<string, SKRect?> anchorFor)
    {
        foreach (var (playerId, (text, start)) in _bubbles)
        {
            var anchor = anchorFor(playerId);
            if (anchor is null) continue;

            float age = nowMs - start;
            float alpha = age <= HoldMs ? 1f : Math.Clamp(1f - (age - HoldMs) / FadeMs, 0f, 1f);
            if (alpha <= 0f) continue;

            DrawBubble(canvas, info, text, anchor.Value, alpha);
        }
    }

    private static void DrawBubble(
        SKCanvas canvas, SKImageInfo info, string text, SKRect anchor, float alpha)
    {
        float size = MathF.Max(12f, MathF.Min(info.Width, info.Height) * 0.022f);
        using var font = new SKFont(SKTypeface.Default, size);

        const float padX = 12f, padY = 8f;
        float maxWidth = info.Width * 0.42f;

        var lines = Wrap(text, font, maxWidth - padX * 2);
        float lineH = size * 1.25f;

        float boxW = 0f;
        foreach (var line in lines) boxW = MathF.Max(boxW, font.MeasureText(line));
        boxW += padX * 2;
        float boxH = lines.Count * lineH + padY * 2;

        // Sit above the seat, and flip below it when that would leave the table —
        // the bubble for the player at the top of the screen has no room above.
        bool above = anchor.Top - boxH - 14f > 0;
        float top  = above ? anchor.Top - boxH - 14f : anchor.Bottom + 14f;

        // Keep the whole bubble on screen horizontally.
        float left = Math.Clamp(anchor.MidX - boxW / 2f, 6f, MathF.Max(6f, info.Width - boxW - 6f));

        var box = new SKRect(left, top, left + boxW, top + boxH);
        byte a = (byte)(alpha * 255);

        using var fill = new SKPaint
        {
            Color = new SKColor(0x0D, 0x25, 0x18, (byte)(alpha * 0xE0)),
            IsAntialias = true,
        };
        using var edge = new SKPaint
        {
            Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(alpha * 0x30)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        };

        canvas.DrawRoundRect(box, 10f, 10f, fill);
        DrawTail(canvas, box, anchor, above, fill);
        canvas.DrawRoundRect(box, 10f, 10f, edge);

        using var ink = new SKPaint { Color = new SKColor(0xF0, 0xEB, 0xE0, a), IsAntialias = true };

        float y = box.Top + padY + size * 0.85f;
        foreach (var line in lines)
        {
            canvas.DrawText(line, box.Left + padX, y, SKTextAlign.Left, font, ink);
            y += lineH;
        }
    }

    /// <summary>The pointer that makes it a speech bubble rather than a floating label.</summary>
    private static void DrawTail(SKCanvas canvas, SKRect box, SKRect anchor, bool above, SKPaint fill)
    {
        float tipX = Math.Clamp(anchor.MidX, box.Left + 16f, box.Right - 16f);
        float baseY = above ? box.Bottom : box.Top;
        float tipY  = above ? box.Bottom + 10f : box.Top - 10f;

        using var path = new SKPath();
        path.MoveTo(tipX - 8f, baseY);
        path.LineTo(tipX + 8f, baseY);
        path.LineTo(tipX, tipY);
        path.Close();

        canvas.DrawPath(path, fill);
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";

        foreach (var word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
                current = candidate;
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0) lines.Add(current);
        // A message that is entirely whitespace still needs a line, or the bubble
        // collapses to a sliver.
        return lines.Count > 0 ? lines : [text];
    }
}
