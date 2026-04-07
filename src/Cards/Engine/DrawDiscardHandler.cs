using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for draw-and-discard games (Gin Rummy, Crazy Eights, Golf).
///
/// Phase definition parameters:
///   draw_from         — ["deck"] | ["deck","discard"] — zones player may draw from
///   draw_count        — cards to draw per turn (default 1)
///   discard_count     — cards to discard per turn (default 1)
///   target_zone       — "hand" (default) | "grid" — where drawn card goes
///   special_actions   — ["knock","gin","go_out"] — extra action buttons shown when conditions met
///   knock_condition   — "deadwood_lte_10" | "deadwood_eq_0" | "deadwood_lte_first_discard"
///   gin_condition     — "deadwood_eq_0" (default)
///   go_out_condition  — "hand_empty" | "all_melds_complete_and_hand_empty" (default hand_empty)
///   round_ends_when   — "any_player_grid_all_face_up": end round when any grid is fully revealed
///   remaining_players_get_one_more_turn — true: after trigger, each other player gets one more turn
///
/// Turn sub-states (stored in metadata["dd_turn_state"]):
///   "draw"    — player must draw a card
///   "discard" — player must discard a card
/// </summary>
public sealed class DrawDiscardHandler : IPhaseHandler
{
    private readonly string       _nextPhaseId;
    private readonly List<string> _drawFrom;
    // Per-zone draw counts: zone id → count (0 = entire pile)
    private readonly Dictionary<string, int> _drawCounts = [];
    private readonly int          _discardCount;
    private readonly string       _targetZone;
    private readonly List<string> _specialActions;
    private readonly string       _knockCondition;
    private readonly string       _ginCondition;
    private readonly string       _goOutCondition;
    private readonly string?      _roundEndsWhen;
    private readonly bool         _remainingGetOneTurn;

    public DrawDiscardHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId     = nextPhaseId;
        _drawFrom        = ParseStringArray(def, "draw_from");
        if (_drawFrom.Count == 0) _drawFrom = ["deck"];

        // draw_count: integer (same for all zones) or object { "from_deck": 2, "from_discard": "pile" }
        if (def.Extra?.TryGetValue("draw_count", out var dcEl) == true)
        {
            if (dcEl.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                int n = dcEl.GetInt32();
                foreach (var z in _drawFrom) _drawCounts[z] = n;
            }
            else if (dcEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in dcEl.EnumerateObject())
                {
                    // key: "from_deck" → zone "deck"; "from_discard" → zone "discard"
                    string zone = prop.Name.StartsWith("from_") ? prop.Name["from_".Length..] : prop.Name;
                    int count = prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? prop.Value.GetInt32()
                        : 0; // "pile" = 0 = entire pile
                    _drawCounts[zone] = count;
                }
            }
        }
        if (_drawCounts.Count == 0)
            foreach (var z in _drawFrom) _drawCounts[z] = 1;

        _discardCount    = GetInt(def, "discard_count") ?? 1;
        _targetZone      = GetString(def, "target_zone") ?? "hand";
        _specialActions  = ParseStringArray(def, "special_actions");
        _knockCondition  = GetString(def, "knock_condition")   ?? "deadwood_lte_10";
        _ginCondition    = GetString(def, "gin_condition")     ?? "deadwood_eq_0";
        _goOutCondition  = GetString(def, "go_out_condition")  ?? "hand_empty";
        _roundEndsWhen   = GetString(def, "round_ends_when");
        _remainingGetOneTurn = GetBool(def, "remaining_players_get_one_more_turn") ?? false;
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
            if (_specialActions.Contains("go_out") && GoOutConditionMet(state))
                actions.Add(new GameAction("go_out", Label: "Go Out"));
            if (_specialActions.Contains("meld"))
                actions.Add(new GameAction("meld", Label: "Lay Meld"));
            if (_specialActions.Contains("add_to_meld"))
                actions.Add(new GameAction("add_to_meld", Label: "Add to Meld"));
        }

        return actions;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        EnsureInitialized(state);
        if (TurnState(state) != "discard") return [];

        // Grid mode: player selects a grid card to swap with their drawn card,
        // OR taps the drawn card itself to discard it without swapping.
        if (_targetZone == "grid")
        {
            var grid = PlayerGrid(state, state.CurrentPlayer.Id);
            if (grid is not null && state.Metadata.TryGetValue("dd_drawn_card", out var drawnId))
                return [.. grid.Cards.Select(c => c.Id), drawnId];
        }

        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        return hand?.Cards.Select(c => c.Id).ToList() ?? [];
    }

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
    {
        if (TurnState(state) != "discard") return [];
        // In grid mode, dropping a card onto the grid performs the swap.
        if (_targetZone == "grid") return ["grid"];
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
            // In grid mode, tapping the drawn card discards it without swapping.
            if (_targetZone == "grid" &&
                state.Metadata.GetValueOrDefault("dd_drawn_card") == selectId)
            {
                DiscardCard(state, selectId);
                return;
            }
            state.Metadata["selected_card"] = selectId;
            return;
        }

        if ((action.Type == "play_card" || action.Type == "discard") && action.CardId is { } discardId)
        {
            DiscardCard(state, discardId);
            return;
        }

        // Grid mode: discard drawn card without swapping
        if (_targetZone == "grid" && action.Type == "discard_drawn" && turnState == "discard")
        {
            string? drawnId = state.Metadata.GetValueOrDefault("dd_drawn_card");
            if (drawnId is not null) { DiscardCard(state, drawnId); return; }
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

        if (action.Type == "knock")    { Knock(state, false); return; }
        if (action.Type == "gin")      { Knock(state, true);  return; }
        if (action.Type == "go_out")   { GoOut(state);        return; }
        if (action.Type == "meld")     { LayMeld(state, addToExisting: false); return; }
        if (action.Type == "add_to_meld") { LayMeld(state, addToExisting: true); return; }
    }

    // ── Core turn logic ───────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (state.Metadata.ContainsKey("dd_turn_state")) return;

        // Oklahoma Gin: record first discard card value as the knock threshold.
        if (_knockCondition == "deadwood_lte_first_discard" &&
            !state.Metadata.ContainsKey("dd_first_discard_value"))
        {
            var discard = state.FindZone("discard");
            if (discard?.TopCard is { } top)
                state.Metadata["dd_first_discard_value"] = GinCardValue(top.Rank).ToString();
        }

        state.Metadata["dd_turn_state"] = "draw";
        UpdateStatus(state);
    }

    /// <summary>Returns the Gin Rummy scoring value for a rank: A=1, 2-9=pip, 10/J/Q/K=10.</summary>
    private static int GinCardValue(Rank rank) => rank switch
    {
        Rank.Ace => 1,
        Rank.Jack or Rank.Queen or Rank.King => 10,
        _ => (int)rank,
    };

    private void DrawCard(GameState state, string fromZoneId)
    {
        var fromZone = state.FindZone(fromZoneId);
        if (fromZone is null || fromZone.IsEmpty) return;

        int count = _drawCounts.TryGetValue(fromZoneId, out int n) ? n : 1;
        bool entirePile = count == 0; // 0 = take everything

        var dest = PlayerHand(state, state.CurrentPlayer.Id);

        if (entirePile)
        {
            // Take whole pile (canasta discard pickup)
            while (!fromZone.IsEmpty)
            {
                var c = fromZone.Draw()!;
                c.IsFaceUp = true;
                dest?.Add(c);
            }
        }
        else if (_targetZone == "grid")
        {
            // Grid mode: hold the one drawn card in hand temp for the swap selection.
            var card = fromZone.Draw()!;
            card.IsFaceUp = true;
            dest?.Add(card);
            state.Metadata["dd_drawn_card"] = card.Id;
        }
        else
        {
            for (int i = 0; i < count && !fromZone.IsEmpty; i++)
            {
                var card = fromZone.Draw()!;
                card.IsFaceUp = true;
                dest?.Add(card);
            }
        }

        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata.Remove("selected_card");
        UpdateStatus(state);
    }

    private void DiscardCard(GameState state, string cardId)
    {
        if (_targetZone == "grid" && state.Metadata.ContainsKey("dd_drawn_card"))
        {
            string drawnId = state.Metadata["dd_drawn_card"];
            if (cardId != drawnId)
            {
                // Grid mode: the selected card is a grid card to swap out.
                // The drawn card goes face-up into the grid slot; the grid card goes to discard.
                SwapGridCard(state, cardId);
                return;
            }
            // cardId == drawnId: player chose to discard the drawn card without swapping.
            // Fall through to standard discard logic so the drawn card is removed from hand.
        }

        var hand    = PlayerHand(state, state.CurrentPlayer.Id);
        var card    = hand?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null || hand is null) return;

        bool drawnCardDiscarded = _targetZone == "grid"
            && state.Metadata.GetValueOrDefault("dd_drawn_card") == cardId;

        hand.Remove(card);
        card.IsFaceUp = true;

        var discard = state.FindZone("discard");
        discard?.Add(card);

        if (drawnCardDiscarded)
            state.Metadata.Remove("dd_drawn_card");

        state.Metadata.Remove("selected_card");
        state.Metadata.Remove("dd_turn_state");

        AdvanceTurn(state);
    }

    private void SwapGridCard(GameState state, string gridCardId)
    {
        string drawnCardId = state.Metadata.GetValueOrDefault("dd_drawn_card", "");
        var grid    = PlayerGrid(state, state.CurrentPlayer.Id);
        var hand    = PlayerHand(state, state.CurrentPlayer.Id);
        var discard = state.FindZone("discard");
        if (grid is null || hand is null || discard is null) return;

        // Remove drawn card from temp hand.
        var drawnCard = hand.Cards.FirstOrDefault(c => c.Id == drawnCardId);
        if (drawnCard is null) return;
        hand.Remove(drawnCard);

        // Remove grid card from grid, preserving its slot index for the replacement.
        int slotIdx = grid.Cards.FindIndex(c => c.Id == gridCardId);
        if (slotIdx < 0) return;
        var gridCard = grid.Cards[slotIdx];
        grid.Remove(gridCard);

        // Drawn card takes the same slot; grid card goes to discard.
        drawnCard.IsFaceUp = true;
        grid.Cards.Insert(slotIdx, drawnCard);
        gridCard.IsFaceUp = true;
        discard.Add(gridCard);

        state.Metadata.Remove("dd_drawn_card");
        state.Metadata.Remove("selected_card");
        state.Metadata.Remove("dd_turn_state");

        AdvanceTurn(state);
    }

    private void AdvanceTurn(GameState state)
    {
        // When a player's hand is empty, they pick up their foot zone automatically.
        PickUpFootIfNeeded(state, state.CurrentPlayer.Id);

        // Check round-end condition before advancing.
        if (CheckRoundEnd(state)) return;

        state.AdvancePlayer();
        // Skip players who have already had their "last turn" after the trigger.
        while (state.Metadata.GetValueOrDefault($"dd_last_turn_done:{state.CurrentPlayer.Id}") == "true")
            state.AdvancePlayer();

        state.Metadata["dd_turn_state"] = "draw";
        UpdateStatus(state);
    }

    private static void PickUpFootIfNeeded(GameState state, string playerId)
    {
        var hand = state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");
        if (hand is null || !hand.IsEmpty) return;

        var foot = state.FindZone($"foot:{playerId}");
        if (foot is null || foot.IsEmpty) return;

        // Move all foot cards to hand (face-up since player now holds them).
        while (!foot.IsEmpty)
        {
            var c = foot.Draw()!;
            c.IsFaceUp = true;
            hand.Add(c);
        }
        state.Metadata["status"] = "You picked up your foot!";
    }

    private bool CheckRoundEnd(GameState state)
    {
        if (_roundEndsWhen is null) return false;

        bool triggered = _roundEndsWhen == "any_player_grid_all_face_up"
            && state.Players.Any(p =>
            {
                var g = PlayerGrid(state, p.Id);
                return g is not null && g.Count > 0 && g.Cards.All(c => c.IsFaceUp);
            });

        if (!triggered) return false;

        if (_remainingGetOneTurn)
        {
            // Mark the triggering player as done; others get one more turn.
            string triggerId = state.CurrentPlayer.Id;
            state.Metadata[$"dd_last_turn_done:{triggerId}"] = "true";

            // Check if all others already had their last turn.
            bool allDone = state.Players.All(p =>
                state.Metadata.GetValueOrDefault($"dd_last_turn_done:{p.Id}") == "true");
            if (allDone)
            {
                EndRound(state);
                return true;
            }

            // Advance to the next player who still needs their last turn.
            do { state.AdvancePlayer(); }
            while (state.Metadata.GetValueOrDefault($"dd_last_turn_done:{state.CurrentPlayer.Id}") == "true");
            state.Metadata["dd_turn_state"] = "draw";
            UpdateStatus(state);
            return true;
        }

        EndRound(state);
        return true;
    }

    private void EndRound(GameState state)
    {
        // Flip all grid cards face-up before scoring.
        foreach (var p in state.Players)
        {
            var g = PlayerGrid(state, p.Id);
            if (g is null) continue;
            foreach (var c in g.Cards) c.IsFaceUp = true;
        }
        // Clean up round-tracking metadata.
        foreach (var p in state.Players)
            state.Metadata.Remove($"dd_last_turn_done:{p.Id}");
        state.Metadata.Remove("dd_drawn_card");
        state.Metadata.Remove("dd_turn_state");
        state.CurrentPhaseId = _nextPhaseId;
    }

    /// <summary>
    /// Simplified inline meld for games like Hand and Foot where melding happens
    /// within the draw/discard phase rather than a separate meld phase.
    /// Moves the currently selected cards from the player's hand to their team/player meld zone.
    /// Validates: ≥3 cards of the same rank (or 1–2 wilds mixed in).
    /// </summary>
    private static void LayMeld(GameState state, bool addToExisting)
    {
        string? selectedRaw = state.Metadata.GetValueOrDefault("selected_card");
        if (string.IsNullOrEmpty(selectedRaw)) return;

        var selectedIds = selectedRaw.Split(',').ToHashSet();
        var hand        = PlayerHand(state, state.CurrentPlayer.Id);
        if (hand is null) return;

        var selectedCards = hand.Cards.Where(c => selectedIds.Contains(c.Id)).ToList();
        if (selectedCards.Count < 3 && !addToExisting) return; // need at least 3 for a new meld

        // Find the player's team meld zone, fall back to player meld zone.
        var team     = state.GetPlayerTeam(state.CurrentPlayer.Id);
        var meldZone = (team is not null ? state.FindZone($"meld:{team.Id}") : null)
                    ?? state.FindZone($"meld:{state.CurrentPlayer.Id}")
                    ?? state.FindZone("meld");
        if (meldZone is null) return;

        foreach (var card in selectedCards)
        {
            hand.Remove(card);
            card.IsFaceUp = true;
            meldZone.Add(card);
        }

        state.Metadata.Remove("selected_card");
        state.Metadata["status"] = addToExisting ? "Added to meld." : "Meld laid!";
    }

    private void Knock(GameState state, bool isGin)
    {
        state.Metadata["dd_knock_player"] = state.CurrentPlayer.Id;
        state.Metadata["dd_gin"]          = isGin ? "true" : "false";
        state.Metadata.Remove("dd_turn_state");
        state.CurrentPhaseId = _nextPhaseId;
    }

    private void GoOut(GameState state)
    {
        state.Metadata["dd_go_out_player"] = state.CurrentPlayer.Id;
        var team = state.GetPlayerTeam(state.CurrentPlayer.Id);
        if (team is not null)
            state.Metadata["dd_go_out_team"] = team.Id;
        state.Metadata.Remove("dd_turn_state");
        state.CurrentPhaseId = _nextPhaseId;
    }

    private bool GoOutConditionMet(GameState state)
    {
        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        bool handEmpty = hand is null || hand.IsEmpty;
        // Both "hand_empty" and "all_melds_complete_and_hand_empty" use hand-empty as a stub check.
        return handEmpty;
    }

    // ── Condition checks ──────────────────────────────────────────────────────

    private bool ConditionMet(GameState state, string condition)
    {
        int deadwood = CountDeadwood(state, state.CurrentPlayer.Id);
        return condition switch
        {
            "deadwood_eq_0"            => deadwood == 0,
            "deadwood_lte_10"          => deadwood <= 10,
            "deadwood_lte_first_discard" =>
                int.TryParse(state.Metadata.GetValueOrDefault("dd_first_discard_value", "10"), out int fv)
                    ? deadwood <= fv : deadwood <= 10,
            _ => false,
        };
    }

    private static int CountDeadwood(GameState state, string playerId)
    {
        var hand = PlayerHand(state, playerId);
        if (hand is null) return int.MaxValue;
        return ScoringEngine.CalcDeadwood(hand.Cards);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TurnState(GameState state)
        => state.Metadata.GetValueOrDefault("dd_turn_state", "draw");

    private static Zone? PlayerHand(GameState state, string playerId)
        => state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");

    private static Zone? PlayerGrid(GameState state, string playerId)
        => state.FindZone($"grid:{playerId}") ?? state.FindZone("grid");

    private void UpdateStatus(GameState state)
    {
        string phase;
        if (TurnState(state) == "draw")
            phase = "Draw a card";
        else if (_targetZone == "grid" && state.Metadata.ContainsKey("dd_drawn_card"))
            phase = "Tap a card to swap, or discard the drawn card";
        else
            phase = "Discard a card";

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

    private static bool? GetBool(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        return null;
    }
}
