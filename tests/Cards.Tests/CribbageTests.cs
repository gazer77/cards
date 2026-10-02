using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Cribbage as it is played: laying away to the crib, the cut, the play to thirty-one
/// with its goes and last card, the show, and a game won the moment the pegs get there.
/// </summary>
public sealed class CribbageTests
{
    private static (GameState State, IGameLogic Logic) Start(int players, ulong seed = 1)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "cribbage")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, players, []);
        return (state, logic);
    }

    private static int _uid = 5000;

    /// <summary>A card with a uid of its own, as a dealt deck gives every card.</summary>
    private static Card C(Suit suit, Rank rank) => new(suit, rank, isFaceUp: true) { Uid = Interlocked.Increment(ref _uid) };
    private static Card Down(Suit suit, Rank rank) { var c = C(suit, rank); c.IsFaceUp = false; return c; }

    /// <summary>A play about to begin: the given hands, player1 dealing, so player0 leads.</summary>
    private static (GameState State, IGameLogic Logic) AtThePlay(params Card[][] hands)
    {
        var (state, logic) = Start(hands.Length);
        foreach (var z in state.Zones.Values) z.Clear();
        foreach (var key in state.Metadata.Keys.Where(k => k.StartsWith("peg_") || k.StartsWith("crib_")).ToList())
            state.Metadata.Remove(key);
        state.PlayerAgents.Clear();
        state.DealerId = "player1";
        for (int i = 0; i < hands.Length; i++) state.Zones[$"hand:player{i}"].AddRange(hands[i]);
        state.Zones["starter"].Add(C(Suit.Diamonds, Rank.Two));
        state.CurrentPhaseId = "play";
        logic.GetValidActions(state);
        return (state, logic);
    }

    private static void Play(GameState state, IGameLogic logic, Card card)
    {
        Assert.Equal(state.Players.Single(p => state.Zones[$"hand:{p.Id}"].Cards.Contains(card)).Id, state.CurrentPlayer.Id);
        logic.Apply(state, new GameAction("play_card", CardId: card.Id, CardUid: card.Uid));
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public void Everyone_lays_away_to_a_crib_of_four_face_down(int players)
    {
        var (state, logic) = Start(players);
        foreach (var p in state.Players) state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);

        for (int i = 0; i < 20 && state.CurrentPhaseId == "discard"; i++)
            logic.Apply(state, logic.GetAutoAction(state));

        Assert.All(state.Players, p => Assert.Equal(4, state.Zones[$"hand:{p.Id}"].Count));
        Assert.Equal(4, state.Zones["crib"].Count);
        Assert.All(state.Zones["crib"].Cards, c => Assert.False(c.IsFaceUp));
    }

    [Fact]
    public void Fifteens_pairs_and_the_last_card_peg_as_they_are_made()
    {
        var ten  = C(Suit.Spades, Rank.Ten);  var fiveH = C(Suit.Hearts, Rank.Five);
        var fiveC = C(Suit.Clubs, Rank.Five); var six   = C(Suit.Diamonds, Rank.Six);
        var (state, logic) = AtThePlay([ten, fiveH], [fiveC, six]);

        Play(state, logic, ten);     // 10
        Play(state, logic, fiveC);   // 15 — fifteen for 2
        Play(state, logic, fiveH);   // 20 — a pair for 2
        Play(state, logic, six);     // 26 — last card for 1

        Assert.Equal(2, state.GetScore("player0"));
        Assert.Equal(3, state.GetScore("player1"));
        Assert.Equal("show", state.CurrentPhaseId);
    }

    [Fact]
    public void A_go_pegs_the_last_to_play_and_the_count_starts_again_from_the_next()
    {
        var kingS = C(Suit.Spades, Rank.King); var queen = C(Suit.Spades, Rank.Queen); var two = C(Suit.Clubs, Rank.Two);
        var kingH = C(Suit.Hearts, Rank.King); var nine  = C(Suit.Diamonds, Rank.Nine);
        var (state, logic) = AtThePlay([kingS, queen, two], [kingH, nine]);

        Play(state, logic, kingS);   // 10
        Play(state, logic, kingH);   // 20 — a pair for 2
        Play(state, logic, queen);   // 30

        // player1's nine would make 39: they can only say go.
        Assert.Equal("player1", state.CurrentPlayer.Id);
        Assert.Equal(["go"], logic.GetValidActions(state).Select(a => a.Type));
        logic.Apply(state, new GameAction("go"));

        // player0's two would make 32 too: the go is theirs, and the count starts again
        // from the seat after them.
        Assert.Equal(1, state.GetScore("player0"));
        Assert.Equal("0", state.Metadata["peg_count"]);
        Assert.Equal("player1", state.CurrentPlayer.Id);

        Play(state, logic, nine);    // 9
        Play(state, logic, two);     // 11 — last card for 1

        Assert.Equal(2, state.GetScore("player0"));
        Assert.Equal(2, state.GetScore("player1"));
    }

    [Fact]
    public void The_game_ends_the_moment_a_player_pegs_out()
    {
        var ten = C(Suit.Spades, Rank.Ten); var five = C(Suit.Clubs, Rank.Five);
        var (state, logic) = AtThePlay([ten, C(Suit.Hearts, Rank.Two)], [five, C(Suit.Hearts, Rank.Three)]);
        state.Scores["player1"] = 120;

        Play(state, logic, ten);
        Play(state, logic, five);    // fifteen for 2: 122

        Assert.True(logic.IsGameOver(state));
        Assert.Equal("player1", state.Metadata["last_winner"]);
    }

    [Fact]
    public void A_jack_turned_for_the_starter_pegs_the_dealer_his_heels()
    {
        var (state, logic) = Start(2);
        state.CurrentPhaseId = "cut";
        var deck = state.Zones["deck"];
        deck.Clear();
        foreach (var suit in new[] { Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades })
            for (int i = 0; i < 5; i++) deck.Add(new Card(suit, Rank.Jack));
        int before = state.GetScore(state.DealerId!);

        logic.Apply(state, new GameAction("tap"));

        Assert.Equal(Rank.Jack, state.Zones["starter"].TopCard!.Rank);
        Assert.Equal(before + 2, state.GetScore(state.DealerId!));
        Assert.Contains(state.Announcements, a => a.PlayerId == state.DealerId && a.Text.Contains("heels"));
    }

    [Fact]
    public void The_show_counts_each_hand_where_it_was_played_and_the_crib_for_the_dealer()
    {
        var (state, logic) = Start(2);
        foreach (var z in state.Zones.Values) z.Clear();
        state.DealerId = "player1";
        state.Zones["starter"].Add(C(Suit.Spades, Rank.Five));
        state.Zones["played:player0"].AddRange([C(Suit.Hearts, Rank.Five), C(Suit.Clubs, Rank.Five), C(Suit.Diamonds, Rank.Five), C(Suit.Spades, Rank.Jack)]);
        state.Zones["played:player1"].AddRange([C(Suit.Hearts, Rank.Two), C(Suit.Clubs, Rank.Four), C(Suit.Diamonds, Rank.Six), C(Suit.Spades, Rank.Eight)]);
        state.Zones["crib"].AddRange([Down(Suit.Hearts, Rank.Seven), Down(Suit.Clubs, Rank.Eight), Down(Suit.Diamonds, Rank.Nine), Down(Suit.Hearts, Rank.Ace)]);
        state.CurrentPhaseId = "show";

        logic.Apply(state, new GameAction("tap"));   // player0, the dealer's left: the 29
        Assert.Equal(29, state.GetScore("player0"));

        logic.Apply(state, new GameAction("tap"));   // player1: 2-4-6-8 with a five — one fifteen...
        int hand = CribbageScore.Show(state.Zones["played:player1"].Cards.ToList(), state.Zones["starter"].TopCard, false).Total;
        Assert.Equal(hand, state.GetScore("player1"));

        logic.Apply(state, new GameAction("tap"));   // the crib, turned over, for the dealer
        Assert.All(state.Zones["crib"].Cards, c => Assert.True(c.IsFaceUp));
        int crib = CribbageScore.Show(state.Zones["crib"].Cards.ToList(), state.Zones["starter"].TopCard, true).Total;
        Assert.Equal(hand + crib, state.GetScore("player1"));

        logic.Apply(state, new GameAction("tap"));   // the deal on the score card, and on
        Assert.Equal(29, state.ScoreHistory[^1].Scores["player0"]);
        Assert.Equal("new_round", state.CurrentPhaseId);
    }
}
