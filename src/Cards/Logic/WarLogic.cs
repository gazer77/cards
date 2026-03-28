using Cards.Engine;

namespace Cards.Logic;

/// <summary>
/// Implements the rules of War (the card game).
///
/// Zones created by this module (in addition to those defined in war.json):
///   play:{playerId}  — the card currently face-up in the battle zone
///   pot              — accumulated face-down war cards (count_only display)
///
/// Phases (stored in GameState.CurrentPhaseId):
///   ready    — waiting for the player to tap to flip
///   result   — round resolved; showing outcome, waiting for tap to collect
///   game_over
/// </summary>
public sealed class WarLogic : IGameLogic
{
    private const int WarFaceDownCount = 3;

    private bool _tieSplit;

    // ── Initialize ────────────────────────────────────────────────────────────

    public void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules)
    {
        _tieSplit = enabledHouseRules.Contains("tie_split");

        // Players
        state.Players.Add(new Player("player0", "You"));
        if (playerCount >= 2)
            state.Players.Add(new Player("player1", "Opponent"));

        // Zones from JSON definition
        foreach (var zoneDef in state.Definition.Zones)
        {
            if (zoneDef.Owner == "each_player")
            {
                foreach (var p in state.Players)
                    state.Zones[$"{zoneDef.Id}:{p.Id}"] =
                        new Zone($"{zoneDef.Id}:{p.Id}", zoneDef.Type, p.Id, zoneDef.Visibility);
            }
            else
            {
                state.Zones[zoneDef.Id] =
                    new Zone(zoneDef.Id, zoneDef.Type, zoneDef.Owner, zoneDef.Visibility);
            }
        }

        // Logic-owned zones
        foreach (var p in state.Players)
            state.Zones[$"play:{p.Id}"] = new Zone($"play:{p.Id}", "spread", p.Id, "all");

        state.Zones["pot"] = new Zone("pot", "pile", null, "count_only");

        // Build, shuffle, and deal
        var deck = DeckBuilder.Build(state.Definition.DeckType);
        DeckBuilder.Shuffle(deck);

        for (int i = 0; i < deck.Count; i++)
            state.Zones[$"hand:{state.Players[i % state.Players.Count].Id}"].Add(deck[i]);

        // Initial phase
        state.CurrentPhaseId = "ready";
        state.CurrentPlayerIndex = 0;
        state.Metadata["status"] = "Tap to flip!";
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state) =>
        state.CurrentPhaseId == "game_over"
            ? (IReadOnlyList<GameAction>)[]
            : [new GameAction("tap")];

    public void Apply(GameState state, GameAction action)
    {
        if (action.Type != "tap") return;

        switch (state.CurrentPhaseId)
        {
            case "ready":  FlipBattle(state);       break;
            case "result": CollectAndContinue(state); break;
        }
    }

    // ── Battle flip ───────────────────────────────────────────────────────────

    private void FlipBattle(GameState state)
    {
        if (AnyHandEmpty(state)) { EndGame(state); return; }

        foreach (var p in state.Players)
            MoveTopCard(Hand(state, p), Play(state, p));

        CompareAndResolve(state);
    }

    // ── Compare (handles chained wars iteratively) ────────────────────────────

    private void CompareAndResolve(GameState state)
    {
        while (true)
        {
            var p0    = state.Players[0];
            var p1    = state.Players[1];
            var card0 = Play(state, p0).TopCard;
            var card1 = Play(state, p1).TopCard;

            if (card0 is null || card1 is null) { EndGame(state); return; }

            int r0 = (int)card0.Rank;
            int r1 = (int)card1.Rank;

            if (r0 > r1) { SetResult(state, p0.Id, "You win this round!");     return; }
            if (r1 > r0) { SetResult(state, p1.Id, "Opponent wins this round."); return; }

            // ── Tie ──
            if (_tieSplit)
            {
                // Each takes back their own card
                Hand(state, p0).Add(Play(state, p0).Draw()!);
                Hand(state, p1).Add(Play(state, p1).Draw()!);
                state.CurrentPhaseId = "result";
                state.Metadata["status"] = "Tie! Cards returned.\nTap to continue.";
                state.Metadata["last_winner"] = "";
                return;
            }

            // ── War ──
            // Need WarFaceDownCount + 1 cards each to continue
            if (state.Players.Any(p => Hand(state, p).Count < WarFaceDownCount + 1))
            {
                EndGame(state); return;
            }

            // Move tied play cards into pot, deal 3 face-down + 1 face-up per player
            var pot = Pot(state);
            foreach (var p in state.Players)
            {
                pot.Add(Play(state, p).Draw()!);
                for (int i = 0; i < WarFaceDownCount; i++)
                    pot.Add(Hand(state, p).Draw()!);
                MoveTopCard(Hand(state, p), Play(state, p));
            }
            // loop → compare the new face-up cards
        }
    }

    // ── Collect after result ──────────────────────────────────────────────────

    private void CollectAndContinue(GameState state)
    {
        string winnerId = state.Metadata.GetValueOrDefault("last_winner", "");

        if (!string.IsNullOrEmpty(winnerId))
        {
            var winnerHand = state.Zones[$"hand:{winnerId}"];
            CollectInto(state, winnerHand);
        }
        // Tie-split: cards already returned; pot/play zones are already empty.

        if (CheckGameOver(state)) return;

        state.CurrentPhaseId = "ready";
        state.RoundNumber++;
        state.Metadata["status"] = "Tap to flip!";
    }

    private void CollectInto(GameState state, Zone destination)
    {
        // Play cards first, then pot
        foreach (var p in state.Players)
            DrainInto(Play(state, p), destination);

        DrainInto(Pot(state), destination);
    }

    // ── Game-over checks ──────────────────────────────────────────────────────

    private bool CheckGameOver(GameState state)
    {
        var h0 = Hand(state, state.Players[0]);
        var h1 = Hand(state, state.Players[1]);

        if (h0.Count == 0) { SetGameOver(state, state.Players[1].Id, "Opponent wins the game!"); return true; }
        if (h1.Count == 0) { SetGameOver(state, state.Players[0].Id, "You win the game!"); return true; }
        return false;
    }

    private bool AnyHandEmpty(GameState state)
        => state.Players.Any(p => Hand(state, p).Count == 0);

    private void EndGame(GameState state)
    {
        int c0 = CardCount(state, state.Players[0]);
        int c1 = CardCount(state, state.Players[1]);

        if (c0 > c1)       SetGameOver(state, state.Players[0].Id, "You win the game!");
        else if (c1 > c0)  SetGameOver(state, state.Players[1].Id, "Opponent wins the game!");
        else               SetGameOver(state, "",                   "It's a draw!");
    }

    private void SetGameOver(GameState state, string winnerId, string msg)
    {
        state.CurrentPhaseId = "game_over";
        state.Metadata["last_winner"] = winnerId;
        state.Metadata["status"] = msg;
    }

    // ── IGameLogic ────────────────────────────────────────────────────────────

    public bool IsGameOver(GameState state) => state.CurrentPhaseId == "game_over";

    public string GetStatusText(GameState state)
        => state.Metadata.GetValueOrDefault("status", "");

    // ── Private helpers ───────────────────────────────────────────────────────

    private void SetResult(GameState state, string winnerId, string msg)
    {
        var pot = Pot(state);
        int potSize = pot.Count + state.Players.Count; // pot + the two play cards
        string extra = potSize > 2 ? $" ({potSize} cards)" : "";
        state.Metadata["last_winner"] = winnerId;
        state.Metadata["status"] = msg + extra + "\nTap to collect.";
        state.CurrentPhaseId = "result";
    }

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
        while (from.Count > 0)
            to.Add(from.Draw()!);
    }

    private static int CardCount(GameState s, Player p)
        => Hand(s, p).Count + Play(s, p).Count;
}
