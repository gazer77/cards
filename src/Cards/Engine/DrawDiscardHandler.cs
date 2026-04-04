using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for draw-and-discard games (Gin Rummy, Crazy Eights, Golf).
///
/// Phase definition parameters:
///   draw_from       — ["deck"] | ["deck","discard"] — zones player may draw from
///   draw_count      — cards to draw per turn (default 1)
///   discard_count   — cards to discard per turn (default 1)
///   target_zone     — "hand" (default) | "grid" — where drawn card goes
///   special_actions — ["knock","gin"] — extra action buttons shown when conditions met
///   knock_condition — "deadwood_lte_10" | "deadwood_eq_0" | "deadwood_lte_first_discard"
///   gin_condition   — "deadwood_eq_0" (default)
///
/// Turn sub-states (stored in metadata["dd_turn_state"]):
///   "draw"    — player must draw a card
///   "discard" — player must discard a card
/// </summary>
public sealed class DrawDiscardHandler : IPhaseHandler
{
    private readonly string       _nextPhaseId;
    private readonly List<string> _drawFrom;
    private readonly int          _drawCount;
    private readonly int          _discardCount;
    private readonly string       _targetZone;
    private readonly List<string> _specialActions;
    private readonly string       _knockCondition;
    private readonly string       _ginCondition;

    public DrawDiscardHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId     = nextPhaseId;
        _drawFrom        = ParseStringArray(def, "draw_from");
        if (_drawFrom.Count == 0) _drawFrom = ["deck"];
        _drawCount       = GetInt(def, "draw_count")    ?? 1;
        _discardCount    = GetInt(def, "discard_count") ?? 1;
        _targetZone      = GetString(def, "target_zone") ?? "hand";
        _specialActions  = ParseStringArray(def, "special_actions");
        _knockCondition  = GetString(def, "knock_condition") ?? "deadwood_lte_10";
        _ginCondition    = GetString(def, "gin_condition")   ?? "deadwood_eq_0";
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        string turnState = TurnState(state);
        var actions = new List<GameAction>();

        if (turnState == "draw")
        {
            foreach (var zoneName in _drawFrom)
            {
                var zone = state.FindZone(zoneName);
                if (zone is not null && !zone.IsEmpty)
                    actions.Add(new GameAction($"draw_from_{zoneName}", Label: $"Draw from {Capitalize(zoneName)}"));
            }
        }
        else // discard
        {
            // Special actions available after drawing (before discarding)
            if (_specialActions.Contains("gin") && ConditionMet(state, _ginCondition))
                actions.Add(new GameAction("gin", Label: "Gin!"));
            if (_specialActions.Contains("knock") && ConditionMet(state, _knockCondition))
                actions.Add(new GameAction("knock", Label: "Knock"));
        }

        return actions;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        EnsureInitialized(state);
        if (TurnState(state) != "discard") return [];

        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        return hand?.Cards.Select(c => c.Id).ToList() ?? [];
    }

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
    {
        if (TurnState(state) != "discard") return [];
        return ["discard"];
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);
        string turnState = TurnState(state);

        if (action.Type.StartsWith("draw_from_"))
        {
            string zoneId = action.Type["draw_from_".Length..];
            DrawCard(state, zoneId);
            return;
        }

        if (action.Type == "select_card" && action.CardId is { } selectId)
        {
            state.Metadata["selected_card"] = selectId;
            return;
        }

        if ((action.Type == "play_card" || action.Type == "discard") && action.CardId is { } discardId)
        {
            DiscardCard(state, discardId);
            return;
        }

        // If no explicit discard but a card was selected, use selected_card
        if (turnState == "discard")
        {
            string? selected = state.Metadata.GetValueOrDefault("selected_card");
            if (selected is not null)
            {
                DiscardCard(state, selected);
                return;
            }
        }

        if (action.Type == "knock") { Knock(state, false); return; }
        if (action.Type == "gin")   { Knock(state, true);  return; }
    }

    // ── Core turn logic ───────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (state.Metadata.ContainsKey("dd_turn_state")) return;
        state.Metadata["dd_turn_state"] = "draw";
        UpdateStatus(state);
    }

    private void DrawCard(GameState state, string fromZoneId)
    {
        var fromZone = state.FindZone(fromZoneId);
        if (fromZone is null || fromZone.IsEmpty) return;

        var card = fromZone.Draw()!;
        card.IsFaceUp = true;

        var dest = PlayerHand(state, state.CurrentPlayer.Id);
        dest?.Add(card);

        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata.Remove("selected_card");
        UpdateStatus(state);
    }

    private void DiscardCard(GameState state, string cardId)
    {
        var hand    = PlayerHand(state, state.CurrentPlayer.Id);
        var card    = hand?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null || hand is null) return;

        hand.Remove(card);
        card.IsFaceUp = true;

        var discard = state.FindZone("discard");
        discard?.Add(card);

        state.Metadata.Remove("selected_card");
        state.Metadata.Remove("dd_turn_state");

        // Advance to next player
        state.AdvancePlayer();
        state.Metadata["dd_turn_state"] = "draw";
        UpdateStatus(state);
    }

    private void Knock(GameState state, bool isGin)
    {
        state.Metadata["dd_knock_player"] = state.CurrentPlayer.Id;
        state.Metadata["dd_gin"]          = isGin ? "true" : "false";
        state.Metadata.Remove("dd_turn_state");
        state.CurrentPhaseId = _nextPhaseId;
    }

    // ── Condition checks ──────────────────────────────────────────────────────

    private bool ConditionMet(GameState state, string condition)
    {
        // Simplified deadwood check: count unmelded card values in hand
        int deadwood = CountDeadwood(state, state.CurrentPlayer.Id);
        return condition switch
        {
            "deadwood_eq_0"  => deadwood == 0,
            "deadwood_lte_10" => deadwood <= 10,
            _ => false,
        };
    }

    private static int CountDeadwood(GameState state, string playerId)
    {
        var hand = PlayerHand(state, playerId);
        if (hand is null) return int.MaxValue;

        // Simple approximation: face cards = 10, A = 1, else pip value
        // Full meld detection requires MeldHandler — this stub enables knock/gin actions
        return hand.Cards.Sum(c => c.Rank switch
        {
            Rank.Ace  => 1,
            >= Rank.Jack => 10,
            _ => (int)c.Rank
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TurnState(GameState state)
        => state.Metadata.GetValueOrDefault("dd_turn_state", "draw");

    private static Zone? PlayerHand(GameState state, string playerId)
        => state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");

    private void UpdateStatus(GameState state)
    {
        string phase  = TurnState(state) == "draw" ? "Draw a card" : "Discard a card";
        string player = state.CurrentPlayer == state.Players[0]
            ? "Your turn" : $"{state.CurrentPlayer.Name}'s turn";
        state.Metadata["status"] = $"{player} — {phase}";
    }

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

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
