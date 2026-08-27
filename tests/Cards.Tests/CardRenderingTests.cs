using SkiaSharp;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Covers card painting, which used to be verifiable only by launching a platform head.
///
/// Suits are drawn as vector paths rather than Unicode text (see SuitShapes), so these
/// assert on actual painted pixels — the previous failure mode was a font silently
/// resolving to nothing and every suit rendering as a tofu box, which no compile-time
/// check could catch.
/// </summary>
public sealed class CardRenderingTests
{
    private static SKBitmap RenderCard(Card card, ICardSkin skin, int w = 120, int h = 168)
    {
        var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            CardRenderer.DrawCardFace(canvas, new SKRect(4, 4, w - 4, h - 4), card, skin);
        }
        return bitmap;
    }

    /// <summary>Fraction of pixels that are neither white nor near-white.</summary>
    private static double InkCoverage(SKBitmap bmp)
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

    public static TheoryData<Suit> AllSuits =>
        [Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades];

    [Theory]
    [MemberData(nameof(AllSuits))]
    public void Every_suit_paints_something(Suit suit)
    {
        using var bmp = RenderCard(new Card(suit, Rank.Seven, isFaceUp: true), new DefaultCardSkin());

        // A seven has seven pips plus two corner suits; if the suit failed to draw at
        // all, coverage would collapse to just the border and rank text.
        Assert.True(InkCoverage(bmp) > 0.04,
            $"{suit} painted almost nothing — coverage {InkCoverage(bmp):P2}.");
    }

    /// <summary>
    /// The four suits must be visually distinct. If they ever collapsed to the same
    /// fallback shape (which is exactly what a missing font produced) the game becomes
    /// unplayable while still looking superficially fine.
    /// </summary>
    [Fact]
    public void Suits_are_distinguishable_from_each_other()
    {
        var skin = new DefaultCardSkin();
        var coverage = new Dictionary<Suit, double>();

        foreach (var suit in new[] { Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades })
        {
            using var bmp = RenderCard(new Card(suit, Rank.Ace, isFaceUp: true), skin);
            coverage[suit] = InkCoverage(bmp);
        }

        // An ace is dominated by one large centre suit, so ink coverage separates the
        // shapes: a diamond is solid, a club is lobed, a heart and spade differ at the tip.
        var values = coverage.Values.OrderBy(v => v).ToList();
        Assert.True(values.Last() - values.First() > 0.01,
            "All four suits produced near-identical coverage, which suggests they are " +
            "rendering as the same shape: " +
            string.Join(", ", coverage.Select(kv => $"{kv.Key}={kv.Value:P2}")));
    }

    [Theory]
    [MemberData(nameof(AllSuits))]
    public void Simplified_skin_paints_every_suit(Suit suit)
    {
        using var bmp = RenderCard(new Card(suit, Rank.Eight, isFaceUp: true), new SimpleCardSkin());
        Assert.True(InkCoverage(bmp) > 0.04,
            $"{suit} on the simplified skin painted almost nothing.");
    }

    [Fact]
    public void Card_back_paints_without_a_face()
    {
        var bitmap = new SKBitmap(120, 168);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            CardRenderer.DrawCardBack(canvas, new SKRect(4, 4, 116, 164), new DefaultCardSkin());
        }
        using (bitmap)
            Assert.True(InkCoverage(bitmap) > 0.3, "Card back barely painted.");
    }
}
