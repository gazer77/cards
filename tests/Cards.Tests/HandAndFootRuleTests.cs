using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Hand and Foot's rules, as written in its definition rather than in C#.
///
/// Both were absent: the discard pile could be claimed unconditionally, and there was no
/// notion of an opening meld requirement anywhere in the codebase.
/// </summary>
public sealed class HandAndFootRuleTests
{
    private static (GameState State, IGameLogic Logic) Table(int seats = 2, ulong seed = 5)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(seed),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);

        // The deal files red threes into the meld strip, and melding a whole stacked
        // hand picks the foot up, filing its red threes too. Both are right and tested
        // in ZoneIntakeTests; here they would put groups in the strip these rule tests
        // did not lay. Strips start clear, and feet hold no red threes.
        foreach (var zone in state.Zones.Values.Where(z => z.Id.StartsWith("meld")))
            zone.Clear();
        foreach (var foot in state.Zones.Values.Where(z => z.Id.StartsWith("foot")))
            foreach (var three in foot.Cards.Where(c => c.Rank == Rank.Three && c.IsRed).ToList())
                foot.Remove(three);

        return (state, logic);
    }

    private static Zone Hand(GameState s)  => s.Zones[$"hand:{s.CurrentPlayer.Id}"];
    private static Zone Melds(GameState s) => s.Zones[$"meld:{s.CurrentPlayer.Id}"];

    private static bool CanDrawFromDiscard(GameState s, IGameLogic logic)
        => logic.GetValidActions(s).Any(a => a.Type == "draw_from_discard");

    private static int _uid = 7000;

    private static List<Card> Give(Zone zone, params (Rank Rank, Suit Suit)[] cards)
    {
        var added = cards.Select(c => new Card(c.Suit, c.Rank, isFaceUp: true) { Uid = _uid++ }).ToList();
        foreach (var card in added) zone.Add(card);
        return added;
    }

    // ── Claiming the discard pile ─────────────────────────────────────────────

    [Fact]
    public void The_discard_pile_cannot_be_claimed_before_melding()
    {
        var (state, logic) = Table();

        var discard = state.Zones["discard"];
        discard.Clear();
        Give(discard, (Rank.Nine, Suit.Spades));

        var hand = Hand(state);
        hand.Clear();
        Give(hand, (Rank.Nine, Suit.Clubs), (Rank.Nine, Suit.Hearts));

        // Two matching cards, but this side has laid nothing down yet.
        Assert.False(CanDrawFromDiscard(state, logic));
    }

    [Fact]
    public void The_discard_pile_cannot_be_claimed_without_two_matching_cards()
    {
        var (state, logic) = Table();

        Melds(state).AddGroup(Give(Melds(state), (Rank.King, Suit.Clubs)));

        var discard = state.Zones["discard"];
        discard.Clear();
        Give(discard, (Rank.Nine, Suit.Spades));

        var hand = Hand(state);
        hand.Clear();
        Give(hand, (Rank.Nine, Suit.Clubs));   // one nine, not two

        Assert.False(CanDrawFromDiscard(state, logic));
    }

    [Fact]
    public void The_discard_pile_can_be_claimed_when_both_conditions_hold()
    {
        var (state, logic) = Table();

        Melds(state).AddGroup(Give(Melds(state), (Rank.King, Suit.Clubs)));

        var discard = state.Zones["discard"];
        discard.Clear();
        Give(discard, (Rank.Nine, Suit.Spades));

        var hand = Hand(state);
        hand.Clear();
        Give(hand, (Rank.Nine, Suit.Clubs), (Rank.Nine, Suit.Hearts));

        Assert.True(CanDrawFromDiscard(state, logic));
    }

    [Fact]
    public void The_deck_is_always_available_to_draw_from()
    {
        var (state, logic) = Table();

        // Only the discard carries conditions; restricting the deck as well would leave
        // a player with no legal move at all.
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "draw_from_deck");
    }

    // ── Opening meld requirement ──────────────────────────────────────────────

    private static void Lay(GameState state, IGameLogic logic, IEnumerable<Card> cards)
    {
        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata["selected_card"] = string.Join(",", cards.Select(c => c.Id));
        logic.Apply(state, new GameAction("meld"));
    }

    [Fact]
    public void A_first_meld_below_the_round_minimum_is_refused()
    {
        var (state, logic) = Table();
        state.RoundNumber = 1;             // needs 50

        var hand = Hand(state);
        hand.Clear();
        var cards = Give(hand, (Rank.Four, Suit.Clubs), (Rank.Four, Suit.Hearts), (Rank.Four, Suit.Spades));

        Lay(state, logic, cards);          // three fours is 15

        Assert.Empty(Melds(state).Groups);
        Assert.Contains("50", state.Metadata.GetValueOrDefault("status", ""));
    }

    [Fact]
    public void A_first_meld_meeting_the_round_minimum_is_allowed()
    {
        var (state, logic) = Table();
        state.RoundNumber = 1;

        var hand = Hand(state);
        hand.Clear();
        var cards = Give(hand, (Rank.Ace, Suit.Clubs), (Rank.Ace, Suit.Hearts), (Rank.Ace, Suit.Spades));

        Lay(state, logic, cards);          // three aces is 60

        Assert.Single(Melds(state).Groups);
    }

    /// <summary>
    /// The requirement rises as the rounds go on — the reason it is a table in the
    /// definition rather than a single number.
    /// </summary>
    [Fact]
    public void The_minimum_rises_with_the_round()
    {
        var (state, logic) = Table();
        state.RoundNumber = 3;             // needs 120

        var hand = Hand(state);
        hand.Clear();
        var cards = Give(hand, (Rank.Ace, Suit.Clubs), (Rank.Ace, Suit.Hearts), (Rank.Ace, Suit.Spades));

        Lay(state, logic, cards);          // 60 was enough in round 1, not now

        Assert.Empty(Melds(state).Groups);
        Assert.Contains("120", state.Metadata.GetValueOrDefault("status", ""));
    }

    [Fact]
    public void Later_melds_are_not_held_to_the_minimum()
    {
        var (state, logic) = Table();
        state.RoundNumber = 1;

        var hand = Hand(state);
        hand.Clear();
        Lay(state, logic, Give(hand, (Rank.Ace, Suit.Clubs), (Rank.Ace, Suit.Hearts), (Rank.Ace, Suit.Spades)));
        Assert.Single(Melds(state).Groups);

        // The side is open now, so a small meld is fine.
        hand.Clear();
        // (A card is kept back: the foot is up, and the last card is the discard.)
        var fours = Give(hand, (Rank.Four, Suit.Clubs), (Rank.Four, Suit.Hearts), (Rank.Four, Suit.Spades),
                               (Rank.Nine, Suit.Clubs));
        Lay(state, logic, fours.Take(3));

        Assert.Equal(2, Melds(state).Groups.Count);
    }

    // ── The foot ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Melding away the last card of the hand picks the foot up at once, and the turn
    /// goes on with it. The pickup only ran after a discard, so a hand emptied by
    /// melding was offered Go Out — with its foot still lying there untouched.
    /// </summary>
    [Fact]
    public void Melding_the_last_card_picks_up_the_foot_and_the_turn_continues()
    {
        var (state, logic) = Table();
        state.RoundNumber = 1;
        logic.Apply(state, new GameAction("draw_from_deck"));

        var me   = state.CurrentPlayer.Id;
        var hand = state.Zones[$"hand:{me}"];
        var foot = state.Zones[$"foot:{me}"];
        int footSize = foot.Count;
        Assert.True(footSize > 0);

        // Hand: exactly one meld's worth, opening at 60.
        hand.Clear();
        var aces = new[]
        {
            new Card(Suit.Clubs,  Rank.Ace) { Uid = 9601 },
            new Card(Suit.Hearts, Rank.Ace) { Uid = 9602 },
            new Card(Suit.Spades, Rank.Ace) { Uid = 9603 },
        };
        foreach (var c in aces) hand.Add(c);
        state.Metadata["selected_card"] = string.Join(",", aces.Select(c => c.Uid));

        logic.Apply(state, new GameAction("meld"));

        Assert.Empty(foot.Cards);                       // picked up
        Assert.Equal(footSize, hand.Count);             // now in hand
        Assert.Equal(me, state.CurrentPlayer.Id);       // still my turn
        Assert.DoesNotContain("go_out", logic.GetValidActions(state).Select(a => a.Type));
    }

    /// <summary>An empty hand with a full foot is halfway, not out.</summary>
    [Fact]
    public void Going_out_needs_the_foot_played_too()
    {
        var (state, logic) = Table();
        logic.Apply(state, new GameAction("draw_from_deck"));

        var me = state.CurrentPlayer.Id;
        state.Zones[$"hand:{me}"].Clear();
        Assert.NotEmpty(state.Zones[$"foot:{me}"].Cards);

        // The books the definition asks for are down; only the foot stands in the way.
        Melds(state).AddGroup(Book(Rank.King, wilds: 0, uidBase: 9900));
        Melds(state).AddGroup(Book(Rank.Nine, wilds: 2, uidBase: 9920));
        Assert.DoesNotContain("go_out", logic.GetValidActions(state).Select(a => a.Type));

        state.Zones[$"foot:{me}"].Clear();
        Assert.Contains("go_out", logic.GetValidActions(state).Select(a => a.Type));
    }

    // ── Going out ─────────────────────────────────────────────────────────────

    private static void EmptyHands(GameState state)
    {
        var me = state.CurrentPlayer.Id;
        state.Zones[$"hand:{me}"].Clear();
        state.Zones[$"foot:{me}"].Clear();
    }

    private static List<Card> Book(Rank rank, int wilds, int uidBase)
    {
        var suits = new[] { Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades };
        var cards = new List<Card>();
        for (int i = 0; i < 7 - wilds; i++)
            cards.Add(new Card(suits[i % 4], rank) { Uid = uidBase + i });
        for (int i = 0; i < wilds; i++)
            cards.Add(new Card(suits[i % 4], Rank.Two) { Uid = uidBase + 10 + i });
        return cards;
    }

    private static bool CanGoOut(GameState state, IGameLogic logic)
        => logic.GetValidActions(state).Any(a => a.Type == "go_out");

    /// <summary>
    /// The go-out rule, at last read from the definition: one natural book and one
    /// wild book. "all_melds_complete" had been a name the engine treated as
    /// "hand empty", so a side could go out with nothing complete at all.
    /// </summary>
    [Fact]
    public void Going_out_needs_a_natural_book_and_a_wild_book()
    {
        var (state, logic) = Table();
        logic.Apply(state, new GameAction("draw_from_deck"));
        EmptyHands(state);

        // Out of cards, nothing complete: no.
        Assert.False(CanGoOut(state, logic));

        // One natural book: still no — the wild one is missing.
        Melds(state).AddGroup(Book(Rank.King, wilds: 0, uidBase: 9700));
        Assert.False(CanGoOut(state, logic));

        // A second natural book does not stand in for a wild one.
        Melds(state).AddGroup(Book(Rank.Queen, wilds: 0, uidBase: 9720));
        Assert.False(CanGoOut(state, logic));

        // Natural plus wild: yes.
        Melds(state).AddGroup(Book(Rank.Nine, wilds: 2, uidBase: 9740));
        Assert.True(CanGoOut(state, logic));
    }

    /// <summary>Six of a rank is a meld, not a book, whatever the definition asks.</summary>
    [Fact]
    public void An_incomplete_meld_is_not_a_book()
    {
        var (state, logic) = Table();
        logic.Apply(state, new GameAction("draw_from_deck"));
        EmptyHands(state);

        Melds(state).AddGroup(Book(Rank.King, wilds: 0, uidBase: 9800).Take(6));
        Melds(state).AddGroup(Book(Rank.Nine, wilds: 2, uidBase: 9820));

        Assert.False(CanGoOut(state, logic));
    }
    // ── Found while teaching the computer players to meld ─────────────────────

    /// <summary>
    /// A red three is filed into the meld strip the moment it is dealt or drawn. It
    /// counted as the side having opened, so one three waived the round's minimum.
    /// </summary>
    [Fact]
    public void A_filed_red_three_is_not_an_opening()
    {
        var (state, logic) = Table();
        state.RoundNumber = 1;
        Melds(state).AddGroup([new Card(Suit.Hearts, Rank.Three, isFaceUp: true) { Uid = 9950 }]);

        var hand = Hand(state);
        hand.Clear();
        var fours = Give(hand, (Rank.Four, Suit.Clubs), (Rank.Four, Suit.Hearts), (Rank.Four, Suit.Spades),
                               (Rank.King, Suit.Clubs));
        Lay(state, logic, fours.Take(3));   // 15, and the minimum is 50

        Assert.Single(Melds(state).Groups);   // only the three
        Assert.Contains("50", state.Metadata.GetValueOrDefault("status", ""));
    }

    /// <summary>
    /// With the foot already up, melding the last card leaves nothing to discard. Short
    /// of the books going out needs, that was a turn with no move at all.
    /// </summary>
    [Fact]
    public void The_last_card_cannot_be_melded_unless_it_goes_out()
    {
        var (state, logic) = Table();
        logic.Apply(state, new GameAction("draw_from_deck"));
        var me = state.CurrentPlayer.Id;
        state.Zones[$"foot:{me}"].Clear();

        // Open, with a meld of kings short of a book.
        Melds(state).AddGroup(Book(Rank.King, wilds: 0, uidBase: 9960).Take(4));
        var hand = Hand(state);
        hand.Clear();
        var king = Give(hand, (Rank.King, Suit.Hearts));

        state.Metadata["selected_card"] = king[0].Uid.ToString();
        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "add_to_meld");
        Assert.Contains("discard", state.Metadata.GetValueOrDefault("status", ""));

        // With a card left over it is an ordinary addition.
        Give(hand, (Rank.Five, Suit.Clubs));
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "add_to_meld");
    }

    /// <summary>...and when the lay does finish what going out needs, it is allowed.</summary>
    [Fact]
    public void The_last_card_may_be_melded_when_it_completes_the_books()
    {
        var (state, logic) = Table();
        logic.Apply(state, new GameAction("draw_from_deck"));
        var me = state.CurrentPlayer.Id;
        state.Zones[$"foot:{me}"].Clear();

        Melds(state).AddGroup(Book(Rank.Nine, wilds: 2, uidBase: 9980));              // the wild book
        Melds(state).AddGroup(Book(Rank.King, wilds: 0, uidBase: 10100).Take(6));      // a king short
        var hand = Hand(state);
        hand.Clear();
        var king = Give(hand, (Rank.King, Suit.Hearts));

        state.Metadata["selected_card"] = king[0].Uid.ToString();
        logic.Apply(state, new GameAction("add_to_meld"));

        Assert.Empty(hand.Cards);
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "go_out");
    }
}
