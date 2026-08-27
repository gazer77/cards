using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

/// <summary>
/// The four suits as vector paths.
///
/// Suits used to be drawn as the Unicode characters SPADE/HEART/DIAMOND/CLUB, resolved
/// through <c>SKFontManager.Default.MatchCharacter</c>. That works on a desktop or phone
/// with a symbol font installed and fails completely in WebAssembly, where there is no
/// system font manager — every suit rendered as a tofu box.
///
/// Drawing them instead of typesetting them removes the dependency entirely: no font to
/// embed, nothing to license, no payload, and — the real win — a card looks identical on
/// Android, iOS, Windows and the web instead of inheriting whatever symbol font each
/// platform happens to ship.
///
/// Paths are authored in a 100x100 box, y down, and scaled to fit on use.
/// </summary>
internal static class SuitShapes
{
    /// <summary>
    /// Fills <paramref name="suit"/> to fit <paramref name="box"/>.
    /// The shape is fitted to the box exactly, so callers control the aspect ratio.
    /// </summary>
    public static void Draw(SKCanvas canvas, Suit suit, SKRect box, SKPaint paint)
    {
        using var path = Build(suit);
        path.Transform(SKMatrix.CreateScaleTranslation(
            box.Width / 100f, box.Height / 100f, box.Left, box.Top));
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// Box for a suit that would have been drawn as text of <paramref name="size"/> with
    /// its baseline at <paramref name="baselineY"/> and its left edge at
    /// <paramref name="left"/>. Keeps the previous text-based layout maths working.
    /// </summary>
    public static SKRect BoxFromBaseline(float left, float baselineY, float size)
    {
        float h = size * 0.72f;   // glyphs sit about this tall above the baseline
        float w = size * 0.70f;
        return new SKRect(left, baselineY - h, left + w, baselineY);
    }

    /// <summary>As <see cref="BoxFromBaseline"/>, but horizontally centred on <paramref name="centerX"/>.</summary>
    public static SKRect BoxFromBaselineCentered(float centerX, float baselineY, float size)
    {
        float h = size * 0.72f;
        float w = size * 0.70f;
        return new SKRect(centerX - w / 2f, baselineY - h, centerX + w / 2f, baselineY);
    }

    /// <summary>Width a suit occupies at a given nominal size — the analogue of MeasureText.</summary>
    public static float Width(float size) => size * 0.70f;

    private static SKPath Build(Suit suit) => suit switch
    {
        Suit.Hearts   => Heart(),
        Suit.Diamonds => Diamond(),
        Suit.Spades   => Spade(),
        _             => Club(),
    };

    private static SKPath Diamond()
    {
        var p = new SKPath();
        p.MoveTo(50, 0);
        p.LineTo(96, 50);
        p.LineTo(50, 100);
        p.LineTo(4, 50);
        p.Close();
        return p;
    }

    private static SKPath Heart()
    {
        var p = new SKPath();
        p.MoveTo(50, 100);
        p.CubicTo(18, 74, 0, 55, 0, 34);
        p.CubicTo(0, 14, 14, 0, 30, 0);
        p.CubicTo(40, 0, 47, 6, 50, 13);
        p.CubicTo(53, 6, 60, 0, 70, 0);
        p.CubicTo(86, 0, 100, 14, 100, 34);
        p.CubicTo(100, 55, 82, 74, 50, 100);
        p.Close();
        return p;
    }

    private static SKPath Spade()
    {
        var p = new SKPath();
        // Inverted heart body.
        p.MoveTo(50, 0);
        p.CubicTo(50, 0, 100, 36, 100, 60);
        p.CubicTo(100, 76, 89, 86, 77, 86);
        p.CubicTo(67, 86, 59, 81, 55, 74);
        // Stem flaring out to the base.
        p.CubicTo(56, 84, 60, 94, 70, 100);
        p.LineTo(30, 100);
        p.CubicTo(40, 94, 44, 84, 45, 74);
        p.CubicTo(41, 81, 33, 86, 23, 86);
        p.CubicTo(11, 86, 0, 76, 0, 60);
        p.CubicTo(0, 36, 50, 0, 50, 0);
        p.Close();
        return p;
    }

    private static SKPath Club()
    {
        // Three lobes plus a stem. Winding fill unions the overlaps for free.
        var p = new SKPath { FillType = SKPathFillType.Winding };
        p.AddCircle(50, 23, 23);
        p.AddCircle(24, 63, 23);
        p.AddCircle(76, 63, 23);

        p.MoveTo(45, 60);
        p.CubicTo(45, 80, 39, 93, 29, 100);
        p.LineTo(71, 100);
        p.CubicTo(61, 93, 55, 80, 55, 60);
        p.Close();
        return p;
    }
}
