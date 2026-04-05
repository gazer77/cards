using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Applies <c>house_rules[].affects</c> overrides to a game definition.
///
/// Supported path patterns:
///   "<phaseId>.<param>"        — sets/replaces a phase Extra parameter
///   "deal.<field>"             — patches a DealDefinition field
///   "win_condition.<field>"    — patches a WinCondition field
///   "scoring.<field>"          — sets/replaces a ScoringDefinition Extra entry
///   "deck"                     — replaces the deck specifier
///   "teams"                    — replaces the teams configuration
///
/// Values are arbitrary JSON (string, number, bool, null, array, or object).
/// The original <see cref="GameDefinition"/> is never mutated; a cloned copy
/// is returned with the overrides applied.
/// </summary>
public static class HouseRuleEngine
{
    private static readonly JsonSerializerOptions _serOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a cloned <see cref="GameDefinition"/> with the affects from all
    /// enabled house rules applied.  Returns <paramref name="source"/> unchanged
    /// when no active rules have affects.
    /// </summary>
    public static GameDefinition Apply(GameDefinition source, IReadOnlyList<string> enabledIds)
    {
        var active = source.HouseRules
            .Where(r => enabledIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)
                        && r.Affects is { Count: > 0 })
            .ToList();

        if (active.Count == 0) return source;

        // Clone via JSON round-trip so the original (cached) definition is never mutated.
        var json  = JsonSerializer.Serialize(source, _serOpts);
        var clone = JsonSerializer.Deserialize<GameDefinition>(json, _serOpts) ?? source;

        // IsEnabled is [JsonIgnore] — restore runtime state from the source.
        foreach (var rule in clone.HouseRules)
        {
            var src = source.HouseRules.FirstOrDefault(r => r.Id == rule.Id);
            rule.IsEnabled = src?.IsEnabled ?? rule.Default;
        }

        // Apply each active rule's affects to the clone.
        foreach (var rule in active)
        {
            foreach (var (path, value) in rule.Affects!)
                ApplyPath(clone, path, value);
        }

        return clone;
    }

    // ── Path dispatch ─────────────────────────────────────────────────────────

    private static void ApplyPath(GameDefinition def, string path, JsonElement value)
    {
        // "deck"
        if (string.Equals(path, "deck", StringComparison.OrdinalIgnoreCase))
        {
            def.Deck = value;
            return;
        }

        // "teams"
        if (string.Equals(path, "teams", StringComparison.OrdinalIgnoreCase))
        {
            def.Teams = value;
            return;
        }

        int dot = path.IndexOf('.');
        if (dot <= 0) return;

        string prefix = path[..dot];
        string rest   = path[(dot + 1)..];

        switch (prefix.ToLowerInvariant())
        {
            case "deal":
                if (def.Deal is not null) PatchDeal(def.Deal, rest, value);
                break;

            case "win_condition":
                if (def.WinCondition is not null) PatchWinCondition(def.WinCondition, rest, value);
                break;

            case "scoring":
                if (def.Scoring is not null)
                {
                    def.Scoring.Extra ??= [];
                    def.Scoring.Extra[rest] = value;
                }
                break;

            default:
                // "<phaseId>.<param>" — set/replace a phase Extra entry.
                var phase = def.Phases.FirstOrDefault(
                    p => string.Equals(p.Id, prefix, StringComparison.OrdinalIgnoreCase));
                if (phase is not null)
                {
                    phase.Extra ??= [];
                    phase.Extra[rest] = value;
                }
                break;
        }
    }

    // ── Field patchers ────────────────────────────────────────────────────────

    private static void PatchDeal(DealDefinition deal, string field, JsonElement value)
    {
        switch (field.ToLowerInvariant())
        {
            case "cards_per_player": deal.CardsPerPlayer = value;           break;
            case "remainder_to":     deal.RemainderTo    = value.GetString(); break;
            case "face":             deal.Face           = value.GetString(); break;
            case "pattern":          deal.Pattern        = value.GetString(); break;
            case "anim_delay_ms":
                if (value.ValueKind == JsonValueKind.Number)
                    deal.AnimDelayMs = value.GetInt32();
                break;
        }
    }

    private static void PatchWinCondition(WinCondition wc, string field, JsonElement value)
    {
        switch (field.ToLowerInvariant())
        {
            case "count":
                wc.Count = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
                break;
            case "score":
                wc.Score = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
                break;
            case "threshold":
                wc.Threshold = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
                break;
            case "winner":
                wc.Winner = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                break;
            case "type":
                if (value.ValueKind == JsonValueKind.String)
                    wc.Type = value.GetString() ?? wc.Type;
                break;
        }
    }
}
