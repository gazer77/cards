using Cards.Engine;

namespace Cards.Logic;

/// <summary>
/// Implements the rules of Blackjack (single player vs dealer, one hand per game).
///
/// Zones:
///   hand:player0  — player's cards (all face-up)
///   hand:player1  — dealer's cards (hole card face-down until dealer turn)
///   deck          — the shoe
///
/// Phases:
///   player_turn   — player chooses Hit or Stand
///   dealer_turn   — hole card revealed; each tap deals one more dealer card
///   game_over     — hand resolved; triggers the game-over overlay
/// </summary>
public sealed class BlackjackLogic : IGameLogic
{
    private bool _dealerHitsHard17;

    // ── Initialize ────────────────────────────────────────────────────────────

    public void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules)
    {
        _dealerHitsHard17 = enabledHouseRules.Contains("dealer_hits_hard_17");

        state.Players.Add(new Player("player0", "You"));
        state.Players.Add(new Player("player1", "Dealer"));

        state.Zones["hand:player0"] = new Zone("hand:player0", "hand", "player0", "all");
        state.Zones["hand:player1"] = new Zone("hand:player1", "hand", "player1", "all");
        state.Zones["deck"]         = new Zone("deck",         "deck", null,       "none");

        var deck = DeckBuilder.Build(state.Definition.DeckType);
        DeckBuilder.Shuffle(deck);
        foreach (var c in deck) state.Zones["deck"].Add(c);

        // Deal: player (up), dealer (up), player (up), dealer hole (down).
        // The sequence is game-specific and can't be expressed as a uniform clockwise
        // deal, so we handle it here and record the result for the animation layer.
        DealFaceUp(state, "player0");
        DealFaceUp(state, "player1");
        DealFaceUp(state, "player0");
        DealFaceDown(state, "player1");

        StandardDealEngine.RecordResult(
            state,
            byPlayer: new Dictionary<int, List<string>>
            {
                [0] = PlayerHand(state).Cards.Select(c => c.Id).ToList(),
                [1] = DealerHand(state).Cards.Select(c => c.Id).ToList(),
            },
            steps:    [(0, 1), (1, 1), (0, 1), (1, 1)],
            animDelayMs: 220);

        state.CurrentPlayerIndex = 0;

        // Immediate blackjack: reveal hole card and go to dealer_turn for the player to see
        if (HandValue(PlayerHand(state)) == 21)
        {
            RevealHoleCard(state);
            EnterDealerTurn(state, playerJustStood: true);
            return;
        }

        state.CurrentPhaseId = "player_turn";
        UpdatePlayerTurnStatus(state);
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state) =>
        state.CurrentPhaseId switch
        {
            "player_turn" when PlayerHand(state).Count == 2 =>
            [
                new GameAction("hit",         Label: "Hit"),
                new GameAction("stand",       Label: "Stand"),
                new GameAction("double_down", Label: "Double"),
            ],
            "player_turn" => [new GameAction("hit", Label: "Hit"), new GameAction("stand", Label: "Stand")],
            "dealer_turn" => [new GameAction("tap")],
            _             => (IReadOnlyList<GameAction>)[],
        };

    public void Apply(GameState state, GameAction action)
    {
        switch (state.CurrentPhaseId)
        {
            case "player_turn":
                if (action.Type == "hit")         PlayerHit(state);
                if (action.Type == "stand")       PlayerStand(state);
                if (action.Type == "double_down") PlayerDoubleDown(state);
                break;
            case "dealer_turn":
                DealerStep(state);
                break;
        }
    }

    // ── Player actions ────────────────────────────────────────────────────────

    private void PlayerHit(GameState state)
    {
        DealFaceUp(state, "player0");
        int val = HandValue(PlayerHand(state));

        if (val > 21)
        {
            // Bust — reveal hole card but go straight to game over (no need to draw dealer cards)
            RevealHoleCard(state);
            state.Metadata["status"] = $"Bust! You had {val}. Dealer wins.";
            state.Metadata["sub"]    = $"You: {val} | Dealer: {HandValue(DealerHand(state))}";
            state.CurrentPhaseId     = "game_over";
            return;
        }

        if (val == 21)
        {
            PlayerStand(state); // auto-stand at 21
            return;
        }

        UpdatePlayerTurnStatus(state);
    }

    private void PlayerDoubleDown(GameState state)
    {
        DealFaceUp(state, "player0");
        int val = HandValue(PlayerHand(state));

        if (val > 21)
        {
            RevealHoleCard(state);
            state.Metadata["status"] = $"Bust on double! You had {val}. Dealer wins.";
            state.Metadata["sub"]    = $"You: {val} | Dealer: {HandValue(DealerHand(state))}";
            state.CurrentPhaseId     = "game_over";
            return;
        }

        PlayerStand(state); // forced stand after one card
    }

    private void PlayerStand(GameState state)
    {
        RevealHoleCard(state);
        EnterDealerTurn(state, playerJustStood: true);
    }

    // ── Dealer turn (one card per tap) ────────────────────────────────────────

    /// <summary>
    /// Called after revealing the hole card. Sets up dealer_turn status so the player
    /// can see the dealer's starting hand before any additional cards are dealt.
    /// </summary>
    private void EnterDealerTurn(GameState state, bool playerJustStood)
    {
        state.CurrentPhaseId = "dealer_turn";
        UpdateDealerTurnStatus(state);
    }

    /// <summary>
    /// Called on each tap during dealer_turn.
    /// If the dealer is already done, resolves the hand.
    /// Otherwise deals one card; if the dealer is now done, resolves the hand.
    /// </summary>
    private void DealerStep(GameState state)
    {
        var (val, isSoft) = HandInfo(DealerHand(state));

        if (DealerIsDone(val, isSoft))
        {
            // Dealer was standing already — player just saw the hole-card reveal; resolve now.
            FinishHand(state);
            return;
        }

        DealFaceUp(state, "player1");

        var (newVal, newSoft) = HandInfo(DealerHand(state));
        if (DealerIsDone(newVal, newSoft))
            FinishHand(state);
        else
            UpdateDealerTurnStatus(state);
    }

    // ── Resolve hand ──────────────────────────────────────────────────────────

    private static void FinishHand(GameState state)
    {
        int pv = HandValue(PlayerHand(state));
        int dv = HandValue(DealerHand(state));

        bool playerBJ = pv == 21 && PlayerHand(state).Count == 2;
        bool dealerBJ = dv == 21 && DealerHand(state).Count == 2;

        string result;
        if      (playerBJ && !dealerBJ) result = "Blackjack! You win!";
        else if (dealerBJ && !playerBJ) result = "Dealer Blackjack. Dealer wins.";
        else if (playerBJ && dealerBJ)  result = "Both Blackjack — Push.";
        else if (pv > 21)               result = "Bust! Dealer wins.";
        else if (dv > 21)               result = "Dealer busts! You win!";
        else if (pv > dv)               result = "You win!";
        else if (dv > pv)               result = "Dealer wins.";
        else                            result = "Push — it's a tie.";

        state.Metadata["status"] = result;
        state.Metadata["sub"]    = $"You: {pv} | Dealer: {dv}";
        state.CurrentPhaseId     = "game_over";
    }

    // ── IGameLogic ────────────────────────────────────────────────────────────

    public bool IsGameOver(GameState state) => state.CurrentPhaseId == "game_over";

    public string GetStatusText(GameState state)
        => state.Metadata.GetValueOrDefault("status", "");

    /// <summary>Dealer turn auto-advances so the player watches cards appear one at a time.</summary>
    public TimeSpan? GetAutoAdvanceDelay(GameState state) =>
        state.CurrentPhaseId == "dealer_turn" ? TimeSpan.FromMilliseconds(650) : null;

    // ── Status helpers ────────────────────────────────────────────────────────

    private static void UpdatePlayerTurnStatus(GameState state)
    {
        int pv = HandValue(PlayerHand(state));
        int dv = VisibleDealerValue(DealerHand(state));
        state.Metadata["status"] = $"You: {pv}  |  Dealer shows: {dv}";
    }

    private void UpdateDealerTurnStatus(GameState state)
    {
        var (val, isSoft) = HandInfo(DealerHand(state));
        string soft   = isSoft ? "soft " : "";
        string prompt = DealerIsDone(val, isSoft) ? "Tap to see result." : "Tap for next card.";
        state.Metadata["status"] = $"Dealer: {soft}{val} — {prompt}";
    }

    // ── Dealing helpers ───────────────────────────────────────────────────────

    private static void DealFaceUp(GameState state, string playerId)
    {
        var card = state.Zones["deck"].Draw();
        if (card is null) return;
        card.IsFaceUp = true;
        state.Zones[$"hand:{playerId}"].Add(card);
    }

    private static void DealFaceDown(GameState state, string playerId)
    {
        var card = state.Zones["deck"].Draw();
        if (card is null) return;
        card.IsFaceUp = false;
        state.Zones[$"hand:{playerId}"].Add(card);
    }

    private static void RevealHoleCard(GameState state)
    {
        foreach (var c in DealerHand(state).Cards) c.IsFaceUp = true;
    }

    // ── Zone accessors ────────────────────────────────────────────────────────

    private static Zone PlayerHand(GameState state) => state.Zones["hand:player0"];
    private static Zone DealerHand(GameState state) => state.Zones["hand:player1"];

    // ── Hand value ────────────────────────────────────────────────────────────

    private static int HandValue(Zone hand)
    {
        int total = 0, aces = 0;
        foreach (var c in hand.Cards)
        {
            if      (c.Rank == Rank.Ace)  { aces++; total += 11; }
            else if (c.Rank >= Rank.Jack) total += 10;
            else                           total += (int)c.Rank;
        }
        while (total > 21 && aces > 0) { total -= 10; aces--; }
        return total;
    }

    /// <summary>Returns (bestValue, isSoft) where isSoft means an Ace still counts as 11.</summary>
    private static (int value, bool isSoft) HandInfo(Zone hand)
    {
        int total = 0, aces = 0;
        foreach (var c in hand.Cards)
        {
            if      (c.Rank == Rank.Ace)  { aces++; total += 11; }
            else if (c.Rank >= Rank.Jack) total += 10;
            else                           total += (int)c.Rank;
        }
        int softAces = aces;
        while (total > 21 && softAces > 0) { total -= 10; softAces--; }
        return (total, softAces > 0);
    }

    /// <summary>Sum only the face-up cards (for display during player_turn).</summary>
    private static int VisibleDealerValue(Zone hand)
    {
        int total = 0, aces = 0;
        foreach (var c in hand.Cards.Where(c => c.IsFaceUp))
        {
            if      (c.Rank == Rank.Ace)  { aces++; total += 11; }
            else if (c.Rank >= Rank.Jack) total += 10;
            else                           total += (int)c.Rank;
        }
        while (total > 21 && aces > 0) { total -= 10; aces--; }
        return total;
    }

    /// <summary>True when the dealer must stop drawing cards.</summary>
    private bool DealerIsDone(int val, bool isSoft)
    {
        if (val > 21) return true;  // busted — done (losing)
        if (val > 17) return true;  // stands on 18+
        if (val == 17 && !_dealerHitsHard17) return true;  // S17: stand on any 17
        // _dealerHitsHard17: hit all 17s — dealer only stands on 18+
        return false;
    }
}
