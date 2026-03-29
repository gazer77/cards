namespace Cards.Engine;

/// <summary>
/// Standard win-condition implementations.  Dispatches by <c>win_condition.type</c>
/// from the game definition.
///
/// Supported types:
///   last_with_cards  — ends when any player has zero cards across all owned zones;
///                      winner has the most total cards.
///   most_books       — ends when deck + all hands are empty; winner has the highest score.
///   fixed_rounds     — ends after <c>win_condition.count</c> rounds; winner has highest score.
/// </summary>
public sealed class WinConditionEngine : IWinCondition
{
    public static readonly WinConditionEngine Instance = new();

    // ── IWinCondition ─────────────────────────────────────────────────────────

    public WinResult? Check(GameState state)
        => state.Definition.WinCondition?.Type switch
        {
            "last_with_cards" => CheckLastWithCards(state),
            "most_books"      => CheckMostBooks(state),
            "fixed_rounds"    => CheckFixedRounds(state),
            _                 => null,
        };

    public WinResult Resolve(GameState state)
        => state.Definition.WinCondition?.Type switch
        {
            "last_with_cards" => ResolveLastWithCards(state),
            "most_books"      => ResolveMostBooks(state),
            "fixed_rounds"    or
            _                 => ResolveByScore(state),
        };

    // ── last_with_cards ───────────────────────────────────────────────────────

    private static WinResult? CheckLastWithCards(GameState state)
    {
        bool anyEmpty = state.Players.Any(p => OwnedCardCount(state, p) == 0);
        return anyEmpty ? ResolveLastWithCards(state) : null;
    }

    private static WinResult ResolveLastWithCards(GameState state)
    {
        var ranked = state.Players
            .Select(p => (Player: p, Count: OwnedCardCount(state, p)))
            .OrderByDescending(x => x.Count)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Count == ranked[1].Count)
            return new WinResult(null, "It's a draw!");

        var winner = ranked[0].Player;
        string msg = winner == state.Players[0]
            ? "You win the game!"
            : $"{winner.Name} wins the game!";
        return new WinResult(winner.Id, msg);
    }

    /// <summary>Sum of all cards the player owns across every non-deck zone.</summary>
    private static int OwnedCardCount(GameState state, Player p)
        => state.Zones.Values.Where(z => z.OwnerId == p.Id).Sum(z => z.Count);

    // ── most_books ────────────────────────────────────────────────────────────

    private static WinResult? CheckMostBooks(GameState state)
    {
        if (state.Zones.TryGetValue("deck", out var deck) && deck.Count > 0) return null;

        bool allHandsEmpty = state.Players.All(p =>
            !state.Zones.TryGetValue($"hand:{p.Id}", out var h) || h.Count == 0);

        return allHandsEmpty ? ResolveMostBooks(state) : null;
    }

    private static WinResult ResolveMostBooks(GameState state)
    {
        var ranked = state.Players
            .Select(p => (Player: p, Score: state.GetScore(p.Id)))
            .OrderByDescending(x => x.Score)
            .ToList();

        // Sub-message: "Your books: 3 | AI books: 2"
        string sub = string.Join(" | ", ranked.Select(x =>
            x.Player == state.Players[0]
                ? $"Your books: {x.Score}"
                : $"{x.Player.Name} books: {x.Score}"));

        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            return new WinResult(null, "It's a tie!", sub);

        var winner = ranked[0].Player;
        string msg = winner == state.Players[0] ? "You win!" : $"{winner.Name} wins!";
        return new WinResult(winner.Id, msg, sub);
    }

    // ── fixed_rounds ──────────────────────────────────────────────────────────

    private static WinResult? CheckFixedRounds(GameState state)
    {
        int rounds = state.Definition.WinCondition?.Count ?? 1;
        return state.RoundNumber >= rounds ? ResolveByScore(state) : null;
    }

    private static WinResult ResolveByScore(GameState state)
    {
        var ranked = state.Players
            .Select(p => (Player: p, Score: state.GetScore(p.Id)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            return new WinResult(null, "It's a tie!");

        var winner = ranked[0].Player;
        string msg = winner == state.Players[0] ? "You win!" : $"{winner.Name} wins!";
        return new WinResult(winner.Id, msg);
    }
}
