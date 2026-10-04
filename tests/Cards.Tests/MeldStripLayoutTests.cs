using Cards.Engine;
using Cards.Rendering;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// Hand and Foot's meld strips, measured. The strip sized its slots for three rows and
/// drew them on one, so every meld was small; and the viewer's own strip came out
/// smaller than the opponent's, which left out the room for its rank headings.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class MeldStripLayoutTests
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick { add { } remove { } }
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Theory]
    [InlineData(952, 890)]    // the window the report came from
    [InlineData(1400, 900)]
    public void Both_strips_draw_their_melds_the_same_size_and_on_one_row(int width, int height)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hand-and-foot")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(3) };
        LogicRegistry.Create(definition).Initialize(state, 2, []);

        // A meld of fours in each side's strip, and nothing else there.
        int uid = 9900;
        foreach (var melds in state.Zones.Values.Where(z => z.Id.StartsWith("meld")))
        {
            melds.Clear();
            melds.AddGroup([.. Enumerable.Range(0, 3).Select(_ => new Card(Suit.Clubs, Rank.Four, isFaceUp: true) { Uid = uid++ })]);
        }

        var renderer = new CardTableRenderer(new StubDriver()) { GameState = state, BottomInset = 70f };
        var info = new SKImageInfo(width, height);
        using var surface = SKSurface.Create(info);
        renderer.Paint(surface.Canvas, info);
        Thread.Sleep(400);   // let the deal's slide-in finish, as a frame loop would
        renderer.Paint(surface.Canvas, info);

        // A stacked meld shows its top card: the third of each.
        var mine   = renderer.CardRects.Single(r => r.Uid == 9902).Rect;
        var theirs = renderer.CardRects.Single(r => r.Uid == 9905).Rect;
        Assert.Equal(theirs.Width, mine.Width, 1f);

        // Twelve slots on one row of the strip's width: the card takes most of a twelfth.
        var strip = ZoneLayoutEngine.Compute(state, info).Single(l => l.Zone.Id == "meld:player0").Bounds;
        Assert.True(mine.Width > strip.Width / 12f * 0.75f,
            $"a meld card {mine.Width:F0} wide in a strip {strip.Width:F0} wide — sized for more rows than it has");
    }
}
