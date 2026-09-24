using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Blackjack played for chips, the way its definition always said it was.
///
/// Before this there were no chips at all — the win condition ranked scores nobody was
/// ever awarded — only the first seat's hand was ever settled, a natural in that seat
/// skipped every other seat's turn, and at "one player" the only seat at the table was
/// the dealer's. Split, double and surrender were declared and offered by nothing.
/// </summary>
public sealed class BlackjackTests
{
    private static (GameState State, IGameLogic Logic) Table(int players, params string[] rules)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("blackjack").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(3) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, players, rules);
        return (state, logic);
    }

    private static Card C(Rank rank, Suit suit = Suit.Spades, bool up = true)
        => new(suit, rank, isFaceUp: up);

    /// <summary>Replaces the dealt hands with known ones and settles whose turn it is.</summary>
    private static void Deal(GameState state, Card[] dealer, params Card[][] players)
    {
        for (int i = 0; i < players.Length; i++)
        {
            var hand = state.Zones[$"hand:player{i}"];
            hand.Clear();
            foreach (var c in players[i]) hand.Add(c);
        }

        var d = state.Zones[$"hand:{state.Players.Single(p => p.Role is not null).Id}"];
        d.Clear();
        foreach (var c in dealer) d.Add(c);

        state.CurrentPlayerIndex = 0;
        state.Metadata["bj_state"] = "player_turn";
    }

    private static void PlayOut(GameState state, IGameLogic logic)
    {
        for (int i = 0; i < 40 && state.Metadata.GetValueOrDefault("bj_state") == "dealer_turn"; i++)
            logic.Apply(state, new GameAction("tap"));
    }

    [Fact]
    public void One_player_means_one_player_and_a_dealer()
    {
        var (state, _) = Table(1);

        Assert.Equal(2, state.Players.Count);
        Assert.Null(state.Players[0].Role);
        Assert.Equal("dealer", state.Players[1].Role);
        Assert.Equal("Dealer", state.Players[1].Name);
    }

    [Fact]
    public void Players_hold_chips_and_the_house_does_not()
    {
        var (state, _) = Table(3);

        Assert.All(state.Players.Where(p => p.Role is null), p => Assert.Equal(100, state.GetScore(p.Id)));
        Assert.False(state.Scores.ContainsKey(state.Players.Single(p => p.Role is not null).Id));
    }

    [Fact]
    public void A_winning_hand_is_paid_and_a_losing_one_is_taken()
    {
        var (state, logic) = Table(2);
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)],
             [C(Rank.Ten), C(Rank.Nine)],       // 19 beats 17
             [C(Rank.Ten), C(Rank.Six)]);       // 16 loses to 17

        logic.Apply(state, new GameAction("stand"));
        logic.Apply(state, new GameAction("stand"));
        PlayOut(state, logic);

        Assert.Equal(110, state.GetScore("player0"));
        Assert.Equal(90,  state.GetScore("player1"));
    }

    [Fact]
    public void A_natural_pays_the_declared_odds()
    {
        var (state, logic) = Table(1);
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)], [C(Rank.Ace), C(Rank.King)]);

        logic.Apply(state, new GameAction("stand"));
        PlayOut(state, logic);

        Assert.Equal(115, state.GetScore("player0"));   // 10 at 3:2
    }

    [Fact]
    public void A_natural_in_the_first_seat_does_not_skip_the_rest_of_the_table()
    {
        // The old handler sent the round to the dealer the moment seat 0 had 21, so
        // every other player sat the hand out without being asked.
        for (ulong seed = 1; seed < 400; seed++)
        {
            var loader = new GameLoader(new EmbeddedGameAssetSource());
            var definition = loader.LoadAsync("blackjack").GetAwaiter().GetResult()!;
            var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
            LogicRegistry.Create(definition).Initialize(state, 3, []);

            var first = state.Zones["hand:player0"];
            bool natural = first.Count == 2 && first.Cards.Any(c => c.Rank == Rank.Ace)
                        && first.Cards.Any(c => c.Rank >= Rank.Ten && c.Rank <= Rank.King);
            if (!natural) continue;

            // Somebody after the first seat is being asked, unless they all have one too.
            bool othersAllNatural = new[] { "player1", "player2" }.All(id =>
                state.Zones[$"hand:{id}"].Cards.Any(c => c.Rank == Rank.Ace)
                && state.Zones[$"hand:{id}"].Cards.Any(c => c.Rank >= Rank.Ten && c.Rank <= Rank.King));
            if (othersAllNatural) continue;

            Assert.Equal("player_turn", state.Metadata["bj_state"]);
            Assert.NotEqual("player0", state.CurrentPlayer.Id);
            return;
        }

        Assert.Fail("No seed dealt the first seat a natural, so nothing was tested.");
    }

    [Fact]
    public void Doubling_doubles_the_stake_and_takes_one_card()
    {
        var (state, logic) = Table(1);
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Eight, up: false)], [C(Rank.Six), C(Rank.Five)]);
        state.Zones["deck"].Add(C(Rank.Ten, up: false));   // the one card the double takes

        Assert.Contains(logic.GetValidActions(state), a => a.Type == "double_down");
        logic.Apply(state, new GameAction("double_down"));

        Assert.Equal(3, state.Zones["hand:player0"].Count);
        PlayOut(state, logic);
        Assert.Equal(120, state.GetScore("player0"));   // 21 beats 18, on a stake of 20
    }

    [Fact]
    public void A_pair_splits_into_two_hands_each_with_its_own_stake()
    {
        var (state, logic) = Table(1);
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)],
             [C(Rank.Eight, Suit.Spades), C(Rank.Eight, Suit.Hearts)]);
        // Cards come off the top of the deck: first to the hand, then to the split hand.
        state.Zones["deck"].Add(C(Rank.Two, up: false));
        state.Zones["deck"].Add(C(Rank.Ten, up: false));
        state.Zones["deck"].Add(C(Rank.King, up: false));

        Assert.Contains(logic.GetValidActions(state), a => a.Type == "split");
        logic.Apply(state, new GameAction("split"));

        Assert.Equal(2, state.Zones["hand:player0"].Count);
        Assert.Equal(2, state.Zones["split:player0"].Count);

        // Stand on both: the turn moves to the second hand, then to the dealer.
        logic.Apply(state, new GameAction("stand"));
        Assert.Equal("split", state.Metadata["bj_active:player0"]);
        logic.Apply(state, new GameAction("stand"));
        PlayOut(state, logic);

        // 18 and 18 against the dealer's 17: two stakes of ten, both won.
        Assert.Equal(120, state.GetScore("player0"));
    }

    [Fact]
    public void Split_is_offered_only_for_a_pair_and_only_where_the_definition_allows_it()
    {
        var (state, logic) = Table(1);
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)], [C(Rank.Eight), C(Rank.Nine)]);
        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "split");

        var (strict, strictLogic) = Table(1, "no_split");
        Deal(strict, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)], [C(Rank.Eight), C(Rank.Eight)]);
        Assert.DoesNotContain(strictLogic.GetValidActions(strict), a => a.Type == "split");
    }

    [Fact]
    public void Surrender_gives_back_half_and_only_where_the_house_allows_it()
    {
        var (plain, plainLogic) = Table(1);
        Deal(plain, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)], [C(Rank.Ten), C(Rank.Six)]);
        Assert.DoesNotContain(plainLogic.GetValidActions(plain), a => a.Type == "surrender");

        var (state, logic) = Table(1, "surrender");
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)], [C(Rank.Ten), C(Rank.Six)]);

        Assert.Contains(logic.GetValidActions(state), a => a.Type == "surrender");
        logic.Apply(state, new GameAction("surrender"));
        PlayOut(state, logic);

        Assert.Equal(95, state.GetScore("player0"));
    }

    [Fact]
    public void Six_five_pays_less_than_three_two()
    {
        var (state, logic) = Table(1, "blackjack_pays_6_5");
        Deal(state, dealer: [C(Rank.Ten), C(Rank.Seven, up: false)], [C(Rank.Ace), C(Rank.Queen)]);

        logic.Apply(state, new GameAction("stand"));
        PlayOut(state, logic);

        Assert.Equal(112, state.GetScore("player0"));   // 10 at 6:5, rounded down
    }

    [Fact]
    public void The_dealer_is_never_the_winner()
    {
        // The house holds no chips and is not ranked: with every player down, one of
        // them still wins the game.
        var (state, _) = Table(2);
        state.Scores["player0"] = 40;
        state.Scores["player1"] = 20;
        state.RoundNumber = 99;

        var result = WinConditionEngine.Instance.Check(state);
        Assert.NotNull(result);
        Assert.Equal("player0", result.WinnerId);
    }

    [Fact]
    public void A_full_game_ends_with_the_chips_accounted_for()
    {
        var (state, logic) = Table(4);
        foreach (var p in state.Players.Where(p => p.Role is null))
            state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);

        for (int i = 0; i < 2000 && !logic.IsGameOver(state); i++)
            logic.Apply(state, logic.GetAutoAction(state));

        Assert.True(logic.IsGameOver(state));
        Assert.Contains(state.GameLog, l => l.Contains("wins +") || l.Contains("dealer wins"));

        // No card was lost along the way: every one is in a hand, the deck or the tray.
        Assert.Equal(52, state.Zones.Values.Sum(z => z.Count));
    }
}
