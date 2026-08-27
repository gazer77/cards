using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

public static class CardRenderer
{
    private static readonly SKColor ShadowColor = new(0x00, 0x00, 0x00, 0x55);

    // Rank text (A 2–10 J Q K) is plain ASCII — the default typeface always covers it.
    // Suits are NOT text: see SuitShapes for why they are drawn as vector paths.
    private static readonly SKTypeface RankTypeface = SKTypeface.Default;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Draw a card respecting card.IsFaceUp.</summary>
    public static void DrawCard(SKCanvas canvas, SKRect bounds, Card card, ICardSkin skin)
    {
        DrawShadow(canvas, bounds);
        if (card.IsFaceUp)
            DrawFace(canvas, bounds, card, skin);
        else
            DrawBack(canvas, bounds, skin);
    }

    /// <summary>Always draw the card face-up, regardless of card.IsFaceUp.</summary>
    public static void DrawCardFace(SKCanvas canvas, SKRect bounds, Card card, ICardSkin skin)
    {
        DrawShadow(canvas, bounds);
        DrawFace(canvas, bounds, card, skin);
    }

    public static void DrawCardBack(SKCanvas canvas, SKRect bounds, ICardSkin skin)
    {
        DrawShadow(canvas, bounds);
        DrawBack(canvas, bounds, skin);
    }

    public static void DrawEmptySlot(SKCanvas canvas, SKRect bounds, ITableTheme theme, string? label = null)
    {
        float r = bounds.Width * 0.08f;
        using var paint = new SKPaint
        {
            Color = theme.ZoneOutlineColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            PathEffect = SKPathEffect.CreateDash([6f, 4f], 0f),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(bounds, r, r, paint);

        if (label is not null)
            DrawZoneLabel(canvas, bounds, label, theme.ZoneLabelColor);
    }

    // ── Card face dispatch ────────────────────────────────────────────────────

    private static void DrawFace(SKCanvas canvas, SKRect bounds, Card card, ICardSkin skin)
    {
        if (skin.FaceStyle == CardFaceStyle.Simplified)
        {
            DrawSimplifiedFace(canvas, bounds, card, skin);
            return;
        }
        DrawClassicFace(canvas, bounds, card, skin);
    }

    // ── Classic face (pip grid + small corner labels) ─────────────────────────

    private static void DrawClassicFace(SKCanvas canvas, SKRect bounds, Card card, ICardSkin skin)
    {
        float r = bounds.Width * skin.CornerRadiusFraction;

        using (var bg = new SKPaint { Color = skin.FaceColor, IsAntialias = true })
            canvas.DrawRoundRect(bounds, r, r, bg);
        using (var border = new SKPaint { Color = skin.FaceBorderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawRoundRect(bounds, r, r, border);

        var suitColor = card.IsRed ? skin.RedSuitColor : skin.BlackSuitColor;
        string rank = RankLabel(card.Rank);

        float pad    = bounds.Width  * 0.09f;
        float rankSz = bounds.Height * 0.13f;
        float suitSz = bounds.Height * 0.11f;

        // Top-left corner
        DrawCornerPair(canvas, rank, card.Suit, bounds.Left + pad, bounds.Top + pad + rankSz, rankSz, suitSz, suitColor);

        // Bottom-right corner (rotated 180°)
        canvas.Save();
        canvas.RotateDegrees(180f, bounds.MidX, bounds.MidY);
        DrawCornerPair(canvas, rank, card.Suit, bounds.Left + pad, bounds.Top + pad + rankSz, rankSz, suitSz, suitColor);
        canvas.Restore();

        DrawCenter(canvas, card, bounds, suitColor, skin);
    }

    // ── Simplified face (phone skin) ──────────────────────────────────────────
    //
    //  Layout:
    //    ┌─────────┐
    //    │ ♠       │  ← small suit in upper-left corner
    //    │         │
    //    │    8    │  ← enormous rank centered on the card
    //    │         │
    //    └─────────┘

    private static void DrawSimplifiedFace(SKCanvas canvas, SKRect bounds, Card card, ICardSkin skin)
    {
        float r = bounds.Width * skin.CornerRadiusFraction;
        var suitColor = card.IsRed ? skin.RedSuitColor : skin.BlackSuitColor;

        using (var bg = new SKPaint { Color = skin.FaceColor, IsAntialias = true })
            canvas.DrawRoundRect(bounds, r, r, bg);
        using (var border = new SKPaint { Color = skin.FaceBorderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true })
            canvas.DrawRoundRect(bounds, r, r, border);

        string rank = RankLabel(card.Rank);
        float pad = bounds.Width * 0.07f;

        // ── Small suit — upper-left corner ────────────────────────────────────
        float suitSz = bounds.Height * 0.15f;
        using (var paint = new SKPaint { Color = suitColor, IsAntialias = true })
            SuitShapes.Draw(canvas, card.Suit,
                SuitShapes.BoxFromBaseline(bounds.Left + pad, bounds.Top + pad + suitSz, suitSz),
                paint);

        // ── Huge rank — centered on the card ─────────────────────────────────
        float rankSz  = bounds.Height * 0.75f;
        float maxRankW = bounds.Width - pad * 2f;

        // Shrink proportionally if the text (e.g. "10") would overflow width
        using (var probe = new SKFont(RankTypeface, rankSz))
        {
            float probeW = probe.MeasureText(rank);
            if (probeW > maxRankW)
                rankSz *= maxRankW / probeW;
        }

        using (var paint = new SKPaint { Color = suitColor, IsAntialias = true })
        using (var font  = new SKFont(RankTypeface, rankSz))
        {
            float textW = font.MeasureText(rank);
            // Visually center: baseline sits ~38 % of font size below mid
            float x = bounds.MidX - textW / 2f;
            float y = bounds.MidY + rankSz * 0.38f;
            canvas.DrawText(rank, x, y, font, paint);
        }
    }

    // ── Corner pair (rank stacked above suit) ─────────────────────────────────

    private static void DrawCornerPair(SKCanvas canvas, string rank, Suit suit,
        float x, float baselineY, float rankSz, float suitSz, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };

        using (var font = new SKFont(RankTypeface, rankSz))
            canvas.DrawText(rank, x, baselineY, font, paint);

        SuitShapes.Draw(canvas, suit,
            SuitShapes.BoxFromBaseline(x, baselineY + suitSz * 1.1f, suitSz), paint);
    }

    // ── Center symbol (classic) ───────────────────────────────────────────────

    private static void DrawCenter(SKCanvas canvas, Card card, SKRect bounds, SKColor suitColor, ICardSkin skin)
    {
        float cx = bounds.MidX;
        float cy = bounds.MidY;

        if (card.Rank is Rank.Ace)
        {
            float sz = bounds.Width * 0.55f;
            DrawCenteredGlyph(canvas, card.Suit, cx, cy + sz * 0.38f, sz, suitColor);
        }
        else if (card.Rank is Rank.Jack or Rank.Queen or Rank.King)
        {
            string label  = RankLabel(card.Rank);
            float labelSz = bounds.Height * 0.32f;
            float suitSz  = bounds.Height * 0.18f;
            var   color   = card.IsRed ? skin.RedSuitColor : skin.BlackSuitColor;

            DrawCenteredRank(canvas, label, cx, cy - labelSz * 0.1f,  labelSz, color);
            DrawCenteredGlyph(canvas, card.Suit,  cx, cy + labelSz * 0.6f, suitSz,  color);
        }
        else
        {
            DrawPipGrid(canvas, card, bounds, suitColor);
        }
    }

    private static void DrawPipGrid(SKCanvas canvas, Card card, SKRect bounds, SKColor color)
    {
        (float c, float r)[] positions = (int)card.Rank switch
        {
            2  => [(0.5f,0.22f),(0.5f,0.78f)],
            3  => [(0.5f,0.18f),(0.5f,0.5f),(0.5f,0.82f)],
            4  => [(0.2f,0.22f),(0.8f,0.22f),(0.2f,0.78f),(0.8f,0.78f)],
            5  => [(0.2f,0.22f),(0.8f,0.22f),(0.5f,0.5f),(0.2f,0.78f),(0.8f,0.78f)],
            6  => [(0.2f,0.2f),(0.8f,0.2f),(0.2f,0.5f),(0.8f,0.5f),(0.2f,0.8f),(0.8f,0.8f)],
            7  => [(0.2f,0.2f),(0.8f,0.2f),(0.5f,0.35f),(0.2f,0.5f),(0.8f,0.5f),(0.2f,0.8f),(0.8f,0.8f)],
            8  => [(0.2f,0.18f),(0.8f,0.18f),(0.5f,0.32f),(0.2f,0.47f),(0.8f,0.47f),(0.5f,0.62f),(0.2f,0.82f),(0.8f,0.82f)],
            9  => [(0.2f,0.15f),(0.8f,0.15f),(0.2f,0.33f),(0.8f,0.33f),(0.5f,0.5f),(0.2f,0.67f),(0.8f,0.67f),(0.2f,0.85f),(0.8f,0.85f)],
            10 => [(0.2f,0.15f),(0.8f,0.15f),(0.5f,0.27f),(0.2f,0.37f),(0.8f,0.37f),(0.2f,0.63f),(0.8f,0.63f),(0.5f,0.73f),(0.2f,0.85f),(0.8f,0.85f)],
            _  => [],
        };

        float insetX  = bounds.Width  * 0.18f;
        float insetY  = bounds.Height * 0.18f;
        float innerW  = bounds.Width  - insetX * 2f;
        float innerH  = bounds.Height - insetY * 2f;
        float originX = bounds.Left + insetX;
        float originY = bounds.Top  + insetY;
        float pipSz   = MathF.Min(bounds.Width * 0.2f, bounds.Height * 0.13f);

        using var paint = new SKPaint { Color = color, IsAntialias = true };

        foreach (var (colF, rowF) in positions)
        {
            float px = originX + colF * innerW;
            float py = originY + rowF * innerH;
            SuitShapes.Draw(canvas, card.Suit,
                SuitShapes.BoxFromBaselineCentered(px, py + pipSz * 0.38f, pipSz), paint);
        }
    }

    // ── Card back ─────────────────────────────────────────────────────────────

    private static void DrawBack(SKCanvas canvas, SKRect bounds, ICardSkin skin)
    {
        float r = bounds.Width * skin.CornerRadiusFraction;

        using (var bg = new SKPaint { Color = skin.BackColor, IsAntialias = true })
            canvas.DrawRoundRect(bounds, r, r, bg);

        var inner = new SKRect(bounds.Left + 4f, bounds.Top + 4f, bounds.Right - 4f, bounds.Bottom - 4f);
        float ri = Math.Max(r - 3f, 2f);

        canvas.Save();
        using (var clipPath = new SKPath())
        {
            clipPath.AddRoundRect(inner, ri, ri);
            canvas.ClipPath(clipPath);

            using var stripe = new SKPaint
            {
                Color = skin.BackPatternColor,
                StrokeWidth = 2.5f,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
            };
            float step = 9f;
            float diag = inner.Height;
            for (float x = inner.Left - diag; x < inner.Right + diag; x += step)
                canvas.DrawLine(x, inner.Top, x + diag, inner.Bottom, stripe);
        }
        canvas.Restore();

        using (var border = new SKPaint { Color = skin.BackBorderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true })
            canvas.DrawRoundRect(inner, ri, ri, border);
    }

    // ── Shadow ────────────────────────────────────────────────────────────────

    private static void DrawShadow(SKCanvas canvas, SKRect bounds)
    {
        var shadow = new SKRect(bounds.Left + 2f, bounds.Top + 3f, bounds.Right + 2f, bounds.Bottom + 3f);
        float r = bounds.Width * 0.08f;
        using var paint = new SKPaint
        {
            Color = ShadowColor,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f),
        };
        canvas.DrawRoundRect(shadow, r, r, paint);
    }

    // ── Text helpers ──────────────────────────────────────────────────────────

    // Centered suit shape
    private static void DrawCenteredGlyph(SKCanvas canvas, Suit suit, float cx, float baselineY, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        SuitShapes.Draw(canvas, suit,
            SuitShapes.BoxFromBaselineCentered(cx, baselineY, size), paint);
    }

    // Centered text using RankTypeface (for rank labels like J Q K)
    private static void DrawCenteredRank(SKCanvas canvas, string text, float cx, float baselineY, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        using var font  = new SKFont(RankTypeface, size);
        float w = font.MeasureText(text);
        canvas.DrawText(text, cx - w / 2f, baselineY, font, paint);
    }

    private static void DrawZoneLabel(SKCanvas canvas, SKRect bounds, string label, SKColor color)
    {
        float size = bounds.Width * 0.15f;
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        using var font  = new SKFont(RankTypeface, size);
        float w = font.MeasureText(label);
        canvas.DrawText(label, bounds.MidX - w / 2f, bounds.MidY + size * 0.4f, font, paint);
    }

    // ── Label helpers ─────────────────────────────────────────────────────────

    private static string RankLabel(Rank rank) => rank switch
    {
        Rank.Ace   => "A",
        Rank.Jack  => "J",
        Rank.Queen => "Q",
        Rank.King  => "K",
        _          => ((int)rank).ToString(),
    };

}
