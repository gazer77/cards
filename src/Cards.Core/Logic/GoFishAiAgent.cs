using Cards.Engine;

namespace Cards.Logic;

/// <summary>
/// AI strategy for the Go Fish AI player.
/// Receives a masked <see cref="GameState"/> in which the opponent's hand is
/// empty, so the agent can only act on information it legitimately holds:
///
///   • Its own hand (visible — zone owner matches viewer).
///   • <c>known_p0_ranks</c> metadata — ranks the AI has observed the player
///     requesting or holding during the game.
///   • Scores, books zones, and pile counts via <c>zone_count:{id}</c>.
///
/// Strategy:
///   1. Prefer ranks held ≥1 of AND listed in known_p0_ranks (smart pick).
///   2. Fallback: rank held in the greatest quantity (greedy pick).
/// </summary>
public sealed class GoFishAiAgent : IPlayerAgent
{
    public string PlayerId { get; }

    public GoFishAiAgent(string playerId) => PlayerId = playerId;

    public GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions)
    {
        var hand = visibleState.Zones[$"hand:{PlayerId}"];
        if (hand.Count == 0) return new GameAction("ask_rank");

        var rankGroups = hand.Cards
            .GroupBy(c => c.Rank)
            .OrderByDescending(g => g.Count())
            .ToList();

        var knownOpponentRanks = ParseKnownRanks(
            visibleState.Metadata.GetValueOrDefault("known_p0_ranks", ""));

        // Smart pick: rank we hold AND know opponent has
        var chosen = rankGroups.FirstOrDefault(g => knownOpponentRanks.Contains(g.Key))
                     ?? rankGroups[0];

        // Return any card of that rank as the ask target (CardId carries rank info)
        var card = hand.Cards.First(c => c.Rank == chosen.Key);
        return new GameAction("ask_rank", CardId: card.Id);
    }

    private static HashSet<Rank> ParseKnownRanks(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return [];
        return [.. csv.Split(',').Select(ParseCode)];
    }

    private static Rank ParseCode(string code) => code switch
    {
        "A" => Rank.Ace,
        "J" => Rank.Jack,
        "Q" => Rank.Queen,
        "K" => Rank.King,
        _   => (Rank)int.Parse(code),
    };
}
