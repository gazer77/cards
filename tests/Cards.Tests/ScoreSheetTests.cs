using Cards.Engine;
using Cards.Rendering;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// The score sheet — one reading of the scores for the panel the table draws and the
/// sheet a phone opens — and the panel's ways of getting out of the way: not drawn at all
/// on a phone, folded down to a trophy on a desktop.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class ScoreSheetTests
{
    private sealed class NoDriver : IAnimationDriver
    {
        public event Action? Tick { add { } remove { } }   // never ticks: these tests paint by hand
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Fact]
    public void A_row_per_player_with_each_rounds_score_and_the_total()
    {
        var state = TestTable.Build("golf", 3);
        state.ScoreHistory.Add(new ScoreRound(1, new() { ["player0"] = 4, ["player1"] = 9 }));
        state.ScoreHistory.Add(new ScoreRound(2, new() { ["player0"] = -2, ["player1"] = 5, ["player2"] = 7 }));
        state.Scores["player0"] = 2; state.Scores["player1"] = 14; state.Scores["player2"] = 7;

        var sheet = ScoreSheet.For(state)!;

        Assert.Equal([1, 2], sheet.Rounds);
        var me = sheet.Rows[0];
        Assert.True(me.IsMe);
        Assert.Equal([4, -2], me.ByRound);
        Assert.Equal(2, me.Total);
        Assert.Equal([null, 7], sheet.Rows[2].ByRound);   // sat out the first round's scoring
    }

    [Fact]
    public void The_house_has_no_row()
    {
        var state = TestTable.Build("blackjack", 2);
        var sheet = ScoreSheet.For(state)!;
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal("Chips", sheet.Label);
    }

    [Fact]
    public void A_game_with_teams_and_no_card_of_its_own_is_scored_by_team()
    {
        var state = TestTable.Build("euchre", 4);
        state.Scores[state.Teams[0].Id] = 3;

        var sheet = ScoreSheet.For(state)!;
        Assert.Equal(state.Teams.Count, sheet.Rows.Count);
        Assert.Contains(sheet.Rows, r => r.IsMe);   // the viewer's own side is marked
    }

    [Fact]
    public void Nothing_to_show_before_any_score_is_kept_in_a_game_without_a_card()
    {
        var state = TestTable.Build("war", 2);
        state.Scores.Clear();
        state.ScoreHistory.Clear();
        Assert.Null(ScoreSheet.For(state));
    }

    [Fact]
    public void On_a_phone_the_table_does_not_draw_the_card()
    {
        var state = TestTable.Build("golf", 2);
        var renderer = new CardTableRenderer(new NoDriver()) { GameState = state };

        using var bitmap = new SKBitmap(900, 700);
        using var canvas = new SKCanvas(bitmap);
        renderer.Paint(canvas, new SKImageInfo(900, 700));
        Assert.NotNull(renderer.ScoreCardBounds);

        renderer.ShowScoreCard = false;
        renderer.Paint(canvas, new SKImageInfo(900, 700));
        Assert.Null(renderer.ScoreCardBounds);
    }

    [Fact]
    public void Folded_it_is_a_small_badge_and_a_tap_opens_it_again()
    {
        var state = TestTable.Build("golf", 2);
        var renderer = new CardTableRenderer(new NoDriver()) { GameState = state };
        using var bitmap = new SKBitmap(900, 700);
        using var canvas = new SKCanvas(bitmap);

        renderer.Paint(canvas, new SKImageInfo(900, 700));
        var open = renderer.ScoreCardBounds!.Value;

        renderer.ScoreCardCollapsed = true;
        renderer.Paint(canvas, new SKImageInfo(900, 700));
        var badge = renderer.ScoreCardBounds!.Value;
        Assert.True(badge.Width * badge.Height < open.Width * open.Height / 4);

        // A tap on the badge unfolds it.
        renderer.OnPointerDown(new SKPoint(badge.MidX, badge.MidY));
        renderer.OnPointerUp(new SKPoint(badge.MidX, badge.MidY));
        Assert.False(renderer.ScoreCardCollapsed);
    }
}
