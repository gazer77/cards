using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for poker betting rounds (Texas Hold'em, Omaha, Stud).
///
/// Phase definition parameters:
///   structure          — "no_limit" | "limit" | "pot_limit"
///   starting_player    — "left_of_dealer" | "two_left_of_dealer" | "three_left_of_dealer"
///                        | "lowest_up_card"   (stud 3rd street: player with lowest face-up card brings in)
///                        | "highest_up_cards" (stud 4th–7th: player with best showing starts)
///   can_check          — true | false: whether check is allowed (false for preflop)
///   post_blinds        — true: auto-post small and big blinds from game definition before betting
///   bring_in           — true: starting player (lowest_up_card) auto-posts a forced bring-in bet
///   bring_in_amount    — chip amount of the bring-in; defaults to game ante amount or 1
///
/// Chip tracking uses state.Scores as chip counts.
/// Pot is tracked in state.Metadata["pot"] (integer string).
/// Current bet to call is in state.Metadata["bet_to_call"].
///
/// State metadata keys written:
///   bet_to_call       — current amount required to call
///   pot               — total pot size
///   bet_leader        — player ID who opened betting
///   bet:{playerId}    — each player's bet this round
///   bet_folded:{id}   — "true" if player has folded
///   bet_all_in:{id}   — "true" if player is all-in
/// </summary>
public sealed class PokerBettingHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly string _structure;
    private readonly string _startingPlayer;
    private readonly bool   _canCheck;
    private readonly bool   _postBlinds;
    private readonly bool   _bringIn;
    private readonly int    _bringInAmount;

    public PokerBettingHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId    = nextPhaseId;
        _structure      = GetString(def, "structure")        ?? "no_limit";
        _startingPlayer = GetString(def, "starting_player")  ?? "left_of_dealer";
        _canCheck       = GetBool(def, "can_check")          ?? true;
        _postBlinds     = GetBool(def, "post_blinds")        ?? false;
        _bringIn        = GetBool(def, "bring_in")           ?? false;
        _bringInAmount  = GetInt(def, "bring_in_amount")     ?? 0; // resolved against ante at runtime
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        if (IsBettingComplete(state)) return [new GameAction("tap")];

        var p = state.CurrentPlayer;
        if (IsFolded(state, p.Id)) return [new GameAction("tap")];

        int toCall  = GetToCall(state);
        int myBet   = GetPlayerBet(state, p.Id);
        int myChips = state.GetScore(p.Id);
        int needed  = toCall - myBet;

        var actions = new List<GameAction>();

        if (needed <= 0 && _canCheck)
            actions.Add(new GameAction("check", Label: "Check"));
        else if (needed > 0 && needed <= myChips)
            actions.Add(new GameAction("call", Label: $"Call {needed}"));

        if (myChips > needed)
            actions.Add(new GameAction("raise", Label: "Raise"));

        actions.Add(new GameAction("fold", Label: "Fold"));

        if (myChips > 0)
            actions.Add(new GameAction("all_in", Label: "All-In"));

        return actions;
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);

        if (IsBettingComplete(state))
        {
            state.CurrentPhaseId = _nextPhaseId;
            ClearBettingState(state);
            return;
        }

        var p = state.CurrentPlayer;

        switch (action.Type)
        {
            case "check":
                AdvanceBetting(state);
                break;

            case "call":
                int toCall = GetToCall(state);
                int myBet  = GetPlayerBet(state, p.Id);
                int amount = Math.Min(toCall - myBet, state.GetScore(p.Id));
                PlaceBet(state, p.Id, amount);
                AdvanceBetting(state);
                break;

            case "raise":
                // Default raise = double current bet or BB
                int minRaise = Math.Max(GetToCall(state) * 2, 2);
                int raiseAmt = Math.Min(minRaise, state.GetScore(p.Id));
                PlaceBet(state, p.Id, raiseAmt - GetPlayerBet(state, p.Id));
                state.Metadata["bet_to_call"]  = raiseAmt.ToString();
                state.Metadata["bet_leader"]    = p.Id;
                AdvanceBetting(state);
                break;

            case "all_in":
                int allIn = state.GetScore(p.Id);
                PlaceBet(state, p.Id, allIn);
                state.Metadata[$"bet_all_in:{p.Id}"] = "true";
                if (allIn + GetPlayerBet(state, p.Id) > GetToCall(state))
                {
                    state.Metadata["bet_to_call"] = (allIn + GetPlayerBet(state, p.Id)).ToString();
                    state.Metadata["bet_leader"]   = p.Id;
                }
                AdvanceBetting(state);
                break;

            case "fold":
                state.Metadata[$"bet_folded:{p.Id}"] = "true";
                AdvanceBetting(state);
                break;
        }
    }

    // ── Initialization ────────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (state.Metadata.ContainsKey("bet_leader")) return;

        if (_postBlinds) PostBlinds(state);

        if (_bringIn)
        {
            PostBringIn(state);
        }
        else
        {
            string leaderId = ResolveStartingPlayer(state);
            state.Metadata["bet_leader"] = leaderId;
            state.Metadata.TryAdd("bet_to_call", "0");
            int leaderIdx = state.Players.FindIndex(p => p.Id == leaderId);
            if (leaderIdx >= 0) state.CurrentPlayerIndex = leaderIdx;
        }

        UpdateStatus(state);
    }

    private void PostBringIn(GameState state)
    {
        string bringInId = FindLowestUpCard(state);
        int amount = _bringInAmount > 0 ? _bringInAmount
                   : state.Definition.Ante?.Amount ?? 1;

        PlaceBet(state, bringInId, amount);
        state.Metadata["bet_to_call"] = amount.ToString();
        state.Metadata["bet_leader"]  = bringInId;  // action completes back at bring-in player

        // First to act is the player after the bring-in player (skip folded).
        int idx = state.Players.FindIndex(p => p.Id == bringInId);
        for (int i = 1; i <= state.Players.Count; i++)
        {
            int next = (idx + i) % state.Players.Count;
            if (!IsFolded(state, state.Players[next].Id))
            { state.CurrentPlayerIndex = next; break; }
        }
    }

    private static void PostBlinds(GameState state)
    {
        var blinds = state.Definition.Blinds;
        if (blinds is null) return;

        if (blinds.Small is { } sb)
        {
            string sbId = ResolvePosition(state, sb.Position);
            PlaceBet(state, sbId, sb.Amount);
        }

        if (blinds.Big is { } bb)
        {
            string bbId = ResolvePosition(state, bb.Position);
            PlaceBet(state, bbId, bb.Amount);
            state.Metadata["bet_to_call"] = bb.Amount.ToString();
            state.Metadata["bet_leader"]  = bbId;  // Betting opens to player after BB.
        }
    }

    private static string ResolvePosition(GameState state, string position)
    {
        if (state.DealerId is null) return state.Players[0].Id;
        int di = state.Players.FindIndex(p => p.Id == state.DealerId);
        int offset = position switch
        {
            "two_left_of_dealer"   => 2,
            "three_left_of_dealer" => 3,
            _                      => 1,
        };
        return state.Players[(di + offset) % state.Players.Count].Id;
    }

    private string ResolveStartingPlayer(GameState state)
    {
        if (_startingPlayer == "lowest_up_card")   return FindLowestUpCard(state);
        if (_startingPlayer == "highest_up_cards") return FindHighestUpCards(state);

        if (state.DealerId is null) return state.Players[0].Id;
        int di = state.Players.FindIndex(p => p.Id == state.DealerId);
        int offset = _startingPlayer switch
        {
            "two_left_of_dealer"   => 2,
            "three_left_of_dealer" => 3,
            _                      => 1,  // left_of_dealer
        };
        return state.Players[(di + offset) % state.Players.Count].Id;
    }

    private string FindLowestUpCard(GameState state)
    {
        string? bestId   = null;
        int     bestRank = int.MaxValue;
        int     bestSuit = int.MaxValue;

        foreach (var p in state.Players)
        {
            if (IsFolded(state, p.Id)) continue;
            var upCards = PlayerHand(state, p.Id)?.Cards.Where(c => c.IsFaceUp).ToList();
            if (upCards is null || upCards.Count == 0) continue;
            var low = upCards.OrderBy(c => (int)c.Rank).ThenBy(c => (int)c.Suit).First();
            if ((int)low.Rank < bestRank || ((int)low.Rank == bestRank && (int)low.Suit < bestSuit))
            {
                bestRank = (int)low.Rank;
                bestSuit = (int)low.Suit;
                bestId   = p.Id;
            }
        }

        return bestId ?? state.Players[0].Id;
    }

    private string FindHighestUpCards(GameState state)
    {
        string? bestId    = null;
        int     bestScore = -1;

        foreach (var p in state.Players)
        {
            if (IsFolded(state, p.Id)) continue;
            var upCards = PlayerHand(state, p.Id)?.Cards.Where(c => c.IsFaceUp).ToList() ?? [];
            if (upCards.Count == 0) continue;

            var ranks  = upCards.Select(c => (int)c.Rank).OrderByDescending(r => r).ToList();
            int pairs  = ranks.GroupBy(r => r).Count(g => g.Count() >= 2);
            int score  = pairs * 10000 + ranks[0] * 100 + (ranks.Count > 1 ? ranks[1] : 0);
            if (score > bestScore) { bestScore = score; bestId = p.Id; }
        }

        return bestId ?? state.Players[0].Id;
    }

    private static Zone? PlayerHand(GameState state, string playerId)
        => state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");

    // ── Betting logic ─────────────────────────────────────────────────────────

    private static void PlaceBet(GameState state, string playerId, int amount)
    {
        if (amount <= 0) return;
        state.AddScore(playerId, -amount);
        int existing = GetPlayerBet(state, playerId);
        state.Metadata[$"bet:{playerId}"] = (existing + amount).ToString();
        int pot = int.TryParse(state.Metadata.GetValueOrDefault("pot", "0"), out int p) ? p + amount : amount;
        state.Metadata["pot"] = pot.ToString();
    }

    private void AdvanceBetting(GameState state)
    {
        // Find next active player
        int current = state.CurrentPlayerIndex;
        string leader = state.Metadata.GetValueOrDefault("bet_leader", state.Players[0].Id);

        for (int i = 1; i <= state.Players.Count; i++)
        {
            int idx = (current + i) % state.Players.Count;
            var next = state.Players[idx];

            if (IsFolded(state, next.Id)) continue;
            if (IsAllIn(state, next.Id))  continue;

            // Stop at leader (everyone has called)
            if (next.Id == leader && HasCalled(state, next.Id))
            {
                AwardPot(state);
                state.CurrentPhaseId = _nextPhaseId;
                ClearBettingState(state);
                return;
            }

            state.CurrentPlayerIndex = idx;
            UpdateStatus(state);
            return;
        }

        // All players folded / all-in
        AwardPot(state);
        state.CurrentPhaseId = _nextPhaseId;
        ClearBettingState(state);
    }

    private static bool IsBettingComplete(GameState state)
        => !state.Metadata.ContainsKey("bet_leader");

    private static bool IsFolded(GameState state, string pid)
        => state.Metadata.GetValueOrDefault($"bet_folded:{pid}") == "true";

    private static bool IsAllIn(GameState state, string pid)
        => state.Metadata.GetValueOrDefault($"bet_all_in:{pid}") == "true";

    private static bool HasCalled(GameState state, string pid)
    {
        int toCall = GetToCall(state);
        int bet    = GetPlayerBet(state, pid);
        return bet >= toCall;
    }

    private static int GetToCall(GameState state)
        => int.TryParse(state.Metadata.GetValueOrDefault("bet_to_call", "0"), out int v) ? v : 0;

    private static int GetPlayerBet(GameState state, string pid)
        => int.TryParse(state.Metadata.GetValueOrDefault($"bet:{pid}", "0"), out int v) ? v : 0;

    private static void AwardPot(GameState state)
    {
        // Find last non-folded player (or do nothing — showdown handles multi-player pot)
        var active = state.Players.Where(p => !IsFolded(state, p.Id)).ToList();
        if (active.Count == 1)
        {
            int pot = int.TryParse(state.Metadata.GetValueOrDefault("pot", "0"), out int p) ? p : 0;
            state.AddScore(active[0].Id, pot);
            state.Metadata["pot"] = "0";
            state.Metadata["status"] = active[0] == state.Players[0]
                ? $"Everyone folded. You win the pot ({pot})!"
                : $"Everyone folded. {active[0].Name} wins the pot ({pot}).";
        }
    }

    private static void ClearBettingState(GameState state)
    {
        state.Metadata.Remove("bet_leader");
        state.Metadata.Remove("bet_to_call");
        foreach (var p in state.Players)
            state.Metadata.Remove($"bet:{p.Id}");
    }

    private static void UpdateStatus(GameState state)
    {
        int pot    = int.TryParse(state.Metadata.GetValueOrDefault("pot", "0"), out int p) ? p : 0;
        int toCall = GetToCall(state);
        string player = state.CurrentPlayer == state.Players[0]
            ? "Your turn" : $"{state.CurrentPlayer.Name}'s turn";
        state.Metadata["status"] = $"{player}  |  Pot: {pot}  |  To call: {toCall}";
    }

    // ── JSON parsing ──────────────────────────────────────────────────────────

    private static string? GetString(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static bool? GetBool(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        return null;
    }

    private static int? GetInt(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }
}
