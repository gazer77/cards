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

    // ── Public path-patch entry point ─────────────────────────────────────────

    /// <summary>
    /// Applies a single path-based override to <paramref name="def"/> in place.
    /// Used by both house-rule affects and definition inheritance overrides.
    /// </summary>
    public static void ApplyPath(GameDefinition def, string path, JsonElement value)
    {
        switch (path.ToLowerInvariant())
        {
            // ── Top-level scalar / element replacements ──────────────────────
            case "deck":
                def.Deck = value;
                return;

            case "teams":
                def.Teams = value;
                def.InvalidateTeamsCache();
                return;

            case "players":
                if (value.ValueKind == JsonValueKind.Object)
                    def.Players = JsonSerializer.Deserialize<PlayerConfig>(value.GetRawText(), _serOpts);
                return;

            case "scoring":
                if (value.ValueKind == JsonValueKind.Object)
                    def.Scoring = JsonSerializer.Deserialize<ScoringDefinition>(value.GetRawText(), _serOpts);
                return;

            case "win_condition":
                if (value.ValueKind == JsonValueKind.Object)
                    def.WinCondition = JsonSerializer.Deserialize<WinCondition>(value.GetRawText(), _serOpts);
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
                    PatchScoring(def.Scoring, rest, value);
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

    /// <summary>
    /// Patches a <see cref="ScoringDefinition"/>'s Extra map.
    /// Special forms:
    ///   <c>card_values: { "append": {...} }</c>  — appends one entry to the card_values array.
    ///   <c>special[name].field</c>               — patches a named entry in the special array.
    /// Everything else is a plain key replace in <c>Scoring.Extra</c>.
    /// </summary>
    private static void PatchScoring(ScoringDefinition scoring, string rest, JsonElement value)
    {
        scoring.Extra ??= [];

        // "special[name].field" — find named special entry and replace a property.
        if (rest.StartsWith("special[", StringComparison.OrdinalIgnoreCase))
        {
            int close = rest.IndexOf(']');
            if (close < 0) { scoring.Extra[rest] = value; return; }
            string name  = rest[8..close];
            string field = rest.Length > close + 2 ? rest[(close + 2)..] : "";

            if (!scoring.Extra.TryGetValue("special", out var specialEl)
                || specialEl.ValueKind != JsonValueKind.Array)
            { scoring.Extra[rest] = value; return; }

            var items = new List<Dictionary<string, JsonElement>>();
            foreach (var el in specialEl.EnumerateArray())
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in el.EnumerateObject())
                    dict[prop.Name] = prop.Value;
                if (el.TryGetProperty("name", out var n)
                    && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)
                    && field.Length > 0)
                    dict[field] = value;
                items.Add(dict);
            }

            string json = JsonSerializer.Serialize(items, _serOpts);
            scoring.Extra["special"] = JsonDocument.Parse(json).RootElement;
            return;
        }

        // "card_values: { append: {...} }" — append one element to the existing array.
        if (rest.Equals("card_values", StringComparison.OrdinalIgnoreCase)
            && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("append", out var appendEl))
        {
            if (!scoring.Extra.TryGetValue("card_values", out var cvEl)
                || cvEl.ValueKind != JsonValueKind.Array)
            { scoring.Extra[rest] = value; return; }

            var parts = cvEl.EnumerateArray().Select(e => e.GetRawText()).ToList();
            parts.Add(appendEl.GetRawText());
            string json = $"[{string.Join(",", parts)}]";
            scoring.Extra["card_values"] = JsonDocument.Parse(json).RootElement;
            return;
        }

        // Default: plain replace.
        scoring.Extra[rest] = value;
    }

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
