using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for simultaneous card passing (Hearts).
///
/// Phase definition parameters:
///   count      — number of cards each player must pass (default 3)
///   direction  — "rotate" | "left" | "right" | "across" (default "rotate")
///   targets    — ["left","right","across","none"] — rotation sequence for "rotate" mode
///
/// When direction is "rotate" the pass direction advances through targets each round
/// using state.RoundNumber % targets.Count.  When the current direction is "none",
/// the phase is skipped automatically.
///
/// State metadata keys:
///   pass_direction     — resolved direction for this round ("left"|"right"|"across"|"none")
///   pass_selected:{id} — comma-separated card IDs selected by player for passing
///   pass_done:{id}     — "true" once a player has confirmed their selection
/// </summary>
public sealed class PassCardsHandler : IPhaseHandler
{
    private readonly string       _nextPhaseId;
    private readonly int          _count;
    private readonly string       _directionMode;
    private readonly List<string> _targets;

    public PassCardsHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId   = nextPhaseId;
        _count         = GetInt(def, "count") ?? 3;
        _directionMode = GetString(def, "direction") ?? "rotate";
        _targets       = ParseStringArray(def, "targets");
        if (_targets.Count == 0) _targets = ["left", "right", "across", "none"];
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnsureInitialized(state);

        if (IsSkipped(state)) return [new GameAction("tap", Label: "Continue")];

        var selected = GetSelected(state, state.CurrentPlayer.Id);
        if (selected.Count == _count && !IsDone(state, state.CurrentPlayer.Id))
            return [new GameAction("confirm_pass", Label: "Pass Cards")];

        return []; // player uses card taps to select
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        EnsureInitialized(state);
        if (IsSkipped(state) || IsDone(state, state.CurrentPlayer.Id)) return [];

        var hand     = PlayerHand(state, state.CurrentPlayer.Id);
        var selected = GetSelected(state, state.CurrentPlayer.Id);

        // Can tap to select up to _count cards; tapping a selected card deselects it
        return hand?.Cards.Select(c => c.Id).ToList() ?? [];
    }

    public void Apply(GameState state, GameAction action)
    {
        EnsureInitialized(state);

        if (IsSkipped(state) || action.Type == "tap")
        {
            if (IsSkipped(state))
                ExecutePasses(state);
            return;
        }

        // play_card is treated as select_card (used by AI agent via GetAutoAction).
        if ((action.Type is "select_card" or "play_card") && action.CardId is { } cardId)
        {
            ToggleSelection(state, state.CurrentPlayer.Id, cardId);
            // Auto-confirm once enough cards are selected (AI workflow).
            var mySelected = GetSelected(state, state.CurrentPlayer.Id);
            if (mySelected.Count == _count)
            {
                state.Metadata[$"pass_done:{state.CurrentPlayer.Id}"] = "true";
                if (state.Players.All(p => IsDone(state, p.Id)))
                    ExecutePasses(state);
                else
                    state.AdvancePlayer();
            }
            return;
        }

        if (action.Type == "confirm_pass")
        {
            state.Metadata[$"pass_done:{state.CurrentPlayer.Id}"] = "true";

            if (state.Players.All(p => IsDone(state, p.Id)))
                ExecutePasses(state);
            else
                state.AdvancePlayer(); // single-device: show next player's hand
        }
    }

    // ── Core logic ────────────────────────────────────────────────────────────

    private void EnsureInitialized(GameState state)
    {
        if (state.Metadata.ContainsKey("pass_direction")) return;

        string direction = ResolveDirection(state);
        state.Metadata["pass_direction"] = direction;

        foreach (var p in state.Players)
        {
            state.Metadata.Remove($"pass_selected:{p.Id}");
            state.Metadata.Remove($"pass_done:{p.Id}");
        }

        UpdateStatus(state, direction);
    }

    private string ResolveDirection(GameState state)
    {
        if (_directionMode != "rotate") return _directionMode;
        if (_targets.Count == 0) return "left";
        return _targets[(state.RoundNumber - 1) % _targets.Count];
    }

    private bool IsSkipped(GameState state)
        => state.Metadata.GetValueOrDefault("pass_direction") == "none";

    private void ExecutePasses(GameState state)
    {
        string direction = state.Metadata.GetValueOrDefault("pass_direction", "left");
        var n = state.Players.Count;

        // Collect cards to pass from each player before moving any
        var toPass = new Dictionary<string, List<Card>>();
        foreach (var p in state.Players)
        {
            var hand     = PlayerHand(state, p.Id);
            var selected = GetSelected(state, p.Id);
            var cards    = hand?.Cards.Where(c => selected.Contains(c.Id)).ToList() ?? [];
            toPass[p.Id] = cards;
        }

        // Move cards to target player hands
        for (int i = 0; i < state.Players.Count; i++)
        {
            var from = state.Players[i];
            int targetIdx = direction switch
            {
                "right"  => (i - 1 + n) % n,
                "across" => (i + n / 2) % n,
                _        => (i + 1) % n,  // "left"
            };
            var to = state.Players[targetIdx];
            var destHand = PlayerHand(state, to.Id);
            var srcHand  = PlayerHand(state, from.Id);
            if (srcHand is null || destHand is null) continue;

            foreach (var card in toPass[from.Id])
            {
                srcHand.Remove(card);
                destHand.Add(card);
            }
        }

        // Clean up metadata
        foreach (var p in state.Players)
        {
            state.Metadata.Remove($"pass_selected:{p.Id}");
            state.Metadata.Remove($"pass_done:{p.Id}");
        }
        state.Metadata.Remove("pass_direction");

        state.CurrentPlayerIndex = 0;
        state.CurrentPhaseId     = _nextPhaseId;
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private void ToggleSelection(GameState state, string playerId, string cardId)
    {
        var selected = GetSelected(state, playerId);
        if (selected.Contains(cardId))
            selected.Remove(cardId);
        else if (selected.Count < _count)
            selected.Add(cardId);
        state.Metadata[$"pass_selected:{playerId}"] = string.Join(",", selected);

        string need = (_count - selected.Count) switch
        {
            0    => "Tap 'Pass Cards' to confirm",
            1    => "Select 1 more card",
            var x => $"Select {x} more cards"
        };
        state.Metadata["status"] = $"Passing {state.Metadata["pass_direction"]}: {need}";
    }

    private static List<string> GetSelected(GameState state, string playerId)
    {
        var raw = state.Metadata.GetValueOrDefault($"pass_selected:{playerId}", "");
        return string.IsNullOrEmpty(raw) ? [] : [.. raw.Split(',')];
    }

    private static bool IsDone(GameState state, string playerId)
        => state.Metadata.GetValueOrDefault($"pass_done:{playerId}") == "true";

    private static Zone? PlayerHand(GameState state, string playerId)
        => state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");

    private void UpdateStatus(GameState state, string direction)
    {
        state.Metadata["status"] = direction == "none"
            ? "No passing this round. Tap to continue."
            : $"Select {_count} cards to pass {direction}.";
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
