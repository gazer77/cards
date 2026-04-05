using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for meld lay-down phases (Gin Rummy, Pinochle, Hand and Foot).
///
/// Phase definition parameters:
///   meld_types      — ["set","run"] | ["canasta"] | ["pinochle"]
///   min_meld_size   — minimum cards in a meld (default 3; ignored for "pinochle" — 1-card melds allowed)
///   wilds_allowed   — true | false (default false)
///   max_wilds_per_meld — max wilds in a single meld (default 1)
///   layoff_allowed  — true | false: add to existing melds (default true)
///
/// Players tap cards to select a group, then tap "Lay Meld" to place it.
/// "Done" ends the meld phase for the current player.
///
/// Pinochle meld type ("pinochle"):
///   Any 1+ card selection is accepted. When the player finishes, all cards in their meld zone
///   are evaluated against standard pinochle meld rules (flush, around, pinochle, marriages,
///   nines) using trump from state.Metadata["bid_trump"]. The total is written to
///   state.Metadata["meld_score:{playerId}"] for ScoringEngine.ApplyPinochle to pick up.
///
/// State metadata keys:
///   meld_turn_player      — player ID currently laying melds
///   meld_selected         — comma-separated selected card IDs
///   meld_score:{playerId} — pinochle meld points for that player (set by "pinochle" type)
/// </summary>
public sealed class MeldHandler : IPhaseHandler
{
    private readonly string       _nextPhaseId;
    private readonly List<string> _meldTypes;
    private readonly int          _minMeldSize;
    private readonly bool         _wildsAllowed;
    private readonly int          _maxWildsPerMeld;
    private readonly bool         _layoffAllowed;

    public MeldHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId      = nextPhaseId;
        _meldTypes        = ParseStringArray(def, "meld_types");
        if (_meldTypes.Count == 0) _meldTypes = ["set", "run"];
        _minMeldSize      = GetInt(def, "min_meld_size")     ?? 3;
        _wildsAllowed     = GetBool(def, "wilds_allowed")    ?? false;
        _maxWildsPerMeld  = GetInt(def, "max_wilds_per_meld") ?? 1;
        _layoffAllowed    = GetBool(def, "layoff_allowed")   ?? true;
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        var actions = new List<GameAction>();
        var selected = GetSelected(state);

        // Pinochle: any 1+ card selection is allowed (nines, marriages, flushes, etc.)
        int effectiveMin = _meldTypes.Contains("pinochle") ? 1 : _minMeldSize;
        if (selected.Count >= effectiveMin && IsValidMeld(state, selected))
            actions.Add(new GameAction("lay_meld", Label: "Lay Meld"));

        if (_layoffAllowed && selected.Count == 1)
            actions.Add(new GameAction("lay_off", Label: "Lay Off"));

        actions.Add(new GameAction("meld_done", Label: "Done"));
        return actions;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        EnsureInitialized(state);
        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        return hand?.Cards.Select(c => c.Id).ToList() ?? [];
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);

        if (action.Type == "select_card" && action.CardId is { } cardId)
        {
            ToggleSelection(state, cardId);
            return;
        }

        if (action.Type == "lay_meld")
        {
            LayMeld(state);
            return;
        }

        if (action.Type == "lay_off")
        {
            LayOff(state);
            return;
        }

        if (action.Type == "meld_done")
        {
            AdvanceOrFinish(state);
        }
    }

    // ── Core logic ────────────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (state.Metadata.ContainsKey("meld_turn_player")) return;
        state.Metadata["meld_turn_player"] = state.CurrentPlayer.Id;
        state.Metadata.Remove("meld_selected");
        UpdateStatus(state);
    }

    private void LayMeld(GameState state)
    {
        var selected = GetSelected(state);
        if (selected.Count < _minMeldSize) return;

        var hand     = PlayerHand(state, state.CurrentPlayer.Id);
        var meldZone = state.FindZone($"meld:{state.CurrentPlayer.Id}") ?? state.FindZone("meld");
        if (hand is null || meldZone is null) return;

        foreach (var cardId in selected)
        {
            var card = hand.Cards.FirstOrDefault(c => c.Id == cardId);
            if (card is null) continue;
            hand.Remove(card);
            card.IsFaceUp = true;
            meldZone.Add(card);
        }

        state.Metadata.Remove("meld_selected");
        UpdateStatus(state);
    }

    private void LayOff(GameState state)
    {
        // Simplified layoff: move selected card to any meld zone
        var selected = GetSelected(state);
        if (selected.Count != 1) return;

        var cardId = selected[0];
        var hand   = PlayerHand(state, state.CurrentPlayer.Id);
        var card   = hand?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null || hand is null) return;

        // Find first opponent meld zone to lay off to
        var targetMeld = state.Zones.Values
            .FirstOrDefault(z => z.Type == "spread" && z.OwnerId != null && z.OwnerId != state.CurrentPlayer.Id);
        if (targetMeld is null) return;

        hand.Remove(card);
        card.IsFaceUp = true;
        targetMeld.Add(card);

        state.Metadata.Remove("meld_selected");
        UpdateStatus(state);
    }

    private void AdvanceOrFinish(GameState state)
    {
        // Score pinochle melds for this player before advancing.
        if (_meldTypes.Contains("pinochle"))
        {
            string trump = state.Metadata.GetValueOrDefault("bid_trump", "spades");
            int pts = ScorePinochleMelds(state, state.CurrentPlayer.Id, trump);
            state.Metadata[$"meld_score:{state.CurrentPlayer.Id}"] = pts.ToString();
        }

        state.Metadata.Remove("meld_turn_player");
        state.Metadata.Remove("meld_selected");

        // Each player melds once per round; advance through all players then move on
        int currentIdx = state.CurrentPlayerIndex;
        int nextIdx    = (currentIdx + 1) % state.Players.Count;

        // If we've cycled through all players, end the phase
        string? startPlayer = state.Metadata.GetValueOrDefault("meld_start_player");
        if (startPlayer is null)
        {
            state.Metadata["meld_start_player"] = state.CurrentPlayer.Id;
            startPlayer = state.CurrentPlayer.Id;
        }

        if (nextIdx == state.Players.FindIndex(p => p.Id == startPlayer) || state.Players.Count == 1)
        {
            state.Metadata.Remove("meld_start_player");
            state.CurrentPhaseId = _nextPhaseId;
        }
        else
        {
            state.CurrentPlayerIndex = nextIdx;
            state.Metadata["meld_turn_player"] = state.CurrentPlayer.Id;
            UpdateStatus(state);
        }
    }

    // ── Meld validation ───────────────────────────────────────────────────────

    private bool IsValidMeld(GameState state, List<string> cardIds)
    {
        // Pinochle: any non-empty selection is valid; actual scoring is at end of meld phase.
        if (_meldTypes.Contains("pinochle")) return cardIds.Count > 0;

        var hand  = PlayerHand(state, state.CurrentPlayer.Id);
        if (hand is null) return false;

        var cards = cardIds
            .Select(id => hand.Cards.FirstOrDefault(c => c.Id == id))
            .Where(c => c is not null)
            .Cast<Card>()
            .ToList();

        if (cards.Count < _minMeldSize) return false;

        if (_meldTypes.Contains("set") && IsSet(cards)) return true;
        if (_meldTypes.Contains("run") && IsRun(cards))  return true;
        return false;
    }

    /// <summary>Set: all cards share the same rank.</summary>
    private static bool IsSet(List<Card> cards)
        => cards.Select(c => c.Rank).Distinct().Count() == 1;

    /// <summary>Run: consecutive ranks of the same suit.</summary>
    private static bool IsRun(List<Card> cards)
    {
        if (cards.Select(c => c.Suit).Distinct().Count() != 1) return false;
        var ranks = cards.Select(c => (int)c.Rank).OrderBy(r => r).ToList();
        for (int i = 1; i < ranks.Count; i++)
            if (ranks[i] != ranks[i - 1] + 1) return false;
        return true;
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private void ToggleSelection(GameState state, string cardId)
    {
        var selected = GetSelected(state);
        if (selected.Contains(cardId))
            selected.Remove(cardId);
        else
            selected.Add(cardId);
        state.Metadata["meld_selected"] = string.Join(",", selected);
    }

    private static List<string> GetSelected(GameState state)
    {
        var raw = state.Metadata.GetValueOrDefault("meld_selected", "");
        return string.IsNullOrEmpty(raw) ? [] : [.. raw.Split(',')];
    }

    private static Zone? PlayerHand(GameState state, string playerId)
        => state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");

    private void UpdateStatus(GameState state)
    {
        string player = state.CurrentPlayer == state.Players[0]
            ? "Your turn" : $"{state.CurrentPlayer.Name}'s turn";
        state.Metadata["status"] = $"{player} — Select cards to meld or tap Done.";
    }

    // ── Pinochle meld scoring ─────────────────────────────────────────────────

    /// <summary>
    /// Evaluates all cards in a player's meld zone against standard pinochle meld rules.
    /// Called when the player finishes their meld turn.
    ///
    /// Counted melds (in priority order):
    ///   Flush (A K Q J 10 trump)      : 1=150, 2=1500
    ///   Around (one of each suit)     : A=100/1000, K=80/800, Q=60/600, J=40/400
    ///   Pinochle (Jd + Qs)            : 1 pair=40, 2 pairs=300
    ///   Royal marriage (KQ trump)     : 40 each (pairs not consumed by flush)
    ///   Marriage (KQ non-trump)       : 20 each pair per suit
    ///   Nine of trump (dix)           : 10 each
    /// </summary>
    private static int ScorePinochleMelds(GameState state, string playerId, string trumpName)
    {
        var meld = state.FindZone($"meld:{playerId}") ?? state.FindZone("meld");
        if (meld is null || meld.IsEmpty) return 0;

        var cards = meld.Cards.ToList();
        Suit trump = ParseSuit(trumpName);

        // Helper: count cards of a specific rank+suit in the meld zone.
        int C(Rank r, Suit s) => cards.Count(c => c.Rank == r && c.Suit == s);
        int MinAll(params int[] vals) => vals.Min();

        int score = 0;

        // ── Flush: A K Q J 10 of trump (each complete set) ───────────────────
        int aTrump = C(Rank.Ace, trump), kTrump = C(Rank.King, trump);
        int qTrump = C(Rank.Queen, trump), jTrump = C(Rank.Jack, trump), tTrump = C(Rank.Ten, trump);
        int flushes = MinAll(aTrump, kTrump, qTrump, jTrump, tTrump);
        score += flushes switch { >= 2 => 1500, 1 => 150, _ => 0 };

        // ── Around: one of each suit per rank ─────────────────────────────────
        (Rank rank, int pts1, int pts2)[] arounds =
        [
            (Rank.Ace,   100, 1000),
            (Rank.King,   80,  800),
            (Rank.Queen,  60,  600),
            (Rank.Jack,   40,  400),
        ];
        foreach (var (rank, pts1, pts2) in arounds)
        {
            int sets = MinAll(C(rank, Suit.Clubs), C(rank, Suit.Diamonds),
                              C(rank, Suit.Hearts), C(rank, Suit.Spades));
            score += sets switch { >= 2 => pts2, 1 => pts1, _ => 0 };
        }

        // ── Pinochle: Jd + Qs pairs ───────────────────────────────────────────
        int pairs = Math.Min(C(Rank.Jack, Suit.Diamonds), C(Rank.Queen, Suit.Spades));
        score += pairs switch { >= 2 => 300, 1 => 40, _ => 0 };

        // ── Royal marriage: KQ trump (K+Q pairs not already consumed by flush) ─
        int royalPairs = Math.Min(kTrump - flushes, qTrump - flushes);
        if (royalPairs > 0) score += royalPairs * 40;

        // ── Marriage: KQ non-trump (each suit) ───────────────────────────────
        foreach (Suit s in Enum.GetValues<Suit>())
        {
            if (s == trump) continue;
            int marriages = Math.Min(C(Rank.King, s), C(Rank.Queen, s));
            score += marriages * 20;
        }

        // ── Nine of trump (dix) ───────────────────────────────────────────────
        score += C(Rank.Nine, trump) * 10;

        return score;
    }

    private static Suit ParseSuit(string name) => name.ToLowerInvariant() switch
    {
        "spades"   => Suit.Spades,
        "hearts"   => Suit.Hearts,
        "diamonds" => Suit.Diamonds,
        _          => Suit.Clubs,
    };

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
