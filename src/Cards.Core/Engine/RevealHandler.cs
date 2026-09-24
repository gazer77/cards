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

    /// <summary>
    /// Dimensions the cards a seat turns must differ in — Golf's rule that the two you
    /// peek at may share a row but not a column. Empty when the game does not care.
    ///
    /// Enforced by leaving the cards that would break it out of the selectable list
    /// rather than by refusing a tap afterwards: a rule that shows itself as "that card
    /// is not on offer" needs no message, and a computer seat cannot pick its way into
    /// an illegal pair either.
    /// </summary>
    private readonly HashSet<string> _distinct = [];

    public RevealHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId = nextPhaseId;
        _zoneId = def.Extra?.TryGetValue("zone", out var z) == true && z.ValueKind == System.Text.Json.JsonValueKind.String
            ? z.GetString() ?? "grid" : "grid";
        _count  = def.Extra?.TryGetValue("count", out var c) == true && c.ValueKind == System.Text.Json.JsonValueKind.Number
            ? c.GetInt32() : 2;
        _confirm = def.Extra?.TryGetValue("confirm", out var f) != true
                || f.ValueKind != System.Text.Json.JsonValueKind.False;

        // "distinct": "column", or ["row", "column"] for a game that wants both.
        if (def.Extra?.TryGetValue("distinct", out var d) == true)
        {
            if (d.ValueKind == System.Text.Json.JsonValueKind.String)
                _distinct.Add(d.GetString()!);
            else if (d.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var entry in d.EnumerateArray())
                    if (entry.ValueKind == System.Text.Json.JsonValueKind.String)
                        _distinct.Add(entry.GetString()!);
        }
    }

    /// <summary>The seat to act is settled on entry, not on the first question asked.</summary>
    public void OnPhaseEnter(GameState state) => Ensure(state);

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
        var picked = Picked(state);
        var taken  = TakenLines(zone, picked);

        return zone.Cards
            .Where(c => !c.IsFaceUp)
            .Where(c => picked.Contains(c.Uid) || IsAllowed(zone, c, taken))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// The rows and columns already spoken for this turn — by a card already turned, or
    /// by one picked and waiting on the button.
    /// </summary>
    private HashSet<(string Dimension, int Line)> TakenLines(Zone zone, List<int> picked)
    {
        var taken = new HashSet<(string, int)>();
        if (_distinct.Count == 0) return taken;

        for (int i = 0; i < zone.Cards.Count; i++)
        {
            var card = zone.Cards[i];
            if (!card.IsFaceUp && !picked.Contains(card.Uid)) continue;

            var (row, col) = Cell(zone, i);
            if (_distinct.Contains("row"))    taken.Add(("row", row));
            if (_distinct.Contains("column")) taken.Add(("column", col));
        }
        return taken;
    }

    private bool IsAllowed(Zone zone, Card card, HashSet<(string Dimension, int Line)> taken)
    {
        if (taken.Count == 0) return true;

        var (row, col) = Cell(zone, zone.Cards.IndexOf(card));
        if (_distinct.Contains("row")    && taken.Contains(("row", row)))    return false;
        if (_distinct.Contains("column") && taken.Contains(("column", col))) return false;
        return true;
    }

    /// <summary>Where a card sits in its grid, row-major — the same order it is drawn in.</summary>
    private static (int Row, int Col) Cell(Zone zone, int index)
    {
        int cols = Math.Max(1, zone.Definition?.Cols ?? 3);
        return (index / cols, index % cols);
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

        // The same rule the selectable list expresses, held here too: an agent or an
        // outside caller sends a card id straight in, and a rule that only exists in a
        // highlight is not a rule.
        var already = Picked(state);
        if (!already.Contains(chosen.Uid) && !IsAllowed(zone, chosen, TakenLines(zone, already))) return;

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
