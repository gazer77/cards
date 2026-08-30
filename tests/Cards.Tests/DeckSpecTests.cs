using System.Text.Json;
using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Deck composition, declared rather than chosen from a list of names.
/// </summary>
public sealed class DeckSpecTests
{
    private static DeckSpec Parse(string json, int players = 4)
        => DeckSpec.Parse(JsonDocument.Parse(json).RootElement, players);

    [Theory]
    [InlineData("standard-52", 52)]
    [InlineData("standard-104", 104)]
    [InlineData("standard-52-jokers", 54)]
    [InlineData("euchre-24", 24)]
    [InlineData("pinochle-48", 48)]
    public void The_old_deck_names_still_work(string name, int size)
    {
        // Every existing definition uses one of these, so they are a compatibility
        // contract, not a convenience.
        var spec = Parse($"\"{name}\"");
        Assert.Equal(size, spec.Size);
        Assert.Equal(size, DeckBuilder.Build(spec).Count);
    }

    [Fact]
    public void A_rank_range_reads_the_way_a_person_describes_a_deck()
    {
        Assert.Equal(24, Parse("""{ "ranks": "9-A" }""").Size);
        Assert.Equal(52, Parse("""{ "ranks": "2-A" }""").Size);
        Assert.Equal(20, Parse("""{ "ranks": "10-A" }""").Size);
    }

    [Fact]
    public void Ranks_can_be_listed_explicitly()
    {
        var spec = Parse("""{ "ranks": ["9","10","J","Q","K","A"], "copies": 2 }""");

        Assert.Equal(48, spec.Size);   // pinochle, without needing to be called that
    }

    [Fact]
    public void Suits_can_be_restricted()
    {
        var spec = Parse("""{ "ranks": "2-A", "suits": ["hearts","spades"] }""");

        Assert.Equal(26, spec.Size);
        Assert.Equal(2, spec.Suits.Count);
    }

    /// <summary>
    /// The case the whole change exists for: a deck larger than any name covered.
    /// </summary>
    [Fact]
    public void A_deck_can_be_any_size()
    {
        var spec = Parse("""{ "ranks": "2-A", "copies": 6, "jokers": 12 }""");

        Assert.Equal(324, spec.Size);
        Assert.Equal(324, DeckBuilder.Build(spec).Count);
    }

    [Theory]
    [InlineData(2, 3 * 52 + 6)]
    [InlineData(3, 4 * 52 + 8)]
    [InlineData(4, 5 * 52 + 10)]
    [InlineData(6, 7 * 52 + 14)]
    public void Copies_and_jokers_scale_with_the_table(int players, int expectedSize)
    {
        // Hand and Foot's rule: one pack per player, plus one, jokers included.
        const string json = """
        {
          "ranks": "2-A",
          "copies": [
            { "max_players": 2, "count": 3 },
            { "max_players": 3, "count": 4 },
            { "max_players": 4, "count": 5 },
            { "max_players": 5, "count": 6 },
            { "count": 7 }
          ],
          "jokers": [
            { "max_players": 2, "count": 6 },
            { "max_players": 3, "count": 8 },
            { "max_players": 4, "count": 10 },
            { "max_players": 5, "count": 12 },
            { "count": 14 }
          ]
        }
        """;

        Assert.Equal(expectedSize, Parse(json, players).Size);
    }

    /// <summary>
    /// An unknown deck must fail rather than quietly becoming a standard 52. The old
    /// fallback meant a typo dealt the wrong deck and failed somewhere unrelated much
    /// later — and across two clients on different builds, silently dealt different
    /// decks from the same definition.
    /// </summary>
    [Theory]
    [InlineData("\"standard-260\"")]
    [InlineData("\"stnadard-52\"")]
    [InlineData("\"\"")]
    public void An_unknown_deck_name_is_an_error(string json)
        => Assert.Throws<FormatException>(() => Parse(json));

    [Theory]
    [InlineData("""{ "ranks": "A-2" }""")]        // backwards
    [InlineData("""{ "ranks": "2-Z" }""")]        // not a rank
    [InlineData("""{ "ranks": "nonsense" }""")]   // not a range at all
    [InlineData("""{ "suits": ["moons"] }""")]    // suits are drawn by hand; no artwork
    [InlineData("""{ "ranks": "2-A", "copies": 0, "jokers": 0 }""")]
    public void A_deck_that_cannot_be_built_is_an_error(string json)
        => Assert.Throws<FormatException>(() => Parse(json));

    [Fact]
    public void Every_card_of_a_declared_deck_is_distinguishable()
    {
        var deck = DeckBuilder.Build(Parse("""{ "ranks": "2-A", "copies": 5, "jokers": 10 }"""));

        Assert.Equal(270, deck.Count);
        Assert.Equal(deck.Count, deck.Select(c => c.Uid).Distinct().Count());

        // Five of each card, all telling themselves apart.
        Assert.Equal(5, deck.Count(c => c.Id == "Ah"));
    }
}

/// <summary>
/// Every shipped game must declare a deck it can actually deal from.
///
/// Hand and Foot shipped for a long time asking for 26 cards a player from a 104-card
/// deck: fine at two players, out of cards at four, and dead before the deal finished
/// at five. Nothing checked, because a deal that runs out simply leaves a game with no
/// legal move — which looks like a rules problem, not an arithmetic one.
/// </summary>
public sealed class DeckSufficiencyTests
{
    private static GameLoader NewLoader() => new(new EmbeddedGameAssetSource());

    public static TheoryData<string, int> EverySupportedSeatCount
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var def in NewLoader().LoadAllAsync().GetAwaiter().GetResult())
                for (int n = def.MinPlayers; n <= def.MaxPlayers; n++)
                    data.Add(def.Id, n);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EverySupportedSeatCount))]
    public async Task The_deal_fits_the_deck(string gameId, int playerCount)
    {
        var definition = await NewLoader().LoadAsync(gameId);
        Assert.NotNull(definition);

        var spec = DeckSpec.Parse(definition!.Deck, playerCount);

        var deal = definition.Deal;
        if (deal is null) return;   // nothing is dealt; nothing to check

        // Games deal either a flat count each or a number of stacks each.
        int perPlayer = deal.StacksPerPlayer > 0
            ? deal.StacksPerPlayer * deal.CardsPerStack
            : deal.GetCardsPerPlayer(playerCount);

        int needed = perPlayer * playerCount;

        Assert.True(needed <= spec.Size,
            $"{gameId} at {playerCount} players deals {needed} cards from a deck of {spec.Size}.");
    }

    /// <summary>
    /// A deal that consumes the whole deck leaves nothing to draw from, which several
    /// games need — Go Fish and Hand and Foot both draw after the deal, and Hand and
    /// Foot also flips one card to start the discard pile.
    /// </summary>
    [Theory]
    [MemberData(nameof(EverySupportedSeatCount))]
    public async Task A_game_that_draws_after_dealing_has_cards_left_to_draw(string gameId, int playerCount)
    {
        var definition = await NewLoader().LoadAsync(gameId);
        var deal = definition?.Deal;
        if (definition is null || deal is null) return;

        // "remainder_to" naming a deck is the definition saying it expects leftovers.
        if (!string.Equals(deal.RemainderTo, "deck", StringComparison.OrdinalIgnoreCase)) return;

        var spec = DeckSpec.Parse(definition.Deck, playerCount);

        int perPlayer = deal.StacksPerPlayer > 0
            ? deal.StacksPerPlayer * deal.CardsPerStack
            : deal.GetCardsPerPlayer(playerCount);

        int remaining = spec.Size - perPlayer * playerCount;

        Assert.True(remaining > 0,
            $"{gameId} at {playerCount} players deals its entire {spec.Size}-card deck, " +
            "leaving nothing in the draw pile it says it wants.");
    }
}

/// <summary>
/// Arithmetic in a definition, where a number is expected.
///
/// The case that demanded it: Hand and Foot's deck is "one pack per player, plus one",
/// which took five max_players tiers for copies and five more for jokers — ten lines to
/// say what a person says in six words, and ten places for the two to disagree.
/// </summary>
public sealed class RuleExpressionTests
{
    private static readonly Dictionary<string, int> Values = new()
    {
        ["players"] = 4,
        ["round"]   = 2,
    };

    [Theory]
    [InlineData("1", 1)]
    [InlineData("players", 4)]
    [InlineData("players + 1", 5)]
    [InlineData("(players + 1) * 2", 10)]
    [InlineData("players * 2 + 1", 9)]      // precedence, not left-to-right
    [InlineData("1 + players * 2", 9)]
    [InlineData("players - round", 2)]
    [InlineData("12 / round", 6)]
    [InlineData("min(players, 3)", 3)]
    [InlineData("max(players, 9)", 9)]
    [InlineData("  players   +   1  ", 5)]  // whitespace is not significant
    [InlineData("-2 + players", 2)]
    public void Evaluates(string text, int expected)
        => Assert.Equal(expected, RuleExpression.Evaluate(text, Values));

    /// <summary>
    /// A rule that cannot be read must fail, not evaluate to something plausible. A deck
    /// silently coming out the wrong size is the failure this whole area already had once.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("players +")]
    [InlineData("+ ")]
    [InlineData("(players")]
    [InlineData("players)")]
    [InlineData("seats")]              // not a name this rule can refer to
    [InlineData("players $ 2")]
    [InlineData("players / 0")]
    [InlineData("sqrt(players)")]      // not a function it can call
    [InlineData("min(players)")]       // wrong arity
    public void Refuses_what_it_cannot_read(string text)
        => Assert.Throws<FormatException>(() => RuleExpression.Evaluate(text, Values));

    [Fact]
    public void An_unknown_name_says_what_is_available()
    {
        var ex = Assert.Throws<FormatException>(() => RuleExpression.Evaluate("seats", Values));

        // Whoever wrote the rule needs to know what they could have written instead.
        Assert.Contains("players", ex.Message);
    }

    [Fact]
    public void Validation_reports_the_problem_without_a_game()
    {
        Assert.True(RuleExpression.IsValid("players + 1", ["players"], out _));

        Assert.False(RuleExpression.IsValid("players + ", ["players"], out var error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// The expressions replaced ten tiers; they must build exactly the decks the tiers did.
    /// </summary>
    [Theory]
    [InlineData(2, 3 * 52 + 6)]
    [InlineData(3, 4 * 52 + 8)]
    [InlineData(4, 5 * 52 + 10)]
    [InlineData(5, 6 * 52 + 12)]
    [InlineData(6, 7 * 52 + 14)]
    public async Task Hand_and_foot_builds_the_same_deck_as_the_tiers_did(int players, int expectedSize)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = await loader.LoadAsync("hand-and-foot");

        Assert.Equal(expectedSize, DeckSpec.Parse(definition!.Deck, players).Size);
    }
}
