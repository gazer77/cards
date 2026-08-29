using Cards.Engine;

namespace Cards.Logic;

/// <summary>
/// Implements the rules of Blackjack (N human players vs dealer, one hand per game).
///
/// Zones:
///   hand:player0 … hand:player{N-1}  — human players' cards (all face-up)
///   hand:player{N}                   — dealer's cards (hole card face-down until dealer turn)
///   deck                             — the shoe
///
/// Phases:
///   player_turn   — current human player chooses Hit, Stand, or Double
///   dealer_turn   — hole card revealed; dealer auto-plays one card at a time
///   game_over     — hand resolved; triggers the game-over overlay
/// </summary>
public sealed class BlackjackLogic : GameLogicBase
{
    private bool _dealerHitsHard17;

    // ── Initialize ────────────────────────────────────────────────────────────

    public override void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules)
    {
        _dealerHitsHard17 = enabledHouseRules.Contains("dealer_hits_hard_17");

        // Always create playerCount human players + 1 dealer (the extra player).
        SetupEngine.Instance.Setup(state, playerCount + 1, enabledHouseRules);

        var deck = DeckBuilder.Build(state.Definition.DeckType);
        DeckBuilder.Shuffle(deck);
        foreach (var c in deck) state.Zones["deck"].Add(c);

        int dealerIdx  = state.Players.Count - 1;
        string dealerId = state.Players[dealerIdx].Id;

        // Deal one card face-up to each human player, then one face-up to dealer,
        // then a second face-up to each human, then the hole card face-down to dealer.
        for (int i = 0; i < dealerIdx; i++) DealFaceUp(state, state.Players[i].Id);
        DealFaceUp(state, dealerId);
        for (int i = 0; i < dealerIdx; i++) DealFaceUp(state, state.Players[i].Id);
        DealFaceDown(state, dealerId);

        // Record deal result for the animation layer.
        var byPlayer = new Dictionary<int, List<int>>();
        for (int i = 0; i <= dealerIdx; i++)
            byPlayer[i] = state.Zones[$"hand:{state.Players[i].Id}"].Cards.Select(c => c.Uid).ToList();

        var steps = new List<(int playerIdx, int count)>();
        for (int i = 0; i <= dealerIdx; i++) steps.Add((i, 1));
        for (int i = 0; i <= dealerIdx; i++) steps.Add((i, 1));

        StandardDealEngine.RecordResult(state, byPlayer, steps, animDelayMs: 220);

        state.CurrentPlayerIndex = 0;

        RegisterPhase("player_turn", new PlayerTurnHandler(this));
        RegisterPhase("dealer_turn", new DealerTurnHandler(this));

        // Immediate blackjack for the first player: reveal hole card and enter dealer_turn
        if (HandValue(PlayerHand(state)) == 21)
        {
            RevealHoleCard(state);
            EnterDealerTurn(state, playerJustStood: true);
            return;
        }

        state.CurrentPhaseId = "player_turn";
        UpdatePlayerTurnStatus(state);
    }

    // ── Phase handlers ────────────────────────────────────────────────────────

    private sealed class PlayerTurnHandler(BlackjackLogic logic) : IPhaseHandler
    {
        public IReadOnlyList<GameAction> GetValidActions(GameState state) =>
            BlackjackLogic.PlayerHand(state).Count == 2
            ? [new GameAction("hit", Label: "Hit"), new GameAction("stand", Label: "Stand"), new GameAction("double_down", Label: "Double")]
            : [new GameAction("hit", Label: "Hit"), new GameAction("stand", Label: "Stand")];

        public void Apply(GameState state, GameAction action)
        {
            if (action.Type == "hit")         logic.PlayerHit(state);
            if (action.Type == "stand")       logic.PlayerStand(state);
            if (action.Type == "double_down") logic.PlayerDoubleDown(state);
        }
    }

    private sealed class DealerTurnHandler(BlackjackLogic logic) : IPhaseHandler
    {
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];
        public void Apply(GameState state, GameAction action)         => logic.DealerStep(state);
        public TimeSpan? GetAutoAdvanceDelay(GameState _)             => TimeSpan.FromMilliseconds(650);
    }

    // ── Player actions ────────────────────────────────────────────────────────

    private void PlayerHit(GameState state)
    {
        DealFaceUp(state, state.Players[state.CurrentPlayerIndex].Id);
        int val = HandValue(PlayerHand(state));

        if (val > 21)
        {
            // Bust — advance to next player or resolve hand
            AdvanceToNextPlayerOrDealer(state, busted: true);
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
        DealFaceUp(state, state.Players[state.CurrentPlayerIndex].Id);
        int val = HandValue(PlayerHand(state));

        if (val > 21)
        {
            AdvanceToNextPlayerOrDealer(state, busted: true);
            return;
        }

        PlayerStand(state); // forced stand after one card
    }

    private void PlayerStand(GameState state)
        => AdvanceToNextPlayerOrDealer(state, busted: false);

    /// <summary>
    /// Moves to the next human player's turn, or to the dealer turn when all
    /// human players have acted.
    /// </summary>
    private void AdvanceToNextPlayerOrDealer(GameState state, bool busted)
    {
        int dealerIdx = state.Players.Count - 1;
        int next      = state.CurrentPlayerIndex + 1;

        if (next < dealerIdx)
        {
            // More human players to act
            state.CurrentPlayerIndex = next;
            UpdatePlayerTurnStatus(state);
        }
        else
        {
            // All human players done — reveal hole card and enter dealer turn
            RevealHoleCard(state);
            EnterDealerTurn(state, playerJustStood: !busted);
        }
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

        DealFaceUp(state, state.Players[^1].Id);

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

    /// <summary>Returns the current human player's hand.</summary>
    private static Zone PlayerHand(GameState state)
        => state.Zones[$"hand:{state.Players[state.CurrentPlayerIndex].Id}"];

    /// <summary>Returns the dealer's hand (always the last player).</summary>
    private static Zone DealerHand(GameState state)
        => state.Zones[$"hand:{state.Players[^1].Id}"];

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
