using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for meld lay-down phases (Gin Rummy, Pinochle, Hand and Foot).
///
/// Phase definition parameters:
///   meld_types      — ["set","run"] | ["canasta"] | ["pinochle"]
///   min_meld_size   — minimum cards in a meld (default 3)
///   wilds_allowed   — true | false (default false)
///   max_wilds_per_meld — max wilds in a single meld (default 1)
///   layoff_allowed  — true | false: add to existing melds (default true)
///
/// Players tap cards to select a group, then tap "Lay Meld" to place it.
/// "Done" ends the meld phase for the current player.
///
/// State metadata keys:
///   meld_turn_player — player ID currently laying melds
///   meld_selected    — comma-separated selected card IDs
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

        if (selected.Count >= _minMeldSize && IsValidMeld(state, selected))
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
