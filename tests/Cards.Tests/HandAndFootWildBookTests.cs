using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Hand and Foot's Wild Books house rule: wilds melded on their own, three to start and
/// seven to a book worth 1,000, and a wild book among the books going out needs. Off,
/// wilds stay stand-ins — and a dirty book is paid 300, as the rules say, not 1,000.
/// </summary>
public sealed class HandAndFootWildBookTests
{
    private static int _uid = 9500;

    private static List<Card> Make(params (Rank Rank, Suit Suit)[] cards)
        => cards.Select(c => new Card(c.Suit, c.Rank, isFaceUp: true) { Uid = _uid++ }).ToList();

    private static List<Card> Give(Zone zone, params (Rank Rank, Suit Suit)[] cards)
    {
        var added = Make(cards);
        foreach (var card in added) zone.Add(card);
        return added;
    }

    private static List<Card> Wilds(int n)
        => Make([.. Enumerable.Range(0, n).Select(i => (i % 2 == 0 ? Rank.Two : Rank.Joker, Suit.Clubs))]);

    private static (GameState State, IGameLogic Logic) Discarding(bool wildBooks)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hand-and-foot")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(5) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, wildBooks ? ["wild_books"] : []);

        foreach (var zone in state.Zones.Values.Where(z => z.Id.StartsWith("meld"))) zone.Clear();

        // Opened, with kings.
        string me = state.CurrentPlayer.Id;
        Melds(state).AddGroup(Make((Rank.King, Suit.Clubs), (Rank.King, Suit.Hearts), (Rank.King, Suit.Spades)));
        Hand(state).Clear();
        state.Metadata["dd_turn_state"] = "discard";
        return (state, logic);
    }

    private static Zone Hand(GameState s)  => s.Zones[$"hand:{s.CurrentPlayer.Id}"];
    private static Zone Melds(GameState s) => s.Zones[$"meld:{s.CurrentPlayer.Id}"];

    private static void Pick(GameState state, IEnumerable<Card> cards)
        => state.Metadata["selected_card"] = string.Join(",", cards.Select(c => c.Uid));

    [Fact]
    public void Three_wilds_start_a_wild_meld_and_more_go_onto_it()
    {
        var (state, logic) = Discarding(wildBooks: true);
        var hand = Hand(state);
        var three = Give(hand, (Rank.Two, Suit.Clubs), (Rank.Joker, Suit.Hearts), (Rank.Two, Suit.Spades));
        var later = Give(hand, (Rank.Two, Suit.Diamonds));
        Give(hand, (Rank.Four, Suit.Clubs), (Rank.Seven, Suit.Hearts));

        Pick(state, three);
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "meld");
        logic.Apply(state, new GameAction("meld"));
        Assert.Equal(2, Melds(state).Groups.Count);
        Assert.True(MeldRules.IsAllWild(Melds(state).GroupCards(1), MeldRules.WildRanks(state.Definition)));
        Assert.Equal(3, Melds(state).GroupCards(0).Count);   // the kings took none

        // One more wild: onto the wild meld, not the kings — and a double tap does it.
        Assert.Equal("add_to_meld", logic.GetDefaultCardAction(state, later[0].Id, later[0].Uid)?.Type);
        logic.Apply(state, logic.GetDefaultCardAction(state, later[0].Id, later[0].Uid)!);
        Assert.Equal(4, Melds(state).GroupCards(1).Count);
        Assert.Equal(3, Melds(state).GroupCards(0).Count);
    }

    [Fact]
    public void Two_wilds_do_not_start_one()
    {
        var (state, logic) = Discarding(wildBooks: true);
        var two = Give(Hand(state), (Rank.Two, Suit.Clubs), (Rank.Two, Suit.Spades));
        Give(Hand(state), (Rank.Four, Suit.Clubs), (Rank.Seven, Suit.Hearts));

        Pick(state, two);
        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "meld");
    }

    [Fact]
    public void Without_the_house_rule_wilds_never_meld_alone()
    {
        var (state, logic) = Discarding(wildBooks: false);
        var three = Give(Hand(state), (Rank.Two, Suit.Clubs), (Rank.Joker, Suit.Hearts), (Rank.Two, Suit.Spades));
        Give(Hand(state), (Rank.Four, Suit.Clubs), (Rank.Seven, Suit.Hearts));

        Pick(state, three);
        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "meld");
    }

    [Fact]
    public void Books_are_paid_by_kind_clean_dirty_and_wild()
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hand-and-foot")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(5) };
        LogicRegistry.Create(definition).Initialize(state, 2, ["wild_books"]);

        foreach (var zone in state.Zones.Values.Where(z => z.Id.StartsWith("meld") || z.Id.StartsWith("hand") || z.Id.StartsWith("foot")))
            zone.Clear();

        int Score(Action<Zone> lay)
        {
            var melds = state.Zones["meld:player0"];
            melds.Clear();
            lay(melds);
            state.Scores.Clear();
            ScoringEngine.Apply(state);
            return state.GetScore("player0");
        }

        // Seven fives (5 each) against seven fives with a wild: the bonus is the difference
        // the two make beyond their cards.
        int clean = Score(m => m.AddGroup(Make([.. Enumerable.Range(0, 7).Select(_ => (Rank.Five, Suit.Clubs))])));
        int dirty = Score(m => m.AddGroup(Make([.. Enumerable.Range(0, 6).Select(_ => (Rank.Five, Suit.Clubs)), (Rank.Two, Suit.Hearts)])));
        int wild  = Score(m => m.AddGroup(Wilds(7)));

        Assert.Equal(7 * 5 + 500, clean);
        Assert.Equal(6 * 5 + 20 + 300, dirty);
        Assert.Equal(4 * 20 + 3 * 50 + 1000, wild);
    }

    [Fact]
    public void Going_out_with_the_house_rule_takes_a_wild_book_too()
    {
        var (state, _) = Discarding(wildBooks: true);
        var melds = Melds(state);
        melds.Clear();
        melds.AddGroup(Make([.. Enumerable.Range(0, 7).Select(_ => (Rank.Five, Suit.Clubs))]));
        melds.AddGroup(Make([.. Enumerable.Range(0, 6).Select(_ => (Rank.Nine, Suit.Clubs)), (Rank.Two, Suit.Hearts)]));

        var condition = state.Definition.Phases.First(p => p.Id == "play").Extra!["go_out_condition"];
        Assert.False(RuleCondition.Evaluate(condition, state));

        melds.AddGroup(Wilds(7));
        Assert.True(RuleCondition.Evaluate(condition, state));
    }

    [Fact]
    public void A_wild_book_is_not_the_dirty_book_going_out_needs()
    {
        // Without the house rule, a clean book and a wild book are not the two books: the
        // dirty one means wilds among naturals.
        var (state, _) = Discarding(wildBooks: true);
        var melds = Melds(state);
        melds.Clear();
        melds.AddGroup(Make([.. Enumerable.Range(0, 7).Select(_ => (Rank.Five, Suit.Clubs))]));
        melds.AddGroup(Wilds(7));

        Assert.Equal(0, RuleCondition.CountBooks(melds, state, "dirty"));
        Assert.Equal(1, RuleCondition.CountBooks(melds, state, "all_wild"));
        Assert.Equal(1, RuleCondition.CountBooks(melds, state, "wild"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Computer_players_build_wild_books_and_finish(int seats)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hand-and-foot")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(11) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, ["wild_books"]);
        foreach (var p in state.Players)
            state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);

        var wilds = MeldRules.WildRanks(state.Definition);
        bool sawWildMeld = false, sawWentOut = false;
        for (int i = 0; i < 40000 && !logic.IsGameOver(state); i++)
        {
            logic.Apply(state, logic.GetAutoAction(state));
            sawWildMeld |= state.Zones.Values.Where(z => z.Id.StartsWith("meld"))
                .Any(z => Enumerable.Range(0, z.Groups.Count).Any(g => MeldRules.IsAllWild(z.GroupCards(g), wilds)));
            sawWentOut |= state.Metadata.ContainsKey("dd_go_out_player");
        }

        Assert.True(logic.IsGameOver(state));
        Assert.True(sawWildMeld, "no computer player ever laid a wild meld");
        Assert.True(sawWentOut, "nobody ever went out");
    }
}
