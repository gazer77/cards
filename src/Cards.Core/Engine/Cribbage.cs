using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Cribbage's four steps, each a phase type a definition strings together:
///
///   crib_discard — everyone lays cards away, face down, to the dealer's crib.
///   cut          — the starter is turned; a jack pegs the dealer his heels.
///   pegging      — the play: cards in turn to a running count of thirty-one, pegging
///                  fifteens, pairs, runs, thirty-ones, goes and the last card.
///   show         — each hand counted with the starter, from the dealer's left round to
///                  the dealer, and then the crib for the dealer.
///
/// Points are pegged the moment they are made, and the game ends the moment a side
/// reaches the target — mid-play if it comes to that, which is how cribbage is won. The
/// arithmetic is <see cref="CribbageScore"/>'s; what each thing is worth is the
/// definition's (<c>points</c> on each phase), defaulting to the standard values.
/// </summary>
internal static class Cribbage
{
    public static Zone? Hand(GameState state, string playerId) => state.FindZone($"hand:{playerId}");

    /// <summary>The seat to the dealer's left: first to lay away, first to play, first counted.</summary>
    public static int FirstSeat(GameState state)
    {
        int dealer = state.DealerId is { } id ? state.Players.FindIndex(p => p.Id == id) : -1;
        return dealer < 0 ? 0 : (dealer + 1) % state.Players.Count;
    }

    /// <summary>Who scores a player's points: their side, at a partnership table.</summary>
    public static string Side(GameState state, string playerId) => state.GetPlayerTeam(playerId)?.Id ?? playerId;

    /// <summary>
    /// Pegs <paramref name="points"/> to the player's side, adds them to the deal's tally
    /// for the score card, and ends the game if that took the side to the target. True
    /// when the game is over.
    /// </summary>
    public static bool Peg(GameState state, string playerId, int points)
    {
        if (points <= 0) return false;
        string side = Side(state, playerId);
        state.AddScore(side, points);
        int deal = int.TryParse(state.Metadata.GetValueOrDefault($"crib_deal_points:{side}"), out int d) ? d : 0;
        state.Metadata[$"crib_deal_points:{side}"] = (deal + points).ToString();
        return EndIfWon(state);
    }

    public static bool EndIfWon(GameState state)
    {
        var win = WinConditionEngine.Instance.Check(state);
        if (win is null) return false;

        RecordDeal(state);
        state.Metadata["status"]      = win.StatusMessage;
        state.Metadata["last_winner"] = win.WinnerId ?? "";
        state.CurrentPhaseId          = "game_over";
        return true;
    }

    /// <summary>The deal as one line of the score card: what each side pegged and counted in it.</summary>
    public static void RecordDeal(GameState state)
    {
        var sides  = state.Teams.Count > 0 ? state.Teams.Select(t => t.Id) : state.Players.Select(p => p.Id);
        var points = sides.ToDictionary(s => s, s =>
            int.TryParse(state.Metadata.GetValueOrDefault($"crib_deal_points:{s}"), out int v) ? v : 0);
        state.ScoreHistory.Add(new ScoreRound(state.RoundNumber, points));
        foreach (var s in points.Keys) state.Metadata.Remove($"crib_deal_points:{s}");
    }

    public static int Points(PhaseDefinition def, string key, int fallback)
        => def.Extra?.TryGetValue("points", out var p) == true && p.ValueKind == JsonValueKind.Object
           && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : fallback;

    public static string? GetString(PhaseDefinition def, string key)
        => def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    public static int? GetInt(PhaseDefinition def, string key)
        => def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;

    /// <summary>The cards a selection names: uids, or ids from older callers.</summary>
    public static List<Card> Picked(Zone hand, string? selection)
    {
        var tokens = (selection ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        return hand.Cards.Where(c => tokens.Any(t => int.TryParse(t, out int u) ? u == c.Uid : t == c.Id)).ToList();
    }

    public static Card? Find(Zone hand, GameAction action)
        => hand.Cards.FirstOrDefault(c => action.CardUid is { } u && u == c.Uid)
        ?? hand.Cards.FirstOrDefault(c => c.Id == action.CardId);
}

