using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for unconstrained card movement (solitaire, Free Play mode).
///
/// Phase definition parameters:
///   end_turn  — "manual" (default): player taps "End Turn" button
///   end_game  — "manual" (default): player taps "End Game" button
///
/// Players can drag any card from any visible zone to any other zone.
/// No rules are enforced.
/// </summary>
public sealed class FreePlayHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly string _endTurn;
    private readonly string _endGame;

    public FreePlayHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId = nextPhaseId;
        _endTurn     = GetString(def, "end_turn") ?? "manual";
        _endGame     = GetString(def, "end_game") ?? "manual";
    }

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        var actions = new List<GameAction>();
        if (_endTurn == "manual" && state.Players.Count > 1)
            actions.Add(new GameAction("end_turn", Label: "End Turn"));
        if (_endGame == "manual")
            actions.Add(new GameAction("end_game", Label: "End Game"));
        return actions;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        // All face-up cards in zones visible to the current player
        return state.Zones.Values
            .Where(z => z.Visibility is "all" or "owner" && z.OwnerId == state.CurrentPlayer.Id
                     || z.Visibility == "all")
            .SelectMany(z => z.Cards.Where(c => c.IsFaceUp))
            .Select(c => c.Id)
            .ToList();
    }

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
    {
        // Cards can be dropped into any non-deck zone
        return state.Zones.Keys
            .Where(id => !state.Zones[id].Type.Equals("deck", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void Apply(GameState state, GameAction action)
    {
        if (action.Type == "end_turn")
        {
            state.AdvancePlayer();
            state.Metadata["status"] = state.CurrentPlayer == state.Players[0]
                ? "Your turn" : $"{state.CurrentPlayer.Name}'s turn";
            return;
        }

        if (action.Type == "end_game")
        {
            state.CurrentPhaseId = "game_over";
            state.Metadata["status"] = "Game ended.";
            return;
        }

        if (action.Type == "play_card" && action.CardId is { } cardId && action.ZoneId is { } zoneId)
        {
            MoveCard(state, cardId, zoneId);
        }
    }

    private static void MoveCard(GameState state, string cardId, string targetZoneId)
    {
        Card? card    = null;
        Zone? srcZone = null;

        foreach (var zone in state.Zones.Values)
        {
            card = zone.Cards.FirstOrDefault(c => c.Id == cardId);
            if (card is not null) { srcZone = zone; break; }
        }

        if (card is null || srcZone is null) return;
        if (!state.Zones.TryGetValue(targetZoneId, out var destZone)) return;

        srcZone.Remove(card);
        card.IsFaceUp = true;
        destZone.Add(card);
    }

    private static string? GetString(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }
}
