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

        foreach (var phase in definition.Phases)
            ValidatePhase(phase, problems);

        return problems;
    }

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
