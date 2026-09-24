using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// What a Golf grid is worth, card by card. Asked because a round came back at −1 and
/// that looked impossible: it is not, and these are the arithmetic to say why.
///
/// Twos and jokers are −2 each under this definition, kings are nothing, and a column
/// of two matching ranks counts zero however high the rank.
/// </summary>
public sealed class GolfScoringTests
{
    /// <summary>A 2x3 grid laid out row-major, every card face-up unless said otherwise.</summary>
    private static int Score(params string[] cards)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(1) };
        LogicRegistry.Create(definition).Initialize(state, 2, []);

        var grid = state.Zones["grid:player0"];
        grid.Cards.Clear();
        foreach (var text in cards)
        {
            bool down = text.EndsWith("!");
            var card = Parse(down ? text[..^1] : text);
            card.IsFaceUp = !down;
            grid.Cards.Add(card);
        }

        state.Scores.Clear();
        ScoringEngine.Apply(state);
        return state.GetScore("player0");
    }

    private static Card Parse(string text)
    {
        if (text is "JKR") return new Card(Suit.Spades, Rank.Joker);

        var suit = text[^1] switch
        {
            'h' => Suit.Hearts, 'd' => Suit.Diamonds, 'c' => Suit.Clubs, _ => Suit.Spades,
        };
        var rank = text[..^1] switch
        {
            "A" => Rank.Ace, "K" => Rank.King, "Q" => Rank.Queen, "J" => Rank.Jack,
            "10" => Rank.Ten, _ => (Rank)int.Parse(text[..^1]),
        };
        return new Card(suit, rank);
    }

    [Fact]
    public void A_hand_of_twos_a_joker_and_an_ace_really_does_come_out_below_zero()
    {
        // The reported round: −2 −2 −2 + 1 + 0 + 4 = −1. Nothing is wrong with it.
        Assert.Equal(-1, Score("2d", "2c", "JKR", "As", "Kd", "4c"));
    }

    [Fact]
    public void Each_card_is_worth_what_the_definition_says()
    {
        // Laid so no column matches — a matching column would cancel to nothing, which
        // costs you when the pair is negative.
        Assert.Equal(-12, Score("2d", "2c", "2h", "JKR", "JKR", "JKR"));  // six at −2
        Assert.Equal(0,   Score("Kd", "Kc", "Kh", "Ks", "Kd", "Kc"));     // kings are nothing…
        Assert.Equal(60,  Score("Qd", "Jc", "10h", "Jd", "10c", "Qs"));   // …courts are ten
    }

    [Fact]
    public void A_column_of_matching_ranks_counts_nothing_at_all()
    {
        // Row-major, so index 0 and index 3 share the first column. Two queens there
        // cancel — 20 points gone, which is the rule people play Golf for.
        Assert.Equal(0,  Score("Qd", "Kc", "Kh", "Qs", "Kd", "Kc"));
        Assert.Equal(20, Score("Qd", "Kc", "Kh", "Jd", "Kd", "Kc"));      // …but only when they match
    }

    [Fact]
    public void A_card_left_face_down_costs_the_declared_penalty()
    {
        // face_down_penalty: 2 — a card nobody turned is charged rather than revealed.
        // Some tables flip everything at the end instead; that would be a different
        // definition, not different code.
        Assert.Equal(2 * 6, Score("2d!", "2c!", "JKR!", "As!", "Kd!", "4c!"));
        Assert.Equal(-2 + 2 * 5, Score("2d", "2c!", "JKR!", "As!", "Kd!", "4c!"));
    }
}

/// <summary>
/// The badge that puts a zone's score on the table, so a Golf player can read what
/// they are holding instead of adding it up every turn.
/// </summary>
public sealed class ZoneScoreBadgeTests
{
    private static (GameState State, IGameLogic Logic) Golf(int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(12) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    [Fact]
    public void Golf_shows_each_grid_what_it_is_worth()
    {
        var (state, _) = Golf(2);
        var grid = state.Definition.Zones.Single(z => z.Id == "grid");

        Assert.Contains(grid.GroupBadges, b => b.Shows == "score");
    }

    [Fact]
    public void The_badge_is_the_number_the_round_would_charge()
    {
        // The whole point: a running figure that turns out to be a guess would be worse
        // than no figure, so it is the same arithmetic the scoring engine runs.
        var (state, _) = Golf(2);
        var zone = state.Zones["grid:player0"];

        // With every card turned there is nothing left to be unknown, so the readout and
        // the charge must agree exactly — including the matched-column rule.
        foreach (var card in zone.Cards) card.IsFaceUp = true;

        int badge = ScoringEngine.ZoneScore(state, zone);

        state.Scores.Clear();
        ScoringEngine.Apply(state);
        Assert.Equal(state.GetScore("player0"), badge);
    }

    [Fact]
    public void A_grid_nobody_has_turned_reads_as_nothing_known()
    {
        var (state, _) = Golf(2);
        var zone = state.Zones["grid:player0"];

        // Not 12 — the six face-down cards would each be charged 2 at the end of the
        // round, but charging them in a running readout tells a player they hold twelve
        // points before they know a single card. The badge shows its zero text instead.
        Assert.All(zone.Cards, c => Assert.False(c.IsFaceUp));
        Assert.Equal(0, ScoringEngine.ZoneScore(state, zone));

        // …while the round itself still charges for them.
        state.Scores.Clear();
        ScoringEngine.Apply(state);
        Assert.Equal(12, state.GetScore("player0"));
    }

    [Fact]
    public void Score_is_a_kind_of_badge_the_validator_knows()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        Assert.Empty(DefinitionValidator.Validate(definition));

        definition.Zones.Single(z => z.Id == "grid").GroupBadges[0].Shows = "vibes";
        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("shows 'vibes'"));
    }
}
