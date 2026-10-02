using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Everyone lays cards away to the dealer's crib, down to <c>keep</c> cards in hand
/// (default 4). The cards go face down as each player confirms, so no one else sees
/// them; once all have, the crib is made up to <c>crib_size</c> (default 4) from the
/// deck — three players lay away one each, and the deck gives the fourth.
///
/// Parameters: keep, to (the crib zone, default "crib"), crib_size.
/// </summary>
public sealed class CribDiscardHandler(PhaseDefinition def, string nextPhaseId) : IPhaseHandler
{
    private readonly int    _keep     = Cribbage.GetInt(def, "keep") ?? 4;
    private readonly int    _cribSize = Cribbage.GetInt(def, "crib_size") ?? 4;
    private readonly string _to       = Cribbage.GetString(def, "to") ?? "crib";

    private int Owed(GameState state) => Math.Max(0, (Cribbage.Hand(state, state.CurrentPlayer.Id)?.Count ?? 0) - _keep);

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        Enter(state);
        var hand = Cribbage.Hand(state, state.CurrentPlayer.Id);
        if (hand is null) return [];
        return Cribbage.Picked(hand, state.Metadata.GetValueOrDefault("selected_card")).Count == Owed(state)
            ? [new GameAction("lay_away", Label: GameText.Action(state, "lay_away", "To the crib"))]
            : [];
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        Enter(state);
        return Cribbage.Hand(state, state.CurrentPlayer.Id)?.Cards.Select(c => c.Id).ToList() ?? [];
    }

    public void Apply(GameState state, GameAction action)
    {
        Enter(state);
        var hand = Cribbage.Hand(state, state.CurrentPlayer.Id);
        if (hand is null) return;

        // A tap picks a card or puts it back. The computer picks with play_card, and
        // lays away the moment it has picked enough.
        if (action.Type is "select_card" or "play_card" && Cribbage.Find(hand, action) is { } card)
        {
            var picked = Cribbage.Picked(hand, state.Metadata.GetValueOrDefault("selected_card"));
            if (picked.Contains(card)) picked.Remove(card);
            else if (picked.Count < Owed(state)) picked.Add(card);
            state.Metadata["selected_card"] = string.Join(",", picked.Select(c => c.Uid));

            if (action.Type == "play_card" && picked.Count == Owed(state)) LayAway(state, hand, picked);
            else UpdateStatus(state);
            return;
        }

        if (action.Type == "lay_away")
        {
            var picked = Cribbage.Picked(hand, state.Metadata.GetValueOrDefault("selected_card"));
            if (picked.Count == Owed(state)) LayAway(state, hand, picked);
        }
    }

    private void Enter(GameState state)
    {
        if (state.Metadata.ContainsKey("crib_laying")) return;
        state.Metadata["crib_laying"] = "0";
        state.Metadata.Remove("selected_card");
        state.CurrentPlayerIndex = Cribbage.FirstSeat(state);
        UpdateStatus(state);
    }

    private void LayAway(GameState state, Zone hand, List<Card> cards)
    {
        var crib = state.FindZone(_to);
        foreach (var c in cards)
        {
            hand.Remove(c);
            c.IsFaceUp = false;
            crib?.Add(c);
        }
        state.Metadata.Remove("selected_card");
        GameText.Log(state, "log_laid_away", "{player} laid away {count} for the crib", state.CurrentPlayer.Id,
                     ("count", cards.Count));

        int done = int.Parse(state.Metadata["crib_laying"]) + 1;
        if (done < state.Players.Count)
        {
            state.Metadata["crib_laying"] = done.ToString();
            state.AdvancePlayer();
            UpdateStatus(state);
            return;
        }

        // Everyone has: make the crib up from the deck, and on to the cut.
        var deck = state.FindZone("deck");
        while (crib is not null && crib.Count < _cribSize && deck?.Draw() is { } extra)
        {
            extra.IsFaceUp = false;
            crib.Add(extra);
        }

        state.Metadata.Remove("crib_laying");
        state.CurrentPlayerIndex = Cribbage.FirstSeat(state);
        state.CurrentPhaseId     = nextPhaseId;
    }

    private void UpdateStatus(GameState state)
    {
        int owed = Owed(state) - Cribbage.Picked(Cribbage.Hand(state, state.CurrentPlayer.Id)!,
                                                 state.Metadata.GetValueOrDefault("selected_card")).Count;
        state.Metadata["status"] = owed > 0
            ? GameText.Message(state, "turn_crib", "{player} to lay away {count} for the crib", state.CurrentPlayer.Id,
                               ("count", owed))
            : GameText.Message(state, "turn_crib_ready", "{player} is laying away for the crib", state.CurrentPlayer.Id);
    }
}

