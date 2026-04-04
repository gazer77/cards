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
///
/// Auto-advances after a delay so players can see the result.
/// </summary>
public sealed class ShowdownHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly string _evaluator;
    private readonly string _communityZone;
    private readonly int    _handSize;

    public ShowdownHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId   = nextPhaseId;
        _evaluator     = GetString(def, "evaluator")      ?? "high_hand";
        _communityZone = GetString(def, "community_zone") ?? "community";
        _handSize      = GetInt(def, "hand_size")         ?? 5;
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
            var hand  = (state.FindZone($"hand:{p.Id}") ?? state.FindZone("hand"))?.Cards ?? [];
            var all   = hand.Concat(community).ToList();
            var rank  = EvaluateBestHand(all, _handSize);

            if (winner is null || rank.CompareTo(bestRank, _evaluator) > 0)
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
    // Evaluates the best 5-card hand from a pool of cards.

    private static HandRank EvaluateBestHand(List<Card> pool, int size)
    {
        if (pool.Count <= size) return Evaluate(pool);

        HandRank best = default;
        // Try all combinations of `size` cards from the pool
        foreach (var combo in Combinations(pool, size))
        {
            var r = Evaluate(combo);
            if (r.CompareTo(best, "high_hand") > 0)
                best = r;
        }
        return best;
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

    // ── Inner type ────────────────────────────────────────────────────────────

    private readonly struct HandRank(int score, string name)
    {
        public int    Score { get; } = score;
        public string Name  { get; } = name;

        public int CompareTo(HandRank other, string evaluator)
            => evaluator == "low_hand"
                ? other.Score.CompareTo(Score)
                : Score.CompareTo(other.Score);
    }
}
