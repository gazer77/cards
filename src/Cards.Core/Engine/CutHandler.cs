using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// The starter: the deck is cut and the card turned face up to <c>to</c> (default
/// "starter"). A jack pegs the dealer his heels (<c>points.his_heels</c>, default 2).
/// </summary>
public sealed class CutHandler(PhaseDefinition def, string nextPhaseId) : IPhaseHandler
{
    private readonly string _to        = Cribbage.GetString(def, "to") ?? "starter";
    private readonly int    _hisHeels  = Cribbage.Points(def, "his_heels", 2);

    public TimeSpan? GetAutoAdvanceDelay(GameState state) => TimeSpan.FromMilliseconds(900);

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        state.Metadata["status"] = GameText.Message(state, "turn_cut", "Cutting for the starter");
        return [new GameAction("tap", Label: GameText.Action(state, "cut", "Cut"))];
    }

    public void Apply(GameState state, GameAction action)
    {
        var deck    = state.FindZone("deck");
        var starter = state.FindZone(_to);
        if (deck is null || starter is null || deck.IsEmpty) { state.CurrentPhaseId = nextPhaseId; return; }

        // Cut somewhere in the middle, and turn the card.
        int at   = state.Rng.Next(Math.Max(1, deck.Count - 8)) + 4;
        var card = deck.Cards[Math.Min(at, deck.Count - 1)];
        deck.Remove(card);
        card.IsFaceUp = true;
        starter.Add(card);

        GameText.Log(state, "log_starter", "The starter is the {card}", values: ("card", GameText.CardName(card)));
        state.CurrentPlayerIndex = Cribbage.FirstSeat(state);
        state.CurrentPhaseId     = nextPhaseId;

        if (card.Rank == Rank.Jack && state.DealerId is { } dealer)
        {
            GameText.Announce(state, dealer, "his_heels", "{player}: his heels for {points}", ("points", _hisHeels));
            Cribbage.Peg(state, dealer, _hisHeels);
        }
    }
}

