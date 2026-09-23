using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Finds the properties in a definition file that nothing reads.
///
/// This is the bug the table kept hitting: a definition states something, the loader
/// accepts it, and no code ever looks. A grid declared "rows": 2, "cols": 3 drew one
/// row; a zone's "initial_face": "down" dealt face-up; a deck badge showed nothing.
/// Each time the definition was right, the game was wrong, and there was no error to
/// read — which is worse than a game that refuses to load, because a refusal says why.
///
/// Two kinds of unread property exist and both are found here:
///
///   • A key no model maps. System.Text.Json silently drops it, so "initial_faces" or
///     "peek_count" costs nothing to write and does nothing at all. Found by walking
///     the file against the model types, which means the check cannot go stale as the
///     models grow — a new property is known the moment it is declared.
///
///   • A key that lands in a phase's or the scoring block's extension bag, which takes
///     anything. Those are checked against what the handler for that phase type
///     actually reads, listed below and held to the handlers' own source by
///     DefinitionAuditTests.
///
/// Reported, not guessed at: the audit never decides what a property meant.
/// </summary>
public static class DefinitionAudit
{
    /// <summary>
    /// Phase parameters each phase type reads, beyond the id/type/next every phase has.
    /// A phase carrying anything else is carrying it for nothing.
    /// </summary>
    public static readonly Dictionary<string, string[]> PhaseKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flip_compare_ready"]  = ["tie_resolution"],
        ["flip_compare_result"] = [],
        ["score"]               = [],
        ["trick_taking"]        = ["collect_tricks_to", "direction", "follow_suit", "lead", "lead_card",
                                   "lead_restrictions", "left_bower", "loner_skips_partner",
                                   "must_play_higher", "no_points_first_trick",
                                   "trick_winner_leads_next", "trump", "winner"],
        ["bidding"]             = ["bid_increment", "competitive_bidding", "direction", "exclude_suit",
                                   "if_accepted", "if_called",
                                   "going_alone", "if_all_pass", "max_bid", "min_bid", "once_around",
                                   "pass_allowed", "special_bids", "stick_the_dealer", "style"],
        ["pass_cards"]          = ["count", "direction", "targets"],
        ["free_play"]           = ["end_game", "end_turn"],
        ["draw_discard"]        = ["discard_count", "draw_count", "draw_from", "flip_after_discard",
                                   "gin_condition", "go_out_condition", "initial_meld_requirement",
                                   "knock_condition", "remaining_players_get_one_more_turn",
                                   "round_ends_when", "special_actions", "target_zone",
                                   "unmeldable_ranks"],
        ["meld"]                = ["layoff_allowed", "max_wilds_per_meld", "meld_types", "min_meld_size",
                                   "return_to_hand", "wilds_allowed"],
        ["poker_betting"]       = ["bring_in", "bring_in_amount", "can_check", "post_blinds",
                                   "starting_player", "structure"],
        ["showdown"]            = ["community_zone", "evaluator", "hand_size", "use_from_hand"],
        ["war"]                 = ["tie_resolution", "war_face_down_count"],
        ["blackjack_round"]     = ["dealer_hits_soft"],
        ["go_fish"]             = ["book_size", "collect_to"],
        ["deal"]                = ["burn_first", "cards", "count", "face", "to"],
        ["name_trump"]          = ["exclude_suit"],
        ["dealer_discard"]      = [],
        ["reveal"]              = ["count", "zone"],
    };

    /// <summary>Parameters any phase may carry, whatever its type.</summary>
    private static readonly string[] CommonPhaseKeys = ["id", "type", "next", "skip", "when", "requires"];

    /// <summary>What the scoring block may say, beyond its typed properties.</summary>
    public static readonly string[] ScoringKeys =
    [
        "accumulate", "bag_penalty", "bid_set_penalty", "blind_nil", "bonus_cards", "book_size",
        "card_values", "count_by", "count_from", "euchred", "evaluator", "face_down_penalty",
        "gin_bonus", "go_out_bonus", "grid", "knock_bonus", "last_trick_bonus", "loner_win",
        "makers_win", "matching_columns", "natural_canasta_bonus", "nil", "per_bid_trick",
        "red_three_penalty", "shoot_the_moon", "special", "trick_points", "undercut_bonus",
        "wild_canasta_bonus", "wild_cards", "wilds",
    ];

    /// <summary>
    /// Every property in <paramref name="json"/> that no engine code reads, as readable
    /// lines naming the path. Empty means the whole file is read by something.
    /// </summary>
    public static IReadOnlyList<string> UnreadProperties(JsonElement json)
    {
        var problems = new List<string>();
        if (json.ValueKind == JsonValueKind.Object)
            Walk(json, typeof(GameDefinition), "", problems);
        return problems;
    }

    /// <summary>Convenience for a definition file's text.</summary>
    public static IReadOnlyList<string> UnreadProperties(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return UnreadProperties(doc.RootElement);
    }

    private static void Walk(JsonElement element, Type type, string path, List<string> problems)
    {
        // A patch map (overrides, house rule effects) is keyed by path into some other
        // part of the definition. Following it would mean re-implementing the patcher,
        // and its keys are checked where they are applied.
        if (type == typeof(JsonElement) || type == typeof(object)) return;

        var known = KnownProperties(type);
        var bag   = ExtensionBagContext(type);

        foreach (var property in element.EnumerateObject())
        {
            // "$schema" points an editor at a schema file; it is for the person writing
            // the definition, and never meant for the engine.
            if (path.Length == 0 && property.Name == "$schema") continue;

            string here = path.Length == 0 ? property.Name : $"{path}.{property.Name}";

            if (known.TryGetValue(property.Name, out var clr))
            {
                Descend(property.Value, clr, here, problems);
                continue;
            }

            if (bag is null)
            {
                problems.Add($"{here}: nothing reads this. Check the spelling, or the "
                           + "schema for where it belongs.");
                continue;
            }

            if (!BagAccepts(bag, element, property.Name))
                problems.Add($"{here}: nothing reads this{BagHint(bag, element)}.");
        }
    }

    private static void Descend(JsonElement value, Type type, string path, List<string> problems)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (value.ValueKind == JsonValueKind.Array)
        {
            var item = ItemType(type);
            if (item is null) return;

            int i = 0;
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                    Walk(entry, item, $"{path}[{i}]", problems);
                i++;
            }
            return;
        }

        if (value.ValueKind != JsonValueKind.Object) return;

        // A dictionary's keys are the definer's own words; its values are checked.
        if (DictionaryValueType(type) is { } valueType)
        {
            foreach (var entry in value.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object)
                    Walk(entry.Value, valueType, $"{path}.{entry.Name}", problems);
            return;
        }

        Walk(value, type, path, problems);
    }

    /// <summary>JSON name → property type, for a model type. Cached; models do not change.</summary>
    private static Dictionary<string, Type> KnownProperties(Type type)
    {
        if (_knownCache.TryGetValue(type, out var cached)) return cached;

        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null) continue;
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            if (property.GetIndexParameters().Length > 0) continue;

            string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            map[name] = property.PropertyType;

            // The loader reads case-insensitively, so "cardScale" finds CardScale too.
            map.TryAdd(property.Name, property.PropertyType);
        }

        _knownCache[type] = map;
        return map;
    }

    private static readonly Dictionary<Type, Dictionary<string, Type>> _knownCache = [];

    /// <summary>Which extension bag a type has, or null when it has none.</summary>
    private static string? ExtensionBagContext(Type type)
        => type == typeof(PhaseDefinition)   ? "phase"
         : type == typeof(ScoringDefinition) ? "scoring"
         : null;

    private static bool BagAccepts(string bag, JsonElement owner, string name)
    {
        if (bag == "scoring") return ScoringKeys.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (CommonPhaseKeys.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        // An unknown phase type is its own error, reported by the logic registry; here
        // it means the audit cannot say what the phase reads, so it says nothing.
        string? phaseType = owner.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (phaseType is null || !PhaseKeys.TryGetValue(phaseType, out var keys)) return true;

        return keys.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static string BagHint(string bag, JsonElement owner)
    {
        if (bag == "scoring")
            return $". scoring reads: {string.Join(", ", ScoringKeys)}";

        string? phaseType = owner.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (phaseType is not null && PhaseKeys.TryGetValue(phaseType, out var keys))
            return keys.Length == 0
                ? $". a {phaseType} phase takes no parameters"
                : $". a {phaseType} phase reads: {string.Join(", ", keys)}";

        return "";
    }

    private static Type? ItemType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        foreach (var iface in Interfaces(type))
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        return null;
    }

    private static Type? DictionaryValueType(Type type)
    {
        foreach (var iface in Interfaces(type))
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                return iface.GetGenericArguments()[1];
        return null;
    }

    private static IEnumerable<Type> Interfaces(Type type)
        => type.IsInterface ? [type, .. type.GetInterfaces()] : type.GetInterfaces();
}
