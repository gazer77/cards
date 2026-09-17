using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The ways a player can say what they want, and whether the table offers them.
///
/// A table once announced "Your turn — Draw a card" while offering no button and no
/// working tap target: a lone action deliberately had no button, and the deck's own top
/// card swallowed taps meant for the deck. The only way through was tapping bare felt,
/// which nothing on screen mentioned.
/// </summary>
public sealed class TableInputTests
{
    private static (GameState State, IGameLogic Logic) HandAndFoot(int seats = 2)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(11),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    private static List<string> ActionTypes(GameState state, IGameLogic logic)
        => logic.GetValidActions(state).Select(a => a.Type).ToList();

    /// <summary>
    /// The bug as reported: it is the player's turn to draw and the game must offer a
    /// way to do it. Naming the action after its zone is what lets a double-tap on the
    /// deck find it without the client hardcoding "deck".
    /// </summary>
    [Fact]
    public void Drawing_is_offered_by_a_zone_named_action()
    {
        var (state, logic) = HandAndFoot();

        Assert.Contains("draw_from_deck", ActionTypes(state, logic));
    }

    /// <summary>
    /// Claiming the discard pile is gated: this side has not melded, so it must not be
    /// offered — the table should never show an action it will refuse.
    /// </summary>
    [Fact]
    public void A_gated_draw_is_not_offered_before_its_condition_holds()
    {
        var (state, logic) = HandAndFoot();

        Assert.DoesNotContain("draw_from_discard", ActionTypes(state, logic));
    }

    [Fact]
    public void Discarding_is_offered_once_the_selection_is_the_right_size()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        // Nothing selected: the definition asks for one card, and none is picked.
        Assert.DoesNotContain("discard", ActionTypes(state, logic));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        state.Metadata["selected_card"] = hand.Cards[0].Id;
        Assert.Contains("discard", ActionTypes(state, logic));

        // Two cards is a meld in progress, not a discard.
        state.Metadata["selected_card"] = $"{hand.Cards[0].Id},{hand.Cards[1].Id}";
        Assert.DoesNotContain("discard", ActionTypes(state, logic));
    }

    /// <summary>
    /// The Discard button carries no card — the selection is the card. This fell
    /// through to a guard that skips the discard fallback in melding games, so the
    /// button did nothing at all.
    /// </summary>
    [Fact]
    public void The_discard_button_discards_the_selected_card()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        var card = hand.Cards[0];
        int before = hand.Count;

        state.Metadata["selected_card"] = card.Id;
        logic.Apply(state, new GameAction("discard"));

        Assert.Equal(before - 1, hand.Count);
        Assert.Equal(card.Id, state.Zones["discard"].TopCard!.Id);
    }

    /// <summary>
    /// The pickup rule's other half. A side that has not melded may still claim the
    /// pile when the top card completes an opening worth enough — often the only way a
    /// side gets open at all, and it was refused outright.
    /// </summary>
    [Fact]
    public void An_unopened_side_may_claim_the_pile_to_open_with_it()
    {
        var (state, logic) = HandAndFoot();

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        // Two aces (20 each) plus the ace on the pile is 60, over round one's 50.
        hand.Add(new Card(Suit.Clubs,  Rank.Ace) { Uid = 7001 });
        hand.Add(new Card(Suit.Hearts, Rank.Ace) { Uid = 7002 });

        var discard = state.Zones["discard"];
        discard.Clear();
        discard.Add(new Card(Suit.Spades, Rank.Ace, isFaceUp: true) { Uid = 7003 });

        Assert.Contains("draw_from_discard", ActionTypes(state, logic));
    }

    /// <summary>
    /// The same hand that cannot reach the opening must still be refused, or the rule
    /// would just be "hold two of the top card".
    /// </summary>
    [Fact]
    public void An_unopened_side_is_refused_when_it_cannot_open()
    {
        var (state, logic) = HandAndFoot();

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        // Two fours (5 each) plus the pile's four is 15, well under 50.
        hand.Add(new Card(Suit.Clubs,  Rank.Four) { Uid = 7101 });
        hand.Add(new Card(Suit.Hearts, Rank.Four) { Uid = 7102 });

        var discard = state.Zones["discard"];
        discard.Clear();
        discard.Add(new Card(Suit.Spades, Rank.Four, isFaceUp: true) { Uid = 7103 });

        Assert.DoesNotContain("draw_from_discard", ActionTypes(state, logic));
    }

    /// <summary>
    /// Claiming the pile takes the number of cards the definition names, not the whole
    /// pile. Hand and Foot's is seven; taking all of it handed over a hand of thirty.
    /// </summary>
    [Fact]
    public void Claiming_the_pile_takes_the_declared_number_of_cards()
    {
        var (state, logic) = HandAndFoot();

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        hand.Add(new Card(Suit.Clubs,  Rank.Ace) { Uid = 7201 });
        hand.Add(new Card(Suit.Hearts, Rank.Ace) { Uid = 7202 });

        var discard = state.Zones["discard"];
        discard.Clear();
        for (int i = 0; i < 20; i++)
            discard.Add(new Card(Suit.Diamonds, Rank.Nine, isFaceUp: true) { Uid = 7300 + i });
        discard.Add(new Card(Suit.Spades, Rank.Ace, isFaceUp: true) { Uid = 7399 });

        logic.Apply(state, new GameAction("draw_from_discard"));

        Assert.Equal(2 + 7, hand.Count);
        Assert.Equal(21 - 7, discard.Count);
    }

    // ── The price of the pile ─────────────────────────────────────────────────

    /// <summary>Stacks a claimable pile: two aces held, an ace on top of the discard.</summary>
    private static void StageClaimablePile(GameState state, Rank topRank = Rank.Ace)
    {
        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        hand.Add(new Card(Suit.Clubs,  topRank) { Uid = 8001 });
        hand.Add(new Card(Suit.Hearts, topRank) { Uid = 8002 });

        var discard = state.Zones["discard"];
        discard.Clear();
        discard.Add(new Card(Suit.Diamonds, Rank.Nine, isFaceUp: true) { Uid = 8003 });
        discard.Add(new Card(Suit.Spades, topRank, isFaceUp: true)     { Uid = 8004 });
    }

    /// <summary>
    /// A 3 on top freezes the pile. 3s cannot be melded at all, so the card can never
    /// be used — holding a pair of them does not change that.
    /// </summary>
    [Fact]
    public void A_pile_topped_by_an_unmeldable_rank_cannot_be_claimed()
    {
        var (state, logic) = HandAndFoot();
        StageClaimablePile(state, Rank.Three);

        Assert.DoesNotContain("draw_from_discard", ActionTypes(state, logic));
    }

    [Fact]
    public void Claiming_the_pile_obliges_the_player_to_meld_the_card()
    {
        var (state, logic) = HandAndFoot();
        StageClaimablePile(state);

        logic.Apply(state, new GameAction("draw_from_discard"));

        Assert.Equal("As", state.Metadata["dd_must_meld"]);
        Assert.Contains("Meld the", state.Metadata["status"]);
    }

    /// <summary>
    /// The obligation is the price of the pickup, so the turn cannot be ended while it
    /// stands. Without this the rule is decoration and the pile is a free fistful.
    /// </summary>
    [Fact]
    public void The_turn_cannot_be_ended_while_a_meld_is_owed()
    {
        var (state, logic) = HandAndFoot();
        StageClaimablePile(state);
        logic.Apply(state, new GameAction("draw_from_discard"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        int held = hand.Count;

        state.Metadata["selected_card"] = hand.Cards[0].Id;
        Assert.DoesNotContain("discard", ActionTypes(state, logic));

        // And the discard is refused even when asked for directly.
        logic.Apply(state, new GameAction("discard"));
        Assert.Equal(held, hand.Count);
    }

    [Fact]
    public void Melding_the_claimed_card_settles_the_obligation()
    {
        var (state, logic) = HandAndFoot();
        StageClaimablePile(state);
        logic.Apply(state, new GameAction("draw_from_discard"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        var aces = hand.Cards.Where(c => c.Rank == Rank.Ace).Select(c => c.Id);
        state.Metadata["selected_card"] = string.Join(",", aces);
        logic.Apply(state, new GameAction("meld"));

        Assert.False(state.Metadata.ContainsKey("dd_must_meld"));

        // And the turn can be ended again.
        state.Metadata["selected_card"] = hand.Cards[0].Id;
        Assert.Contains("discard", ActionTypes(state, logic));
    }
}
