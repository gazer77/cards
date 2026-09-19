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
        Lay(state, logic, Give(hand, (Rank.Four, Suit.Clubs), (Rank.Four, Suit.Hearts), (Rank.Four, Suit.Spades)));

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

        Assert.DoesNotContain("go_out", logic.GetValidActions(state).Select(a => a.Type));

        state.Zones[$"foot:{me}"].Clear();
        Assert.Contains("go_out", logic.GetValidActions(state).Select(a => a.Type));
    }
}
