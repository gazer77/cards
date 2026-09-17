using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// What counts as a meld, and what a laid meld is worth.
///
/// Melding was documented as validating "three or more of a rank, wilds allowed" and
/// validated none of it: any three selected cards were accepted and then scored as though
/// they counted. Melds also piled into one zone, so canasta scoring had to reconstruct
/// them by grouping the pile by rank and handing out wilds greedily — right often enough
/// to look correct.
/// </summary>
public sealed class MeldTests
{
    /// <summary>
    /// A dealt Hand and Foot table, with the logic that dealt it.
    ///
    /// The logic must be the same instance: DefaultGameLogic builds its phase handlers
    /// during Initialize, so a second instance created from the same definition has no
    /// handlers at all and silently answers every question with nothing — no actions, no
    /// effect from Apply, and a test that looks like a rules failure.
    /// </summary>
    private static (GameState State, IGameLogic Logic) HandAndFoot(int seats = 2)
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(3),
        };

        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    private static Zone Hand(GameState state) =>
        state.Zones[$"hand:{state.CurrentPlayer.Id}"];

    private static Zone Melds(GameState state)
    {
        var team = state.GetPlayerTeam(state.CurrentPlayer.Id);
        return (team is not null ? state.FindZone($"meld:{team.Id}") : null)
            ?? state.FindZone($"meld:{state.CurrentPlayer.Id}")
            ?? state.Zones["meld"];
    }

    /// <summary>
    /// Uids handed out by <see cref="Stack"/>. Kept unique across calls because a group
    /// names its cards by uid, so two cards sharing one would make a meld ambiguous —
    /// the invariant the real deck builder maintains.
    /// </summary>
    private static int _nextUid = 9000;

    /// <summary>Stacks the current player's hand with exactly these cards.</summary>
    private static List<Card> Stack(GameState state, params (Rank Rank, Suit Suit)[] cards)
    {
        var hand = Hand(state);
        hand.Clear();

        int uid = _nextUid;
        _nextUid += cards.Length;
        var added = new List<Card>();
        foreach (var (rank, suit) in cards)
        {
            var card = new Card(suit, rank, isFaceUp: true) { Uid = uid++ };
            hand.Add(card);
            added.Add(card);
        }
        return added;
    }

    /// <summary>
    /// Lays a meld. Melding happens after drawing, so the turn has to be in its discard
    /// half — the actions are not offered during the draw, and applying one there does
    /// nothing.
    /// </summary>
    private static void Lay(GameState state, IGameLogic logic, IEnumerable<Card> cards)
    {
        state.Metadata["dd_turn_state"]  = "discard";
        state.Metadata["selected_card"] = string.Join(",", cards.Select(c => c.Id));
        logic.Apply(state, new GameAction("meld"));
    }

    /// <summary>
    /// Lays a qualifying first meld, so these tests exercise what counts as a meld
    /// rather than the separate rule about what a side's opening meld must be worth.
    /// Three aces is 60, over round one's 50. Returns the table with that meld laid.
    /// </summary>
    private static (GameState State, IGameLogic Logic) OpenTable(int seats = 2)
    {
        var (state, logic) = HandAndFoot(seats);
        state.RoundNumber = 1;

        Lay(state, logic, Stack(state,
            (Rank.Ace, Suit.Clubs), (Rank.Ace, Suit.Hearts), (Rank.Ace, Suit.Spades)));

        return (state, logic);
    }

    /// <summary>Melds laid since the table was opened.</summary>
    private static int MeldsLaid(GameState state) => Melds(state).Groups.Count - 1;

    [Fact]
    public void Three_of_a_rank_is_a_meld()
    {
        var (state, logic) = OpenTable();

        var cards = Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Seven, Suit.Hearts), (Rank.Seven, Suit.Spades));

        Lay(state, logic, cards);

        Assert.Equal(1, MeldsLaid(state));
        Assert.Equal(6, Melds(state).Count);   // the opening aces plus these sevens
    }

    /// <summary>
    /// The bug this whole change exists for: unrelated cards were accepted as a meld.
    /// </summary>
    [Fact]
    public void Three_unrelated_cards_are_not_a_meld()
    {
        var (state, logic) = OpenTable();

        var cards = Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Nine, Suit.Hearts), (Rank.King, Suit.Spades));
        Lay(state, logic, cards);

        Assert.Equal(0, MeldsLaid(state));
        Assert.Equal(3, Melds(state).Count);   // only the opening meld
        Assert.Equal(3, Hand(state).Count);   // and the cards stay in hand
    }

    [Fact]
    public void Wilds_can_stand_in_for_missing_cards()
    {
        var (state, logic) = OpenTable();

        var cards = Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Seven, Suit.Hearts), (Rank.Two, Suit.Spades));
        Lay(state, logic, cards);

        Assert.Equal(1, MeldsLaid(state));
    }

    [Fact]
    public void Wilds_cannot_outnumber_the_real_cards()
    {
        var (state, logic) = OpenTable();

        // One seven propped up by two wilds is not a set of sevens.
        var cards = Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Two, Suit.Hearts), (Rank.Two, Suit.Spades));
        Lay(state, logic, cards);

        Assert.Equal(0, MeldsLaid(state));
    }

    [Fact]
    public void All_wilds_is_not_a_meld()
    {
        var (state, logic) = OpenTable();

        var cards = Stack(state, (Rank.Two, Suit.Clubs), (Rank.Two, Suit.Hearts), (Rank.Two, Suit.Spades));
        Lay(state, logic, cards);

        // A meld of wilds has no rank to be a meld of.
        Assert.Equal(0, MeldsLaid(state));
    }

    [Fact]
    public void Melds_are_kept_apart_from_each_other()
    {
        var (state, logic) = OpenTable();

        var sevens = Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Seven, Suit.Hearts), (Rank.Seven, Suit.Spades));
        Lay(state, logic, sevens);

        var kings = Stack(state, (Rank.King, Suit.Clubs), (Rank.King, Suit.Hearts), (Rank.King, Suit.Spades));
        Lay(state, logic, kings);

        // Two melds, not one pile of six — which is what add-to-meld and canasta
        // detection both depend on.
        Assert.Equal(3, Melds(state).Groups.Count);   // aces, sevens, kings
        Assert.All(Melds(state).Groups, g => Assert.Equal(3, g.Count));
    }

    [Fact]
    public void Adding_to_a_meld_joins_the_one_of_that_rank()
    {
        var (state, logic) = OpenTable();

        Lay(state, logic, Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Seven, Suit.Hearts), (Rank.Seven, Suit.Spades)));
        Lay(state, logic, Stack(state, (Rank.King, Suit.Clubs), (Rank.King, Suit.Hearts), (Rank.King, Suit.Spades)));

        var extra = Stack(state, (Rank.Seven, Suit.Diamonds));
        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata["selected_card"] = extra[0].Id;
        logic.Apply(state, new GameAction("add_to_meld"));

        var melds = Melds(state);
        Assert.Equal(3, melds.Groups.Count);

        var sevenMeld = Enumerable.Range(0, melds.Groups.Count)
            .Select(melds.GroupCards)
            .Single(g => g.Any(c => c.Rank == Rank.Seven));

        Assert.Equal(4, sevenMeld.Count);
    }

    [Fact]
    public void A_card_leaving_the_zone_leaves_its_meld()
    {
        var (state, logic) = OpenTable();

        var cards = Stack(state, (Rank.Seven, Suit.Clubs), (Rank.Seven, Suit.Hearts), (Rank.Seven, Suit.Spades));
        Lay(state, logic, cards);

        var melds = Melds(state);
        int before = melds.Count;

        melds.Remove(melds.Cards[0]);

        // A group naming a card the zone no longer holds would score cards that are gone.
        Assert.Equal(before - 1, melds.Count);
        Assert.Equal(melds.Count, melds.Groups.Sum(g => g.Count));
    }

    // ── Several melds in one action ──────────────────────────────────────────

    /// <summary>
    /// A selection may hold more than one meld: three tens and three queens laid
    /// together. One-meld-per-action made a 50-point opening unsatisfiable for a hand
    /// whose biggest single meld was worth 30 — the requirement applied at exactly the
    /// moment it could not be met.
    /// </summary>
    [Fact]
    public void Two_melds_can_be_laid_in_one_action()
    {
        var (state, logic) = OpenTable();

        Lay(state, logic, Stack(state,
            (Rank.Ten, Suit.Clubs), (Rank.Ten, Suit.Hearts), (Rank.Ten, Suit.Spades),
            (Rank.Queen, Suit.Clubs), (Rank.Queen, Suit.Hearts), (Rank.Queen, Suit.Spades)));

        Assert.Equal(2, MeldsLaid(state));
        Assert.Equal(0, Hand(state).Count);
    }

    [Fact]
    public void A_combined_lay_meets_an_opening_a_single_meld_cannot()
    {
        var (state, logic) = HandAndFoot();
        state.RoundNumber = 1;   // opening requirement: 50

        // Tens are 10 each: 30 alone, refused. With queens it is 60.
        Lay(state, logic, Stack(state,
            (Rank.Ten, Suit.Clubs), (Rank.Ten, Suit.Hearts), (Rank.Ten, Suit.Spades)));
        Assert.Equal(0, Melds(state).Groups.Count);

        Lay(state, logic, Stack(state,
            (Rank.Ten, Suit.Clubs), (Rank.Ten, Suit.Hearts), (Rank.Ten, Suit.Spades),
            (Rank.Queen, Suit.Clubs), (Rank.Queen, Suit.Hearts), (Rank.Queen, Suit.Spades)));
        Assert.Equal(2, Melds(state).Groups.Count);
    }

    [Fact]
    public void Wilds_spread_across_the_melds_that_need_them()
    {
        var (state, logic) = OpenTable();

        // Two tens and two queens, two wilds: each pair needs one.
        Lay(state, logic, Stack(state,
            (Rank.Ten, Suit.Clubs), (Rank.Ten, Suit.Hearts),
            (Rank.Queen, Suit.Clubs), (Rank.Queen, Suit.Hearts),
            (Rank.Two, Suit.Spades), (Rank.Two, Suit.Diamonds)));

        Assert.Equal(2, MeldsLaid(state));
    }

    [Fact]
    public void A_leftover_pair_spoils_the_whole_lay()
    {
        var (state, logic) = OpenTable();

        // Three tens are a meld; two queens are not, and there is no wild to help.
        var cards = Stack(state,
            (Rank.Ten, Suit.Clubs), (Rank.Ten, Suit.Hearts), (Rank.Ten, Suit.Spades),
            (Rank.Queen, Suit.Clubs), (Rank.Queen, Suit.Hearts));
        Lay(state, logic, cards);

        // Nothing moves: half a lay reaching the table would strand the queens.
        Assert.Equal(0, MeldsLaid(state));
        Assert.Equal(5, Hand(state).Count);
    }

    [Fact]
    public void A_lay_of_a_rank_already_on_the_table_joins_that_meld()
    {
        var (state, logic) = OpenTable();   // opening meld is three aces

        Lay(state, logic, Stack(state,
            (Rank.Ace, Suit.Diamonds), (Rank.Ace, Suit.Clubs), (Rank.Ace, Suit.Hearts)));

        // One meld of six aces, not two meld groups of the same rank.
        Assert.Equal(1, Melds(state).Groups.Count);
        Assert.Equal(6, Melds(state).GroupCards(0).Count);
    }

    // ── Ranks that may not be melded, and wilds joining melds ────────────────

    /// <summary>
    /// Hand and Foot's 3s exist to be discarded and score against you; the definition
    /// bars them from melding and the engine must refuse, wilds or no wilds.
    /// </summary>
    [Fact]
    public void Threes_cannot_be_melded()
    {
        var (state, logic) = OpenTable();

        var cards = Stack(state,
            (Rank.Three, Suit.Clubs), (Rank.Three, Suit.Hearts), (Rank.Three, Suit.Spades));
        Lay(state, logic, cards);

        Assert.Equal(0, MeldsLaid(state));
        Assert.Equal(3, Hand(state).Count);
    }

    [Fact]
    public void A_wild_can_join_an_existing_meld_on_its_own()
    {
        var (state, logic) = OpenTable();   // three aces are down

        var wild = Stack(state, (Rank.Two, Suit.Hearts));
        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata["selected_card"] = wild[0].Id;
        logic.Apply(state, new GameAction("add_to_meld"));

        Assert.Equal(1, Melds(state).Groups.Count);
        Assert.Equal(4, Melds(state).GroupCards(0).Count);
        Assert.Empty(Hand(state).Cards);
    }

    /// <summary>
    /// The ratio holds on additions too: a meld may never carry more wilds than
    /// naturals, which the old path only checked when the meld was first laid.
    /// </summary>
    [Fact]
    public void Wilds_cannot_flood_a_meld_through_additions()
    {
        var (state, logic) = OpenTable();   // three natural aces are down

        // Three naturals take three wilds; the fourth would tip the balance.
        for (int i = 0; i < 4; i++)
        {
            var wild = Stack(state, (Rank.Two, (Suit)(i % 4)));
            state.Metadata["dd_turn_state"] = "discard";
            state.Metadata["selected_card"] = wild[0].Id;
            logic.Apply(state, new GameAction("add_to_meld"));
        }

        Assert.Equal(6, Melds(state).GroupCards(0).Count);   // 3 aces + 3 wilds, not 7
        Assert.Single(Hand(state).Cards);                    // the refused wild stays put
    }

    [Fact]
    public void Adding_a_rank_with_no_meld_down_is_refused()
    {
        var (state, logic) = OpenTable();   // only aces are down

        var cards = Stack(state, (Rank.King, Suit.Clubs));
        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata["selected_card"] = cards[0].Id;
        logic.Apply(state, new GameAction("add_to_meld"));

        // The old path quietly created a one-card "meld".
        Assert.Equal(1, Melds(state).Groups.Count);
        Assert.Single(Hand(state).Cards);
    }
}
