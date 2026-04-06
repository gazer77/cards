using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for auction-style bidding (Spades, Euchre, Pinochle).
///
/// Phase definition parameters:
///   style         — "number" | "suit_or_pass" | "accept_or_pass" | "number_and_suit"
///   order         — "left_of_dealer" (default)
///   min_bid       — minimum numeric bid (default 0)
///   max_bid       — maximum numeric bid (default 13)
///   pass_allowed  — true | false (default true)
///   special_bids  — ["nil", "blind_nil"] — extra action buttons shown
///   stick_the_dealer — true | false: dealer cannot pass last (default false)
///   once_around   — true | false: only one round of bidding (default false)
///   going_alone   — true | false: Euchre loner option (default false)
///   bid_increment — step between numeric bids (default 1; e.g., 10 for Pinochle)
///
/// State metadata keys written:
///   bid:{playerId}      — bid value ("0"–"13" | "nil" | "blind_nil" | suit name)
///   bid_trump           — trump suit chosen (used by trick_taking when trump = "bid_result")
///   bid_winner          — playerId of the highest numeric bidder (number style only)
///   bidding_leader      — player who led the bidding this round
///   bidding_pass_count  — how many consecutive passes (for once_around)
/// </summary>
public sealed class BiddingHandler : IPhaseHandler
{
    private readonly string  _nextPhaseId;
    private readonly string  _style;
    private readonly int     _minBid;
    private readonly int     _maxBid;
    private readonly bool    _passAllowed;
    private readonly bool    _stickTheDealer;
    private readonly bool    _onceAround;
    private readonly bool    _goingAlone;
    private readonly int     _bidIncrement;
    private readonly List<string> _specialBids;
    private readonly string? _excludeSuit;          // "turned_card_suit" or literal suit name
    private readonly string? _ifAcceptedNext;       // phase to go to when someone accepts
    private readonly string? _ifCalledNext;         // phase to go to when someone calls trump
    private readonly string? _ifAllPassNext;        // phase to go to when all pass
    private readonly string? _dealerAction;         // "swap_turned_card": dealer picks up kitty on accept

    private static readonly string[] AllSuits = ["clubs", "diamonds", "hearts", "spades"];

    public BiddingHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId    = nextPhaseId;
        _style          = GetString(def, "style") ?? "number";
        _minBid         = GetInt(def, "min_bid") ?? 0;
        _maxBid         = GetInt(def, "max_bid") ?? 13;
        _passAllowed    = GetBool(def, "pass_allowed") ?? true;
        _stickTheDealer = GetBool(def, "stick_the_dealer") ?? false;
        _onceAround     = GetBool(def, "once_around") ?? false;
        _goingAlone     = GetBool(def, "going_alone") ?? false;
        _bidIncrement   = GetInt(def, "bid_increment") ?? 1;
        _specialBids    = ParseStringArray(def, "special_bids");
        _excludeSuit    = GetString(def, "exclude_suit");
        _ifAcceptedNext = GetNestedString(def, "if_accepted", "next");
        _ifCalledNext   = GetNestedString(def, "if_called",   "next");
        _ifAllPassNext  = GetString(def, "if_all_pass");
        _dealerAction   = GetNestedString(def, "if_accepted", "dealer_action");
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        var actions = new List<GameAction>();

        switch (_style)
        {
            case "number":
                for (int i = _minBid; i <= _maxBid; i += _bidIncrement)
                    actions.Add(new GameAction($"bid_{i}", Label: i.ToString()));
                foreach (var s in _specialBids)
                    actions.Add(new GameAction($"bid_{s}", Label: Capitalize(s)));
                if (_passAllowed && !IsLastAndStuck(state))
                    actions.Add(new GameAction("bid_pass", Label: "Pass"));
                break;

            case "accept_or_pass":
                actions.Add(new GameAction("bid_accept", Label: "Order Up"));
                if (!IsLastAndStuck(state))
                    actions.Add(new GameAction("bid_pass", Label: "Pass"));
                break;

            case "suit_or_pass":
                string? excluded = state.Metadata.GetValueOrDefault("bid_excluded_suit");
                foreach (var suit in AllSuits)
                {
                    if (suit != excluded)
                        actions.Add(new GameAction($"bid_{suit}", Label: Capitalize(suit)));
                }
                if (!IsLastAndStuck(state))
                    actions.Add(new GameAction("bid_pass", Label: "Pass"));
                break;

            case "number_and_suit":
                for (int i = _minBid; i <= _maxBid; i++)
                    foreach (var suit in AllSuits)
                        actions.Add(new GameAction($"bid_{i}_{suit}", Label: $"{i} {Capitalize(suit)}"));
                if (_passAllowed && !IsLastAndStuck(state))
                    actions.Add(new GameAction("bid_pass", Label: "Pass"));
                break;
        }

        if (_goingAlone)
            actions.Add(new GameAction("bid_alone", Label: "Go Alone"));

        return actions;
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);
        var player = state.CurrentPlayer;

        if (action.Type == "bid_pass")
        {
            HandlePass(state, player);
            return;
        }

        if (action.Type == "bid_alone")
        {
            state.Metadata[$"bid_alone:{player.Id}"] = "true";
            // Going alone doesn't end bidding — player still needs to name trump
            UpdateStatus(state);
            return;
        }

        // Resolve bid value
        string bidValue = action.Type.StartsWith("bid_") ? action.Type["bid_".Length..] : action.Type;
        state.Metadata[$"bid:{player.Id}"] = bidValue;

        // If bid sets trump (suit or accept), record it
        if (IsSuit(bidValue))
            state.Metadata["bid_trump"] = bidValue;
        else if (action.Type == "bid_accept")
            RecordAcceptedTrump(state);

        // In once_around mode a non-pass bid ends bidding immediately (Euchre-style).
        if (_onceAround)
        {
            state.Metadata["euchre_maker"] = player.Id;
            string next = _ifAcceptedNext ?? _ifCalledNext ?? _nextPhaseId;

            // dealer_action: "swap_turned_card" — move kitty card to dealer's hand.
            // The dealer will then discard via the dealer_discard phase.
            if (_dealerAction == "swap_turned_card" && state.DealerId is { } dealerId)
            {
                var kitty     = state.FindZone("kitty");
                var dealerHand = state.FindZone($"hand:{dealerId}") ?? state.FindZone("hand");
                if (kitty?.TopCard is { } kittyCard && dealerHand is not null)
                {
                    kitty.Remove(kittyCard);
                    kittyCard.IsFaceUp = false;   // dealer holds it face-down
                    dealerHand.Add(kittyCard);
                    // Point dealer_discard phase back to its configured next phase.
                    state.Metadata["dealer_discard_next"] = _ifAcceptedNext ?? _nextPhaseId;
                    // Set current player to dealer for the discard phase.
                    int di = state.Players.FindIndex(p => p.Id == dealerId);
                    if (di >= 0) state.CurrentPlayerIndex = di;
                    next = "dealer_discard";
                }
            }

            FinishBiddingWith(state, next);
            return;
        }

        // Bidding ends when all players have bid
        if (BiddingComplete(state))
            FinishBidding(state);
        else
        {
            state.AdvancePlayer();
            UpdateStatus(state);
        }
    }

    // ── Initialization ────────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (state.Metadata.ContainsKey("bidding_leader")) return;

        // Clear any stale bid keys from a previous bidding phase.
        foreach (var p in state.Players)
            state.Metadata.Remove($"bid:{p.Id}");

        // Set excluded suit for suit_or_pass style (e.g. Euchre's "no upcard suit").
        if (_excludeSuit is not null)
        {
            string excluded = _excludeSuit == "turned_card_suit"
                ? (state.FindZone("kitty")?.TopCard?.Suit.ToString().ToLower() ?? "")
                : _excludeSuit;
            if (excluded.Length > 0)
                state.Metadata["bid_excluded_suit"] = excluded;
        }

        // Set starting player: left of dealer.
        string leaderId = LeftOfDealer(state);
        state.Metadata["bidding_leader"]     = leaderId;
        state.Metadata["bidding_pass_count"] = "0";

        int leaderIdx = state.Players.FindIndex(p => p.Id == leaderId);
        if (leaderIdx >= 0) state.CurrentPlayerIndex = leaderIdx;

        UpdateStatus(state);
    }

    // ── Pass handling ─────────────────────────────────────────────────────────

    private void HandlePass(GameState state, Player player)
    {
        state.Metadata[$"bid:{player.Id}"] = "pass";
        int passes = int.TryParse(state.Metadata.GetValueOrDefault("bidding_pass_count"), out int p) ? p + 1 : 1;
        state.Metadata["bidding_pass_count"] = passes.ToString();

        if (_onceAround && passes >= state.Players.Count)
        {
            // All passed in once_around — use the conditional all-pass next if provided.
            FinishBiddingWith(state, _ifAllPassNext ?? _nextPhaseId);
            return;
        }

        if (BiddingComplete(state))
        {
            FinishBidding(state);
            return;
        }

        state.AdvancePlayer();
        UpdateStatus(state);
    }

    // ── Completion ────────────────────────────────────────────────────────────

    private bool BiddingComplete(GameState state)
    {
        // Complete when all players have a bid entry
        return state.Players.All(p => state.Metadata.ContainsKey($"bid:{p.Id}"));
    }

    private void FinishBidding(GameState state)
        => FinishBiddingWith(state, _nextPhaseId);

    private static void FinishBiddingWith(GameState state, string nextPhaseId)
    {
        // Record the high bidder for number-style auctions (used by name_trump phase).
        RecordBidWinner(state);

        // Clear bidding bookkeeping (keep bid:{playerId} values for scoring)
        state.Metadata.Remove("bidding_leader");
        state.Metadata.Remove("bidding_pass_count");
        state.Metadata.Remove("bid_excluded_suit");

        state.CurrentPhaseId = nextPhaseId;
    }

    private static void RecordBidWinner(GameState state)
    {
        string? bestId  = null;
        int     bestBid = -1;
        foreach (var p in state.Players)
        {
            if (state.Metadata.TryGetValue($"bid:{p.Id}", out var bidStr)
                && int.TryParse(bidStr, out int b) && b > bestBid)
            {
                bestBid = b;
                bestId  = p.Id;
            }
        }
        if (bestId is not null)
        {
            state.Metadata["bid_winner"] = bestId;
            // Set the current player to the winner so name_trump phase starts correctly.
            int idx = state.Players.FindIndex(p => p.Id == bestId);
            if (idx >= 0) state.CurrentPlayerIndex = idx;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RecordAcceptedTrump(GameState state)
    {
        // For accept_or_pass: trump is the turned-up kitty card's suit
        var kitty = state.FindZone("kitty");
        if (kitty?.TopCard is { } top)
            state.Metadata["bid_trump"] = top.Suit.ToString().ToLower();
    }

    private bool IsLastAndStuck(GameState state)
    {
        if (!_stickTheDealer || state.DealerId is null) return false;
        return state.CurrentPlayer.Id == state.DealerId &&
               int.TryParse(state.Metadata.GetValueOrDefault("bidding_pass_count"), out int p) &&
               p >= state.Players.Count - 1;
    }

    private static bool IsSuit(string value)
        => Array.Exists(AllSuits, s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));

    private string LeftOfDealer(GameState state)
    {
        if (state.DealerId is null) return state.Players[0].Id;
        int idx = state.Players.FindIndex(p => p.Id == state.DealerId);
        return state.Players[(idx + 1) % state.Players.Count].Id;
    }

    private void UpdateStatus(GameState state)
    {
        string player = state.CurrentPlayer == state.Players[0]
            ? "Your bid" : $"{state.CurrentPlayer.Name}'s bid";
        state.Metadata["status"] = player;
    }

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..].Replace('_', ' ');

    // ── JSON parsing ──────────────────────────────────────────────────────────

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

    private static bool? GetBool(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        return null;
    }

    private static string? GetNestedString(PhaseDefinition def, string outerKey, string innerKey)
    {
        if (def.Extra?.TryGetValue(outerKey, out var outer) == true
            && outer.ValueKind == JsonValueKind.Object
            && outer.TryGetProperty(innerKey, out var inner)
            && inner.ValueKind == JsonValueKind.String)
            return inner.GetString();
        return null;
    }

    private static List<string> ParseStringArray(PhaseDefinition def, string key)
    {
        var list = new List<string>();
        if (def.Extra?.TryGetValue(key, out var el) != true || el.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list;
    }
}
