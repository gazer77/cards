using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for a Blackjack round (N human players vs dealer).
///
/// Phase definition parameters:
///   dealer_hits_soft — highest soft-hand value the dealer must hit (default 16 = S17).
///                      Set to 17 to apply the H17 rule (dealer hits soft 17).
///
/// OnGameStart performs the initial deal so the animation layer sees the
/// correct <see cref="GameState.LastDealResult"/> during initialization.
///
/// State metadata:
///   bj_state         — "player_turn" | "dealer_turn" | "collecting"
///   status / sub     — display text
/// </summary>
public sealed class BlackjackRoundHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly bool   _hitSoft17;

    public BlackjackRoundHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId = nextPhaseId;
        int hitThreshold = GetInt(def, "dealer_hits_soft") ?? 16;
        _hitSoft17 = hitThreshold >= 17;
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public void OnGameStart(GameState state) => DealHand(state);

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        string bjState = state.Metadata.GetValueOrDefault("bj_state", "player_turn");
        return bjState switch
        {
            "player_turn" => GetPlayerActions(state),
            _             => [new GameAction("tap")],  // dealer_turn + collecting
        };
    }

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
        => state.Metadata.GetValueOrDefault("bj_state") == "dealer_turn"
            ? TimeSpan.FromMilliseconds(650)
            : null;

    public void Apply(GameState state, GameAction action)
    {
        string bjState = state.Metadata.GetValueOrDefault("bj_state", "player_turn");
        switch (bjState)
        {
            case "player_turn":
                ApplyPlayerAction(state, action);
                break;
            case "dealer_turn":
                DealerStep(state);
                break;
            case "collecting":
                CollectAndContinue(state);
                break;
        }
    }

    // ── Player turn ───────────────────────────────────────────────────────────

    private static IReadOnlyList<GameAction> GetPlayerActions(GameState state)
    {
        var hand = CurrentPlayerHand(state);
        if (hand.Count == 2)
            return [new GameAction("hit", Label: "Hit"),
                    new GameAction("stand", Label: "Stand"),
                    new GameAction("double_down", Label: "Double")];
        return [new GameAction("hit", Label: "Hit"),
                new GameAction("stand", Label: "Stand")];
    }

    private void ApplyPlayerAction(GameState state, GameAction action)
    {
        switch (action.Type)
        {
            case "hit":
                DealFaceUp(state, state.Players[state.CurrentPlayerIndex].Id);
                int val = HandValue(CurrentPlayerHand(state));
                if (val >= 21)
                    AdvancePlayerOrDealer(state);
                else
                    UpdatePlayerStatus(state);
                break;

            case "double_down":
                DealFaceUp(state, state.Players[state.CurrentPlayerIndex].Id);
                AdvancePlayerOrDealer(state); // forced stand after one card
                break;

            case "stand":
                AdvancePlayerOrDealer(state);
                break;
        }
    }

    /// <summary>
    /// Moves to the next human player, or reveals hole card and enters dealer_turn.
    /// </summary>
    private void AdvancePlayerOrDealer(GameState state)
    {
        int dealerIdx = state.Players.Count - 1;
        int next      = state.CurrentPlayerIndex + 1;

        if (next < dealerIdx)
        {
            state.CurrentPlayerIndex = next;
            UpdatePlayerStatus(state);
        }
        else
        {
            RevealHoleCard(state);
            EnterDealerTurn(state);
        }
    }

    private void EnterDealerTurn(GameState state)
    {
        state.Metadata["bj_state"] = "dealer_turn";
        UpdateDealerStatus(state);
    }

    // ── Dealer turn ───────────────────────────────────────────────────────────

    private void DealerStep(GameState state)
    {
        var (val, isSoft) = HandInfo(DealerHand(state));

        if (DealerIsDone(val, isSoft))
        {
            FinishHand(state);
            return;
        }

        DealFaceUp(state, state.Players[^1].Id);
        var (newVal, newSoft) = HandInfo(DealerHand(state));

        if (DealerIsDone(newVal, newSoft))
            FinishHand(state);
        else
            UpdateDealerStatus(state);
    }

    // ── Hand resolution ───────────────────────────────────────────────────────

    private static void FinishHand(GameState state)
    {
        // Evaluate first human player vs dealer (extend for multi-player later)
        int pv = HandValue(state.Zones[$"hand:{state.Players[0].Id}"]);
        int dv = HandValue(DealerHand(state));

        bool playerBJ = pv == 21 && state.Zones[$"hand:{state.Players[0].Id}"].Count == 2;
        bool dealerBJ = dv == 21 && DealerHand(state).Count == 2;

        string result = (playerBJ, dealerBJ, pv, dv) switch
        {
            (true,  false, _, _) => "Blackjack! You win!",
            (false, true,  _, _) => "Dealer Blackjack. Dealer wins.",
            (true,  true,  _, _) => "Both Blackjack — Push.",
            _ when pv > 21       => "Bust! Dealer wins.",
            _ when dv > 21       => "Dealer busts! You win!",
            _ when pv > dv       => "You win!",
            _ when dv > pv       => "Dealer wins.",
            _                    => "Push — it's a tie.",
        };

        state.Metadata["status"]   = result + "\nTap to continue.";
        state.Metadata["sub"]      = $"You: {pv} | Dealer: {dv}";
        state.Metadata["bj_state"] = "collecting";
    }

    private void CollectAndContinue(GameState state)
    {
        state.RoundNumber++;

        var win = WinConditionEngine.Instance.Check(state);
        if (win is not null)
        {
            state.Metadata["status"]      = win.StatusMessage;
            state.Metadata["last_winner"] = win.WinnerId ?? "";
            state.Metadata.Remove("bj_state");
            state.Metadata.Remove("sub");
            state.CurrentPhaseId = "game_over";
            return;
        }

        // Spent hands go to the discard tray, not out of existence. Clearing them
        // destroyed several cards a round, which the deck rebuild below then quietly
        // replaced — so the shoe stayed plausible while the pack was being consumed.
        var discard = state.FindZone("discard");

        foreach (var p in state.Players)
        {
            if (!state.Zones.TryGetValue($"hand:{p.Id}", out var hand)) continue;

            foreach (var card in hand.Cards.ToList())
            {
                hand.Remove(card);
                card.IsFaceUp = true;

                // Without a discard zone the cards go back to the bottom of the deck.
                // Less like a real table, but a game that loses cards is worse.
                (discard ?? state.Zones["deck"]).Add(card);
            }
        }

        state.Metadata.Remove("bj_state");
        state.Metadata.Remove("sub");
        DealHand(state);
    }

    // ── Dealing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a full blackjack deal: human players face-up, dealer gets one
    /// face-up card and one face-down hole card.
    /// Sets bj_state = "player_turn" (or "dealer_turn" on immediate blackjack).
    /// </summary>
    private void DealHand(GameState state)
    {
        var deck = state.Zones["deck"];

        int needed = state.Players.Count * 2 + 4;

        // Running low: reshuffle the discard tray back in, the way a real shoe is
        // recycled. Rebuilding from scratch instead was what hid the cards being
        // destroyed at the end of every round.
        if (deck.Count < needed && state.FindZone("discard") is { Count: > 0 } discard)
        {
            foreach (var card in discard.Cards.ToList())
            {
                discard.Remove(card);
                card.IsFaceUp = false;
                deck.Add(card);
            }
            DeckBuilder.Shuffle(deck.Cards, state.Rng);
        }

        // Still short — the first deal, where the engine leaves the deck empty.
        if (deck.Count < needed)
        {
            deck.Clear();
            var cards = DeckBuilder.Build(state.Definition, state.Players.Count);
            DeckBuilder.Shuffle(cards, state.Rng);
            foreach (var c in cards) deck.Add(c);
        }

        int dealerIdx  = state.Players.Count - 1;
        string dealerId = state.Players[dealerIdx].Id;

        // Pass 1: one face-up card to every seat (including dealer)
        for (int i = 0; i <= dealerIdx; i++) DealFaceUp(state, state.Players[i].Id);
        // Pass 2: face-up for humans, face-down hole card for dealer
        for (int i = 0; i < dealerIdx; i++) DealFaceUp(state, state.Players[i].Id);
        DealFaceDown(state, dealerId);

        // Record deal animation
        var byPlayer = new Dictionary<int, List<int>>();
        for (int i = 0; i <= dealerIdx; i++)
            byPlayer[i] = state.Zones[$"hand:{state.Players[i].Id}"].Cards.Select(c => c.Uid).ToList();

        var steps = new List<(int, int)>();
        for (int i = 0; i <= dealerIdx; i++) steps.Add((i, 1));
        for (int i = 0; i <= dealerIdx; i++) steps.Add((i, 1));
        StandardDealEngine.RecordResult(state, byPlayer, steps, animDelayMs: 220);

        state.CurrentPlayerIndex = 0;
        state.Metadata["bj_state"] = "player_turn";

        // Immediate blackjack for player 0 → jump to dealer_turn
        if (HandValue(state.Zones[$"hand:{state.Players[0].Id}"]) == 21)
        {
            RevealHoleCard(state);
            EnterDealerTurn(state);
        }
        else
        {
            UpdatePlayerStatus(state);
        }
    }

    /// <summary>
    /// Takes a card from the shoe, reshuffling the discard tray back in when it runs
    /// dry.
    ///
    /// Drawing straight from the deck returned null on an empty shoe and the caller
    /// silently gave up, so a hit added no card, the hand never changed value, and the
    /// seat could never finish its turn. Six seats get through a single pack inside one
    /// round, which is why only the six-player table stalled.
    /// </summary>
    private static Card? DrawFromShoe(GameState state)
    {
        var deck = state.Zones["deck"];

        if (deck.IsEmpty && state.FindZone("discard") is { Count: > 0 } discard)
        {
            foreach (var card in discard.Cards.ToList())
            {
                discard.Remove(card);
                card.IsFaceUp = false;
                deck.Add(card);
            }
            DeckBuilder.Shuffle(deck.Cards, state.Rng);
        }

        return deck.Draw();
    }

    private static void DealFaceUp(GameState state, string playerId)
    {
        var card = DrawFromShoe(state);
        if (card is null) return;
        card.IsFaceUp = true;
        state.Zones[$"hand:{playerId}"].Add(card);
    }

    private static void DealFaceDown(GameState state, string playerId)
    {
        var card = DrawFromShoe(state);
        if (card is null) return;
        card.IsFaceUp = false;
        state.Zones[$"hand:{playerId}"].Add(card);
    }

    private static void RevealHoleCard(GameState state)
    {
        foreach (var c in DealerHand(state).Cards) c.IsFaceUp = true;
    }

    // ── Status helpers ────────────────────────────────────────────────────────

    private static void UpdatePlayerStatus(GameState state)
    {
        int pv = HandValue(CurrentPlayerHand(state));
        int dv = VisibleDealerValue(DealerHand(state));
        state.Metadata["status"] = $"You: {pv}  |  Dealer shows: {dv}";
        state.Metadata.Remove("sub");
    }

    private void UpdateDealerStatus(GameState state)
    {
        var (val, isSoft) = HandInfo(DealerHand(state));
        string soft   = isSoft ? "soft " : "";
        string prompt = DealerIsDone(val, isSoft) ? "Tap to see result." : "Tap for next card.";
        state.Metadata["status"] = $"Dealer: {soft}{val} — {prompt}";
        state.Metadata.Remove("sub");
    }

    // ── Zone accessors ────────────────────────────────────────────────────────

    private static Zone CurrentPlayerHand(GameState state)
        => state.Zones[$"hand:{state.Players[state.CurrentPlayerIndex].Id}"];

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

    private bool DealerIsDone(int val, bool isSoft)
    {
        if (val > 21) return true;
        if (val > 17) return true;
        if (val == 17 && (!isSoft || !_hitSoft17)) return true;
        return false;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static int? GetInt(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }
}
