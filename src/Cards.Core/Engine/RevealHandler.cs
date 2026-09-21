using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Each player in turn chooses cards of their own to turn face-up — Golf's two peeks
/// before play. A rule about choice, so a phase rather than a side effect of the deal:
/// the deal turned the first two it happened to lay, which is not the player's choice
/// and never was.
///
/// Phase definition parameters:
///   zone   — base id of the zone the cards are in, resolved per player (default "grid")
///   count  — how many each player turns (default 2)
///
/// The seat to act is stored in metadata["reveal_seat"] and how many it has turned in
/// metadata["reveal_done"]. Once every seat has turned its count, the phase ends and
/// the first player is to act in the next.
/// </summary>
public sealed class RevealHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly string _zoneId;
    private readonly int    _count;

    public RevealHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId = nextPhaseId;
        _zoneId = def.Extra?.TryGetValue("zone", out var z) == true && z.ValueKind == System.Text.Json.JsonValueKind.String
            ? z.GetString() ?? "grid" : "grid";
        _count  = def.Extra?.TryGetValue("count", out var c) == true && c.ValueKind == System.Text.Json.JsonValueKind.Number
            ? c.GetInt32() : 2;
    }

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        Ensure(state);
        return [];   // the affordance is the cards themselves
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        Ensure(state);
        var zone = ZoneFor(state);
        return zone is null ? [] : zone.Cards.Where(c => !c.IsFaceUp).Select(c => c.Id).ToList();
    }

    public void Apply(GameState state, GameAction action)
    {
        Ensure(state);
        if (action.Type is not ("select_card" or "play_card") || action.CardId is null) return;

        var zone = ZoneFor(state);
        if (zone is null) return;

        var card = action.CardUid is int uid
            ? zone.Cards.FirstOrDefault(c => c.Uid == uid)
            : zone.Cards.FirstOrDefault(c => c.Id == action.CardId && !c.IsFaceUp);
        if (card is null || card.IsFaceUp) return;

        card.IsFaceUp = true;
        int done = Done(state) + 1;
        state.Metadata["reveal_done"] = done.ToString();

        if (done < _count) { UpdateStatus(state); return; }

        // This seat is done; the next seat picks, or the phase is over.
        state.AdvancePlayer();
        state.Metadata["reveal_done"] = "0";
        if (state.CurrentPlayerIndex == 0)
        {
            state.Metadata.Remove("reveal_done");
            state.CurrentPhaseId = _nextPhaseId;
            return;
        }
        UpdateStatus(state);
    }

    /// <summary>An AI seat picks at once; a person is waited for.</summary>
    public TimeSpan? GetAutoAdvanceDelay(GameState state)
        => state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id) ? TimeSpan.FromMilliseconds(350) : null;

    private void Ensure(GameState state)
    {
        if (state.Metadata.ContainsKey("reveal_done")) return;
        state.CurrentPlayerIndex = 0;
        state.Metadata["reveal_done"] = "0";
        UpdateStatus(state);
    }

    private static int Done(GameState state)
        => int.TryParse(state.Metadata.GetValueOrDefault("reveal_done"), out int n) ? n : 0;

    private Zone? ZoneFor(GameState state)
        => state.FindZone($"{_zoneId}:{state.CurrentPlayer.Id}") ?? state.FindZone(_zoneId);

    private void UpdateStatus(GameState state)
    {
        int left = _count - Done(state);
        state.Metadata["status"] = left == 1
            ? GameText.Message(state, "reveal_one", "{player}'s turn — Tap a card to turn over", state.CurrentPlayer.Id)
            : GameText.Message(state, "reveal_more", "{player}'s turn — Tap {count} cards to turn over",
                               state.CurrentPlayer.Id, ("count", left));
    }
}
