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
///   go_out_condition  — "hand_empty" (default), or a condition object that must also hold
///                       — being out of cards (hand and foot) is required underneath either
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

    /// <summary>
    /// What a side must have on the table before going out, when go_out_condition is
    /// a condition rather than a name — Hand and Foot's two books. Being out of cards
    /// is required underneath whatever this says.
    /// </summary>
    private readonly JsonElement? _goOutRequires;
    private readonly string?      _roundEndsWhen;
    private readonly bool         _remainingGetOneTurn;
    // Golf: a player who discards the drawn card unplayed must then turn one of their
    // own face-down cards up. The turn is not over until they have.
    private readonly bool         _flipAfterDiscard;

    // Grid mode: whether a tap proposes and a button commits, rather than a tap doing
    // it. The drawn card sits where a player reaches past it, and the tap that meant
    // "replace this one" was ending the turn instead.
    private readonly bool         _confirm;

    /// <summary>Zone id → the condition under which it may be drawn from.</summary>
    private readonly Dictionary<string, JsonElement> _drawRequires = [];

    /// <summary>Round number → points needed for a side's first meld. Empty if unset.</summary>
    private readonly List<(int? Round, int Points)> _initialMeldRequirement = [];

    /// <summary>Zone id → what drawing there obliges the player to do next.</summary>
    private readonly Dictionary<string, string> _drawObligations = [];

    /// <summary>Ranks the definition forbids melding — Hand and Foot's 3s.</summary>
    private readonly HashSet<Rank> _unmeldableRanks = [];

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

        foreach (var name in ParseStringArray(def, "unmeldable_ranks"))
            if (MeldRules.ParseRank(name) is { } rank)
                _unmeldableRanks.Add(rank);

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
        if (def.Extra?.TryGetValue("go_out_condition", out var goOut) == true
            && goOut.ValueKind == JsonValueKind.Object)
            _goOutRequires = goOut.Clone();
        _roundEndsWhen   = GetString(def, "round_ends_when");
        _remainingGetOneTurn = GetBool(def, "remaining_players_get_one_more_turn") ?? false;
        _flipAfterDiscard    = GetBool(def, "flip_after_discard") ?? false;
        _confirm             = GetBool(def, "confirm") ?? false;
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);
        string turnState = TurnState(state);
        var actions = new List<GameAction>();

        // Conditions are evaluated against the state alone, so what this phase asks of
        // an opening meld has to be readable from there. Published every time rather
        // than once, because the requirement rises by round — and only by the games
        // that have one, since state is hashed and a game without openings should not
        // change shape because another game needed a value.
        if (_initialMeldRequirement.Count > 0)
            state.Metadata["dd_opening_requirement"] = RequiredOpeningMeld(state).ToString();

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

                actions.Add(new GameAction($"draw_from_{zoneName}",
                    Label: GameText.Action(state, $"draw_from_{zoneName}", $"Draw from {Capitalize(zoneName)}")));
            }
        }
        else // discard
        {
            // A card owed to the table blocks everything that would end the turn: the
            // obligation is the price of the pickup, so it cannot be walked away from.
            bool owesMeld = state.Metadata.ContainsKey("dd_must_meld");

            // Grid mode asks before it acts, when the definition says to. Tapping the
            // drawn card used to discard it there and then, which is the same gesture a
            // player makes when reaching past it for the card they meant to replace —
            // and the turn was over before they saw it go.
            if (_targetZone == "grid" && Confirming(state))
            {
                if (state.Metadata.ContainsKey("dd_must_flip"))
                {
                    if (state.Metadata.GetValueOrDefault("selected_card") is { Length: > 0 })
                        actions.Add(new GameAction("flip", Label: GameText.Action(state, "flip", "Flip")));
                }
                else if (state.Metadata.ContainsKey("dd_drawn_card"))
                {
                    actions.Add(new GameAction("discard_drawn",
                        Label: GameText.Action(state, "discard_drawn", "Discard Drawn")));
                }
            }

            // Special actions available after drawing (before discarding)
            if (_specialActions.Contains("gin") && ConditionMet(state, _ginCondition))
                actions.Add(new GameAction("gin", Label: GameText.Action(state, "gin", "Gin!")));
            if (_specialActions.Contains("knock") && ConditionMet(state, _knockCondition))
                actions.Add(new GameAction("knock", Label: GameText.Action(state, "knock", "Knock")));
            if (!owesMeld && _specialActions.Contains("go_out") && GoOutConditionMet(state))
                actions.Add(new GameAction("go_out", Label: GameText.Action(state, "go_out", "Go Out")));
            string? sel = state.Metadata.GetValueOrDefault("selected_card");

            // Meld buttons appear only when pressing them would do something. Offering
            // them unconditionally meant a button whose only job was to say no — and a
            // player with 3s picked saw "Lay Meld", pressed it, and was scolded. The
            // reason the selection will not lay goes on the status line instead, so the
            // absence of a button explains itself.
            string? whyNot = null;
            if (_specialActions.Contains("meld"))
            {
                if (PlanMeld(state, addToExisting: false, out var meldReason) is not null)
                    actions.Add(new GameAction("meld", Label: GameText.Action(state, "meld", "Lay Meld")));
                else whyNot ??= meldReason;
            }
            if (_specialActions.Contains("add_to_meld"))
            {
                if (PlanMeld(state, addToExisting: true, out var addReason) is not null)
                    actions.Add(new GameAction("add_to_meld", Label: GameText.Action(state, "add_to_meld", "Add to Meld")));
                else whyNot ??= addReason;
            }

            // Only a multi-card selection is an attempted meld; a single card is a
            // discard in waiting and its "not a meld" reason would just be noise.
            if (whyNot is not null && !string.IsNullOrEmpty(sel) && sel.Contains(','))
                state.Metadata["status"] = whyNot;

            // Discarding is offered once the selection is the size the definition asks
            // for. In a game that also melds, the same selection means two things, so
            // the player needs a way to say which — tapping the pile is the other.
            int selectedCount = string.IsNullOrEmpty(sel)
                ? 0
                : sel.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
            if (!owesMeld && selectedCount == _discardCount && _targetZone != "grid")
                actions.Add(new GameAction("discard", Label: GameText.Action(state, "discard", "Discard")));

            // Clear selection when cards are multi-selected for melding
            if (!string.IsNullOrEmpty(sel) && sel.Contains(','))
                actions.Add(new GameAction("clear_selection", Label: GameText.Action(state, "clear_selection", "Clear")));
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
            // A flip owed after discarding: only the face-down cards can answer it.
            if (grid is not null && state.Metadata.ContainsKey("dd_must_flip"))
                return grid.Cards.Where(c => !c.IsFaceUp).Select(c => c.Id).ToList();
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

        // The Flip button: turn the card picked for the obligation.
        if (_targetZone == "grid" && action.Type == "flip"
            && state.Metadata.ContainsKey("dd_must_flip")
            && state.Metadata.GetValueOrDefault("selected_card") is { Length: > 0 } pickedToken
            && CardFromToken(state, pickedToken) is { IsFaceUp: false } pickedCard)
        {
            FlipOwed(state, pickedCard);
            return;
        }

        // A flip owed after discarding the drawn card. An agent sends play_card, a tap
        // sends select_card; both must answer the obligation, or the AI's turn — its
        // play_card having fallen through to a discard from an empty hand — never ends.
        if (_targetZone == "grid" && state.Metadata.ContainsKey("dd_must_flip")
            && action.Type is "select_card" or "play_card" or "flip_card" && action.CardId is not null)
        {
            var grid = PlayerGrid(state, state.CurrentPlayer.Id);
            var toFlip = action.CardUid is int flipUid
                ? grid?.Cards.FirstOrDefault(c => c.Uid == flipUid)
                : grid?.Cards.FirstOrDefault(c => c.Id == action.CardId && !c.IsFaceUp);
            if (toFlip is null || toFlip.IsFaceUp) return;

            // Picked rather than turned, when this game asks first — a double tap
            // (flip_card) says the player is sure and skips the asking.
            if (action.Type != "flip_card" && Confirming(state))
            {
                string pick = toFlip.Uid.ToString();
                if (state.Metadata.GetValueOrDefault("selected_card") == pick)
                    state.Metadata.Remove("selected_card");
                else
                    state.Metadata["selected_card"] = pick;
                UpdateStatus(state);
                return;
            }

            FlipOwed(state, toFlip);
            return;
        }

        if (action.Type == "select_card" && action.CardId is { } selectId)
        {
            var current = (state.Metadata.GetValueOrDefault("selected_card") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            // Selection is by physical card, not description: a five-deck game holds
            // several cards answering to "4h", and selecting one used to light them
            // all — the opponent's included — while making a second copy unselectable,
            // since its id was already "in" the selection. A tap carries the uid; an
            // agent sending only the id gets the first copy not yet selected, so
            // selecting "4h" twice means two fours rather than a toggle.
            var chosen = ResolveCard(state, action.CardUid, selectId, current);
            if (chosen is null) return;
            string token = chosen.Uid.ToString();

            // In grid mode, tapping the drawn card discards it without swapping — or,
            // when the game asks first, only picks it out, and the Discard button ends
            // the turn. The drawn-card channel stays id-keyed: it holds exactly one
            // card, so the ambiguity uids exist for cannot arise there.
            if (_targetZone == "grid" &&
                state.Metadata.GetValueOrDefault("dd_drawn_card") == chosen.Id)
            {
                if (Confirming(state))
                {
                    if (state.Metadata.GetValueOrDefault("selected_card") == token)
                        state.Metadata.Remove("selected_card");
                    else
                        state.Metadata["selected_card"] = token;
                    return;
                }

                DiscardCard(state, token);
                return;
            }

            // In grid mode, tapping a grid card while holding the drawn card swaps them
            // then and there. It used to only select, and the swap waited on a second
            // gesture — dropping onto the grid — that nothing on screen suggested.
            if (_targetZone == "grid" && state.Metadata.ContainsKey("dd_drawn_card")
                && PlayerGrid(state, state.CurrentPlayer.Id)?.Cards.Contains(chosen) == true)
            {
                SwapGridCard(state, chosen);
                return;
            }

            // Multi-select mode: when meld special actions are active, toggle the card
            // in a comma-separated list so the player can assemble a 3+ card meld.
            if (_specialActions.Contains("meld") || _specialActions.Contains("add_to_meld"))
            {
                if (!current.Remove(token)) current.Add(token);
                state.Metadata["selected_card"] = string.Join(",", current);
                return;
            }

            state.Metadata["selected_card"] = token;
            return;
        }

        if ((action.Type == "play_card" || action.Type == "discard") && action.CardId is { } discardId)
        {
            DiscardCard(state, discardId);
            return;
        }

        // A Discard button carries no card: the selection is the card. Without this the
        // action fell through to the meld-mode guard below and did nothing at all.
        if (action.Type == "discard"
            && state.Metadata.GetValueOrDefault("selected_card") is { Length: > 0 } picked
            && !picked.Contains(','))
        {
            DiscardCard(state, picked);
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
    private (List<string> Zones, Dictionary<string, JsonElement> Requires)
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

            // What the player owes the table for having drawn here.
            if (entry.TryGetProperty("then_must", out var owed)
                && owed.GetString() is { Length: > 0 } obligation)
                _drawObligations[zone] = obligation;
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

        // The card the pile was claimed for, read before anything moves.
        var claimed = fromZone.TopCard;

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

        if (dest is not null) ZoneIntake.Settle(state, dest);

        // Claiming a pile can come with a condition attached: in Hand and Foot the card
        // you claimed it for must go down this turn, which is what stops the pickup
        // being a free way to fatten a hand.
        if (_drawObligations.TryGetValue(fromZoneId, out var obligation)
            && obligation == "meld_top_card"
            && claimed is not null)
            state.Metadata["dd_must_meld"] = claimed.Id;

        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata.Remove("selected_card");
        UpdateStatus(state);
    }

    private void DiscardCard(GameState state, string cardId)
    {
        // Owing a meld outranks every route to a discard — the button, the drop, and
        // the "one card selected" fallback all arrive here.
        if (state.Metadata.TryGetValue("dd_must_meld", out var owed))
        {
            state.Metadata["status"] = GameText.Message(state, "must_meld_first",
                "The {card} taken must be melded before discarding.",
                state.CurrentPlayer.Id, ("card", CardName(state, owed)));
            return;
        }

        // Any token form — a uid from a tap, an id from drag-drop or an agent.
        var card = CardFromToken(state, cardId);
        if (card is null) return;

        if (_targetZone == "grid" && state.Metadata.ContainsKey("dd_drawn_card"))
        {
            if (card.Id != state.Metadata["dd_drawn_card"])
            {
                // Grid mode: the selected card is a grid card to swap out.
                // The drawn card goes face-up into the grid slot; the grid card goes to discard.
                SwapGridCard(state, card);
                return;
            }
            // Player chose to discard the drawn card without swapping. Fall through to
            // standard discard logic so the drawn card is removed from hand.
        }

        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        if (hand is null || !hand.Cards.Contains(card)) return;

        bool drawnCardDiscarded = _targetZone == "grid"
            && state.Metadata.GetValueOrDefault("dd_drawn_card") == card.Id;

        hand.Remove(card);
        card.IsFaceUp = true;

        var discard = state.FindZone("discard");
        discard?.Add(card);

        if (drawnCardDiscarded)
            state.Metadata.Remove("dd_drawn_card");

        state.Metadata.Remove("selected_card");

        // Discarding the draw unplayed owes a flip while there is still a card to turn.
        if (drawnCardDiscarded && _flipAfterDiscard
            && PlayerGrid(state, state.CurrentPlayer.Id)?.Cards.Any(c => !c.IsFaceUp) == true)
        {
            state.Metadata["dd_must_flip"] = "true";
            UpdateStatus(state);
            return;
        }

        state.Metadata.Remove("dd_turn_state");

        AdvanceTurn(state);
    }

    private void SwapGridCard(GameState state, Card gridCard)
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
        int slotIdx = grid.Cards.IndexOf(gridCard);
        if (slotIdx < 0) return;
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


    /// <summary>
    /// Whether this seat is asked before an irreversible tap goes through — the drawn
    /// card being discarded, or the card owed to the flip.
    ///
    /// Only people are asked. An agent has no mind to change, and waiting for it to
    /// press its own button would be a pause with nothing behind it.
    /// </summary>
    private bool Confirming(GameState state)
        => _confirm && !state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id);

    /// <summary>Turns the card owed after discarding the draw, and ends the turn.</summary>
    private void FlipOwed(GameState state, Card card)
    {
        card.IsFaceUp = true;
        state.Metadata.Remove("dd_must_flip");
        state.Metadata.Remove("selected_card");
        state.Metadata.Remove("dd_turn_state");
        AdvanceTurn(state);
    }

    /// <summary>
    /// What a double tap on a card means here: discard the card just drawn, turn the
    /// card owed to a flip, or swap a grid card for the one in hand. Each is the thing
    /// a single tap proposes, done without the asking.
    /// </summary>
    public GameAction? DefaultCardAction(GameState state, string cardId, int? uid)
    {
        if (_targetZone != "grid" || TurnState(state) != "discard") return null;

        if (state.Metadata.ContainsKey("dd_must_flip"))
            return new GameAction("flip_card", CardId: cardId, CardUid: uid);

        if (state.Metadata.GetValueOrDefault("dd_drawn_card") == cardId)
            return new GameAction("discard_drawn");

        return null;   // a grid card already swaps on a single tap
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
        // Revealing the foot is another way cards reach the hand, and the same rules
        // apply to them: a red three in the foot goes straight to the threes pile.
        ZoneIntake.Settle(state, hand);

        state.Metadata["status"] = GameText.Message(state, "foot_picked_up",
            "{player} picked up their foot!", forPlayerId: playerId);
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
    /// Inline meld for games like Hand and Foot where melding happens within the
    /// draw/discard phase rather than a separate meld phase.
    ///
    /// A selection may hold SEVERAL melds at once — three tens and three queens laid
    /// together. That is not a convenience: an opening minimum of 50 often cannot be met
    /// by any single meld a hand holds, so one-meld-per-action made the requirement
    /// unsatisfiable at exactly the moment it applied.
    /// </summary>
    /// <summary>A lay the table would accept: which cards, into which melds, joining which group.</summary>
    private sealed record MeldPlan(
        List<Card> Cards, List<List<Card>> Melds, int AddTarget, Zone MeldZone, HashSet<Rank> Wilds);

    /// <summary>
    /// Works out whether the current selection could be laid, and how. Returns the plan,
    /// or null with <paramref name="reason"/> saying why not — in the player's words,
    /// since the same text is what the status line shows.
    ///
    /// One judgment for two callers: the action list offers Lay Meld only when this
    /// succeeds, and Apply lays exactly what this planned. Splitting them would let the
    /// button and the refusal drift apart, so a button appears that then says no.
    /// </summary>
    private MeldPlan? PlanMeld(GameState state, bool addToExisting, out string? reason)
    {
        reason = null;
        string? selectedRaw = state.Metadata.GetValueOrDefault("selected_card");

        // A meld action with nothing picked, while a card is owed, assembles the debt
        // itself: the owed card, its rank-mates, and — if the side still has to open —
        // everything else layable, which is exactly what the pickup condition counted
        // when it allowed the claim. This is how an agent pays; a player who has
        // selected cards keeps their own selection.
        if (string.IsNullOrEmpty(selectedRaw) && !addToExisting
            && state.Metadata.TryGetValue("dd_must_meld", out var owedId))
            selectedRaw = AssembleOwedMeld(state, owedId);

        if (string.IsNullOrEmpty(selectedRaw)) return null;   // nothing picked: no reason to give

        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        if (hand is null) return null;

        // Tokens are uids, so two physical fours are two cards here — matching by id
        // collapsed identical copies into one and made a natural pair unmeldable.
        var selectedCards = selectedRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => CardFromToken(state, t))
            .OfType<Card>()
            .Where(hand.Cards.Contains)
            .Distinct()
            .ToList();
        if (selectedCards.Count == 0) return null;

        var team     = state.GetPlayerTeam(state.CurrentPlayer.Id);
        var meldZone = (team is not null ? state.FindZone($"meld:{team.Id}") : null)
                    ?? state.FindZone($"meld:{state.CurrentPlayer.Id}")
                    ?? state.FindZone("meld");
        if (meldZone is null) return null;

        // What is wild comes from the definition (scoring.wild_cards), not from the
        // handler: 2s are wild in Hand and Foot because its rules say so, not because
        // every draw-discard game agrees.
        var wilds = MeldRules.WildRanks(state.Definition);

        // Ranks the definition forbids melding — Hand and Foot's 3s, which exist to be
        // discarded and score against you, never to be laid.
        var barred = selectedCards.FirstOrDefault(
            c => !MeldRules.IsWild(c, wilds) && _unmeldableRanks.Contains(c.Rank));
        if (barred is not null)
        {
            reason = GameText.Message(state, "rank_unmeldable", "{rank}s cannot be melded.",
                values: ("rank", RankName(barred.Rank)));
            return null;
        }

        List<List<Card>> melds;
        int addTarget = -1;
        if (addToExisting)
        {
            // Adding stays one meld per action: the action targets one group.
            addTarget = FindAdditionTarget(state, meldZone, selectedCards, wilds, out reason);
            if (addTarget < 0) return null;
            melds = [selectedCards];
        }
        // A new lay may hold several melds at once, partitioned by rank with the wilds
        // shared out. An opening minimum often cannot be met by any single meld a hand
        // holds, so one-meld-per-action made the requirement unsatisfiable at exactly
        // the moment it applied. Each part is validated as a meld in its own right —
        // this was documented as validated and was not: any three selected cards were
        // accepted, so a "meld" of unrelated cards was legal and scored as if it counted.
        else if (MeldRules.PartitionIntoMelds(selectedCards, wilds) is { } parts)
        {
            melds = parts;
        }
        else
        {
            reason = GameText.Message(state, "not_a_meld", "That is not a meld — pick three or more of a rank.");
            return null;
        }

        // A side that has not melded must open with enough in one go. The requirement
        // rises by round in Hand and Foot, which is why it is a table in the definition
        // rather than a number. Adding to a meld presupposes one is down, so it is
        // never an opening.
        int required = addToExisting ? 0 : RequiredOpeningMeld(state);
        if (required > 0)
        {
            int offered = ScoringEngine.CardPointValue(state.Definition, selectedCards);
            if (offered < required)
            {
                reason = GameText.Message(state, "opening_too_low",
                    "{player}'s first meld this round must be worth {required}; that is {offered}.",
                    forPlayerId: state.CurrentPlayer.Id, ("required", required), ("offered", offered));
                return null;
            }
        }

        return new MeldPlan(selectedCards, melds, addTarget, meldZone, wilds);
    }

    private void LayMeld(GameState state, bool addToExisting)
    {
        var plan = PlanMeld(state, addToExisting, out var reason);
        if (plan is null)
        {
            if (reason is not null) state.Metadata["status"] = reason;
            return;
        }

        var (selectedCards, melds, addTarget, meldZone, wilds) = plan;
        var hand = PlayerHand(state, state.CurrentPlayer.Id)!;

        foreach (var card in selectedCards)
        {
            hand.Remove(card);
            card.IsFaceUp = true;
        }

        // Each part joins the table's meld of its rank when one exists — a second set of
        // sevens belongs on the first, which is what canasta counting builds on.
        bool joined = false;
        foreach (var meld in melds)
        {
            int existing = addToExisting
                ? addTarget
                : FindGroupOfRank(meldZone, MeldRules.MeldRankOf(meld, wilds), wilds);
            if (existing >= 0)
            {
                foreach (var card in meld) meldZone.AddToGroup(existing, card);
                joined = true;
            }
            else
            {
                meldZone.AddGroup(meld);
            }
        }

        // A card owed to the table is paid off by reaching it, whether it went down in
        // its own meld or joined one already there.
        if (state.Metadata.TryGetValue("dd_must_meld", out var owed)
            && selectedCards.Any(c => c.Id == owed))
            state.Metadata.Remove("dd_must_meld");

        state.Metadata.Remove("selected_card");
        state.Metadata["status"] = melds.Count > 1
            ? GameText.Message(state, "melds_laid", "{count} melds laid!", values: ("count", melds.Count))
            : joined
                ? GameText.Message(state, "added_to_meld", "Added to meld.")
                : GameText.Message(state, "meld_laid", "Meld laid!");

        // Melding away the last card of the hand picks the foot up at once, and the
        // turn goes on with it. The pickup only ran after a discard, so a hand emptied
        // by melding was offered Go Out with its foot still lying there untouched.
        PickUpFootIfNeeded(state, state.CurrentPlayer.Id);
    }
    /// <summary>
    /// The group the selection may join, or -1 with the refusal in status.
    ///
    /// Naturals name their meld; an all-wild addition goes to the biggest meld with the
    /// capacity, the one nearest to a canasta. Either way the meld it lands on must stay
    /// legal — wilds never outnumbering the naturals — which the old path never checked
    /// once a meld was down.
    /// </summary>
    private static int FindAdditionTarget(
        GameState state, Zone meldZone, IReadOnlyList<Card> selection, HashSet<Rank> wilds,
        out string? reason)
    {
        reason = null;
        var naturals   = selection.Where(c => !MeldRules.IsWild(c, wilds)).ToList();
        int wildsAdded = selection.Count - naturals.Count;

        if (naturals.Count > 0 && naturals.Any(c => c.Rank != naturals[0].Rank))
        {
            reason = GameText.Message(state, "add_one_rank", "Pick cards of one rank to add to a meld.");
            return -1;
        }

        if (naturals.Count > 0)
        {
            int target = FindGroupOfRank(meldZone, naturals[0].Rank, wilds);
            if (target < 0)
            {
                reason = GameText.Message(state, "no_meld_of_rank",
                    "No meld of {rank}s on the table — lay it as a new meld.",
                    values: ("rank", RankName(naturals[0].Rank)));
                return -1;
            }
            if (!StaysLegal(meldZone.GroupCards(target), naturals.Count, wildsAdded, wilds))
            {
                reason = GameText.Message(state, "too_many_wilds", "That would leave the meld more wild than real.");
                return -1;
            }
            return target;
        }

        // All wilds: the choice of meld is the player's in principle, but with no way
        // to point at a group yet, the biggest legal taker is the least surprising.
        int best = -1;
        for (int i = 0; i < meldZone.Groups.Count; i++)
            if (StaysLegal(meldZone.GroupCards(i), 0, wildsAdded, wilds)
                && (best < 0 || meldZone.Groups[i].Count > meldZone.Groups[best].Count))
                best = i;

        if (best < 0)
            reason = GameText.Message(state, "no_meld_takes_wilds", "No meld can take that many wilds.");
        return best;

        static bool StaysLegal(
            IReadOnlyList<Card> group, int naturalsAdded, int wildsAdded, HashSet<Rank> wilds)
        {
            int naturals = group.Count(c => !MeldRules.IsWild(c, wilds)) + naturalsAdded;
            int wildCnt  = group.Count(c => MeldRules.IsWild(c, wilds)) + wildsAdded;
            return wildCnt <= naturals;
        }
    }

    /// <summary>Index of the meld already holding this rank, or -1.</summary>
    private static int FindGroupOfRank(Zone zone, Rank rank, HashSet<Rank> wilds)
    {
        for (int i = 0; i < zone.Groups.Count; i++)
            if (zone.GroupCards(i).Any(c => !MeldRules.IsWild(c, wilds) && c.Rank == rank))
                return i;

        return -1;
    }

    /// <summary>
    /// The physical card an action means, in the current player's hand. A uid names it
    /// outright. An id alone prefers a copy not already selected, so an agent that only
    /// knows descriptions can still pick up a second identical card instead of
    /// toggling the first back off.
    /// </summary>
    /// <summary>The zones the current player selects from: their hand, and in grid mode their grid.</summary>
    private IEnumerable<Card> SelectableCards(GameState state)
    {
        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        foreach (var c in hand?.Cards ?? Enumerable.Empty<Card>()) yield return c;

        if (_targetZone == "grid")
        {
            var grid = PlayerGrid(state, state.CurrentPlayer.Id);
            foreach (var c in grid?.Cards ?? Enumerable.Empty<Card>()) yield return c;
        }
    }

    private Card? ResolveCard(GameState state, int? uid, string cardId, List<string> selectedTokens)
    {
        var cards = SelectableCards(state).ToList();

        if (uid is int u)
            return cards.FirstOrDefault(c => c.Uid == u);

        var copies = cards.Where(c => c.Id == cardId).ToList();
        return copies.FirstOrDefault(c => !selectedTokens.Contains(c.Uid.ToString()))
            ?? copies.FirstOrDefault();
    }

    /// <summary>
    /// The card a selection token names — a uid, or (for callers predating uids, such
    /// as drag-drop and outside agents) a card id resolved to its first copy.
    /// </summary>
    private Card? CardFromToken(GameState state, string token)
    {
        return int.TryParse(token, out int uid)
            ? SelectableCards(state).FirstOrDefault(c => c.Uid == uid)
            : SelectableCards(state).FirstOrDefault(c => c.Id == token);
    }

    /// <summary>
    /// The selection that pays a meld debt, as uid tokens: every natural of the owed
    /// card's rank, plus — while the side still owes an opening — every other rank
    /// group of three or more naturals. Mirrors what
    /// <c>can_open_with_top_discard</c> counted when it allowed the pickup, so a claim
    /// that condition permitted is always one this can pay.
    /// </summary>
    private string? AssembleOwedMeld(GameState state, string owedId)
    {
        var hand = PlayerHand(state, state.CurrentPlayer.Id);
        var owed = hand?.Cards.FirstOrDefault(c => c.Id == owedId);
        if (hand is null || owed is null) return null;

        var wilds = MeldRules.WildRanks(state.Definition);
        bool Natural(Card c) => !MeldRules.IsWild(c, wilds) && !_unmeldableRanks.Contains(c.Rank);

        var selection = hand.Cards.Where(c => Natural(c) && c.Rank == owed.Rank).ToList();

        if (RequiredOpeningMeld(state) > 0)
            foreach (var group in hand.Cards.Where(c => Natural(c) && c.Rank != owed.Rank)
                                            .GroupBy(c => c.Rank))
                if (group.Count() >= 3)
                    selection.AddRange(group);

        return string.Join(",", selection.Select(c => c.Uid));
    }

    /// <summary>A card named the way a player would say it, for a message about it.</summary>
    private static string CardName(GameState state, string cardId)
    {
        foreach (var zone in state.Zones.Values)
            if (zone.Cards.FirstOrDefault(c => c.Id == cardId) is { } card)
                return card.Rank == Rank.Joker
                    ? "Joker"
                    : $"{RankName(card.Rank)} of {card.Suit}";

        return "card";
    }

    private static string RankName(Rank rank) => rank switch
    {
        Rank.Ace   => "Ace",
        Rank.Jack  => "Jack",
        Rank.Queen => "Queen",
        Rank.King  => "King",
        Rank.Joker => "Joker",
        _          => ((int)rank).ToString(),
    };

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

        // A foot still waiting is cards still to play: an empty hand with a full foot
        // is halfway, not out. Go Out was offered at exactly that moment, with the
        // foot never picked up.
        var  foot      = state.FindZone($"foot:{state.CurrentPlayer.Id}");
        bool footEmpty = foot is null || foot.IsEmpty;

        // What the definition asks for on top of that — Hand and Foot's two books.
        bool requiresMet = _goOutRequires is not { } req || RuleCondition.Evaluate(req, state);

        return handEmpty && footEmpty && requiresMet;
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
        string me = state.CurrentPlayer.Id;

        // One key per situation; the _you form is picked for the person at this screen.
        state.Metadata["status"] =
            state.Metadata.TryGetValue("dd_must_meld", out var owed)
                ? GameText.Message(state, "turn_owed", "{player}'s turn — Meld the {card} they took",
                                   me, ("card", CardName(state, owed)))
            : TurnState(state) == "draw"
                ? GameText.Message(state, "turn_draw", "{player}'s turn — Draw a card", me)
            : state.Metadata.ContainsKey("dd_must_flip")
                ? (state.Metadata.GetValueOrDefault("selected_card") is { Length: > 0 }
                    ? GameText.Message(state, "turn_flip_ready", "{player}'s turn — Press Flip to turn it over", me)
                    : GameText.Message(state, "turn_flip", "{player}'s turn — Tap a face-down card to turn over", me))
            : _targetZone == "grid" && state.Metadata.ContainsKey("dd_drawn_card")
                ? GameText.Message(state, "turn_swap", "{player}'s turn — Tap a card to swap, or discard the drawn card", me)
                : GameText.Message(state, "turn_discard", "{player}'s turn — Discard a card", me);
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
