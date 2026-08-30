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
///                       "stock_exhausted": end round when the deck runs out, so a game
///                       whose players can no longer draw cannot run forever
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

    /// <summary>Zone id → the condition under which it may be drawn from.</summary>
    private readonly Dictionary<string, JsonElement> _drawRequires = [];

    /// <summary>Round number → points needed for a side's first meld. Empty if unset.</summary>
    private readonly List<(int? Round, int Points)> _initialMeldRequirement = [];

    public DrawDiscardHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId     = nextPhaseId;
        // draw_from is either zone names, or objects carrying the conditions under which
        // that zone may be drawn from:
        //   ["deck", "discard"]
        //   [ { "zone": "deck", "count": 2 },
        //     { "zone": "discard", "count": "pile", "requires": { … } } ]
        (_drawFrom, _drawRequires) = ParseDrawFrom(def);
        if (_drawFrom.Count == 0) _drawFrom = ["deck"];

        // Points a side must lay in one go before it has melded at all, by round. Real
        // Hand and Foot asks for 50, then 90, 120 and 150 as the rounds go on.
        _initialMeldRequirement = ParseInitialMeldRequirement(def);

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
                if (zone is null || zone.IsEmpty) continue;

                // A zone may carry conditions — claiming the discard pile in Hand and
                // Foot needs a side that has melded and two cards matching its top.
                // Offering the action only when it is legal beats refusing it after.
                if (_drawRequires.TryGetValue(zoneName, out var requires)
                    && !RuleCondition.Evaluate(requires, state))
                    continue;

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

            // Clear selection when cards are multi-selected for melding
            string? sel = state.Metadata.GetValueOrDefault("selected_card");
            if (!string.IsNullOrEmpty(sel) && sel.Contains(','))
                actions.Add(new GameAction("clear_selection", Label: "Clear"));
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

        if (action.Type == "clear_selection")
        {
            state.Metadata.Remove("selected_card");
            return;
        }

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

            // Multi-select mode: when meld special actions are active, toggle the card
            // in a comma-separated list so the player can assemble a 3+ card meld.
            if (_specialActions.Contains("meld") || _specialActions.Contains("add_to_meld"))
            {
                var current = (state.Metadata.GetValueOrDefault("selected_card") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                if (current.Remove(selectId))
                    state.Metadata["selected_card"] = string.Join(",", current);
                else
                {
                    current.Add(selectId);
                    state.Metadata["selected_card"] = string.Join(",", current);
                }
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

        // If no explicit discard but a single card was selected, use it as the discard.
        // Skip this fallback in multi-select meld mode (selected_card may be a comma-separated list).
        if (turnState == "discard" && !_specialActions.Contains("meld") && !_specialActions.Contains("add_to_meld"))
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

    /// <summary>
    /// Reads <c>draw_from</c> in either form — a list of zone names, or objects that also
    /// carry a count and the condition under which that zone may be drawn from. Names
    /// keep working, so no existing definition changes.
    /// </summary>
    private static (List<string> Zones, Dictionary<string, JsonElement> Requires)
        ParseDrawFrom(PhaseDefinition def)
    {
        var zones    = new List<string>();
        var requires = new Dictionary<string, JsonElement>();

        if (def.Extra?.TryGetValue("draw_from", out var element) != true
            || element.ValueKind != JsonValueKind.Array)
            return (zones, requires);

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                if (entry.GetString() is { } name) zones.Add(name);
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("zone", out var zoneEl)) continue;
            if (zoneEl.GetString() is not { } zone) continue;

            zones.Add(zone);
            if (entry.TryGetProperty("requires", out var condition))
                requires[zone] = condition.Clone();
        }

        return (zones, requires);
    }

    private static List<(int? Round, int Points)> ParseInitialMeldRequirement(PhaseDefinition def)
    {
        var tiers = new List<(int?, int)>();

        if (def.Extra?.TryGetValue("initial_meld_requirement", out var element) != true)
            return tiers;

        if (element.ValueKind == JsonValueKind.Number)
        {
            tiers.Add((null, element.GetInt32()));
            return tiers;
        }

        if (element.ValueKind != JsonValueKind.Array) return tiers;

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            int? round = entry.TryGetProperty("round", out var r) ? r.GetInt32() : null;
            int points = entry.TryGetProperty("points", out var p) ? p.GetInt32() : 0;
            tiers.Add((round, points));
        }

        return tiers;
    }

    /// <summary>
    /// Points the side to act must lay in one go, if it has not melded yet. Zero once it
    /// has, or when the game sets no requirement.
    /// </summary>
    private int RequiredOpeningMeld(GameState state)
    {
        if (_initialMeldRequirement.Count == 0) return 0;
        if (MeldZoneFor(state) is { Count: > 0 }) return 0;   // already open

        // First tier naming this round, else the first without a round — the default.
        foreach (var (round, points) in _initialMeldRequirement)
            if (round is null || round == state.RoundNumber)
                return points;

        return 0;
    }

    private static Zone? MeldZoneFor(GameState state)
    {
        var playerId = state.CurrentPlayer.Id;
        var team     = state.GetPlayerTeam(playerId);

        return (team is not null ? state.FindZone($"meld:{team.Id}") : null)
            ?? state.FindZone($"meld:{playerId}")
            ?? state.FindZone("meld");
    }

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
        var player = state.Players.FirstOrDefault(p => p.Id == playerId);
        string footMsg = player == state.Players[0] ? "You picked" : $"{player?.Name ?? "Player"} picked";
        state.Metadata["status"] = $"{footMsg} up their foot!";
    }

    private bool CheckRoundEnd(GameState state)
    {
        if (_roundEndsWhen is null) return false;

        bool triggered = _roundEndsWhen switch
        {
            "any_player_grid_all_face_up" => state.Players.Any(p =>
            {
                var g = PlayerGrid(state, p.Id);
                return g is not null && g.Count > 0 && g.Cards.All(c => c.IsFaceUp);
            }),

            // The stock is gone and cannot be replenished. Without this the game runs
            // forever: players keep drawing the single discard and putting one back,
            // hands growing, nothing able to end it. Real Hand and Foot ends the round
            // when the stock runs out, and a game that cannot terminate is a bug however
            // unlikely the position.
            "stock_exhausted" => state.FindZone("deck") is { IsEmpty: true },

            _ => false,
        };

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
    private void LayMeld(GameState state, bool addToExisting)
    {
        string? selectedRaw = state.Metadata.GetValueOrDefault("selected_card");
        if (string.IsNullOrEmpty(selectedRaw)) return;

        var selectedIds = selectedRaw.Split(',').ToHashSet();
        var hand        = PlayerHand(state, state.CurrentPlayer.Id);
        if (hand is null) return;

        var selectedCards = hand.Cards.Where(c => selectedIds.Contains(c.Id)).ToList();
        if (selectedCards.Count == 0) return;
        if (selectedCards.Count < 3 && !addToExisting) return; // need at least 3 for a new meld

        // Find the player's team meld zone, fall back to player meld zone.
        var team     = state.GetPlayerTeam(state.CurrentPlayer.Id);
        var meldZone = (team is not null ? state.FindZone($"meld:{team.Id}") : null)
                    ?? state.FindZone($"meld:{state.CurrentPlayer.Id}")
                    ?? state.FindZone("meld");
        if (meldZone is null) return;

        // A meld is cards of one rank, with wilds standing in for the rest. This was
        // documented as validated and was not: any three selected cards were accepted,
        // so a "meld" of unrelated cards was legal and then scored as though it counted.
        if (!IsValidMeld(selectedCards, out var meldRank))
        {
            state.Metadata["status"] = "That is not a meld — pick three or more of a rank.";
            return;
        }

        // A side that has not melded must open with enough in one go. The requirement
        // rises by round in Hand and Foot, which is why it is a table in the definition
        // rather than a number.
        int required = RequiredOpeningMeld(state);
        if (required > 0)
        {
            int offered = ScoringEngine.CardPointValue(state.Definition, selectedCards);
            if (offered < required)
            {
                state.Metadata["status"] =
                    $"Your first meld this round must be worth {required}; that is {offered}.";
                return;
            }
        }

        // Adding to an existing meld joins the one of the same rank, so a later card
        // lands in the meld it belongs to rather than loose in the pile.
        int targetGroup = addToExisting ? FindGroupOfRank(meldZone, meldRank) : -1;

        foreach (var card in selectedCards)
        {
            hand.Remove(card);
            card.IsFaceUp = true;
        }

        if (targetGroup >= 0)
            foreach (var card in selectedCards) meldZone.AddToGroup(targetGroup, card);
        else
            meldZone.AddGroup(selectedCards);

        state.Metadata.Remove("selected_card");
        state.Metadata["status"] = targetGroup >= 0 ? "Added to meld." : "Meld laid!";
    }

    /// <summary>
    /// A meld is three or more cards of one rank, wilds allowed as stand-ins. Returns the
    /// rank the meld is of; a selection that is all wilds has no rank and is not a meld.
    /// </summary>
    private static bool IsValidMeld(IReadOnlyList<Card> cards, out Rank rank)
    {
        rank = Rank.Joker;

        var naturals = cards.Where(c => !IsWild(c)).ToList();
        if (naturals.Count == 0) return false;

        var meldRank = naturals[0].Rank;
        rank = meldRank;
        if (naturals.Any(c => c.Rank != meldRank)) return false;

        // Wilds may not outnumber the real cards; a meld is a set with help, not a pile
        // of substitutes.
        return cards.Count - naturals.Count <= naturals.Count;
    }

    private static bool IsWild(Card card) => card.IsWild || card.Rank == Rank.Two;

    /// <summary>Index of the meld already holding this rank, or -1.</summary>
    private static int FindGroupOfRank(Zone zone, Rank rank)
    {
        for (int i = 0; i < zone.Groups.Count; i++)
            if (zone.GroupCards(i).Any(c => !IsWild(c) && c.Rank == rank))
                return i;

        return -1;
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
