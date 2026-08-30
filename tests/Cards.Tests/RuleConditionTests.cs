using System.Text.Json;
using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Questions a definition can ask about the position.
///
/// These matter more than most tests: a condition that quietly answers false makes a rule
/// unreachable, and one that quietly answers true makes an illegal move legal. Neither
/// looks like a failure from outside — the game simply plays wrong.
/// </summary>
public sealed class RuleConditionTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static (GameState State, IGameLogic Logic) Table(int seats = 2)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(5),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    private static Zone Hand(GameState s) => s.Zones[$"hand:{s.CurrentPlayer.Id}"];
    private static Zone Melds(GameState s) => s.Zones[$"meld:{s.CurrentPlayer.Id}"];

    private static bool Holds(string json, GameState state)
        => RuleCondition.Evaluate(Json(json), state);

    // ── Terms ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Stock_exhausted_follows_the_deck()
    {
        var (state, _) = Table();

        Assert.False(Holds("\"stock_exhausted\"", state));

        state.Zones["deck"].Clear();
        Assert.True(Holds("\"stock_exhausted\"", state));
    }

    [Fact]
    public void Team_has_melded_follows_the_meld_area()
    {
        var (state, _) = Table();

        Assert.False(Holds("\"team_has_melded\"", state));

        Melds(state).AddGroup([new Card(Suit.Clubs, Rank.Seven) { Uid = 5001 }]);
        Assert.True(Holds("\"team_has_melded\"", state));
    }

    [Fact]
    public void Hand_empty_follows_the_hand()
    {
        var (state, _) = Table();

        Assert.False(Holds("\"hand_empty\"", state));

        Hand(state).Clear();
        Assert.True(Holds("\"hand_empty\"", state));
    }

    /// <summary>
    /// The Hand and Foot rule for claiming the discard pile: you must already hold two
    /// cards matching the card on top of it.
    /// </summary>
    [Fact]
    public void Hand_count_of_rank_can_read_the_top_of_the_discard()
    {
        var (state, _) = Table();

        var discard = state.Zones["discard"];
        discard.Clear();
        discard.Add(new Card(Suit.Spades, Rank.Nine, isFaceUp: true) { Uid = 5100 });

        var hand = Hand(state);
        hand.Clear();
        hand.Add(new Card(Suit.Clubs, Rank.Nine) { Uid = 5101 });

        const string rule = """{ "hand_count_of_rank": "top_discard", "at_least": 2 }""";

        Assert.False(Holds(rule, state));   // one nine is not two

        hand.Add(new Card(Suit.Hearts, Rank.Nine) { Uid = 5102 });
        Assert.True(Holds(rule, state));
    }

    [Fact]
    public void Hand_count_of_rank_can_name_a_rank_outright()
    {
        var (state, _) = Table();

        var hand = Hand(state);
        hand.Clear();
        hand.Add(new Card(Suit.Clubs, Rank.King) { Uid = 5200 });
        hand.Add(new Card(Suit.Hearts, Rank.King) { Uid = 5201 });

        Assert.True(Holds("""{ "hand_count_of_rank": "K", "at_least": 2 }""", state));
        Assert.False(Holds("""{ "hand_count_of_rank": "Q", "at_least": 1 }""", state));
    }

    // ── Combinators ───────────────────────────────────────────────────────────

    [Fact]
    public void All_any_and_not_compose_terms()
    {
        var (state, _) = Table();
        state.Zones["deck"].Clear();   // stock_exhausted now true, team_has_melded false

        Assert.False(Holds("""{ "all": [ "stock_exhausted", "team_has_melded" ] }""", state));
        Assert.True(Holds("""{ "any": [ "stock_exhausted", "team_has_melded" ] }""", state));
        Assert.True(Holds("""{ "not": "team_has_melded" }""", state));
        Assert.False(Holds("""{ "not": "stock_exhausted" }""", state));
    }

    [Fact]
    public void An_absent_condition_means_the_rule_always_applies()
    {
        var (state, _) = Table();

        // A draw option with no "requires" is unconditional, which is what every
        // existing definition assumes.
        Assert.True(RuleCondition.Evaluate(default, state));
    }

    /// <summary>
    /// An object naming no term asserts nothing. Reading that as "always" would make a
    /// mistyped rule silently permissive, which is the worse of the two failures.
    /// </summary>
    [Fact]
    public void An_object_naming_no_term_is_false()
    {
        var (state, _) = Table();
        Assert.False(Holds("""{ "hand_conut_of_rank": "K" }""", state));
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Well_formed_conditions_validate()
    {
        Assert.Empty(RuleCondition.Validate(Json("\"stock_exhausted\"")));
        Assert.Empty(RuleCondition.Validate(Json("""{ "hand_count_of_rank": "top_discard", "at_least": 2 }""")));
        Assert.Empty(RuleCondition.Validate(Json("""{ "all": [ "team_has_melded", "hand_empty" ] }""")));
        Assert.Empty(RuleCondition.Validate(Json("""{ "not": "stock_exhausted" }""")));
    }

    /// <summary>
    /// A typo must stop the definition loading. The engine silently ignoring a rule it
    /// does not recognise is how a game ends up playing subtly wrong forever.
    /// </summary>
    [Theory]
    [InlineData("\"stock_exhuasted\"")]
    [InlineData("""{ "hand_conut_of_rank": "K" }""")]
    [InlineData("""{ "all": "team_has_melded" }""")]
    [InlineData("42")]
    public void Malformed_conditions_are_reported(string json)
        => Assert.NotEmpty(RuleCondition.Validate(Json(json)));

    [Fact]
    public void A_bad_term_says_what_was_available()
    {
        var problems = RuleCondition.Validate(Json("\"stock_exhuasted\""));

        Assert.Contains(problems, p => p.Contains("stock_exhausted"));
    }

    [Fact]
    public void Validation_reaches_inside_combinators()
    {
        var problems = RuleCondition.Validate(
            Json("""{ "all": [ "team_has_melded", "nonsense_term" ] }"""));

        Assert.NotEmpty(problems);
    }
}
