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

    // ── One tap, one card ─────────────────────────────────────────────────────

    /// <summary>
    /// Selection is by physical card. It was keyed by description, so in a five-deck
    /// game tapping one 4♥ selected every 4♥ on the table — the opponent's included —
    /// and tapping the second copy toggled the first back off, which made a natural
    /// pair of identical cards impossible to meld.
    /// </summary>
    [Fact]
    public void Selecting_two_identical_cards_selects_two_cards()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        var first  = new Card(Suit.Hearts, Rank.Four) { Uid = 9001 };
        var second = new Card(Suit.Hearts, Rank.Four) { Uid = 9002 };
        hand.Add(first);
        hand.Add(second);

        logic.Apply(state, new GameAction("select_card", CardId: first.Id,  CardUid: first.Uid));
        logic.Apply(state, new GameAction("select_card", CardId: second.Id, CardUid: second.Uid));

        var tokens = state.Metadata["selected_card"].Split(',');
        Assert.Equal(["9001", "9002"], tokens);
    }

    /// <summary>
    /// An agent that only knows descriptions still gets a second copy: naming an id
    /// already selected resolves to a copy not yet picked rather than toggling.
    /// </summary>
    [Fact]
    public void Selecting_by_description_prefers_an_unselected_copy()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        hand.Add(new Card(Suit.Hearts, Rank.Four) { Uid = 9101 });
        hand.Add(new Card(Suit.Hearts, Rank.Four) { Uid = 9102 });

        logic.Apply(state, new GameAction("select_card", CardId: "4h"));
        logic.Apply(state, new GameAction("select_card", CardId: "4h"));

        Assert.Equal(2, state.Metadata["selected_card"].Split(',').Length);
    }

    /// <summary>
    /// And tapping the same physical card again is still a deselect — the toggle
    /// belongs to the card, not to its description.
    /// </summary>
    [Fact]
    public void Retapping_the_same_physical_card_deselects_it()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        var card = hand.Cards[0];

        logic.Apply(state, new GameAction("select_card", CardId: card.Id, CardUid: card.Uid));
        logic.Apply(state, new GameAction("select_card", CardId: card.Id, CardUid: card.Uid));

        Assert.Equal("", state.Metadata.GetValueOrDefault("selected_card", ""));
    }

    /// <summary>Two identical naturals plus a third card of the rank make a meld.</summary>
    [Fact]
    public void Identical_copies_can_be_melded_together()
    {
        var (state, logic) = HandAndFoot();
        state.Metadata["dd_turn_state"] = "discard";

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        var cards = new[]
        {
            new Card(Suit.Hearts, Rank.Ace) { Uid = 9201 },
            new Card(Suit.Hearts, Rank.Ace) { Uid = 9202 },
            new Card(Suit.Clubs,  Rank.Ace) { Uid = 9203 },
        };
        foreach (var c in cards) hand.Add(c);

        state.Metadata["selected_card"] = string.Join(",", cards.Select(c => c.Uid));
        logic.Apply(state, new GameAction("meld"));

        // All three left the hand. (The hand is not EMPTY — melding its last card
        // picks the foot up, which is a different rule and its own test.)
        Assert.DoesNotContain(hand.Cards, c => cards.Contains(c));
    }

    /// <summary>
    /// The deadlock from a real table: the AI legally claimed the pile to open, owed
    /// the meld, and had no way to pay — the auto loop only taps cards, tapping routes
    /// to a refused discard, and the game stopped mid-round for good. An agent owing a
    /// meld must choose to meld, and a meld action with nothing selected assembles the
    /// debt itself: the owed card, its rank-mates, and whatever else the opening needs.
    /// </summary>
    [Fact]
    public void An_agent_owing_a_meld_pays_it_and_the_turn_moves_on()
    {
        var (state, logic) = HandAndFoot();

        // Seat the AI exactly as the stuck game had it: unopened, holding a pair of
        // the top card and enough on the side to reach round one's 50.
        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        hand.Add(new Card(Suit.Clubs,    Rank.Four) { Uid = 9301 });
        hand.Add(new Card(Suit.Spades,   Rank.Four) { Uid = 9302 });
        hand.Add(new Card(Suit.Clubs,    Rank.Nine) { Uid = 9303 });
        hand.Add(new Card(Suit.Spades,   Rank.Nine) { Uid = 9304 });
        hand.Add(new Card(Suit.Hearts,   Rank.Nine) { Uid = 9305 });
        hand.Add(new Card(Suit.Clubs,    Rank.Ace)  { Uid = 9306 });
        hand.Add(new Card(Suit.Spades,   Rank.Ace)  { Uid = 9307 });
        hand.Add(new Card(Suit.Hearts,   Rank.Ace)  { Uid = 9308 });
        hand.Add(new Card(Suit.Diamonds, Rank.King) { Uid = 9309 });

        var discard = state.Zones["discard"];
        discard.Clear();
        discard.Add(new Card(Suit.Diamonds, Rank.Four, isFaceUp: true) { Uid = 9310 });

        state.PlayerAgents[state.CurrentPlayer.Id] =
            new SmartDefaultAiAgent(state.CurrentPlayer.Id, state.Rng);

        Assert.Contains("draw_from_discard", ActionTypes(state, logic));
        logic.Apply(state, new GameAction("draw_from_discard"));
        Assert.True(state.Metadata.ContainsKey("dd_must_meld"));

        // The stuck game looped here. Drive it the way the auto loop does.
        for (int step = 0; step < 5 && state.Metadata.ContainsKey("dd_must_meld"); step++)
            logic.Apply(state, logic.GetAutoAction(state));

        Assert.False(state.Metadata.ContainsKey("dd_must_meld"));

        var melds = state.FindZone($"meld:{state.CurrentPlayer.Id}")!;
        Assert.True(melds.Groups.Count >= 2);   // fours plus at least one opening mate
        Assert.True(ScoringEngine.CardPointValue(state.Definition, melds.Cards) >= 50);
    }

    // ── Buttons that would only say no ────────────────────────────────────────

    /// <summary>
    /// Lay Meld is offered only when the selection would actually lay. It used to be
    /// offered unconditionally, so a player with 3s picked saw a button, pressed it,
    /// and was scolded. The reason goes on the status line instead.
    /// </summary>
    [Fact]
    public void Lay_meld_is_offered_only_when_the_selection_would_lay()
    {
        var (state, logic) = HandAndFoot();
        state.RoundNumber = 1;
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        var threes = new[]
        {
            new Card(Suit.Clubs,  Rank.Three) { Uid = 9401 },
            new Card(Suit.Hearts, Rank.Three) { Uid = 9402 },
            new Card(Suit.Spades, Rank.Three) { Uid = 9403 },
        };
        var aces = new[]
        {
            new Card(Suit.Clubs,  Rank.Ace) { Uid = 9404 },
            new Card(Suit.Hearts, Rank.Ace) { Uid = 9405 },
            new Card(Suit.Spades, Rank.Ace) { Uid = 9406 },
        };
        foreach (var c in threes.Concat(aces)) hand.Add(c);

        // Nothing selected: no meld to offer.
        Assert.DoesNotContain("meld", ActionTypes(state, logic));

        // 3s cannot be melded — no button, and the status says so.
        state.Metadata["selected_card"] = string.Join(",", threes.Select(c => c.Uid));
        Assert.DoesNotContain("meld", ActionTypes(state, logic));
        Assert.Contains("3s cannot be melded", state.Metadata["status"]);

        // Three aces open at 60 — the button is back.
        state.Metadata["selected_card"] = string.Join(",", aces.Select(c => c.Uid));
        Assert.Contains("meld", ActionTypes(state, logic));
    }

    [Fact]
    public void Add_to_meld_is_offered_only_when_a_meld_of_that_rank_is_down()
    {
        var (state, logic) = HandAndFoot();
        state.RoundNumber = 1;
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        hand.Clear();
        var king = new Card(Suit.Clubs, Rank.King) { Uid = 9501 };
        hand.Add(king);
        state.Metadata["selected_card"] = king.Uid.ToString();

        // Nothing on the table: nothing to add to.
        Assert.DoesNotContain("add_to_meld", ActionTypes(state, logic));

        // A meld of kings appears; now the king has somewhere to go.
        var melds = state.FindZone($"meld:{state.CurrentPlayer.Id}")!;
        melds.AddGroup([
            new Card(Suit.Hearts,   Rank.King) { Uid = 9502 },
            new Card(Suit.Spades,   Rank.King) { Uid = 9503 },
            new Card(Suit.Diamonds, Rank.King) { Uid = 9504 },
        ]);
        Assert.Contains("add_to_meld", ActionTypes(state, logic));
    }
}
