using Cards.Engine;
using Cards.Rendering;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// The declared layout engine is pure geometry, so it can be asked exactly where it put
/// things. What matters: a definition written for the bottom seat lands mirrored for the
/// seat across the table, zones sharing a region divide it, and an exact place lands
/// where it says.
/// </summary>
public sealed class DeclaredLayoutTests
{
    private static readonly SKImageInfo Canvas = new(1000, 800);

    private static GameState HandAndFoot(int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;
        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(1),
        };
        LogicRegistry.Create(definition).Initialize(state, seats, []);
        return state;
    }

    private static ZoneLayout Layout(IReadOnlyList<ZoneLayout> layouts, string zoneId)
        => layouts.Single(l => l.Zone.Id == zoneId);

    [Fact]
    public void Hand_and_foot_uses_the_declared_engine()
    {
        var state   = HandAndFoot(2);
        var layouts = ZoneLayoutEngine.Compute(state, Canvas);

        // Every zone the state holds is on the table — nothing is silently unplaced.
        Assert.Equal(state.Zones.Count, layouts.Count);
    }

    [Fact]
    public void The_bottom_seat_and_the_seat_across_mirror_each_other()
    {
        var state   = HandAndFoot(2);
        var layouts = ZoneLayoutEngine.Compute(state, Canvas);

        var mine   = Layout(layouts, "hand:player0").Bounds;
        var theirs = Layout(layouts, "hand:player1").Bounds;

        // Same shape, reflected through the table's centre.
        Assert.Equal(mine.Width,  theirs.Width,  1f);
        Assert.Equal(mine.Height, theirs.Height, 1f);
        Assert.Equal(Canvas.Height - mine.MidY, theirs.MidY, 1f);
        Assert.True(mine.MidY > Canvas.Height / 2f);
        Assert.True(theirs.MidY < Canvas.Height / 2f);

        // And the far seat's cards are turned to face it.
        Assert.Equal(180f, Layout(layouts, "hand:player1").RotationDegrees);
        Assert.Equal(0f,   Layout(layouts, "hand:player0").RotationDegrees);
    }

    [Fact]
    public void An_exact_place_lands_where_it_says()
    {
        var state   = HandAndFoot(2);
        var layouts = ZoneLayoutEngine.Compute(state, Canvas);

        // The meld area is declared at x 50%, y 75%, 88% wide, 20% high — for the bottom seat.
        var meld = Layout(layouts, "meld:player0").Bounds;
        Assert.Equal(0.50f * Canvas.Width,  meld.MidX,   1f);
        Assert.Equal(0.75f * Canvas.Height, meld.MidY,   1f);
        Assert.Equal(0.88f * Canvas.Width,  meld.Width,  1f);
        Assert.Equal(0.20f * Canvas.Height, meld.Height, 1f);

        // The same declaration, turned for the seat across: x and y both reflected.
        var across = Layout(layouts, "meld:player1").Bounds;
        Assert.Equal(Canvas.Width  - meld.MidX, across.MidX, 1f);
        Assert.Equal(Canvas.Height - meld.MidY, across.MidY, 1f);
    }

    [Fact]
    public void Zones_sharing_a_region_divide_it_between_them()
    {
        var state   = HandAndFoot(2);
        var layouts = ZoneLayoutEngine.Compute(state, Canvas);

        var deck    = Layout(layouts, "deck").Bounds;
        var discard = Layout(layouts, "discard").Bounds;

        // Side by side, not on top of each other, and both within the centre band.
        Assert.False(deck.IntersectsWith(discard));
        Assert.True(deck.Right <= discard.Left + 1f);
        Assert.Equal(deck.MidY, discard.MidY, 1f);
    }

    [Fact]
    public void Side_seats_are_turned_on_their_side()
    {
        var state   = HandAndFoot(4);
        var layouts = ZoneLayoutEngine.Compute(state, Canvas);

        var right = Layout(layouts, "hand:player1");
        var left  = Layout(layouts, "hand:player3");

        Assert.Equal(90f,  right.RotationDegrees);
        Assert.Equal(270f, left.RotationDegrees);
        Assert.True(right.Bounds.MidX > Canvas.Width * 0.75f);
        Assert.True(left.Bounds.MidX  < Canvas.Width * 0.25f);

        // A seat band on the side is tall and narrow — width and height traded places.
        Assert.True(right.Bounds.Height > right.Bounds.Width);
    }

    public static TheoryData<string, int> EveryTable
    {
        get
        {
            var data   = new TheoryData<string, int>();
            var loader = new GameLoader(new EmbeddedGameAssetSource());
            foreach (var def in loader.LoadAllAsync().GetAwaiter().GetResult())
                for (int seats = def.MinPlayers; seats <= def.MaxPlayers; seats++)
                    data.Add(def.Id, seats);
            return data;
        }
    }

    /// <summary>
    /// Every shipped game, at every seat count it advertises, on the declared engine:
    /// every zone the state holds is on the table, inside the canvas, and no two zones
    /// overlap. The hand-tuned engine placed only the zones it knew by name and let
    /// the rest fall off; this is the check that nothing does now.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryTable))]
    public void Every_zone_is_on_the_table_and_none_overlap(string gameId, int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync(gameId).GetAwaiter().GetResult()!;
        Assert.True(definition.Zones.Any(z => z.Layout is not null), $"{gameId} is not on the declared engine.");

        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(1) };
        LogicRegistry.Create(definition).Initialize(state, seats, []);

        var layouts = ZoneLayoutEngine.Compute(state, Canvas);
        Assert.Equal(state.Zones.Count, layouts.Count);

        var canvas = new SKRect(0, 0, Canvas.Width, Canvas.Height);
        foreach (var l in layouts)
            Assert.True(canvas.Contains(l.Bounds), $"{gameId}/{seats}p: {l.Zone.Id} at {l.Bounds} is off the table.");

        for (int i = 0; i < layouts.Count; i++)
            for (int j = i + 1; j < layouts.Count; j++)
            {
                var a = layouts[i].Bounds; var b = layouts[j].Bounds;
                a.Inflate(-1f, -1f); b.Inflate(-1f, -1f);
                Assert.False(a.IntersectsWith(b),
                    $"{gameId}/{seats}p: {layouts[i].Zone.Id} {layouts[i].Bounds} overlaps {layouts[j].Zone.Id} {layouts[j].Bounds}.");
            }
    }
}
