using System.Text.Json;

namespace Cards.Engine;

/// <summary>
/// Questions a game definition can ask about the position.
///
/// Written as JSON objects rather than a string syntax, so there is no second parser to
/// maintain and every term can be checked when a definition loads. A bare string is a
/// term with no arguments:
///
/// <code>
/// "stock_exhausted"
///
/// { "all": [
///     "team_has_melded",
///     { "hand_count_of_rank": "top_discard", "at_least": 2 } ] }
/// </code>
///
/// Terms are deliberately few, and each one exists because a real game needed it —
/// the alternative is a vocabulary rich enough to be complex and too poor to be
/// complete, which is worse than either.
/// </summary>
public static class RuleCondition
{
    /// <summary>Term names taking no arguments.</summary>
    private static readonly string[] SimpleTerms =
    [
        "stock_exhausted",
        "team_has_melded",
        "hand_empty",
        "always",
        "never",
    ];

    /// <summary>Term names taking arguments, as object properties.</summary>
    private static readonly string[] ObjectTerms =
    [
        "hand_count_of_rank",
        "meld_value_at_least",
    ];

    private static readonly string[] Combinators = ["all", "any", "not"];

    // ── Evaluation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the condition holds for the player currently to act. An absent or
    /// undefined condition is true: a rule with no condition applies always.
    /// </summary>
    public static bool Evaluate(JsonElement condition, GameState state)
    {
        switch (condition.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return true;

            case JsonValueKind.True or JsonValueKind.False:
                return condition.GetBoolean();

            case JsonValueKind.String:
                return EvaluateTerm(condition.GetString() ?? "", condition, state);

            case JsonValueKind.Object:
                return EvaluateObject(condition, state);

            default:
                return false;
        }
    }

    private static bool EvaluateObject(JsonElement condition, GameState state)
    {
        if (condition.TryGetProperty("all", out var all))
            return all.EnumerateArray().All(c => Evaluate(c, state));

        if (condition.TryGetProperty("any", out var any))
            return any.EnumerateArray().Any(c => Evaluate(c, state));

        if (condition.TryGetProperty("not", out var not))
            return !Evaluate(not, state);

        foreach (var term in ObjectTerms)
            if (condition.TryGetProperty(term, out _))
                return EvaluateTerm(term, condition, state);

        // An object naming no term asserts nothing, which is safer read as false than as
        // an accidental "always".
        return false;
    }

    private static bool EvaluateTerm(string term, JsonElement condition, GameState state) => term switch
    {
        "always" => true,
        "never"  => false,

        "stock_exhausted" => state.FindZone("deck") is null or { IsEmpty: true },

        "hand_empty" => CurrentHand(state) is null or { IsEmpty: true },

        // "Has this side put anything down yet" — the gate on picking up the discard
        // pile in Hand and Foot, and on laying off in most rummy games.
        "team_has_melded" => MeldZone(state) is { Count: > 0 },

        "hand_count_of_rank" => HandCountOfRank(condition, state),

        "meld_value_at_least" => MeldValueAtLeast(condition, state),

        _ => false,
    };

    /// <summary>
    /// How many cards of a named rank the player holds. The rank may be given literally
    /// ("K") or as <c>"top_discard"</c>, which is the rank a player must match to claim
    /// the pile.
    /// </summary>
    private static bool HandCountOfRank(JsonElement condition, GameState state)
    {
        var hand = CurrentHand(state);
        if (hand is null) return false;

        string spec = condition.GetProperty("hand_count_of_rank").GetString() ?? "";

        Rank? rank = spec.Equals("top_discard", StringComparison.OrdinalIgnoreCase)
            ? state.FindZone("discard")?.TopCard?.Rank
            : ParseRank(spec);

        if (rank is null) return false;

        int held = hand.Cards.Count(c => c.Rank == rank);
        int need = condition.TryGetProperty("at_least", out var n) ? n.GetInt32() : 1;

        return held >= need;
    }

    private static bool MeldValueAtLeast(JsonElement condition, GameState state)
    {
        var melds = MeldZone(state);
        if (melds is null) return false;

        int need = condition.GetProperty("meld_value_at_least").GetInt32();
        return melds.Count >= need;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Problems with a condition, as readable lines. Empty means it is well formed.
    ///
    /// Checked when a definition loads so an unknown term stops the game appearing. A
    /// rule the engine silently ignores is worse than one that fails: the game plays,
    /// and plays wrong.
    /// </summary>
    public static IReadOnlyList<string> Validate(JsonElement condition)
    {
        var problems = new List<string>();
        Walk(condition, problems);
        return problems;
    }

    private static void Walk(JsonElement condition, List<string> problems)
    {
        switch (condition.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
            case JsonValueKind.True or JsonValueKind.False:
                return;

            case JsonValueKind.String:
            {
                string term = condition.GetString() ?? "";
                if (!SimpleTerms.Contains(term))
                    problems.Add($"'{term}' is not a condition. Known: {Vocabulary()}.");
                return;
            }

            case JsonValueKind.Object:
            {
                foreach (var property in condition.EnumerateObject())
                {
                    if (Combinators.Contains(property.Name))
                    {
                        if (property.Name == "not") Walk(property.Value, problems);
                        else if (property.Value.ValueKind == JsonValueKind.Array)
                            foreach (var child in property.Value.EnumerateArray()) Walk(child, problems);
                        else
                            problems.Add($"'{property.Name}' takes a list of conditions.");
                        return;
                    }

                    if (ObjectTerms.Contains(property.Name)) return;
                }

                problems.Add($"No condition named here. Known: {Vocabulary()}.");
                return;
            }

            default:
                problems.Add($"A condition cannot be {condition.ValueKind}.");
                return;
        }
    }

    private static string Vocabulary()
        => string.Join(", ", SimpleTerms.Concat(ObjectTerms).Concat(Combinators).Order(StringComparer.Ordinal));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Zone? CurrentHand(GameState state)
        => state.Players.Count == 0
            ? null
            : state.FindZone($"hand:{state.CurrentPlayer.Id}") ?? state.FindZone("hand");

    /// <summary>
    /// The meld area belonging to the player to act — their team's where the game has
    /// teams, otherwise their own.
    /// </summary>
    private static Zone? MeldZone(GameState state)
    {
        if (state.Players.Count == 0) return null;

        var playerId = state.CurrentPlayer.Id;
        var team     = state.GetPlayerTeam(playerId);

        return (team is not null ? state.FindZone($"meld:{team.Id}") : null)
            ?? state.FindZone($"meld:{playerId}")
            ?? state.FindZone("meld");
    }

    private static Rank? ParseRank(string token) => token.Trim().ToUpperInvariant() switch
    {
        "A" or "ACE"   => Rank.Ace,
        "K" or "KING"  => Rank.King,
        "Q" or "QUEEN" => Rank.Queen,
        "J" or "JACK"  => Rank.Jack,
        "T" or "10"    => Rank.Ten,
        "JOKER"        => Rank.Joker,
        var n when int.TryParse(n, out int v) && v is >= 2 and <= 10 => (Rank)v,
        _ => null,
    };
}
