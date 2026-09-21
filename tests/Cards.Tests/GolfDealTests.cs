using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Golf's grids are dealt face-down and two cards are peeked. initial_face sat in the
/// zone definition unread, so "face": "owner" into a zone everyone may see dealt every
/// card face-up and the peek turned two that were already up: the game was played with
/// every grid exposed, and only a debug flag being switched off made that visible.
/// </summary>
public sealed class GolfDealTests
{
    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)]
    public void Each_grid_is_dealt_face_down_and_each_player_turns_two_of_their_choosing(int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);

        // Dealt face-down, every card. The choice of what to turn is the player's.
        foreach (var p in state.Players)
            Assert.Equal(0, state.Zones[$"grid:{p.Id}"].Cards.Count(c => c.IsFaceUp));
        Assert.Equal("peek", state.CurrentPhaseId);

        // Each seat turns the two it picks — here the LAST two, which the deal's old
        // "first two" could never have produced.
        for (int seat = 0; seat < seats; seat++)
        {
            Assert.Equal(seat, state.CurrentPlayerIndex);
            var grid = state.Zones[$"grid:{state.CurrentPlayer.Id}"];
            foreach (var pick in new[] { grid.Cards[5], grid.Cards[4] })
                logic.Apply(state, new GameAction("select_card", CardId: pick.Id, CardUid: pick.Uid));
            Assert.Equal(2, grid.Cards.Count(c => c.IsFaceUp));
            Assert.True(grid.Cards[4].IsFaceUp && grid.Cards[5].IsFaceUp);
        }

        Assert.Equal("play", state.CurrentPhaseId);
        Assert.Equal(0, state.CurrentPlayerIndex);

        // And the seat count is the seat count.
        Assert.Equal(seats, state.Players.Count);
        Assert.Equal(seats, state.Zones.Keys.Count(k => k.StartsWith("grid:")));
    }
}

/// <summary>
/// The Golf turn as reported broken: a drawn card could not be moved onto the grid, and
/// there was no way to turn a card over. Tapping a grid card while holding the draw swaps
/// them; discarding the draw unplayed owes a flip of one face-down card, which is the
/// only way a card turns over in play.
/// </summary>
public sealed class GolfTurnTests
{
    private static (GameState State, IGameLogic Logic, Zone Grid) InPlay()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);

        // Both seats peek their first two so play starts with player 0.
        for (int seat = 0; seat < 2; seat++)
        {
            var g = state.Zones[$"grid:{state.CurrentPlayer.Id}"];
            foreach (var pick in new[] { g.Cards[0], g.Cards[1] })
                logic.Apply(state, new GameAction("select_card", CardId: pick.Id, CardUid: pick.Uid));
        }
        Assert.Equal("play", state.CurrentPhaseId);
        return (state, logic, state.Zones[$"grid:{state.CurrentPlayer.Id}"]);
    }

    [Fact]
    public void Tapping_a_grid_card_while_holding_the_draw_swaps_them()
    {
        var (state, logic, grid) = InPlay();
        var me = state.CurrentPlayer.Id;
        logic.Apply(state, new GameAction("draw_from_deck"));
        var drawn = state.Zones[$"hand:{me}"].Cards.Single();
        var target = grid.Cards[3];
        Assert.False(target.IsFaceUp);

        logic.Apply(state, new GameAction("select_card", CardId: target.Id, CardUid: target.Uid));

        Assert.Same(drawn, grid.Cards[3]);
        Assert.True(drawn.IsFaceUp);
        Assert.Same(target, state.Zones["discard"].Cards[^1]);
        Assert.NotEqual(me, state.CurrentPlayer.Id);
    }

    [Fact]
    public void Discarding_the_draw_unplayed_owes_a_flip_of_a_face_down_card()
    {
        var (state, logic, grid) = InPlay();
        var me = state.CurrentPlayer.Id;
        logic.Apply(state, new GameAction("draw_from_deck"));
        var drawn = state.Zones[$"hand:{me}"].Cards.Single();

        logic.Apply(state, new GameAction("select_card", CardId: drawn.Id, CardUid: drawn.Uid));

        // Still my turn; only the face-down cards are on offer.
        Assert.Equal(me, state.CurrentPlayer.Id);
        Assert.Same(drawn, state.Zones["discard"].Cards[^1]);
        var offered = logic.GetSelectableCardIds(state);
        Assert.Equal(grid.Cards.Where(c => !c.IsFaceUp).Select(c => c.Id).OrderBy(x => x), offered.OrderBy(x => x));

        var flip = grid.Cards[5];
        logic.Apply(state, new GameAction("select_card", CardId: flip.Id, CardUid: flip.Uid));
        Assert.True(flip.IsFaceUp);
        Assert.Equal(3, grid.Cards.Count(c => c.IsFaceUp));
        Assert.NotEqual(me, state.CurrentPlayer.Id);
    }

    /// <summary>
    /// As reported: "It should be my turn but I have no options." The AI seat had
    /// discarded its draw and owed a flip; its play_card fell through to a discard from
    /// an empty hand, and the table stopped on "Player 2's turn — Tap a face-down card".
    /// </summary>
    [Fact]
    public void An_ai_seat_that_owes_a_flip_flips_and_the_turn_passes()
    {
        var (state, logic, grid) = InPlay();
        var me = state.CurrentPlayer.Id;
        state.PlayerAgents[me] = new SmartDefaultAiAgent(me, state.Rng);
        logic.Apply(state, new GameAction("draw_from_deck"));
        var drawn = state.Zones[$"hand:{me}"].Cards.Single();
        logic.Apply(state, new GameAction("play_card", CardId: drawn.Id));
        Assert.True(state.Metadata.ContainsKey("dd_must_flip"));

        logic.Apply(state, logic.GetAutoAction(state));

        Assert.False(state.Metadata.ContainsKey("dd_must_flip"));
        Assert.Equal(3, grid.Cards.Count(c => c.IsFaceUp));
        Assert.NotEqual(me, state.CurrentPlayer.Id);
    }
}
