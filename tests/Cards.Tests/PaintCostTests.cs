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
/// table. Reported rather than asserted: this machine is not the browser, and
/// WebAssembly measured roughly 19x slower on the same work.
///
/// Run with: PAINT_COST=1 dotnet test --filter PaintCost
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class PaintCostTests
{
    private const int Cards  = 52;
    private const int Frames = 60;

    private static double MeasureMsPerFrame(Action<SKCanvas, SKRect, Card> draw)
    {
        var card = new Card(Suit.Spades, Rank.Queen, isFaceUp: true);

        using var bitmap = new SKBitmap(1280, 800);
        using var canvas = new SKCanvas(bitmap);

        // Warm up: the first paint of each card pays for its cache entry.
        for (int i = 0; i < Cards; i++)
            draw(canvas, Rect(i), card);

        var clock = Stopwatch.StartNew();
        for (int f = 0; f < Frames; f++)
            for (int i = 0; i < Cards; i++)
                draw(canvas, Rect(i), card);
        clock.Stop();

        return clock.Elapsed.TotalMilliseconds / Frames;

        static SKRect Rect(int i)
        {
            float x = 10 + (i % 13) * 90;
            float y = 10 + (i / 13) * 160;
            return new SKRect(x, y, x + 100, y + 150);
        }
    }

    [Fact]
    public void Report_card_paint_cost()
    {
        if (Environment.GetEnvironmentVariable("PAINT_COST") is not ("1" or "true"))
            return;

        var skin = new DefaultCardSkin();

        CardRenderer.ClearCache();
        double cached = MeasureMsPerFrame((c, r, card) => CardRenderer.DrawCardFace(c, r, card, skin));

        Console.WriteLine(
            $"[paint] {Cards} card faces: {cached:F2} ms/frame ({cached / Cards:F3} ms/card) " +
            $"=> {1000.0 / cached:F0} fps ceiling on this machine, before layout or backing-store cost.");
    }

    /// <summary>
    /// The cache must actually be faster, not merely present. Asserted as a ratio
    /// rather than an absolute time so it means the same thing on any machine.
    /// </summary>
    [Fact]
    public void Cached_cards_are_much_cheaper_than_redrawing_them()
    {
        var skin = new DefaultCardSkin();
        var card = new Card(Suit.Hearts, Rank.King, isFaceUp: true);

        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        var rect = new SKRect(10, 10, 110, 160);

        CardRenderer.ClearCache();
        CardRenderer.DrawCardFace(canvas, rect, card, skin);   // populate

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++)
            CardRenderer.DrawCardFace(canvas, rect, card, skin);
        double cachedMs = clock.Elapsed.TotalMilliseconds;

        // Defeat the cache by asking for a different size every time, which is the
        // same work the renderer used to do on every card of every frame.
        clock.Restart();
        for (int i = 0; i < 2000; i++)
        {
            CardRenderer.ClearCache();
            CardRenderer.DrawCardFace(canvas, rect, card, skin);
        }
        double uncachedMs = clock.Elapsed.TotalMilliseconds;

        Assert.True(cachedMs * 3 < uncachedMs,
            $"Cached draws ({cachedMs:F1} ms) are not meaningfully cheaper than " +
            $"rebuilding ({uncachedMs:F1} ms) — the cache is not doing its job.");
    }

    [Fact]
    public void Cache_is_bounded()
    {
        var skin = new DefaultCardSkin();
        var card = new Card(Suit.Clubs, Rank.Two, isFaceUp: true);

        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);

        CardRenderer.ClearCache();

        // A window being dragged resizes continuously; every size is a new entry.
        for (int i = 0; i < 900; i++)
            CardRenderer.DrawCardFace(canvas, new SKRect(0, 0, 40 + i * 0.5f, 60 + i * 0.75f), card, skin);

        Assert.True(CardRenderer.CacheSize <= 512,
            $"Cache grew to {CardRenderer.CacheSize} entries — it is unbounded.");
    }

    [Fact]
    public void Cached_and_uncached_cards_look_the_same()
    {
        var skin = new DefaultCardSkin();
        var card = new Card(Suit.Diamonds, Rank.Seven, isFaceUp: true);
        var rect = new SKRect(20, 20, 140, 190);

        SKBitmap Render()
        {
            var bmp = new SKBitmap(200, 240);
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.White);
            CardRenderer.DrawCardFace(c, rect, card, skin);
            return bmp;
        }

        CardRenderer.ClearCache();
        using var first = Render();    // renders and caches
        using var second = Render();   // served from cache

        // Caching must be invisible. Compared on ink coverage rather than pixel
        // equality, since going through an offscreen surface can shift a subpixel.
        double a = Coverage(first), b = Coverage(second);
        Assert.True(Math.Abs(a - b) < 0.02,
            $"Cached card differs from a freshly drawn one: {a:P2} vs {b:P2} ink.");

        static double Coverage(SKBitmap bmp)
        {
            int ink = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.Red < 230 || p.Green < 230 || p.Blue < 230) ink++;
                }
            return (double)ink / (bmp.Width * bmp.Height);
        }
    }
}
