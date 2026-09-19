using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Checks that a game definition says things the engine can actually read.
///
/// Runs when a definition loads, and a failure keeps the game out of the list. That is
/// deliberately harsher than it sounds: a rule the engine does not recognise is not
/// ignored loudly, it is ignored silently. The game appears, deals, and plays — with one
/// of its rules simply absent. Hand and Foot shipped that way for a long time.
///
/// The same reasoning already applies to decks, where an unknown name used to fall back
/// to a standard 52 and quietly deal the wrong cards.
/// </summary>
public static class DefinitionValidator
{
    /// <summary>Names a deck expression may use. Deck size is fixed before a hand exists.</summary>
    private static readonly string[] DeckExpressionNames = ["players"];

    /// <summary>
    /// Problems with a definition, as readable lines. Empty means it is well formed.
    /// </summary>
    public static IReadOnlyList<string> Validate(GameDefinition definition)
    {
        var problems = new List<string>();

        ValidateDeck(definition, problems);

        foreach (var zone in definition.Zones)
        {
            if (zone.Arrangement is { } arr && arr is not ("full" or "compact" or "stack"))
                problems.Add(
                    $"zone '{zone.Id}': arrangement '{arr}' is not full, compact, or stack.");

            ValidateLabel(zone.Id, "label", zone.Label, problems);
            ValidateLabel(zone.Id, "group_label", zone.GroupLabel, problems);

            if (zone.GroupLayout is not ("flow" or "by_rank"))
                problems.Add($"zone '{zone.Id}': group_layout '{zone.GroupLayout}' is not flow or by_rank.");

            for (int i = 0; i < zone.GroupBadges.Count; i++)
                ValidateBadge(zone.Id, i, zone.GroupBadges[i], problems);
        }

        foreach (var phase in definition.Phases)
            ValidatePhase(phase, problems);

        return problems;
    }

    private static void ValidateLabel(string zoneId, string field, ZoneLabelDefinition? label, List<string> problems)
    {
        if (label is null) return;

        if (label.Placement is not ("top" or "bottom" or "left" or "right"))
            problems.Add($"zone '{zoneId}'.{field}: placement '{label.Placement}' is not top, bottom, left, or right.");

        if (label.Orientation is not ("horizontal" or "vertical" or "angled"))
            problems.Add($"zone '{zoneId}'.{field}: orientation '{label.Orientation}' is not horizontal, vertical, or angled.");

        // {rank} means nothing for a zone as a whole, only for a group within one.
        if (field == "label" && label.Text.Contains("{rank}"))
            problems.Add($"zone '{zoneId}'.label: {{rank}} is only meaningful in a group_label.");

        if (label.When is { } when)
            foreach (var problem in RuleCondition.Validate(when))
                problems.Add($"zone '{zoneId}'.{field}.when: {problem}");
    }

    private static void ValidateBadge(string zoneId, int index, BadgeDefinition badge, List<string> problems)
    {
        string where = $"zone '{zoneId}'.group_badges[{index}]";

        if (badge.Shows is not ("cards" or "books" or "loose"))
            problems.Add($"{where}: shows '{badge.Shows}' is not cards, books, or loose.");

        if (badge.Placement is not ("top" or "bottom" or "left" or "right"))
            problems.Add($"{where}: placement '{badge.Placement}' is not top, bottom, left, or right.");

        if (badge.Orientation is not ("horizontal" or "vertical" or "angled"))
            problems.Add($"{where}: orientation '{badge.Orientation}' is not horizontal, vertical, or angled.");

        foreach (var (field, value) in new[] { ("color", badge.Color), ("text_color", badge.TextColor) })
            if (value is not null && !IsHexColor(value))
                problems.Add($"{where}: {field} '{value}' is not a #RRGGBB colour.");

        if (badge.When is { } when)
            foreach (var problem in RuleCondition.Validate(when))
                problems.Add($"{where}.when: {problem}");
    }

    private static bool IsHexColor(string text)
        => text.Length is 7 or 9 && text[0] == '#'
           && text.Skip(1).All(Uri.IsHexDigit);

    private static void ValidateDeck(GameDefinition definition, List<string> problems)
    {
        // Parsed at a couple of table sizes: an expression is only wrong at a given
        // player count if it divides by zero or names something unknown, and both show
        // up on the first evaluation.
        foreach (int players in new[] { definition.MinPlayers, definition.MaxPlayers })
        {
            try
            {
                var spec = DeckSpec.Parse(definition.Deck, players);
                if (spec.Size == 0)
                    problems.Add($"deck: produces no cards at {players} players.");
            }
            catch (FormatException ex)
            {
                problems.Add($"deck: {ex.Message}");
                return;   // one report is enough; the second size says the same thing
            }
        }
    }

    private static void ValidatePhase(PhaseDefinition phase, List<string> problems)
    {
        if (phase.Extra is null) return;

        // Conditions attached to draw sources.
        if (phase.Extra.TryGetValue("draw_from", out var drawFrom)
            && drawFrom.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in drawFrom.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                if (!entry.TryGetProperty("zone", out _))
                    problems.Add($"{phase.Id}: a draw source must name a zone.");

                if (entry.TryGetProperty("requires", out var condition))
                    foreach (var problem in RuleCondition.Validate(condition))
                        problems.Add($"{phase.Id}.draw_from.requires: {problem}");

                if (entry.TryGetProperty("then_must", out var owed)
                    && owed.GetString() is not "meld_top_card")
                    problems.Add(
                        $"{phase.Id}.draw_from.then_must: '{owed}' is not an obligation the engine knows.");
            }
        }

        // Ranks a phase bars from melding must be ranks — a typo here would quietly
        // make the barred rank meldable again.
        if (phase.Extra.TryGetValue("unmeldable_ranks", out var unmeldable)
            && unmeldable.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in unmeldable.EnumerateArray())
                if (entry.ValueKind != JsonValueKind.String
                    || MeldRules.ParseRank(entry.GetString() ?? "") is null)
                    problems.Add($"{phase.Id}.unmeldable_ranks: '{entry}' is not a rank.");
        }

        // go_out_condition is a name or a condition; only the condition needs checking.
        if (phase.Extra.TryGetValue("go_out_condition", out var goOut)
            && goOut.ValueKind == JsonValueKind.Object)
            foreach (var problem in RuleCondition.Validate(goOut))
                problems.Add($"{phase.Id}.go_out_condition: {problem}");

        // Conditions anywhere else a phase declares one.
        foreach (var key in new[] { "requires", "when" })
            if (phase.Extra.TryGetValue(key, out var condition))
                foreach (var problem in RuleCondition.Validate(condition))
                    problems.Add($"{phase.Id}.{key}: {problem}");
    }

    /// <summary>
    /// Whether a deck expression is readable, without needing a game. Exposed so the
    /// deck vocabulary has one definition of "valid" rather than two.
    /// </summary>
    public static bool IsValidDeckExpression(string text, out string error)
        => RuleExpression.IsValid(text, DeckExpressionNames, out error);
}
