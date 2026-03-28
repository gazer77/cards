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
            if (def is not null)
            {
                // Apply default house rule states
                foreach (var rule in def.HouseRules)
                    rule.IsEnabled = rule.Default;

                _cache[gameId] = def;
            }
            return def;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLoader] Failed to load {gameId}: {ex.Message}");
            return null;
        }
    }

    public void ClearCache() => _cache.Clear();
}
