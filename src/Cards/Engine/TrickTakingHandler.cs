using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for trick-taking games (Hearts, Spades, Euchre, Pinochle).
///
/// Phase definition parameters:
///   trump              — "spades" | "hearts" | null | "bid_result"
///                        "bid_result" reads state.Metadata["bid_trump"]
///   lead               — "left_of_dealer" (default) | "current_player" (honour preceding phase's active player, e.g. Pinochle bid winner)
///   follow_suit        — true (default) | false
///   lead_restrictions  — array of { suit, until } or { card, first_trick }
///   no_points_first_trick — true | false (default): hearts + Qs blocked first trick
///   left_bower         — true | false (default): Euchre rule
///   winner             — "highest" | "highest_trump_then_lead" (default)
///   trick_winner_leads_next — true (default) | false
///   collect_tricks_to  — zone id prefix, e.g. "won_tricks" (owner expanded)
///   must_play_higher   — true | false (default): when following trump, must beat current best if able (Pinochle)
///   loner_skips_partner — true | false (default): skip loner's partner when bid_alone:{id}=true (Euchre)
///
/// State metadata keys (written by this handler):
///   trick_trump        — resolved trump suit string, or "" for no trump
///   trick_leader       — player ID leading the current trick
///   trick_lead_suit    — suit string of the first card played to the trick
///   trick_collecting   — "true" while waiting to collect (auto-advance active)
///   trick_hearts_broken — "true" once a heart has been played as a non-lead card
///   tricks_taken:{id}  — cumulative trick count per player
///   trick_played:{id}  — card ID each player played to the current trick
///   trick_last_winner  — player ID who won the final trick (for last-trick bonus scoring)
/// </summary>
public sealed class TrickTakingHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;

    // Config read from phase definition
    private readonly string? _trumpConfig;         // null, "spades", "bid_result", etc.
    private readonly bool    _followSuit;
    private readonly string? _leadCard;            // "2_of_clubs" — player with this card leads
    private readonly string  _leadPolicy;          // "left_of_dealer" | "current_player" (default left_of_dealer)
    private readonly string  _winnerMode;          // "highest" or "highest_trump_then_lead"
    private readonly bool    _trickWinnerLeadsNext;
    private readonly string  _collectTo;           // zone id prefix, default "won_tricks"
    private readonly bool    _noPointsFirstTrick;
    private readonly bool    _leftBower;
    private readonly bool    _mustPlayHigher;
    private readonly bool    _lonerSkipsPartner;
    private readonly string  _direction;             // "clockwise" | "counter_clockwise"

    // Lead restrictions: hearts/spades broken tracking, Qs first trick
    private readonly List<LeadRestriction> _leadRestrictions;

    public TrickTakingHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId          = nextPhaseId;
        _trumpConfig          = GetString(def, "trump");
        _followSuit           = GetBool(def, "follow_suit") ?? true;
        _leadCard             = GetString(def, "lead_card");
        _leadPolicy           = GetString(def, "lead") ?? "left_of_dealer";
        _winnerMode           = GetString(def, "winner") ?? "highest_trump_then_lead";
        _trickWinnerLeadsNext = GetBool(def, "trick_winner_leads_next") ?? true;
        _collectTo            = GetString(def, "collect_tricks_to") ?? "won_tricks";
        _noPointsFirstTrick   = GetBool(def, "no_points_first_trick") ?? false;
        _leftBower            = GetBool(def, "left_bower") ?? false;
        _mustPlayHigher       = GetBool(def, "must_play_higher") ?? false;
        _lonerSkipsPartner    = GetBool(def, "loner_skips_partner") ?? false;
        _direction            = GetString(def, "direction") ?? "clockwise";

        _leadRestrictions = ParseLeadRestrictions(def);
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        return [new GameAction("tap")];
    }

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
        => IsCollecting(state) ? TimeSpan.FromMilliseconds(1400) : null;

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        EnsureInitialized(state);
        if (IsCollecting(state)) return [];
        if (!IsMyTurn(state)) return [];

        var hand = CurrentHand(state);
        if (hand is null) return [];

        string leadSuit = state.Metadata.GetValueOrDefault("trick_lead_suit", "");
        string trump    = ResolveTrump(state);

        // On the very first card of the trick: apply lead restrictions.
        if (string.IsNullOrEmpty(leadSuit))
            return FilterLeads(state, hand.Cards, trump).Select(c => c.Id).ToList();

        // Must follow suit if possible (treating left-bower as trump suit).
        if (_followSuit)
        {
            var followers = hand.Cards.Where(c => EffectiveSuit(c, trump) == leadSuit).ToList();
            if (followers.Count > 0)
            {
                // must_play_higher: when following trump, must beat current best trump if able.
                if (_mustPlayHigher && !string.IsNullOrEmpty(trump)
                    && string.Equals(leadSuit, trump, StringComparison.OrdinalIgnoreCase))
                {
                    Card? bestPlayed = BestTrumpPlayed(state, trump);
                    if (bestPlayed is not null)
                    {
                        var higher = followers.Where(c => LeftBowerRank(c, trump) > LeftBowerRank(bestPlayed, trump)).ToList();
                        if (higher.Count > 0) return higher.Select(c => c.Id).ToList();
                    }
                }
                return followers.Select(c => c.Id).ToList();
            }
        }

        // no_points_first_trick: when void in lead suit on trick 1, still block hearts + Qs.
        if (_noPointsFirstTrick)
        {
            string trickNum = state.Metadata.GetValueOrDefault("trick_number", "1");
            if (trickNum == "1")
            {
                var safe = hand.Cards
                    .Where(c => !(c.Suit == Suit.Hearts ||
                                  (c.Rank == Rank.Queen && c.Suit == Suit.Spades)))
                    .ToList();
                if (safe.Count > 0) return safe.Select(c => c.Id).ToList();
                // Only points in hand — must play one; return full hand.
            }
        }

        return hand.Cards.Select(c => c.Id).ToList();
    }

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
    {
        if (!IsMyTurn(state) || IsCollecting(state)) return [];
        var zone = TrickZoneFor(state, state.CurrentPlayer.Id);
        return zone is null ? [] : [zone.Id];
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);
        if (IsCollecting(state))
        {
            CollectTrick(state);
            return;
        }

        // Expecting play_card or select_card action
        string? cardId = action.CardId;
        if (cardId is null && action.Type == "tap") return; // nothing to do

        if (action.Type == "select_card")
        {
            state.Metadata["selected_card"] = cardId!;
            return;
        }

        if (action.Type != "play_card" && action.Type != "tap") return;

        // Resolve play: if action has CardId use it; else use selected_card
        cardId ??= state.Metadata.GetValueOrDefault("selected_card");
        if (cardId is null) return;
        state.Metadata.Remove("selected_card");

        PlayCard(state, cardId);
    }

    // ── Initialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Auto-initializes on first entry to this phase (when trick_leader is absent).
    /// Also re-initializes after a new round re-enters this phase.
    /// </summary>
    private void EnsureInitialized(GameState state)
    {
        if (!state.Metadata.ContainsKey("trick_leader"))
            StartHand(state);
    }

    /// <summary>
    /// Sets up trump and the opening leader.  Called on first entry and after
    /// each new-round deal.  Can also be called directly by game-specific logic.
    /// </summary>
    public void StartHand(GameState state)
    {
        string trump = ResolveTrump(state);
        state.Metadata["trick_trump"]     = trump;
        state.Metadata["trick_direction"] = _direction;

        // Find who leads.
        string leaderId = FindFirstLeader(state);
        state.Metadata["trick_leader"]   = leaderId;
        state.Metadata["trick_lead_suit"] = "";
        state.Metadata.Remove("trick_collecting");

        int leaderIdx = state.Players.FindIndex(p => p.Id == leaderId);
        if (leaderIdx >= 0) state.CurrentPlayerIndex = leaderIdx;

        UpdateStatus(state);
    }

    // ── Core play logic ───────────────────────────────────────────────────────

    private void PlayCard(GameState state, string cardId)
    {
        var player = state.Players[state.CurrentPlayerIndex];
        var hand   = PlayerHand(state, player.Id);
        var card   = hand?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null || hand is null) return;

        // Move card to this player's trick zone (per-player or shared)
        hand.Remove(card);
        card.IsFaceUp = true;
        var trick = TrickZoneFor(state, player.Id);
        if (trick is null) return;
        trick.Add(card);

        // Record who played what
        state.Metadata[$"trick_played:{player.Id}"] = cardId;

        // Record lead suit on first card of trick
        string trump = ResolveTrump(state);
        if (string.IsNullOrEmpty(state.Metadata.GetValueOrDefault("trick_lead_suit", "")))
            state.Metadata["trick_lead_suit"] = EffectiveSuit(card, trump);

        // Hearts broken tracking
        if (card.Suit == Suit.Hearts)
            state.Metadata["trick_hearts_broken"] = "true";
        if (card.Suit == Suit.Spades && !string.IsNullOrEmpty(trump) &&
            string.Equals(trump, "spades", StringComparison.OrdinalIgnoreCase))
            state.Metadata["trick_spades_broken"] = "true";

        // Check if all active players have played (loner's partner is excluded).
        string? partnerToSkip = GetLonerPartnerId(state);
        int activePlayers  = state.Players.Count - (partnerToSkip is not null ? 1 : 0);
        int playedCount    = state.Players.Count(p =>
            p.Id != partnerToSkip && state.Metadata.ContainsKey($"trick_played:{p.Id}"));

        if (playedCount < activePlayers)
        {
            // Advance to next player, skipping the loner's partner.
            do { state.AdvancePlayer(_direction); }
            while (partnerToSkip is not null && state.CurrentPlayer.Id == partnerToSkip);
            UpdateStatus(state);
        }
        else
        {
            // Trick complete — enter collecting state
            state.Metadata["trick_collecting"] = "true";
            string winnerId = DetermineWinner(state, trump);
            state.Metadata["trick_winner"] = winnerId;
            var winner = state.Players.FirstOrDefault(p => p.Id == winnerId);
            string winMsg = winner == state.Players[0] ? "You win the trick!" : $"{winner?.Name} wins the trick.";
            state.Metadata["status"] = winMsg;
        }
    }

    private void CollectTrick(GameState state)
    {
        string winnerId   = state.Metadata.GetValueOrDefault("trick_winner", "");
        var    trickZones = AllTrickZones(state).ToList();

        // Move all trick cards to winner's collection zone.
        // Try player zone first, then team zone, then fall back to unowned zone.
        if (!string.IsNullOrEmpty(winnerId))
        {
            var dest = state.FindZone($"{_collectTo}:{winnerId}");
            if (dest is null)
            {
                var team = state.GetPlayerTeam(winnerId);
                if (team is not null) dest = state.FindZone($"{_collectTo}:{team.Id}");
            }
            dest ??= state.FindZone(_collectTo);
            if (dest is not null)
                foreach (var tz in trickZones)
                    while (!tz.IsEmpty) dest.Add(tz.Draw()!);

            int prev = int.TryParse(state.Metadata.GetValueOrDefault($"tricks_taken:{winnerId}"), out int p) ? p : 0;
            state.Metadata[$"tricks_taken:{winnerId}"] = (prev + 1).ToString();
        }
        else
        {
            foreach (var tz in trickZones) tz.Clear(); // Discard if no winner (edge case)
        }

        // Clear per-player play records
        foreach (var player in state.Players)
        {
            state.Metadata.Remove($"trick_played:{player.Id}");
        }
        state.Metadata.Remove("trick_collecting");
        state.Metadata.Remove("trick_lead_suit");
        state.Metadata.Remove("trick_winner");

        // Advance trick number
        int trickNum = int.TryParse(state.Metadata.GetValueOrDefault("trick_number", "1"), out int tn) ? tn : 1;
        state.Metadata["trick_number"] = (trickNum + 1).ToString();

        // Check if hands are empty — move to next phase
        bool handsEmpty = state.Players.All(p => PlayerHand(state, p.Id)?.IsEmpty ?? true);
        if (handsEmpty)
        {
            // Record who won the last trick for last-trick-bonus scoring.
            if (!string.IsNullOrEmpty(winnerId))
                state.Metadata["trick_last_winner"] = winnerId;
            FinishHand(state);
            return;
        }

        // Set next leader
        if (_trickWinnerLeadsNext && !string.IsNullOrEmpty(winnerId))
        {
            state.Metadata["trick_leader"]   = winnerId;
            int idx = state.Players.FindIndex(p => p.Id == winnerId);
            if (idx >= 0) state.CurrentPlayerIndex = idx;
        }
        else
        {
            state.AdvancePlayer(_direction);
            state.Metadata["trick_leader"] = state.CurrentPlayer.Id;
        }

        state.Metadata["trick_lead_suit"] = "";
        UpdateStatus(state);
    }

    private void FinishHand(GameState state)
    {
        // Leave tricks_taken:{playerId} intact for the scoring phase.
        // NewRoundHandler clears them at the start of the next round.
        state.Metadata.Remove("trick_number");
        state.Metadata.Remove("trick_trump");
        state.Metadata.Remove("trick_direction");
        state.Metadata.Remove("trick_hearts_broken");
        state.Metadata.Remove("trick_spades_broken");

        state.CurrentPhaseId = _nextPhaseId;
    }

    // ── Trump resolution ──────────────────────────────────────────────────────

    private string ResolveTrump(GameState state)
    {
        string cached = state.Metadata.GetValueOrDefault("trick_trump", "");
        if (!string.IsNullOrEmpty(cached)) return cached;

        return _trumpConfig?.ToLower() switch
        {
            null or "null" or "none" => "",
            "bid_result" or "turn_up" or "bidder_choice"
                => state.Metadata.GetValueOrDefault("bid_trump", ""),
            var suit => suit,
        };
    }

    // ── First-leader determination ────────────────────────────────────────────

    private string FindFirstLeader(GameState state)
    {
        // If a specific lead card is named, the player holding it leads.
        if (_leadCard is not null)
        {
            string cardId = NormalizeCardId(_leadCard);
            foreach (var p in state.Players)
            {
                var hand = PlayerHand(state, p.Id);
                if (hand?.Cards.Any(c => c.Id == cardId) == true)
                    return p.Id;
            }
        }

        // "current_player": honour whoever the preceding phase left as active
        // (e.g. Pinochle name_trump sets bid winner as current player).
        if (_leadPolicy == "current_player")
            return state.CurrentPlayer.Id;

        // Default: left of dealer
        return LeftOfDealer(state);
    }

    private string LeftOfDealer(GameState state)
    {
        if (state.DealerId is null) return state.Players[0].Id;
        int dealerIdx = state.Players.FindIndex(p => p.Id == state.DealerId);
        if (dealerIdx < 0) return state.Players[0].Id;
        int n = state.Players.Count;
        // "Left of dealer" in card-game convention = clockwise = the player at the dealer's left,
        // which is index−1 in the screen layout (bottom→left→top→right).
        int offset = string.Equals(_direction, "clockwise", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        return state.Players[(dealerIdx + offset + n) % n].Id;
    }

    // ── Trick winner determination ────────────────────────────────────────────

    private string DetermineWinner(GameState state, string trump)
    {
        string leadSuit = state.Metadata.GetValueOrDefault("trick_lead_suit", "");

        // Build map: cardId → playerId
        var cardToPlayer = new Dictionary<string, string>();
        foreach (var p in state.Players)
            if (state.Metadata.TryGetValue($"trick_played:{p.Id}", out var cid))
                cardToPlayer[cid] = p.Id;

        Card? best     = null;
        string bestPid = state.Players[0].Id;

        foreach (var card in AllTrickZones(state).SelectMany(z => z.Cards))
        {
            if (!cardToPlayer.TryGetValue(card.Id, out var pid)) continue;

            if (best is null || Beats(card, best, leadSuit, trump))
            {
                best    = card;
                bestPid = pid;
            }
        }

        return bestPid;
    }

    private bool Beats(Card challenger, Card current, string leadSuit, string trump)
    {
        string challengerSuit = EffectiveSuit(challenger, trump);
        string currentSuit    = EffectiveSuit(current,    trump);
        bool   hasTrump       = !string.IsNullOrEmpty(trump);

        // Trump beats non-trump
        if (hasTrump)
        {
            bool cIsTrump  = string.Equals(challengerSuit, trump, StringComparison.OrdinalIgnoreCase);
            bool cuIsTrump = string.Equals(currentSuit,    trump, StringComparison.OrdinalIgnoreCase);
            if (cIsTrump && !cuIsTrump) return true;
            if (!cIsTrump && cuIsTrump) return false;
        }

        // Lead suit beats off-suit
        bool cIsLead  = string.Equals(challengerSuit, leadSuit, StringComparison.OrdinalIgnoreCase);
        bool cuIsLead = string.Equals(currentSuit,    leadSuit, StringComparison.OrdinalIgnoreCase);
        if (cIsLead && !cuIsLead) return true;
        if (!cIsLead && cuIsLead) return false;

        // Same suit — higher rank wins; left bower (if enabled) outranks right bower
        if (_leftBower && !string.IsNullOrEmpty(trump))
        {
            int cr  = LeftBowerRank(challenger, trump);
            int cur = LeftBowerRank(current,    trump);
            return cr > cur;
        }
        return (int)challenger.Rank > (int)current.Rank;
    }

    // ── Effective suit (left bower treated as trump) ──────────────────────────

    private string EffectiveSuit(Card card, string trump)
    {
        if (_leftBower && card.Rank == Rank.Jack && !string.IsNullOrEmpty(trump))
        {
            // Right bower: same suit as trump
            if (string.Equals(card.Suit.ToString(), trump, StringComparison.OrdinalIgnoreCase))
                return trump;
            // Left bower: Jack of same-colour suit counts as trump
            if (SameColor(card.Suit, ParseSuit(trump)))
                return trump;
        }
        return card.Suit.ToString().ToLower();
    }

    private int LeftBowerRank(Card card, string trump)
    {
        // Right bower ranks highest, left bower second
        if (card.Rank == Rank.Jack &&
            string.Equals(card.Suit.ToString(), trump, StringComparison.OrdinalIgnoreCase))
            return 100; // right bower
        if (card.Rank == Rank.Jack && _leftBower &&
            SameColor(card.Suit, ParseSuit(trump)))
            return 99;  // left bower
        return (int)card.Rank;
    }

    // ── Lead restriction filtering ────────────────────────────────────────────

    private IReadOnlyList<Card> FilterLeads(GameState state, List<Card> hand, string trump)
    {
        string trickNum = state.Metadata.GetValueOrDefault("trick_number", "1");
        bool isFirstTrick = trickNum == "1";

        var blocked = new HashSet<string>(); // card Ids blocked from leading

        foreach (var r in _leadRestrictions)
        {
            if (r.Suit is not null)
            {
                string flag = $"trick_{r.Suit}_broken";
                bool broken = state.Metadata.GetValueOrDefault(flag, "false") == "true";
                if (!broken)
                {
                    foreach (var c in hand.Where(c => EffectiveSuit(c, trump) == r.Suit))
                        blocked.Add(c.Id);
                }
            }
            else if (r.CardId is not null && r.BlockFirstTrick && isFirstTrick)
            {
                blocked.Add(r.CardId);
            }
        }

        // no_points_first_trick: block hearts and Qs on trick 1
        if (_noPointsFirstTrick && isFirstTrick)
        {
            foreach (var c in hand.Where(c => c.Suit == Suit.Hearts ||
                                              (c.Rank == Rank.Queen && c.Suit == Suit.Spades)))
                blocked.Add(c.Id);
        }

        var allowed = hand.Where(c => !blocked.Contains(c.Id)).ToList();
        return allowed.Count > 0 ? allowed : hand; // must play something
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void UpdateStatus(GameState state)
    {
        string trump   = ResolveTrump(state);
        string player  = state.CurrentPlayer == state.Players[0]
            ? "Your turn" : $"{state.CurrentPlayer.Name}'s turn";
        string trumpStr = string.IsNullOrEmpty(trump) ? "" : $"  |  Trump: {trump}";
        state.Metadata["status"] = $"{player}{trumpStr}";
    }

    // ── Loner and must-play-higher helpers ───────────────────────────────────

    /// <summary>
    /// Returns the ID of the loner's partner to skip, or null if not going alone.
    /// </summary>
    private string? GetLonerPartnerId(GameState state)
    {
        if (!_lonerSkipsPartner) return null;
        foreach (var p in state.Players)
        {
            if (state.Metadata.GetValueOrDefault($"bid_alone:{p.Id}") != "true") continue;
            var team = state.GetPlayerTeam(p.Id);
            if (team is null) continue;
            return team.PlayerIds.FirstOrDefault(id => id != p.Id);
        }
        return null;
    }

    /// <summary>
    /// Returns the highest-ranked trump card already played to the current trick, or null.
    /// </summary>
    private Card? BestTrumpPlayed(GameState state, string trump)
    {
        Card? best = null;
        foreach (var card in AllTrickZones(state).SelectMany(z => z.Cards))
        {
            if (!string.Equals(EffectiveSuit(card, trump), trump, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is null || LeftBowerRank(card, trump) > LeftBowerRank(best, trump))
                best = card;
        }
        return best;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsCollecting(GameState state)
        => state.Metadata.GetValueOrDefault("trick_collecting") == "true";

    private static bool IsMyTurn(GameState state) => true; // single-device; always the current player

    private static Zone? CurrentHand(GameState state)
        => PlayerHand(state, state.CurrentPlayer.Id);

    private static Zone? PlayerHand(GameState state, string playerId)
        => state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");

    /// <summary>Returns this player's trick zone (per-player if available, shared fallback).</summary>
    private static Zone? TrickZoneFor(GameState state, string playerId)
        => state.FindZone($"trick:{playerId}") ?? state.FindZone("trick");

    /// <summary>Returns all per-player trick zones, or the single shared zone if no per-player zones exist.</summary>
    private static IEnumerable<Zone> AllTrickZones(GameState state)
    {
        var perPlayer = state.Zones.Values.Where(z => z.Type == "trick" && z.OwnerId is not null).ToList();
        if (perPlayer.Count > 0) return perPlayer;
        var shared = state.FindZone("trick");
        return shared is null ? [] : [shared];
    }

    private static string NormalizeCardId(string spec)
    {
        // "2_of_clubs" → "2c", "Qs" → "Qs", etc.
        if (spec.Contains("_of_"))
        {
            var parts = spec.Split("_of_");
            if (parts.Length == 2)
            {
                string rank = parts[0].ToUpper() switch
                {
                    "2" => "2", "3" => "3", "4" => "4", "5" => "5",
                    "6" => "6", "7" => "7", "8" => "8", "9" => "9",
                    "10" => "10", "JACK" or "J" => "J",
                    "QUEEN" or "Q" => "Q", "KING" or "K" => "K",
                    "ACE" or "A" => "A", _ => parts[0]
                };
                char suit = parts[1].ToLower() switch
                {
                    "clubs" or "c" => 'c', "diamonds" or "d" => 'd',
                    "hearts" or "h" => 'h', "spades" or "s" => 's',
                    _ => parts[1].ToLower()[0]
                };
                return $"{rank}{suit}";
            }
        }
        return spec; // already in short form like "Qs"
    }

    private static Suit ParseSuit(string s) => s.ToLower() switch
    {
        "clubs"    or "c" => Suit.Clubs,
        "diamonds" or "d" => Suit.Diamonds,
        "hearts"   or "h" => Suit.Hearts,
        "spades"   or "s" => Suit.Spades,
        _ => Suit.Spades
    };

    private static bool SameColor(Suit a, Suit b)
        => (a is Suit.Hearts or Suit.Diamonds) == (b is Suit.Hearts or Suit.Diamonds);

    // ── JSON parsing helpers ──────────────────────────────────────────────────

    private static string? GetString(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true)
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Null)   return null;
        }
        return null;
    }

    private static bool? GetBool(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        return null;
    }

    private static List<LeadRestriction> ParseLeadRestrictions(PhaseDefinition def)
    {
        var list = new List<LeadRestriction>();
        if (def.Extra?.TryGetValue("lead_restrictions", out var el) != true) return list;
        if (el.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in el.EnumerateArray())
        {
            if (item.TryGetProperty("suit", out var suit))
            {
                string? until = item.TryGetProperty("until", out var u) ? u.GetString() : null;
                list.Add(new LeadRestriction { Suit = suit.GetString(), Until = until });
            }
            else if (item.TryGetProperty("card", out var card))
            {
                bool firstTrick = item.TryGetProperty("first_trick", out var ft) && !ft.GetBoolean();
                list.Add(new LeadRestriction { CardId = NormalizeCardId(card.GetString()!), BlockFirstTrick = firstTrick });
            }
        }
        return list;
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class LeadRestriction
    {
        public string? Suit          { get; init; } // e.g. "hearts"
        public string? Until         { get; init; } // e.g. "hearts_broken"
        public string? CardId        { get; init; } // e.g. "Qs"
        public bool    BlockFirstTrick { get; init; }
    }
}
