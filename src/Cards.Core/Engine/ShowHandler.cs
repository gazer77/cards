using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// The show: each hand counted with the starter, from the dealer's left round to the
/// dealer, then the crib for the dealer — one count a step, so the table can read each.
/// A hand is what the player played (<c>played</c>) and is counted where it lies; the crib
/// is turned over for its count.
///
/// Parameters: played, crib (zone, default "crib"), starter (zone, default "starter"),
/// points { fifteen, pair, run_card, flush_card, nobs }.
/// </summary>
public sealed class ShowHandler(PhaseDefinition def, string nextPhaseId) : IPhaseHandler
{
    private readonly string _played  = Cribbage.GetString(def, "played") ?? "played";
    private readonly string _crib    = Cribbage.GetString(def, "crib") ?? "crib";
    private readonly string _starter = Cribbage.GetString(def, "starter") ?? "starter";
    private readonly CribbageScore.ShowValues _values = new(
        Fifteen:   Cribbage.Points(def, "fifteen", 2),
        Pair:      Cribbage.Points(def, "pair", 2),
        RunCard:   Cribbage.Points(def, "run_card", 1),
        FlushCard: Cribbage.Points(def, "flush_card", 1),
        Nobs:      Cribbage.Points(def, "nobs", 1));

    public TimeSpan? GetAutoAdvanceDelay(GameState state) => TimeSpan.FromMilliseconds(2200);

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
        => [new GameAction("tap", Label: GameText.Action(state, "count", "Count"))];

    /// <summary>Steps: each player from the dealer's left round to the dealer, then the crib.</summary>
    public void Apply(GameState state, GameAction action)
    {
        int step = int.TryParse(state.Metadata.GetValueOrDefault("show_step"), out int s) ? s : 0;
        int n    = state.Players.Count;
        var starter = state.FindZone(_starter)?.TopCard;

        if (step < n)
        {
            var p    = state.Players[(Cribbage.FirstSeat(state) + step) % n];
            var hand = state.FindZone($"{_played}:{p.Id}")?.Cards.ToList() ?? [];
            state.CurrentPlayerIndex   = state.Players.IndexOf(p);
            state.Metadata["show_step"] = (step + 1).ToString();
            Count(state, p.Id, hand, starter, crib: false);
            return;
        }

        if (step == n && state.FindZone(_crib) is { } crib && state.DealerId is { } dealer)
        {
            foreach (var c in crib.Cards) c.IsFaceUp = true;
            state.CurrentPlayerIndex   = state.Players.FindIndex(p => p.Id == dealer);
            state.Metadata["show_step"] = (step + 1).ToString();
            Count(state, dealer, crib.Cards.ToList(), starter, crib: true);
            return;
        }

        // Everything counted: the deal goes on the score card, and on to the next.
        state.Metadata.Remove("show_step");
        Cribbage.RecordDeal(state);
        state.CurrentPhaseId = nextPhaseId;
    }

    /// <summary>Counts one hand, says it, and pegs it — which may end the game, the show with it.</summary>
    private void Count(GameState state, string playerId, List<Card> cards, Card? starter, bool crib)
    {
        var points = CribbageScore.Show(cards, starter, crib, _values);

        var parts = new List<string>();
        if (points.Fifteens > 0) parts.Add(GameText.Message(state, "show_fifteens", "fifteens {points}", values: ("points", points.Fifteens)));
        if (points.Pairs    > 0) parts.Add(GameText.Message(state, "show_pairs", "pairs {points}", values: ("points", points.Pairs)));
        if (points.Runs     > 0) parts.Add(GameText.Message(state, "show_runs", "runs {points}", values: ("points", points.Runs)));
        if (points.Flush    > 0) parts.Add(GameText.Message(state, "show_flush", "a flush {points}", values: ("points", points.Flush)));
        if (points.Nobs     > 0) parts.Add(GameText.Message(state, "show_nobs", "his nobs {points}", values: ("points", points.Nobs)));
        string what = parts.Count > 0 ? string.Join(", ", parts)
                                      : GameText.Message(state, "show_nothing", "nineteen — nothing");

        string line = crib
            ? GameText.Message(state, "show_crib", "{player}'s crib: {what} = {points}", playerId, ("what", what), ("points", points.Total))
            : GameText.Message(state, "show_hand", "{player}'s hand: {what} = {points}", playerId, ("what", what), ("points", points.Total));
        state.GameLog.Add(line);
        state.Metadata["status"] = line;

        Cribbage.Peg(state, playerId, points.Total);
    }
}
