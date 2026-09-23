using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Each player in turn chooses cards of their own to turn face-up — Golf's two peeks
/// before play. A rule about choice, so a phase rather than a side effect of the deal:
/// the deal turned the first two it happened to lay, which is not the player's choice
/// and never was.
///
/// Phase definition parameters:
///   zone     — base id of the zone the cards are in, resolved per player (default "grid")
///   count    — how many each player turns (default 2)
///   confirm  — true (default): tapping picks a card and a button turns them over, so a
///              finger landing on the wrong card costs nothing. false: a tap turns the
///              card immediately.
///
/// Either way a double tap turns a card there and then, for a player who knows which
/// card they want and does not need asking twice.
///
/// The seat to act is stored in metadata["reveal_seat"] and how many it has turned in
/// metadata["reveal_done"]; what it has picked but not yet turned is in
/// metadata["selected_card"], as uids — the same channel every other phase uses to say
/// what is picked, so picked cards get the selection border without the renderer
/// learning a second word for it.
/// </summary>
public sealed class RevealHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly string _zoneId;
    private readonly int    _count;
    private readonly bool   _confirm;

    public RevealHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId = nextPhaseId;
        _zoneId = def.Extra?.TryGetValue("zone", out var z) == true && z.ValueKind == System.Text.Json.JsonValueKind.String
            ? z.GetString() ?? "grid" : "grid";
        _count  = def.Extra?.TryGetValue("count", out var c) == true && c.ValueKind == System.Text.Json.JsonValueKind.Number
            ? c.GetInt32() : 2;
        _confirm = def.Extra?.TryGetValue("confirm", out var f) != true
                || f.ValueKind != System.Text.Json.JsonValueKind.False;
    }

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        Ensure(state);
        if (!Confirming(state)) return [];   // the affordance is the cards themselves

        // Offered from the first pick, not only a full hand of them: a player who meant
        // to turn one card should not have to turn two to get on with the game.
        var picked = Picked(state);
        return picked.Count == 0
            ? []
            : [new GameAction("flip", Label: GameText.Action(state, "flip", "Flip"))];
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        Ensure(state);
        var zone = ZoneFor(state);
        if (zone is null) return [];

        // Picked cards stay selectable so a second tap takes the pick back — the whole
        // point of asking before turning.
        return zone.Cards.Where(c => !c.IsFaceUp).Select(c => c.Id).ToList();
    }

    /// <summary>A double tap turns that card now, whatever the confirm setting says.</summary>
    public GameAction? DefaultCardAction(GameState state, string cardId, int? uid)
        => new GameAction("flip_card", CardId: cardId, CardUid: uid);

    public void Apply(GameState state, GameAction action)
    {
        Ensure(state);

        if (action.Type == "flip")
        {
            foreach (var uid in Picked(state))
                if (CardByUid(state, uid) is { IsFaceUp: false } card)
                    Turn(state, card);

            state.Metadata.Remove("selected_card");
            Settle(state);
            return;
        }

        if (action.Type is not ("select_card" or "play_card" or "flip_card") || action.CardId is null) return;

        var zone = ZoneFor(state);
        if (zone is null) return;

        var chosen = action.CardUid is int tapped
            ? zone.Cards.FirstOrDefault(c => c.Uid == tapped)
            : zone.Cards.FirstOrDefault(c => c.Id == action.CardId && !c.IsFaceUp);
        if (chosen is null || chosen.IsFaceUp) return;

        // Picking, when this game asks before turning and there is a person to ask. An
        // agent has no mind to change, and waiting for it to press its own button would
        // only be a pause.
        if (action.Type != "flip_card" && Confirming(state))
        {
            var picked = Picked(state);
            if (!picked.Remove(chosen.Uid))
            {
                if (picked.Count >= _count - Done(state)) return;   // already holding its share
                picked.Add(chosen.Uid);
            }

            if (picked.Count == 0) state.Metadata.Remove("selected_card");
            else state.Metadata["selected_card"] = string.Join(",", picked);

            UpdateStatus(state);
            return;
        }

        Turn(state, chosen);
        if (action.Type == "flip_card")
        {
            var picked = Picked(state);
            if (picked.Remove(chosen.Uid))
                state.Metadata["selected_card"] = string.Join(",", picked);
        }
        Settle(state);
    }

    /// <summary>An AI seat picks at once; a person is waited for.</summary>
    public TimeSpan? GetAutoAdvanceDelay(GameState state)
        => state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id) ? TimeSpan.FromMilliseconds(350) : null;

    private static void Turn(GameState state, Card card)
    {
        card.IsFaceUp = true;
        state.Metadata["reveal_done"] = (Done(state) + 1).ToString();
    }

    /// <summary>Hands on to the next seat, or to the next phase, once a seat is done.</summary>
    private void Settle(GameState state)
    {
        if (Done(state) < _count) { UpdateStatus(state); return; }

        state.AdvancePlayer();
        state.Metadata["reveal_done"] = "0";
        state.Metadata.Remove("selected_card");
        if (state.CurrentPlayerIndex == 0)
        {
            state.Metadata.Remove("reveal_done");
            state.CurrentPhaseId = _nextPhaseId;
            return;
        }
        UpdateStatus(state);
    }

    /// <summary>Whether this seat is being asked before its cards are turned.</summary>
    private bool Confirming(GameState state)
        => _confirm && !state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id);

    private void Ensure(GameState state)
    {
        if (state.Metadata.ContainsKey("reveal_done")) return;
        state.CurrentPlayerIndex = 0;
        state.Metadata["reveal_done"] = "0";
        UpdateStatus(state);
    }

    private static int Done(GameState state)
        => int.TryParse(state.Metadata.GetValueOrDefault("reveal_done"), out int n) ? n : 0;

    private static List<int> Picked(GameState state)
        => (state.Metadata.GetValueOrDefault("selected_card") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToList();

    private Card? CardByUid(GameState state, int uid)
        => ZoneFor(state)?.Cards.FirstOrDefault(c => c.Uid == uid);

    private Zone? ZoneFor(GameState state)
        => state.FindZone($"{_zoneId}:{state.CurrentPlayer.Id}") ?? state.FindZone(_zoneId);

    private void UpdateStatus(GameState state)
    {
        int left   = _count - Done(state);
        int picked = Picked(state).Count;

        state.Metadata["status"] = picked > 0
            ? GameText.Message(state, "reveal_ready", "{player}'s turn — Press Flip to turn {count} over",
                               state.CurrentPlayer.Id, ("count", picked))
            : left == 1
                ? GameText.Message(state, "reveal_one", "{player}'s turn — Tap a card to turn over",
                                   state.CurrentPlayer.Id)
                : GameText.Message(state, "reveal_more", "{player}'s turn — Tap {count} cards to turn over",
                                   state.CurrentPlayer.Id, ("count", left));
    }
}
