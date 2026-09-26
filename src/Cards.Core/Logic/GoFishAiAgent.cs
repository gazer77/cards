using Cards.Engine;

namespace Cards.Logic;

/// <summary>
/// How a computer seat plays Go Fish: which rank to ask for, and whom to ask.
///
/// It sees what a player at the table sees — its own hand (the view it is given hides
/// everyone else's) and what the table remembers in the open: the ranks each seat has
/// asked for (<c>gf_known:{seat}</c>) and the ranks each has said it does not hold
/// (<c>gf_denied:{seat}</c>).
///
/// Strategy, best first:
///   1. A rank it holds that some seat has been seen to hold — ask that seat.
///   2. The rank it holds most of, from the seat with the most cards that has not
///      already said no to it.
///   3. Anything it holds, from anyone — a wasted ask is still an ask.
///
/// Remembering refusals matters: without them the greedy pick is deterministic, and the
/// computer empties the deck asking the same question over and over.
/// </summary>
public sealed class GoFishAiAgent : IPlayerAgent
{
    public string PlayerId { get; }

    public GoFishAiAgent(string playerId) => PlayerId = playerId;

    public GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions)
        => validActions.Any(a => a.Type == "ai_step") ? validActions.First(a => a.Type == "ai_step")
         : validActions.Count > 0 ? validActions[0]
         : new GameAction("tap");

    /// <summary>
    /// The ask: a card of the chosen rank as <see cref="GameAction.CardId"/>, and the
    /// hand of the seat to ask as <see cref="GameAction.ZoneId"/>.
    /// </summary>
    public static GameAction Choose(GameState visibleState, string playerId)
    {
        var hand = visibleState.Zones[$"hand:{playerId}"];
        var others = visibleState.Players
            .Where(p => p.Id != playerId && p.Role is null)
            .Select(p => (p.Id, Cards: CountOf(visibleState, $"hand:{p.Id}")))
            .ToList();
        var holding = others.Where(o => o.Cards > 0).ToList();
        if (holding.Count > 0) others = holding;

        var ranks = hand.Cards
            .GroupBy(c => GoFishHandlerRank(c.Id))
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

        // 1. Seen to hold it.
        foreach (var rank in ranks)
            foreach (var (id, _) in others)
                if (Recall(visibleState, $"gf_known:{id}").Contains(rank))
                    return Ask(hand, rank, id);

        // 2. Most-held rank, from the biggest hand that has not refused it.
        foreach (var rank in ranks)
        {
            var target = others
                .Where(o => !Recall(visibleState, $"gf_denied:{o.Id}").Contains(rank))
                .OrderByDescending(o => o.Cards)
                .FirstOrDefault();
            if (target.Id is not null) return Ask(hand, rank, target.Id);
        }

        // 3. Everything has been refused by everyone.
        return Ask(hand, ranks[0], others.OrderByDescending(o => o.Cards).First().Id);
    }

    private static GameAction Ask(Zone hand, string rank, string targetId)
        => new("ask", ZoneId: $"hand:{targetId}", CardId: hand.Cards.First(c => GoFishHandlerRank(c.Id) == rank).Id);

    /// <summary>How many cards a zone holds — counted even when its cards are hidden from this seat.</summary>
    private static int CountOf(GameState state, string zoneId)
        => int.TryParse(state.Metadata.GetValueOrDefault($"zone_count:{zoneId}"), out var n)
            ? n
            : state.Zones.GetValueOrDefault(zoneId)?.Count ?? 0;

    private static HashSet<string> Recall(GameState state, string key)
    {
        var val = state.Metadata.GetValueOrDefault(key, "");
        return string.IsNullOrEmpty(val) ? [] : [.. val.Split(',')];
    }

    private static string GoFishHandlerRank(string cardId) => cardId[..^1];
}
