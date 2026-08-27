using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for War (the card game).
///
/// Phase definition parameters:
///   war_face_down_count — cards placed face-down per player during war (default 3)
///   tie_resolution      — "war" (default) | "split" — on tie, go to war or each player
///                         takes their own card back
///
/// State metadata:
///   war_state      — "ready" (waiting for flip tap) | "collect" (trick done, tap to collect)
///   last_winner    — player ID of round winner, or "" for tie-split
/// </summary>
public sealed class WarHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly int    _warFaceDownCount;
    private readonly bool   _tieSplit;

    public WarHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId      = nextPhaseId;
        _warFaceDownCount = GetInt(def, "war_face_down_count") ?? 3;
        _tieSplit         = string.Equals(GetString(def, "tie_resolution"), "split",
                                StringComparison.OrdinalIgnoreCase);
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        return [new GameAction("tap")];
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);
        if (action.Type != "tap") return;

        string warState = state.Metadata.GetValueOrDefault("war_state", "ready");

        if (warState == "collect")
            CollectAndContinue(state);
        else
            FlipBattle(state);
    }

    // ── Flip & compare ────────────────────────────────────────────────────────

    private void FlipBattle(GameState state)
    {
        if (AnyHandEmpty(state)) { ApplyWinResult(state, WinConditionEngine.Instance.Resolve(state)); return; }

        foreach (var p in state.Players)
            MoveTopCard(Hand(state, p), Play(state, p));

        CompareAndResolve(state);
    }

    private void CompareAndResolve(GameState state)
    {
        while (true)
        {
            var p0    = state.Players[0];
            var p1    = state.Players[1];
            var card0 = Play(state, p0).TopCard;
            var card1 = Play(state, p1).TopCard;

            if (card0 is null || card1 is null) { ApplyWinResult(state, WinConditionEngine.Instance.Resolve(state)); return; }

            int r0 = (int)card0.Rank;
            int r1 = (int)card1.Rank;

            if (r0 > r1) { SetCollectResult(state, p0.Id, "You win this round!");       return; }
            if (r1 > r0) { SetCollectResult(state, p1.Id, "Opponent wins this round."); return; }

            // ── Tie ──
            if (_tieSplit)
            {
                Hand(state, p0).Add(Play(state, p0).Draw()!);
                Hand(state, p1).Add(Play(state, p1).Draw()!);
                state.Metadata["war_state"]   = "collect";
                state.Metadata["last_winner"] = "";
                state.Metadata["status"]      = "Tie! Cards returned.\nTap to continue.";
                return;
            }

            // War: need warFaceDownCount + 1 cards each
            if (state.Players.Any(p => Hand(state, p).Count < _warFaceDownCount + 1))
            {
                ApplyWinResult(state, WinConditionEngine.Instance.Resolve(state)); return;
            }

            var pot = Pot(state);
            foreach (var p in state.Players)
            {
                pot.Add(Play(state, p).Draw()!);
                for (int i = 0; i < _warFaceDownCount; i++) pot.Add(Hand(state, p).Draw()!);
                MoveTopCard(Hand(state, p), Play(state, p));
            }
            // Loop — compare the new face-up cards
        }
    }

    // ── Collect ───────────────────────────────────────────────────────────────

    private void CollectAndContinue(GameState state)
    {
        string winnerId = state.Metadata.GetValueOrDefault("last_winner", "");

        if (!string.IsNullOrEmpty(winnerId))
        {
            var dest = Hand(state, state.Players.First(p => p.Id == winnerId));
            foreach (var p in state.Players) DrainInto(Play(state, p), dest);
            DrainInto(Pot(state), dest);
        }

        state.Metadata["war_state"] = "ready";
        state.RoundNumber++;

        var win = WinConditionEngine.Instance.Check(state);
        if (win is not null) { ApplyWinResult(state, win); return; }

        state.Metadata["status"] = "Tap to flip!";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (!state.Metadata.ContainsKey("war_state"))
        {
            state.Metadata["war_state"] = "ready";
            state.Metadata["status"]    = "Tap to flip!";
        }
    }

    private static void SetCollectResult(GameState state, string winnerId, string msg)
    {
        var pot    = Pot(state);
        int potSize = pot.Count + state.Players.Count;
        string extra = potSize > 2 ? $" ({potSize} cards)" : "";
        state.Metadata["last_winner"] = winnerId;
        state.Metadata["war_state"]   = "collect";
        state.Metadata["status"]      = msg + extra + "\nTap to collect.";
    }

    private static void ApplyWinResult(GameState state, WinResult result)
    {
        state.CurrentPhaseId          = "game_over";
        state.Metadata["last_winner"] = result.WinnerId ?? "";
        state.Metadata["status"]      = result.StatusMessage;
    }

    private static bool AnyHandEmpty(GameState state)
        => state.Players.Any(p => Hand(state, p).Count == 0);

    private static Zone Hand(GameState s, Player p) => s.Zones[$"hand:{p.Id}"];
    private static Zone Play(GameState s, Player p) => s.Zones[$"play:{p.Id}"];
    private static Zone Pot(GameState s)             => s.Zones["pot"];

    private static void MoveTopCard(Zone from, Zone to)
    {
        var c = from.Draw();
        if (c is not null) { c.IsFaceUp = true; to.Add(c); }
    }

    private static void DrainInto(Zone from, Zone to)
    {
        while (from.Count > 0) to.Add(from.Draw()!);
    }

    private static string? GetString(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static int? GetInt(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }
}
