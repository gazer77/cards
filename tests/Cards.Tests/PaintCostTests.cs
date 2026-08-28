using System.Diagnostics;
using SkiaSharp;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Measures how long it takes to paint a card, to explain animation smoothness.
///
/// A fly-in is sampled once per painted frame, so the frame rate the renderer can
/// actually sustain sets how many positions a card is drawn at on its way across the
/// table. Reported as a diagnostic rather than asserted: this machine is not the
/// browser, and WebAssembly runs the same Skia work several times slower.
///
/// Run with: PAINT_COST=1 dotnet test --filter PaintCost
/// </summary>
public sealed class PaintCostTests
{
    [Fact]
    public void Report_card_paint_cost()
    {
        if (Environment.GetEnvironmentVariable("PAINT_COST") is not ("1" or "true"))
            return;

        var skin = new DefaultCardSkin();
        var card = new Card(Suit.Spades, Rank.Queen, isFaceUp: true);

        using var bitmap = new SKBitmap(1280, 800);
        using var canvas = new SKCanvas(bitmap);

        // Warm up: first paint pays for lazy typeface and path setup.
        for (int i = 0; i < 20; i++)
            CardRenderer.DrawCardFace(canvas, new SKRect(10, 10, 110, 150), card, skin);

        const int cards = 52;
        const int frames = 60;

        var clock = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
            for (int i = 0; i < cards; i++)
            {
                float x = 10 + (i % 13) * 90;
                float y = 10 + (i / 13) * 160;
                CardRenderer.DrawCardFace(canvas, new SKRect(x, y, x + 100, y + 150), card, skin);
            }
        clock.Stop();

        double perFrame = clock.Elapsed.TotalMilliseconds / frames;
        double perCard  = perFrame / cards;

        Console.WriteLine(
            $"[paint] {cards} card faces: {perFrame:F2} ms/frame ({perCard:F3} ms/card) " +
            $"=> {1000.0 / perFrame:F0} fps ceiling on this machine, before layout or backing-store cost.");

        Assert.True(perFrame > 0);
    }
}
