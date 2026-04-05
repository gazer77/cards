using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for poker showdowns.  Reveals all remaining hands,
/// evaluates them with the hand-rank evaluator, awards the pot to the winner,
/// then advances to the next phase.
///
/// Phase definition parameters:
///   evaluator      — "high_hand" (default) | "low_hand" | "high_low"
///   community_zone — zone id to include in hand evaluation (default "community")
///   hand_size      — best N cards from hand + community (default 5)
///   use_from_hand  — { "min": N, "max": M } restrict how many hand cards must be used (Omaha)
///
/// Wild cards: reads scoring.wilds from the game definition. Each entry is
///   {"rank": "2"} — all cards of that rank are wild, or
///   {"card": "Jh"} — a specific card is wild.
/// Wild cards are substituted with the best possible rank+suit during evaluation.
///
/// Auto-advances after a delay so players can see the result.
/// </summary>
public sealed class ShowdownHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly string _evaluator;
    private readonly string _communityZone;
    private readonly int    _handSize;
    private readonly int    _useFromHandMin;
    private readonly int    _useFromHandMax;

    public ShowdownHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId    = nextPhaseId;
        _evaluator      = GetString(def, "evaluator")      ?? "high_hand";
        _communityZone  = GetString(def, "community_zone") ?? "community";
        _handSize       = GetInt(def, "hand_size")         ?? 5;
        _useFromHandMin = GetNestedInt(def, "use_from_hand", "min") ?? 0;
        _useFromHandMax = GetNestedInt(def, "use_from_hand", "max") ?? int.MaxValue;
    }

    public TimeSpan? GetAutoAdvanceDelay(GameState _) => TimeSpan.FromMilliseconds(3500);
    public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];

    public void Apply(GameState state, GameAction action)
    {
        // Reveal all hands
        foreach (var p in state.Players)
        {
            var hand = state.FindZone($"hand:{p.Id}") ?? state.FindZone("hand");
            hand?.Cards.ForEach(c => c.IsFaceUp = true);
        }

        // Build wild-card predicate from scoring.wilds definition.
        var isWild = BuildWildPredicate(state);

        // Allow scoring.evaluator (set by house rules like Razz) to override the phase-level evaluator.
        string effectiveEvaluator = _evaluator;
        if (state.Definition.Scoring?.Extra?.TryGetValue("evaluator", out var evalEl) == true
            && evalEl.ValueKind == JsonValueKind.String
            && evalEl.GetString() is { } overrideEval)
            effectiveEvaluator = overrideEval;

        // Evaluate and find winner
        var community = state.FindZone(_communityZone)?.Cards ?? [];
        var active    = state.Players
            .Where(p => state.Metadata.GetValueOrDefault($"bet_folded:{p.Id}") != "true")
            .ToList();

        if (active.Count == 0) active = state.Players.ToList();

        Player? winner = null;
        HandRank bestRank = default;

        foreach (var p in active)
        {
            var handCards = (state.FindZone($"hand:{p.Id}") ?? state.FindZone("hand"))?.Cards ?? [];
            var commCards = community.ToList();
            HandRank rank;

            if (_useFromHandMax < int.MaxValue || _useFromHandMin > 0)
                rank = EvaluateBestHandConstrained(handCards.ToList(), commCards, _handSize,
                                                   _useFromHandMin, _useFromHandMax, isWild, effectiveEvaluator);
            else
                rank = EvaluateBestHand(handCards.Concat(commCards).ToList(), _handSize, isWild, effectiveEvaluator);

            if (winner is null || rank.CompareTo(bestRank, effectiveEvaluator) > 0)
            {
                winner   = p;
                bestRank = rank;
            }
        }

        // Award pot
        int pot = int.TryParse(state.Metadata.GetValueOrDefault("pot", "0"), out int p2) ? p2 : 0;
        if (winner is not null && pot > 0)
        {
            state.AddScore(winner.Id, pot);
            state.Metadata["pot"] = "0";
        }

        string winMsg = winner is null ? "No winner."
            : winner == state.Players[0] ? $"You win! ({bestRank.Name})"
            : $"{winner.Name} wins! ({bestRank.Name})";


        state.Metadata["status"]      = winMsg;
        state.Metadata["last_winner"] = winner?.Id ?? "";

        // Clear folded status
        foreach (var p in state.Players)
            state.Metadata.Remove($"bet_folded:{p.Id}");

        state.CurrentPhaseId = _nextPhaseId;
    }

    // ── Hand rank evaluation ──────────────────────────────────────────────────

    private static HandRank EvaluateBestHand(List<Card> pool, int size, Func<Card, bool> isWild,
        string evaluator = "high_hand")
    {
        var wilds    = pool.Where(isWild).ToList();
        var naturals = pool.Where(c => !isWild(c)).ToList();

        if (wilds.Count == 0)
        {
            if (pool.Count <= size) return EvaluateForMode(pool, evaluator);
            HandRank best = default;
            foreach (var combo in Combinations(pool, size))
            {
                var r = EvaluateForMode(combo, evaluator);
                if (r.CompareTo(best, evaluator) > 0) best = r;
            }
            return best;
        }

        HandRank bestWild = default;
        SubstituteWilds(naturals, wilds.Count, size, [], evaluator, ref bestWild);
        return bestWild;
    }

    /// <summary>Evaluate best hand with use_from_hand constraints (e.g., Omaha: exactly 2 from hand).</summary>
    private static HandRank EvaluateBestHandConstrained(
        List<Card> hand, List<Card> community, int size,
        int minFromHand, int maxFromHand, Func<Card, bool> isWild, string evaluator = "high_hand")
    {
        HandRank best = default;
        int maxH = Math.Min(maxFromHand, Math.Min(hand.Count, size));
        int minH = Math.Max(minFromHand, size - community.Count);

        for (int fromHand = minH; fromHand <= maxH; fromHand++)
        {
            int fromComm = size - fromHand;
            if (fromComm < 0 || fromComm > community.Count) continue;

            foreach (var hCombo in Combinations(hand, fromHand))
            foreach (var cCombo in Combinations(community, fromComm))
            {
                var pool  = hCombo.Concat(cCombo).ToList();
                var wilds = pool.Where(isWild).ToList();
                var nats  = pool.Where(c => !isWild(c)).ToList();
                HandRank r;
                if (wilds.Count == 0)
                    r = EvaluateForMode(pool, evaluator);
                else
                { r = default; SubstituteWilds(nats, wilds.Count, pool.Count, [], evaluator, ref r); }
                if (r.CompareTo(best, evaluator) > 0) best = r;
            }
        }
        return best;
    }

    private static HandRank EvaluateForMode(IEnumerable<Card> cards, string evaluator)
        => evaluator == "ace_to_five_low" ? EvaluateAceToFive(cards) : Evaluate(cards);

    /// <summary>
    /// Ace-to-five low evaluation (Razz): Aces count as 1, straights and flushes
    /// are ignored. Unpaired low hands win; higher score = worse hand.
    /// Best hand: A-2-3-4-5.
    /// </summary>
    private static HandRank EvaluateAceToFive(IEnumerable<Card> cards)
    {
        // Ace = 1, all others at face value.
        var ranks = cards.Select(c => c.Rank == Rank.Ace ? 1 : (int)c.Rank)
                         .OrderByDescending(r => r)
                         .ToList();
        if (ranks.Count == 0) return new HandRank(0, "No cards");

        var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count())
                          .ThenByDescending(g => g.Key).ToList();
        int[] gc = groups.Select(g => g.Count()).ToArray();

        // Category: 0=unpaired(best for low), escalating penalty for pairs etc.
        int cat = gc[0] switch
        {
            4 => 5, // quads
            3 when gc.Length > 1 && gc[1] >= 2 => 4, // full house
            3 => 3, // trips
            2 when gc.Length > 1 && gc[1] == 2 => 2, // two pair
            2 => 1, // pair
            _ => 0, // unpaired
        };

        // Within category: higher card sum = worse hand. Build rank score descending.
        int score = 0;
        for (int i = 0; i < Math.Min(ranks.Count, 5); i++)
            score = score * 15 + ranks[i];

        string name = cat == 0
            ? $"{A5Name(ranks[0])}-{A5Name(ranks[Math.Min(1, ranks.Count - 1)])} Low"
            : cat switch { 1 => "One Pair", 2 => "Two Pair", 3 => "Three of a Kind",
                           4 => "Full House", _ => "Four of a Kind" };

        return new HandRank(cat * 1_000_000 + score, name);
    }

    private static string A5Name(int r) => r switch
        { 1 => "A", 10 => "T", 11 => "J", 12 => "Q", 13 => "K", _ => r.ToString() };

    private static void SubstituteWilds(
        List<Card> naturals, int remaining, int size,
        List<Card> soFar, string evaluator, ref HandRank best)
    {
        if (remaining == 0)
        {
            var pool = naturals.Concat(soFar).ToList();
            HandRank r;
            if (pool.Count <= size)
                r = EvaluateForMode(pool, evaluator);
            else
            {
                r = default;
                foreach (var combo in Combinations(pool, size))
                {
                    var cr = EvaluateForMode(combo, evaluator);
                    if (cr.CompareTo(r, evaluator) > 0) r = cr;
                }
            }
            if (r.CompareTo(best, evaluator) > 0) best = r;
            return;
        }

        foreach (Rank rank in Enum.GetValues<Rank>())
        foreach (Suit suit in Enum.GetValues<Suit>())
        {
            soFar.Add(new Card(suit, rank, true));
            SubstituteWilds(naturals, remaining - 1, size, soFar, evaluator, ref best);
            soFar.RemoveAt(soFar.Count - 1);
        }
    }

    private static HandRank Evaluate(IEnumerable<Card> cards)
    {
        var c = cards.OrderByDescending(x => (int)x.Rank).ToList();
        if (c.Count == 0) return new HandRank(0, "No cards");

        var suits    = c.GroupBy(x => x.Suit);
        var ranks    = c.GroupBy(x => x.Rank).OrderByDescending(g => g.Count()).ThenByDescending(g => (int)g.Key).ToList();
        bool flush   = suits.Any(g => g.Count() >= 5);
        bool straight = IsStraight(c.Select(x => (int)x.Rank).Distinct().OrderDescending().ToList());

        int topRank = (int)c[0].Rank;
        var groups  = ranks.Select(g => g.Count()).ToList();

        // Rank category (higher = better hand)
        (int cat, string name) = (groups, flush, straight) switch
        {
            _ when flush && straight && topRank == (int)Rank.Ace => (9, "Royal Flush"),
            _ when flush && straight                              => (8, "Straight Flush"),
            _ when groups[0] == 4                                 => (7, "Four of a Kind"),
            _ when groups[0] == 3 && groups.Count > 1 && groups[1] >= 2 => (6, "Full House"),
            _ when flush                                          => (5, "Flush"),
            _ when straight                                       => (4, "Straight"),
            _ when groups[0] == 3                                 => (3, "Three of a Kind"),
            _ when groups[0] == 2 && groups.Count > 1 && groups[1] == 2 => (2, "Two Pair"),
            _ when groups[0] == 2                                 => (1, "One Pair"),
            _                                                     => (0, "High Card"),
        };

        return new HandRank(cat * 100 + topRank, name);
    }

    private static bool IsStraight(List<int> distinctRanks)
    {
        if (distinctRanks.Count < 5) return false;
        for (int i = 0; i <= distinctRanks.Count - 5; i++)
            if (distinctRanks[i] - distinctRanks[i + 4] == 4)
                return true;
        // Wheel: A-2-3-4-5
        if (distinctRanks.Contains((int)Rank.Ace) &&
            distinctRanks.Contains(2) && distinctRanks.Contains(3) &&
            distinctRanks.Contains(4) && distinctRanks.Contains(5))
            return true;
        return false;
    }

    private static IEnumerable<List<Card>> Combinations(List<Card> pool, int k)
    {
        if (k == 0) { yield return []; yield break; }
        for (int i = 0; i <= pool.Count - k; i++)
        {
            var first = pool[i];
            foreach (var rest in Combinations(pool.Skip(i + 1).ToList(), k - 1))
            {
                rest.Insert(0, first);
                yield return rest;
            }
        }
    }

    // ── Wild card helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a predicate that returns true for cards declared wild in scoring.wilds.
    /// Each entry is {"rank":"2"} (rank-wild) or {"card":"Jh"} (specific-card wild).
    /// </summary>
    private static Func<Card, bool> BuildWildPredicate(GameState state)
    {
        var wildsDef = state.Definition.Scoring?.Extra;
        if (wildsDef is null || !wildsDef.TryGetValue("wilds", out var el)
            || el.ValueKind != JsonValueKind.Array)
            return _ => false;

        var wildRanks = new HashSet<Rank>();
        var wildCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in el.EnumerateArray())
        {
            if (item.TryGetProperty("rank", out var rv) && rv.GetString() is { } rankStr)
            {
                if (TryParseRank(rankStr, out var rank)) wildRanks.Add(rank);
            }
            else if (item.TryGetProperty("card", out var cv) && cv.GetString() is { } cardStr)
            {
                wildCards.Add(cardStr);
            }
        }

        if (wildRanks.Count == 0 && wildCards.Count == 0) return _ => false;

        return card =>
        {
            if (wildRanks.Contains(card.Rank)) return true;
            if (wildCards.Count > 0)
            {
                string id = $"{card.Rank switch {
                    Rank.Ace   => "A", Rank.King => "K", Rank.Queen => "Q",
                    Rank.Jack  => "J", Rank.Ten  => "T",
                    _          => ((int)card.Rank).ToString()
                }}{card.Suit switch {
                    Suit.Spades => "s", Suit.Hearts => "h",
                    Suit.Diamonds => "d", _ => "c"
                }}";
                if (wildCards.Contains(id)) return true;
            }
            return false;
        };
    }

    private static bool TryParseRank(string s, out Rank rank)
    {
        switch (s.ToUpperInvariant())
        {
            case "A": rank = Rank.Ace;   return true;
            case "K": rank = Rank.King;  return true;
            case "Q": rank = Rank.Queen; return true;
            case "J": rank = Rank.Jack;  return true;
            case "T": case "10": rank = Rank.Ten; return true;
            default:
                if (int.TryParse(s, out int n) && Enum.IsDefined(typeof(Rank), n))
                { rank = (Rank)n; return true; }
                rank = default; return false;
        }
    }

    // ── JSON parsing ──────────────────────────────────────────────────────────

    private static string? GetString(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static int? GetInt(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }

    private static int? GetNestedInt(PhaseDefinition def, string outerKey, string innerKey)
    {
        if (def.Extra?.TryGetValue(outerKey, out var outer) == true
            && outer.ValueKind == JsonValueKind.Object
            && outer.TryGetProperty(innerKey, out var inner)
            && inner.ValueKind == JsonValueKind.Number)
            return inner.GetInt32();
        return null;
    }

    // ── Inner type ────────────────────────────────────────────────────────────

    private readonly struct HandRank(int score, string name)
    {
        public int    Score { get; } = score;
        public string Name  { get; } = name;

        public int CompareTo(HandRank other, string evaluator)
            => evaluator is "low_hand" or "ace_to_five_low"
                ? other.Score.CompareTo(Score)
                : Score.CompareTo(other.Score);
    }
}
