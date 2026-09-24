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
        "can_open_with_top_discard",
        "top_discard_is_meldable",
        "always",
        "never",
    ];

    /// <summary>Term names taking arguments, as object properties.</summary>
    private static readonly string[] ObjectTerms =
    [
        "hand_count_of_rank",
        "meld_value_at_least",
        "books_at_least",
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
        "team_has_melded" => MeldRules.HasOpened(MeldZone(state), state),

        // "Can this side open using the top of the discard?" — the other half of the
        // Hand and Foot pickup rule. A side that has not melded may still claim the
        // pile when the top card completes an opening worth enough, which is often the
        // only way a side gets open at all.
        "can_open_with_top_discard" => CanOpenWithTopDiscard(state),

        // "Could the top card be laid at all?" A rank the phase bars from melding can
        // never be used, so the pile it sits on cannot be claimed — holding two 3s in
        // Hand and Foot does not make a 3 on top pickable.
        "top_discard_is_meldable" => TopDiscardIsMeldable(state),

        "hand_count_of_rank" => HandCountOfRank(condition, state),

        "meld_value_at_least" => MeldValueAtLeast(condition, state),

        // "Does this side hold enough complete books?" — what a canasta game asks
        // before letting a player go out. `kind` narrows it to natural (no wilds) or
        // wild books; absent, any book counts.
        "books_at_least" => BooksAtLeast(condition, state),

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

        // Point value, not card count — scored the same way the game scores those cards,
        // so a definition's "worth 120" means the same number everywhere it is written.
        return ScoringEngine.CardPointValue(state.Definition, melds.Cards) >= need;
    }

    private static bool BooksAtLeast(JsonElement condition, GameState state)
    {
        var melds = MeldZone(state);
        if (melds is null) return false;

        int need = condition.GetProperty("books_at_least").GetInt32();
        string kind = condition.TryGetProperty("kind", out var k) ? k.GetString() ?? "any" : "any";

        return CountBooks(melds, state, kind) >= need;
    }

    /// <summary>
    /// Complete books in a meld zone, by kind. A book is a group of at least
    /// <c>scoring.book_size</c> cards; natural means no wild in it. Reads the same
    /// book size scoring pays bonuses by, so "may I go out" and "what am I paid"
    /// agree on what a book is.
    /// </summary>
    public static int CountBooks(Zone melds, GameState state, string kind = "any")
    {
        int bookSize   = ScoringEngine.BookSize(state.Definition);
        var wilds      = MeldRules.WildRanks(state.Definition);
        var unmeldable = MeldRules.UnmeldableRanks(state);
        int count      = 0;

        for (int i = 0; i < melds.Groups.Count; i++)
        {
            var group = melds.GroupCards(i);
            if (group.Count < bookSize) continue;
            if (!MeldRules.IsMeldGroup(group, wilds, unmeldable)) continue;   // filed cards, not a meld

            bool hasWild = group.Any(c => MeldRules.IsWild(c, wilds));
            bool counts = kind switch
            {
                "natural" => !hasWild,
                "wild"    => hasWild,
                _         => true,
            };
            if (counts) count++;
        }

        return count;
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

                    if (ObjectTerms.Contains(property.Name))
                    {
                        // A kind the counter does not know would silently count nothing.
                        if (property.Name == "books_at_least"
                            && condition.TryGetProperty("kind", out var kind)
                            && kind.GetString() is not ("natural" or "wild" or "any"))
                            problems.Add($"books_at_least: kind '{kind}' is not natural, wild, or any.");
                        return;
                    }
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

    /// <summary>
    /// Whether the discard's top card is one the current phase allows melding.
    ///
    /// Read from the phase definition rather than from state: which ranks may be melded
    /// is fixed configuration, so there is one place it is written and no chance of the
    /// draw rule and the meld rule disagreeing about 3s.
    /// </summary>
    private static bool TopDiscardIsMeldable(GameState state)
    {
        var top = state.FindZone("discard")?.TopCard;
        if (top is null) return false;

        var phase = state.Definition?.Phases
            .FirstOrDefault(p => p.Id == state.CurrentPhaseId);

        // A wild is never a meld by itself — a meld needs a natural for the wild to
        // stand in for — so a pile topped by one cannot be claimed "to meld that card".
        // Hand and Foot says the same in its own words: a wild on top freezes the pile.
        if (MeldRules.IsWild(top, MeldRules.WildRanks(state.Definition))) return false;

        if (phase?.Extra?.TryGetValue("unmeldable_ranks", out var barred) != true
            || barred.ValueKind != JsonValueKind.Array)
            return true;   // nothing else is barred

        foreach (var entry in barred.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String
                && MeldRules.ParseRank(entry.GetString() ?? "") == top.Rank)
                return false;

        return true;
    }

    /// <summary>
    /// Whether the player could lay an opening meld, worth what this round demands,
    /// that uses the top card of the discard.
    ///
    /// The requirement is published into metadata by the phase that owns it, since a
    /// condition sees only the state. No requirement, or a side already open, means
    /// there is nothing to open with and the answer is no — the caller asks this as the
    /// alternative to <c>team_has_melded</c>, so saying yes there would let an open side
    /// claim the pile on the strength of a rule that no longer applies to it.
    /// </summary>
    private static bool CanOpenWithTopDiscard(GameState state)
    {
        if (MeldRules.HasOpened(MeldZone(state), state)) return false;   // already open

        int required = int.TryParse(
            state.Metadata.GetValueOrDefault("dd_opening_requirement"), out int r) ? r : 0;
        if (required <= 0) return false;

        var hand = CurrentHand(state);
        var top  = state.FindZone("discard")?.TopCard;
        if (hand is null || top is null) return false;

        // The meld the top card would join must itself be legal, so the hand has to
        // hold two of its rank — the same pair the pickup rule asks for.
        var wilds = MeldRules.WildRanks(state.Definition);
        if (MeldRules.IsWild(top, wilds)) return false;

        var matching = hand.Cards.Where(c => !MeldRules.IsWild(c, wilds) && c.Rank == top.Rank).ToList();
        if (matching.Count < 2) return false;

        // Everything the hand could lay alongside it counts toward the opening, since
        // the opening is measured across the whole lay.
        var layable = new List<Card>(matching) { top };
        foreach (var group in hand.Cards.Where(c => !MeldRules.IsWild(c, wilds) && c.Rank != top.Rank)
                                        .GroupBy(c => c.Rank))
            if (group.Count() >= 3)
                layable.AddRange(group);

        return ScoringEngine.CardPointValue(state.Definition, layable) >= required;
    }

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
