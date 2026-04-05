using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

public class GameLoader
{
    // Games listed here correspond to JSON files bundled as raw assets under games/.
    // When a new game JSON is added, add its id here.
    private static readonly string[] GameIds =
    [
        "free-play",
        "high-card",
        "war",
        "go-fish",
        "blackjack",
        "hearts",
        "spades",
        "gin-rummy",
        "golf",
        "texas-holdem",
        "poker-wilds",
        "poker-stud",
        "euchre-4p",
        "euchre-3p",
        "pinochle",
        "hand-and-foot",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, GameDefinition> _cache = [];

    public async Task<List<GameDefinition>> LoadAllAsync()
    {
        var games = new List<GameDefinition>();
        foreach (var id in GameIds)
        {
            var game = await LoadAsync(id);
            if (game is not null)
                games.Add(game);
        }
        return games;
    }

    public async Task<GameDefinition?> LoadAsync(string gameId)
    {
        if (_cache.TryGetValue(gameId, out var cached))
            return cached;

        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync($"games/{gameId}.json");
            var def = await JsonSerializer.DeserializeAsync<GameDefinition>(stream, JsonOptions);
            if (def is null) return null;

            // Apply inheritance: merge with parent when "extends" is present.
            if (def.Extends is { Length: > 0 } parentId)
            {
                var parent = await LoadAsync(parentId);
                if (parent is not null)
                    def = MergeWithParent(parent, def);
            }

            // Apply default house rule states.
            foreach (var rule in def.HouseRules)
                rule.IsEnabled = rule.Default;

            _cache[gameId] = def;
            return def;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLoader] Failed to load {gameId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Merges a child definition onto a parent clone.
    ///
    /// Merge rules:
    ///   1. Start with a JSON clone of the parent.
    ///   2. Apply any top-level fields from the child (id, name, version, help,
    ///      house_rules, etc.) — these replace the parent's fields.
    ///   3. Apply each entry in the child's <c>overrides</c> map as a path patch.
    /// </summary>
    private static GameDefinition MergeWithParent(GameDefinition parent, GameDefinition child)
    {
        // Step 1 — deep clone parent via JSON round-trip.
        string parentJson = JsonSerializer.Serialize(parent, JsonOptions);
        var merged = JsonSerializer.Deserialize<GameDefinition>(parentJson, JsonOptions) ?? parent;

        // Re-apply IsEnabled (lost during round-trip).
        foreach (var rule in merged.HouseRules)
        {
            var pr = parent.HouseRules.FirstOrDefault(r => r.Id == rule.Id);
            rule.IsEnabled = pr?.IsEnabled ?? rule.Default;
        }

        // Step 2 — overlay direct child fields.
        if (child.Id.Length > 0)           merged.Id           = child.Id;
        if (child.Name.Length > 0)         merged.Name         = child.Name;
        if (child.Version.Length > 0)      merged.Version      = child.Version;
        if (child.Help is not null)         merged.Help         = child.Help;
        if (child.HouseRules.Count > 0)    merged.HouseRules   = child.HouseRules;
        if (child.Tags.Count > 0)          merged.Tags         = child.Tags;
        if (child.Ui is not null)           merged.Ui           = child.Ui;

        // Step 3 — apply overrides map as path patches.
        if (child.Overrides is { Count: > 0 } overrides)
        {
            foreach (var (path, value) in overrides)
                HouseRuleEngine.ApplyPath(merged, path, value);
        }

        // Clear extends on the merged result so it doesn't re-inherit.
        merged.Extends = null;
        merged.Overrides = null;

        return merged;
    }

    public void ClearCache() => _cache.Clear();
}
