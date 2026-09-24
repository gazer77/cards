using System.Text.Json;
using System.Text.Json.Nodes;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Resolves a game definition into the shape a particular table is playing.
///
/// A definition may declare <c>configurations</c>: the parts that differ by how many
/// are playing, or by which variant of the game was chosen. This merges the ones that
/// apply onto the common definition, once, before anything is dealt — so the rest of
/// the engine goes on reading one definition and never learns that shapes exist.
///
/// The merge is by JSON, deeply: an object merges key by key and anything else replaces.
/// A configuration therefore says only what it changes, and an array it does mention —
/// the phase list, the zones — it replaces whole, because half a phase list is not a
/// thing anyone means.
/// </summary>
public static class GameConfiguration
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new SafeJsonElementConverter() },
    };

    /// <summary>
    /// The definition as this table plays it.
    ///
    /// Every configuration whose <c>when</c> matches is applied in declaration order,
    /// then the named one — the one asked for, or the default — if the game offers any.
    /// A definition with no configurations comes back untouched, which is every game
    /// that has one shape.
    /// </summary>
    public static GameDefinition Resolve(GameDefinition definition, int playerCount, string? chosen = null)
    {
        if (definition.Configurations.Count == 0) return definition;

        var applied = Applicable(definition, playerCount, chosen);
        if (applied.Count == 0) return definition;

        var node = JsonSerializer.SerializeToNode(definition, _opts)?.AsObject();
        if (node is null) return definition;

        // The shapes are a property of the file, not of the table playing it. Leaving
        // them in would let a save resolve twice and a validator check the same rules
        // against two different games.
        node.Remove("configurations");

        foreach (var configuration in applied)
        {
            if (configuration.Extra is null) continue;
            foreach (var (key, value) in configuration.Extra)
                Merge(node, key, value);
        }

        var resolved = node.Deserialize<GameDefinition>(_opts);
        if (resolved is null) return definition;

        // House rule state is runtime, not serialised, so it does not survive the trip.
        foreach (var rule in resolved.HouseRules)
            rule.IsEnabled = definition.HouseRules.FirstOrDefault(r => r.Id == rule.Id)?.IsEnabled ?? rule.Default;

        return resolved;
    }

    /// <summary>
    /// The named shapes a table of this size may choose between, in declaration order.
    /// Empty when the game offers no choice — which is not the same as having no
    /// configurations, since a game may vary by seat count and ask nothing.
    /// </summary>
    public static IReadOnlyList<ConfigurationDefinition> Offered(GameDefinition definition, int playerCount)
        => definition.Configurations
            .Where(c => c.Name is { Length: > 0 })
            .Where(c => c.When is null || c.When.Matches(playerCount))
            .ToList();

    /// <summary>
    /// The named shape a table of this size plays when nobody has chosen: the one marked
    /// default, else the first offered, else null.
    /// </summary>
    public static ConfigurationDefinition? DefaultFor(GameDefinition definition, int playerCount)
    {
        var offered = Offered(definition, playerCount);
        return offered.FirstOrDefault(c => c.Default) ?? offered.FirstOrDefault();
    }

    /// <summary>
    /// The configurations that apply to this table, in the order they are merged:
    /// matched ones first, the chosen or default named one last, so a variant has the
    /// final word on anything the seat count also touched.
    /// </summary>
    public static IReadOnlyList<ConfigurationDefinition> Applicable(
        GameDefinition definition, int playerCount, string? chosen = null)
    {
        var applied = definition.Configurations
            .Where(c => c.Name is not { Length: > 0 })
            .Where(c => c.When is not null && c.When.Matches(playerCount))
            .ToList();

        var named = chosen is { Length: > 0 }
            ? Offered(definition, playerCount)
                .FirstOrDefault(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase))
              ?? DefaultFor(definition, playerCount)
            : DefaultFor(definition, playerCount);

        if (named is not null) applied.Add(named);
        return applied;
    }

    /// <summary>
    /// Merges one key of a configuration onto the definition being built. Objects merge
    /// into objects; everything else — a value, an array, a null — replaces what was
    /// there, because a configuration that mentions the phases means those phases.
    /// </summary>
    private static void Merge(JsonObject target, string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && target[key] is JsonObject existing)
        {
            foreach (var property in value.EnumerateObject())
                Merge(existing, property.Name, property.Value);
            return;
        }

        target[key] = JsonNode.Parse(value.GetRawText());
    }
}
