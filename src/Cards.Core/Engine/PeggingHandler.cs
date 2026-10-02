using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// The play. From the dealer's left, each plays a card to a running count no higher than
/// <c>limit</c> (31), pegging as it goes; a player who cannot says go, the others play
/// on while they can, and the last to play pegs the go — or thirty-one, which is its own
/// reward. Then the count starts again from whoever comes next. The last card of all
/// pegs one. Cards played go face up to the player's <c>played</c> zone, where the show
/// counts them.
///
/// Parameters: limit, played (zone base id, default "played"),
/// points { fifteen, thirty_one, pair, pair_royal, double_pair_royal, run_card, go, last_card }.
/// </summary>
public sealed class PeggingHandler(PhaseDefinition def, string nextPhaseId) : IPhaseHandler
{
    private readonly int    _limit  = Cribbage.GetInt(def, "limit") ?? 31;
    private readonly string _played = Cribbage.GetString(def, "played") ?? "played";
    private readonly CribbageScore.PegValues _values = new(
        Fifteen:          Cribbage.Points(def, "fifteen", 2),
        ThirtyOne:        Cribbage.Points(def, "thirty_one", 2),
        Pair:             Cribbage.Points(def, "pair", 2),
        PairRoyal:        Cribbage.Points(def, "pair_royal", 6),
        DoublePairRoyal:  Cribbage.Points(def, "double_pair_royal", 12),
        RunCard:          Cribbage.Points(def, "run_card", 1));
    private readonly int _go       = Cribbage.Points(def, "go", 1);
    private readonly int _lastCard = Cribbage.Points(def, "last_card", 1);

    // ── State ─────────────────────────────────────────────────────────────────

    private static int Count(GameState state)
        => int.TryParse(state.Metadata.GetValueOrDefault("peg_count"), out int n) ? n : 0;

    /// <summary>The cards played since the count was last reset, in order, by uid.</summary>
    private static List<int> Run(GameState state)
        => (state.Metadata.GetValueOrDefault("peg_run") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToList();

    private static bool SaidGo(GameState state, string playerId)
        => state.Metadata.GetValueOrDefault($"peg_go:{playerId}") == "true";

    private List<Card> Playable(GameState state, string playerId)
        => Cribbage.Hand(state, playerId)?.Cards.Where(c => Count(state) + CribbageScore.Value(c) <= _limit).ToList() ?? [];

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        Enter(state);
        if (Playable(state, state.CurrentPlayer.Id).Count == 0)
            return [new GameAction("go", Label: GameText.Action(state, "go", "Go"))];
        return state.Metadata.ContainsKey("selected_card")
            ? [new GameAction("play_card", Label: GameText.Action(state, "play", "Play"))]
            : [];
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        Enter(state);
        return Playable(state, state.CurrentPlayer.Id).Select(c => c.Id).ToList();
    }

    public GameAction? DefaultCardAction(GameState state, string cardId, int? uid)
        => Playable(state, state.CurrentPlayer.Id).Any(c => c.Id == cardId)
            ? new GameAction("play_card", CardId: cardId, CardUid: uid)
            : null;

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
        => state.FindZone($"{_played}:{state.CurrentPlayer.Id}") is { } z ? [z.Id] : [];

    public void Apply(GameState state, GameAction action)
    {
        Enter(state);
        string me   = state.CurrentPlayer.Id;
        var    hand = Cribbage.Hand(state, me);
        if (hand is null) return;

        if (action.Type == "select_card")
        {
            if (Playable(state, me).Any(c => c.Id == action.CardId || c.Uid == action.CardUid))
                state.Metadata["selected_card"] = (action.CardUid ?? hand.Cards.First(c => c.Id == action.CardId).Uid).ToString();
            return;
        }

        if (action.Type == "go")
        {
            if (Playable(state, me).Count > 0) return;   // a player who can play must
            state.Metadata[$"peg_go:{me}"] = "true";
            GameText.Log(state, "log_go", "{player}: go", me);
            Next(state);
            return;
        }

        if (action.Type is "play_card" or "tap")
        {
            var card = (action.CardId is not null || action.CardUid is not null ? Cribbage.Find(hand, action) : null)
                    ?? Cribbage.Picked(hand, state.Metadata.GetValueOrDefault("selected_card")).FirstOrDefault();
            if (card is null || !Playable(state, me).Contains(card)) return;
            Play(state, me, hand, card);
        }
    }

    // ── The play ──────────────────────────────────────────────────────────────

    private void Enter(GameState state)
    {
        if (state.Metadata.ContainsKey("peg_count")) return;
        state.Metadata["peg_count"] = "0";
        state.Metadata["peg_run"]   = "";
        state.Metadata.Remove("selected_card");
        state.CurrentPlayerIndex = Cribbage.FirstSeat(state);
        UpdateStatus(state);
    }

    private void Play(GameState state, string me, Zone hand, Card card)
    {
        state.Metadata.Remove("selected_card");
        hand.Remove(card);
        card.IsFaceUp = true;
        state.FindZone($"{_played}:{me}")?.Add(card);

        int count = Count(state) + CribbageScore.Value(card);
        var run   = Run(state);
        run.Add(card.Uid);
        state.Metadata["peg_count"] = count.ToString();
        state.Metadata["peg_run"]   = string.Join(",", run);
        state.Metadata["peg_last"]  = me;

        var cards  = run.Select(u => state.Zones.Values.SelectMany(z => z.Cards).First(c => c.Uid == u)).ToList();
        var scored = CribbageScore.Peg(cards, _values);

        GameText.Log(state, "log_pegged", "{player} played the {card} — {count}", me,
                     ("card", GameText.CardName(card)), ("count", count));

        var said = new List<string>();
        if (scored.Fifteen   > 0) said.Add(GameText.Message(state, "peg_fifteen", "fifteen for {points}", values: ("points", scored.Fifteen)));
        if (scored.ThirtyOne > 0) said.Add(GameText.Message(state, "peg_thirty_one", "thirty-one for {points}", values: ("points", scored.ThirtyOne)));
        if (scored.Pairs > 0)
            said.Add(scored.PairCount switch
            {
                2 => GameText.Message(state, "peg_pair", "a pair for {points}", values: ("points", scored.Pairs)),
                3 => GameText.Message(state, "peg_pair_royal", "three of a kind for {points}", values: ("points", scored.Pairs)),
                _ => GameText.Message(state, "peg_double_pair_royal", "four of a kind for {points}", values: ("points", scored.Pairs)),
            });
        if (scored.Run > 0)
            said.Add(GameText.Message(state, "peg_run", "a run of {length} for {points}",
                                      values: [("length", scored.RunLength), ("points", scored.Run)]));

        // The last card of all, when it is not already thirty-one.
        bool allOut = state.Players.All(p => Cribbage.Hand(state, p.Id)?.IsEmpty ?? true);
        int  last   = allOut && count != _limit ? _lastCard : 0;
        if (last > 0) said.Add(GameText.Message(state, "peg_last", "last card for {points}", values: ("points", last)));

        if (said.Count > 0)
        {
            GameText.Announce(state, me, "peg_scored", "{player}: {what}", ("what", string.Join(", ", said)));
            if (Cribbage.Peg(state, me, scored.Total + last)) return;
        }

        if (count == _limit) Reset(state);
        Next(state);
    }

    /// <summary>
    /// Whose play it is now. The next seat round that still holds a card and has not
    /// said go — offered the go button if nothing fits. When nobody is left to play to
    /// this count, the last to play pegs the go and the count starts again from the seat
    /// after them. When nobody holds a card at all, the play is over.
    /// </summary>
    private void Next(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;
        int n = state.Players.Count;

        if (state.Players.All(p => Cribbage.Hand(state, p.Id)?.IsEmpty ?? true))
        {
            Finish(state);
            return;
        }

        for (int step = 1; step <= n; step++)
        {
            int seat = (state.CurrentPlayerIndex + step) % n;
            var p    = state.Players[seat];
            if ((Cribbage.Hand(state, p.Id)?.IsEmpty ?? true) || SaidGo(state, p.Id)) continue;

            // Nothing fits, and everyone else is already out of this count: there is no
            // one to say go to. The go is pegged, as a table does, without asking.
            bool othersDone = state.Players.Where(o => o.Id != p.Id)
                .All(o => (Cribbage.Hand(state, o.Id)?.IsEmpty ?? true) || SaidGo(state, o.Id));
            if (Playable(state, p.Id).Count == 0 && othersDone) break;

            state.CurrentPlayerIndex = seat;
            UpdateStatus(state);
            return;
        }

        // Nobody can go on at this count: the go to the last to play.
        if (state.Metadata.GetValueOrDefault("peg_last") is { } lastId && Count(state) > 0)
        {
            GameText.Announce(state, lastId, "peg_scored", "{player}: {what}",
                ("what", GameText.Message(state, "peg_go", "a go for {points}", values: ("points", _go))));
            if (Cribbage.Peg(state, lastId, _go)) return;
            state.CurrentPlayerIndex = state.Players.FindIndex(p => p.Id == lastId);
        }
        Reset(state);
        Next(state);
    }

    private static void Reset(GameState state)
    {
        state.Metadata["peg_count"] = "0";
        state.Metadata["peg_run"]   = "";
        foreach (var p in state.Players) state.Metadata.Remove($"peg_go:{p.Id}");
    }

    private void Finish(GameState state)
    {
        state.Metadata.Remove("peg_count");
        state.Metadata.Remove("peg_run");
        state.Metadata.Remove("peg_last");
        foreach (var p in state.Players) state.Metadata.Remove($"peg_go:{p.Id}");
        state.CurrentPlayerIndex = Cribbage.FirstSeat(state);
        state.CurrentPhaseId     = nextPhaseId;
    }

    private void UpdateStatus(GameState state)
    {
        string me = state.CurrentPlayer.Id;
        state.Metadata["status"] = Playable(state, me).Count > 0
            ? GameText.Message(state, "turn_peg", "{player} to play  |  Count: {count}", me, ("count", Count(state)))
            : GameText.Message(state, "turn_peg_go", "{player} can't play — go  |  Count: {count}", me, ("count", Count(state)));
    }
}

