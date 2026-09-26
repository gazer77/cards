using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// A round of Blackjack: every player against the dealer, for chips.
///
/// Phase definition parameters:
///   dealer_hits_soft   — highest soft total the dealer must hit (16 = stands on soft 17,
///                        17 = hits soft 17). Default 16.
///   bet                — chips staked on each hand. Default 10.
///   blackjack_pays     — what a natural pays, as "3:2" or "6:5". Default "3:2".
///   allow_double_down  — double the stake on the first two cards, take one card, stand.
///   allow_split        — split a pair into two hands, each with its own stake.
///   allow_surrender    — give up the first two cards for half the stake back.
///   split_zone         — the zone a split hand plays in. Default "split".
///
/// The dealer is the table's role seat — the last seat, added after the players by the
/// definition's <c>roles</c> — and plays by fixed rules; it holds no chips.
///
/// State metadata:
///   bj_state              — "player_turn" | "dealer_turn" | "collecting"
///   bj_bet:{player}       — the stake on the seat's hand
///   bj_split_bet:{player} — the stake on its split hand, once split
///   bj_active:{player}    — "hand" or "split": which of the seat's hands is being played
///   bj_surrendered:{player}
///
/// Until this was written the game had no chips at all: its win condition ranked scores
/// nobody was ever awarded, only the first seat's hand was ever settled, and a natural in
/// that seat skipped every other seat's turn. Split, double and surrender were declared
/// by the definition and offered by nothing.
/// </summary>
public sealed class BlackjackRoundHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly bool   _hitSoft17;
    private readonly int    _bet;
    private readonly int    _payNumerator;
    private readonly int    _payDenominator;
    private readonly bool   _allowDouble;
    private readonly bool   _allowSplit;
    private readonly bool   _allowSurrender;
    private readonly string _splitZone;

    public BlackjackRoundHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId    = nextPhaseId;
        _hitSoft17      = (GetInt(def, "dealer_hits_soft") ?? 16) >= 17;
        _bet            = Math.Max(1, GetInt(def, "bet") ?? 10);
        _allowDouble    = GetBool(def, "allow_double_down") ?? true;
        _allowSplit     = GetBool(def, "allow_split") ?? true;
        _allowSurrender = GetBool(def, "allow_surrender") ?? false;
        _splitZone      = GetString(def, "split_zone") ?? "split";

        (_payNumerator, _payDenominator) = ParsePayout(GetString(def, "blackjack_pays") ?? "3:2");
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public void OnGameStart(GameState state) => DealHand(state);

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
        => state.Metadata.GetValueOrDefault("bj_state", "player_turn") == "player_turn"
            ? PlayerActions(state)
            : [new GameAction("tap")];   // dealer_turn + collecting: the table is the affordance

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
        => state.Metadata.GetValueOrDefault("bj_state") == "dealer_turn"
            ? TimeSpan.FromMilliseconds(650)
            : null;

    public void Apply(GameState state, GameAction action)
    {
        switch (state.Metadata.GetValueOrDefault("bj_state", "player_turn"))
        {
            case "player_turn": ApplyPlayerAction(state, action); break;
            case "dealer_turn": DealerStep(state);                break;
            case "collecting":  CollectAndContinue(state);        break;
        }
    }

    // ── Player turn ───────────────────────────────────────────────────────────

    /// <summary>
    /// What the seat to act may do with the hand in front of it. Double, split and
    /// surrender are the definition's to allow, and each is a first-decision move: once
    /// a third card is in the hand they are gone.
    /// </summary>
    private IReadOnlyList<GameAction> PlayerActions(GameState state)
    {
        var seat = state.CurrentPlayer.Id;
        var hand = ActiveHand(state, seat);

        var actions = new List<GameAction>
        {
            new("hit",   Label: GameText.Action(state, "hit", "Hit")),
            new("stand", Label: GameText.Action(state, "stand", "Stand")),
        };

        if (hand.Count != 2) return actions;

        bool onFirstHand = Active(state, seat) == "hand";
        bool hasSplit    = SplitHand(state, seat) is { Count: > 0 };

        if (_allowDouble)
            actions.Add(new("double_down", Label: GameText.Action(state, "double_down", "Double")));

        // A pair, on the seat's own first hand, and only once — a split hand does not
        // split again here.
        if (_allowSplit && onFirstHand && !hasSplit && SplitHand(state, seat) is not null
            && hand.Cards[0].Rank == hand.Cards[1].Rank)
            actions.Add(new("split", Label: GameText.Action(state, "split", "Split")));

        // Surrender is for the hand as dealt: not after a split, not after a hit.
        if (_allowSurrender && onFirstHand && !hasSplit)
            actions.Add(new("surrender", Label: GameText.Action(state, "surrender", "Surrender")));

        return actions;
    }

    private void ApplyPlayerAction(GameState state, GameAction action)
    {
        var seat = state.CurrentPlayer.Id;
        var hand = ActiveHand(state, seat);

        switch (action.Type)
        {
            case "hit":
                DealFaceUp(state, hand);
                if (HandValue(hand) >= 21) FinishActiveHand(state);
                else                       UpdatePlayerStatus(state);
                break;

            case "stand":
                FinishActiveHand(state);
                break;

            case "double_down" when _allowDouble && hand.Count == 2:
                // The stake doubles and the hand takes exactly one more card.
                string key = Active(state, seat) == "split" ? $"bj_split_bet:{seat}" : $"bj_bet:{seat}";
                state.Metadata[key] = (Stake(state, key) * 2).ToString();
                GameText.Log(state, "bj_doubled", "{player} doubled down", seat);
                DealFaceUp(state, hand);
                FinishActiveHand(state);
                break;

            case "split" when _allowSplit && hand.Count == 2 && SplitHand(state, seat) is { Count: 0 } split
                              && hand.Cards[0].Rank == hand.Cards[1].Rank:
                // The pair becomes two hands, each with its own stake and its own second
                // card, played one after the other.
                var moved = hand.Cards[1];
                hand.Remove(moved);
                split.Add(moved);
                state.Metadata[$"bj_split_bet:{seat}"] = Stake(state, $"bj_bet:{seat}").ToString();
                GameText.Log(state, "bj_split", "{player} split their {rank}s", seat,
                             ("rank", MeldRules.RankDisplayName(moved.Rank)));
                DealFaceUp(state, hand);
                DealFaceUp(state, split);
                if (HandValue(hand) >= 21) FinishActiveHand(state);
                else                       UpdatePlayerStatus(state);
                break;

            case "surrender" when _allowSurrender && hand.Count == 2 && Active(state, seat) == "hand":
                state.Metadata[$"bj_surrendered:{seat}"] = "true";
                GameText.Log(state, "bj_surrendered", "{player} surrendered", seat);
                AdvanceSeat(state);
                break;
        }
    }

    /// <summary>
    /// The hand being played is finished — stood, doubled, busted or at 21. A seat that
    /// split plays its second hand next; otherwise the turn passes on.
    /// </summary>
    private void FinishActiveHand(GameState state)
    {
        var seat = state.CurrentPlayer.Id;
        if (Active(state, seat) == "hand" && SplitHand(state, seat) is { Count: > 0 } split)
        {
            state.Metadata[$"bj_active:{seat}"] = "split";
            if (HandValue(split) >= 21) { AdvanceSeat(state); return; }
            UpdatePlayerStatus(state);
            return;
        }

        AdvanceSeat(state);
    }

    /// <summary>
    /// On to the next player who has a decision to make, or to the dealer. A natural has
    /// no decision to make, so it is passed over — and only that seat: a natural in the
    /// first seat used to send the round straight to the dealer, and every other player
    /// sat out the hand without being asked.
    /// </summary>
    private void AdvanceSeat(GameState state)
    {
        for (int i = state.CurrentPlayerIndex + 1; i < DealerIndex(state); i++)
        {
            if (IsNatural(Hand(state, state.Players[i].Id))) continue;

            state.CurrentPlayerIndex = i;
            UpdatePlayerStatus(state);
            return;
        }

        RevealHoleCard(state);
        EnterDealerTurn(state);
    }

    private void EnterDealerTurn(GameState state)
    {
        state.CurrentPlayerIndex = DealerIndex(state);
        state.Metadata["bj_state"] = "dealer_turn";

        // Nobody left standing to beat: every hand busted or surrendered, and the house
        // does not draw against an empty table.
        if (!Players(state).Any(p => HasLiveHand(state, p.Id)))
        {
            FinishRound(state);
            return;
        }

        UpdateDealerStatus(state);
    }

    // ── Dealer turn ───────────────────────────────────────────────────────────

    private void DealerStep(GameState state)
    {
        var dealer = DealerHand(state);
        if (DealerIsDone(dealer)) { FinishRound(state); return; }

        DealFaceUp(state, dealer);
        if (DealerIsDone(dealer)) FinishRound(state);
        else                      UpdateDealerStatus(state);
    }

    // ── Settlement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Settles every hand at the table against the dealer's, in chips. The first seat's
    /// result is the one on the status line — it belongs to the person at this screen —
    /// and every seat's is written to the log.
    /// </summary>
    private void FinishRound(GameState state)
    {
        var dealer   = DealerHand(state);
        int dv       = HandValue(dealer);
        bool dealerBJ = IsNatural(dealer);

        var linesBy = new Dictionary<string, List<string>>();
        foreach (var p in Players(state))
        {
            int net = 0;
            var lines = new List<string>();

            if (state.Metadata.ContainsKey($"bj_surrendered:{p.Id}"))
            {
                int lost = Stake(state, $"bj_bet:{p.Id}") / 2;
                net -= lost;
                lines.Add(GameText.Message(state, "bj_result_surrender",
                    "{player} surrendered — {amount} back", p.Id, ("amount", Stake(state, $"bj_bet:{p.Id}") - lost)));
            }
            else
            {
                foreach (var (hand, stakeKey, canBeNatural) in HandsOf(state, p.Id))
                {
                    var (delta, line) = Settle(state, p.Id, hand, Stake(state, stakeKey), canBeNatural, dv, dealerBJ);
                    net += delta;
                    lines.Add(line);
                }
            }

            state.AddScore(p.Id, net);
            foreach (var line in lines) state.GameLog.Add(line);
            linesBy[p.Id] = lines;
        }

        // Each seat reads its own results, in its own words; seat 0 reads what it always did.
        string tapOn = GameText.Message(state, "bj_continue", "Tap to continue.");
        state.Metadata["status"] = GameText.PerViewer(state, viewer =>
            (viewer is not null && linesBy.TryGetValue(viewer, out var own)
                ? string.Join("  ·  ", own.Select(l => GameText.Render(state, l, viewer)))
                : "") + "\n" + tapOn);
        state.Metadata["sub"]      = GameText.Message(state, "bj_dealer_had", "Dealer: {value}", values: ("value", dv));
        state.Metadata["bj_state"] = "collecting";
    }

    /// <summary>What one hand wins or loses, and the sentence that says so.</summary>
    private (int Delta, string Line) Settle(
        GameState state, string seat, Zone hand, int stake, bool canBeNatural, int dv, bool dealerBJ)
    {
        int  pv      = HandValue(hand);
        bool natural = canBeNatural && IsNatural(hand);

        // A natural pays the declared odds, rounded down, unless the dealer has one too.
        if (natural && !dealerBJ)
        {
            int won = stake * _payNumerator / _payDenominator;
            return (won, GameText.Message(state, "bj_result_natural", "{player}: Blackjack! +{amount}",
                                          seat, ("amount", won)));
        }
        if (natural && dealerBJ)
            return (0, GameText.Message(state, "bj_result_push", "{player}: push", seat));
        if (pv > 21)
            return (-stake, GameText.Message(state, "bj_result_bust", "{player}: bust −{amount}",
                                             seat, ("amount", stake)));
        if (dealerBJ)
            return (-stake, GameText.Message(state, "bj_result_lose", "{player}: {value}, dealer wins −{amount}",
                                             seat, ("value", pv), ("amount", stake)));
        if (dv > 21 || pv > dv)
            return (stake, GameText.Message(state, "bj_result_win", "{player}: {value} wins +{amount}",
                                            seat, ("value", pv), ("amount", stake)));
        if (pv < dv)
            return (-stake, GameText.Message(state, "bj_result_lose", "{player}: {value}, dealer wins −{amount}",
                                             seat, ("value", pv), ("amount", stake)));

        return (0, GameText.Message(state, "bj_result_push", "{player}: push", seat));
    }

    private void CollectAndContinue(GameState state)
    {
        state.RoundNumber++;

        var win = WinConditionEngine.Instance.Check(state);
        if (win is not null)
        {
            state.Metadata["status"]      = win.StatusMessage;
            state.Metadata["last_winner"] = win.WinnerId ?? "";
            ClearRound(state);
            state.CurrentPhaseId = "game_over";
            return;
        }

        // Spent hands go to the discard tray, not out of existence. Clearing them
        // destroyed several cards a round, which the deck rebuild then quietly replaced
        // — so the shoe stayed plausible while the pack was being consumed.
        var tray = state.FindZone("discard") ?? state.Zones["deck"];
        foreach (var p in state.Players)
            foreach (var zone in new[] { Hand(state, p.Id), SplitHand(state, p.Id) })
            {
                if (zone is null) continue;
                foreach (var card in zone.Cards.ToList())
                {
                    zone.Remove(card);
                    card.IsFaceUp = true;
                    tray.Add(card);
                }
            }

        ClearRound(state);
        DealHand(state);
    }

    private static void ClearRound(GameState state)
    {
        foreach (var key in state.Metadata.Keys.Where(k => k.StartsWith("bj_")).ToList())
            state.Metadata.Remove(key);
        state.Metadata.Remove("sub");
    }

    // ── Dealing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Stakes every player's bet and deals: each seat a card face-up, round the table
    /// twice, the dealer's second card face-down.
    /// </summary>
    private void DealHand(GameState state)
    {
        var deck   = state.Zones["deck"];
        int needed = state.Players.Count * 2 + 4;

        // Running low: reshuffle the discard tray back in, the way a real shoe is
        // recycled.
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

        foreach (var p in Players(state))
        {
            state.Metadata[$"bj_bet:{p.Id}"]    = _bet.ToString();
            state.Metadata[$"bj_active:{p.Id}"] = "hand";
        }

        int dealerIdx = DealerIndex(state);
        for (int i = 0; i <= dealerIdx; i++) DealFaceUp(state, Hand(state, state.Players[i].Id));
        for (int i = 0; i <  dealerIdx; i++) DealFaceUp(state, Hand(state, state.Players[i].Id));
        DealFaceDown(state, DealerHand(state));

        var byPlayer = new Dictionary<int, List<int>>();
        for (int i = 0; i <= dealerIdx; i++)
            byPlayer[i] = Hand(state, state.Players[i].Id).Cards.Select(c => c.Uid).ToList();

        var steps = new List<(int, int)>();
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i <= dealerIdx; i++) steps.Add((i, 1));
        StandardDealEngine.RecordResult(state, byPlayer, steps, animDelayMs: 220);

        state.Metadata["bj_state"] = "player_turn";
        state.CurrentPlayerIndex   = -1;
        AdvanceSeat(state);   // the first seat with a decision to make, or the dealer
    }

    private static Card? DrawFromShoe(GameState state)
    {
        var deck = state.Zones["deck"];

        // Six seats get through a single pack inside one round; an empty shoe reshuffles
        // the tray rather than handing a hit no card, which stalled the whole table.
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

    private static void DealFaceUp(GameState state, Zone hand)
    {
        var card = DrawFromShoe(state);
        if (card is null) return;
        card.IsFaceUp = true;
        hand.Add(card);
    }

    private static void DealFaceDown(GameState state, Zone hand)
    {
        var card = DrawFromShoe(state);
        if (card is null) return;
        card.IsFaceUp = false;   // the dealer's hole card, genuinely face-down
        hand.Add(card);
    }

    private static void RevealHoleCard(GameState state)
    {
        foreach (var c in DealerHand(state).Cards) c.IsFaceUp = true;
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void UpdatePlayerStatus(GameState state)
    {
        var seat = state.CurrentPlayer.Id;
        int pv   = HandValue(ActiveHand(state, seat));
        int dv   = VisibleValue(DealerHand(state));

        state.Metadata["status"] = Active(state, seat) == "split"
            ? GameText.Message(state, "blackjack_split_hand",
                "{player}: second hand {value}  |  Dealer shows: {dealer}",
                seat, ("value", pv), ("dealer", dv))
            : GameText.Message(state, "blackjack_hand",
                "You: {value}  |  Dealer shows: {dealer}", values: [("value", pv), ("dealer", dv)]);
        state.Metadata.Remove("sub");
    }

    private void UpdateDealerStatus(GameState state)
    {
        var hand = DealerHand(state);
        var (val, soft) = HandInfo(hand);
        string softText = soft ? "soft " : "";
        state.Metadata["status"] = DealerIsDone(hand)
            ? GameText.Message(state, "dealer_done", "Dealer: {soft}{value} — Tap to see result.",
                               values: [("soft", softText), ("value", val)])
            : GameText.Message(state, "dealer_drawing", "Dealer: {soft}{value} — Tap for next card.",
                               values: [("soft", softText), ("value", val)]);
        state.Metadata.Remove("sub");
    }

    // ── Seats and hands ───────────────────────────────────────────────────────

    /// <summary>The dealer: the table's role seat, which sits after every player.</summary>
    private static int DealerIndex(GameState state)
    {
        int i = state.Players.FindIndex(p => p.Role is not null);
        return i >= 0 ? i : state.Players.Count - 1;
    }

    /// <summary>Everyone playing against the dealer.</summary>
    private static IEnumerable<Player> Players(GameState state)
        => state.Players.Take(DealerIndex(state));

    private static Zone Hand(GameState state, string seat) => state.Zones[$"hand:{seat}"];

    private Zone? SplitHand(GameState state, string seat) => state.FindZone($"{_splitZone}:{seat}");

    private static Zone DealerHand(GameState state) => Hand(state, state.Players[DealerIndex(state)].Id);

    private static string Active(GameState state, string seat)
        => state.Metadata.GetValueOrDefault($"bj_active:{seat}", "hand");

    private Zone ActiveHand(GameState state, string seat)
        => Active(state, seat) == "split" && SplitHand(state, seat) is { } split ? split : Hand(state, seat);

    /// <summary>Each hand a seat is playing, with its stake — two once it has split.</summary>
    private IEnumerable<(Zone Hand, string StakeKey, bool CanBeNatural)> HandsOf(GameState state, string seat)
    {
        bool split = SplitHand(state, seat) is { Count: > 0 };

        // A split hand that reaches 21 in two cards is twenty-one, not blackjack.
        yield return (Hand(state, seat), $"bj_bet:{seat}", !split);
        if (split) yield return (SplitHand(state, seat)!, $"bj_split_bet:{seat}", false);
    }

    private bool HasLiveHand(GameState state, string seat)
        => !state.Metadata.ContainsKey($"bj_surrendered:{seat}")
        && HandsOf(state, seat).Any(h => HandValue(h.Hand) <= 21);

    private static int Stake(GameState state, string key)
        => int.TryParse(state.Metadata.GetValueOrDefault(key), out int n) ? n : 0;

    // ── Hand values ───────────────────────────────────────────────────────────

    private static bool IsNatural(Zone hand) => hand.Count == 2 && HandValue(hand) == 21;

    private static int HandValue(Zone hand) => HandInfo(hand).Value;

    private static (int Value, bool IsSoft) HandInfo(Zone hand) => Count(hand.Cards);

    private static int VisibleValue(Zone hand) => Count(hand.Cards.Where(c => c.IsFaceUp)).Value;

    private static (int Value, bool IsSoft) Count(IEnumerable<Card> cards)
    {
        int total = 0, aces = 0;
        foreach (var c in cards)
        {
            if      (c.Rank == Rank.Ace)  { aces++; total += 11; }
            else if (c.Rank >= Rank.Jack) total += 10;
            else                          total += (int)c.Rank;
        }
        while (total > 21 && aces > 0) { total -= 10; aces--; }
        return (total, aces > 0);
    }

    private bool DealerIsDone(Zone hand)
    {
        var (val, soft) = HandInfo(hand);
        if (val > 17) return true;
        if (val == 17) return !soft || !_hitSoft17;
        return false;
    }

    // ── Definition helpers ────────────────────────────────────────────────────

    /// <summary>"3:2" → (3, 2). Anything unreadable pays even money.</summary>
    private static (int, int) ParsePayout(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2
               && int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den) && den > 0
            ? (num, den)
            : (1, 1);
    }

    private static int? GetInt(PhaseDefinition def, string key)
        => def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : null;

    private static bool? GetBool(PhaseDefinition def, string key)
        => def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean() : null;

    private static string? GetString(PhaseDefinition def, string key)
        => def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}
