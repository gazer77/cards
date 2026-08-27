namespace Cards.Engine;

/// <summary>
/// Standard win-condition implementations.  Dispatches by <c>win_condition.type</c>
/// from the game definition.
///
/// Supported types:
///   last_with_cards  — ends when any player has zero cards across all owned zones;
///                      winner has the most total cards.
///   most_books       — ends when deck + all hands are empty; winner has the highest score.
///   fixed_rounds     — ends after <c>win_condition.count</c> rounds; winner determined by
///                      win_condition.winner ("lowest_score" or default highest).
///   lowest_score     — ends when any player's score reaches win_condition.threshold;
///                      winner is the player with the lowest score.
///   highest_score    — ends when any player's score reaches win_condition.threshold;
///                      winner is the player with the highest score.
///   target_score     — ends when any player's score reaches win_condition.score;
///                      that player wins (first to reach it).
///   last_with_chips  — ends when only one player has a score > 0;
///                      that player wins.
///   manual           — never triggers automatically; game logic sets game_over directly.
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
            "lowest_score"    => CheckLowestScore(state),
            "highest_score"   => CheckHighestScore(state),
            "target_score"    => CheckTargetScore(state),
            "last_with_chips" => CheckLastWithChips(state),
            "manual"          => null,
            _                 => null,
        };

    public WinResult Resolve(GameState state)
        => state.Definition.WinCondition?.Type switch
        {
            "last_with_cards" => ResolveLastWithCards(state),
            "most_books"      => ResolveMostBooks(state),
            "lowest_score"    => state.Teams.Count > 0 ? ResolveLowestTeamScore(state) : ResolveLowestScore(state),
            "highest_score"   or
            "target_score"    or
            "last_with_chips" => ResolveByScore(state),
            "fixed_rounds"    => ResolveFixed(state),
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
        return state.RoundNumber >= rounds ? ResolveFixed(state) : null;
    }

    private static WinResult ResolveFixed(GameState state)
    {
        // winner field can be "lowest_score" to invert; default is highest.
        bool lowestWins = string.Equals(
            state.Definition.WinCondition?.Winner, "lowest_score",
            StringComparison.OrdinalIgnoreCase);
        return lowestWins ? ResolveLowestScore(state) : ResolveByScore(state);
    }

    // ── lowest_score ──────────────────────────────────────────────────────────

    private static WinResult? CheckLowestScore(GameState state)
    {
        int threshold = state.Definition.WinCondition?.Threshold ?? int.MaxValue;
        if (state.Teams.Count > 0)
        {
            bool triggered = state.Teams.Any(t => state.GetTeamScore(t.Id) >= threshold);
            return triggered ? ResolveLowestTeamScore(state) : null;
        }
        bool triggeredP = state.Players.Any(p => state.GetScore(p.Id) >= threshold);
        return triggeredP ? ResolveLowestScore(state) : null;
    }

    private static WinResult ResolveLowestScore(GameState state)
    {
        var ranked = state.Players
            .Select(p => (Player: p, Score: state.GetScore(p.Id)))
            .OrderBy(x => x.Score)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            return new WinResult(null, "It's a tie!");

        var winner = ranked[0].Player;
        string msg = winner == state.Players[0] ? "You win!" : $"{winner.Name} wins!";
        return new WinResult(winner.Id, msg);
    }

    private static WinResult ResolveLowestTeamScore(GameState state)
    {
        var ranked = state.Teams
            .Select(t => (Team: t, Score: state.GetTeamScore(t.Id)))
            .OrderBy(x => x.Score)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            return new WinResult(null, "It's a tie!");

        var winner = ranked[0].Team;
        string msg = IsHumanTeam(state, winner)
            ? $"Your team wins! ({winner.Name})"
            : $"{winner.Name} wins!";
        return new WinResult(winner.Id, msg);
    }

    // ── highest_score ─────────────────────────────────────────────────────────

    private static WinResult? CheckHighestScore(GameState state)
    {
        int threshold = state.Definition.WinCondition?.Threshold ?? int.MaxValue;
        if (state.Teams.Count > 0)
        {
            bool triggered = state.Teams.Any(t => state.GetTeamScore(t.Id) >= threshold);
            return triggered ? ResolveByTeamScore(state) : null;
        }
        bool triggeredP = state.Players.Any(p => state.GetScore(p.Id) >= threshold);
        return triggeredP ? ResolveByScore(state) : null;
    }

    // ── target_score ──────────────────────────────────────────────────────────

    private static WinResult? CheckTargetScore(GameState state)
    {
        int target = state.Definition.WinCondition?.Score ?? int.MaxValue;

        if (state.Teams.Count > 0)
        {
            var winner = state.Teams.FirstOrDefault(t => state.GetTeamScore(t.Id) >= target);
            if (winner is null) return null;
            string msg = IsHumanTeam(state, winner)
                ? $"Your team wins! ({winner.Name})"
                : $"{winner.Name} wins!";
            return new WinResult(winner.Id, msg);
        }

        var winnerP = state.Players.FirstOrDefault(p => state.GetScore(p.Id) >= target);
        if (winnerP is null) return null;
        string msgP = winnerP == state.Players[0] ? "You win!" : $"{winnerP.Name} wins!";
        return new WinResult(winnerP.Id, msgP);
    }

    // ── last_with_chips ───────────────────────────────────────────────────────

    private static WinResult? CheckLastWithChips(GameState state)
    {
        var withChips = state.Players.Where(p => state.GetScore(p.Id) > 0).ToList();
        if (withChips.Count != 1) return null;

        var winner = withChips[0];
        string msg = winner == state.Players[0] ? "You win!" : $"{winner.Name} wins!";
        return new WinResult(winner.Id, msg);
    }

    // ── shared ────────────────────────────────────────────────────────────────

    private static WinResult ResolveByScore(GameState state)
    {
        if (state.Teams.Count > 0)
            return ResolveByTeamScore(state);

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

    private static WinResult ResolveByTeamScore(GameState state)
    {
        var ranked = state.Teams
            .Select(t => (Team: t, Score: state.GetTeamScore(t.Id)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
            return new WinResult(null, "It's a tie!");

        var winner = ranked[0].Team;
        string msg = IsHumanTeam(state, winner)
            ? $"Your team wins! ({winner.Name})"
            : $"{winner.Name} wins!";
        return new WinResult(winner.Id, msg);
    }

    /// <summary>True when the human player (Players[0]) is on this team.</summary>
    private static bool IsHumanTeam(GameState state, Team team)
        => state.Players.Count > 0 && team.Contains(state.Players[0].Id);
}
