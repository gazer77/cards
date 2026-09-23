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

            // The person at seat 0 is asked before anything turns, so a finger landing
            // on the wrong card costs nothing. The AI seats have no mind to change and
            // turn theirs as they pick.
            if (logic.GetValidActions(state).Any(a => a.Type == "flip"))
            {
                Assert.Equal(0, grid.Cards.Count(c => c.IsFaceUp));
                logic.Apply(state, new GameAction("flip"));
            }

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
            if (logic.GetValidActions(state).Any(a => a.Type == "flip"))
                logic.Apply(state, new GameAction("flip"));
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

        // Tapping the drawn card only picks it out. The reported accident: this is the
        // same gesture as reaching past it for the grid card you meant to replace, and
        // it used to end the turn before you saw the card go.
        logic.Apply(state, new GameAction("select_card", CardId: drawn.Id, CardUid: drawn.Uid));
        Assert.Contains(state.Zones[$"hand:{me}"].Cards, c => c.Uid == drawn.Uid);
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "discard_drawn");

        logic.Apply(state, new GameAction("discard_drawn"));

        // Still my turn; only the face-down cards are on offer.
        Assert.Equal(me, state.CurrentPlayer.Id);
        Assert.Same(drawn, state.Zones["discard"].Cards[^1]);
        var offered = logic.GetSelectableCardIds(state);
        Assert.Equal(grid.Cards.Where(c => !c.IsFaceUp).Select(c => c.Id).OrderBy(x => x), offered.OrderBy(x => x));

        // The flip is asked for too: pick, then Flip.
        var flip = grid.Cards[5];
        logic.Apply(state, new GameAction("select_card", CardId: flip.Id, CardUid: flip.Uid));
        Assert.False(flip.IsFaceUp);
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "flip");

        logic.Apply(state, new GameAction("flip"));
        Assert.True(flip.IsFaceUp);
        Assert.Equal(3, grid.Cards.Count(c => c.IsFaceUp));
        Assert.NotEqual(me, state.CurrentPlayer.Id);
    }

    /// <summary>
    /// A player who knows their mind should not be made to say so twice: a double tap
    /// does the thing a single tap proposes. The phase names what that is, so the
    /// gesture means something different where the game means something different.
    /// </summary>
    [Fact]
    public void A_double_tap_discards_the_drawn_card_without_the_asking()
    {
        var (state, logic, _) = InPlay();
        var me = state.CurrentPlayer.Id;
        logic.Apply(state, new GameAction("draw_from_deck"));
        var drawn = state.Zones[$"hand:{me}"].Cards.Single();

        var shortcut = logic.GetDefaultCardAction(state, drawn.Id, drawn.Uid);
        Assert.NotNull(shortcut);
        Assert.Equal("discard_drawn", shortcut.Type);

        logic.Apply(state, shortcut);
        Assert.Same(drawn, state.Zones["discard"].Cards[^1]);
    }

    [Fact]
    public void A_double_tap_turns_a_peeked_card_there_and_then()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);

        var grid = state.Zones[$"grid:{state.CurrentPlayer.Id}"];
        var card = grid.Cards[3];

        var shortcut = logic.GetDefaultCardAction(state, card.Id, card.Uid);
        Assert.NotNull(shortcut);

        logic.Apply(state, shortcut);
        Assert.True(card.IsFaceUp);          // turned, with no button in between
        Assert.Equal(1, grid.Cards.Count(c => c.IsFaceUp));
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

/// <summary>
/// The gestures themselves, at the level the client uses them: a double tap asks the
/// game what it means, and a phase that names nothing leaves it an ordinary tap.
/// </summary>
public sealed class DefaultCardActionTests
{
    private static (GameState State, IGameLogic Logic) Game(string id, int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync(id).GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(5) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    [Fact]
    public void A_phase_with_no_shortcut_leaves_the_gesture_alone()
    {
        // Hearts passes cards; a double tap there has nothing special to mean, and the
        // client falls back to selecting, which is what it did before any of this.
        var (state, logic) = Game("hearts", 4);
        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];

        Assert.Null(logic.GetDefaultCardAction(state, hand.Cards[0].Id, hand.Cards[0].Uid));
    }

    [Fact]
    public void The_shortcut_is_the_action_the_phase_would_have_taken()
    {
        // Golf's peek: the double tap turns the card, which is what Flip would have
        // done for the same card a moment later.
        var (state, logic) = Game("golf", 2);
        var grid = state.Zones[$"grid:{state.CurrentPlayer.Id}"];

        var shortcut = logic.GetDefaultCardAction(state, grid.Cards[2].Id, grid.Cards[2].Uid);
        Assert.NotNull(shortcut);
        Assert.Equal(grid.Cards[2].Id, shortcut.CardId);

        logic.Apply(state, shortcut);
        Assert.True(grid.Cards[2].IsFaceUp);
    }
}
