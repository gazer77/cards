using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cards.Models;

public class GameDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("extends")]
    public string? Extends { get; set; }

    [JsonPropertyName("deck")]
    public JsonElement Deck { get; set; }

    [JsonPropertyName("deal")]
    public DealDefinition? Deal { get; set; }

    [JsonPropertyName("players")]
    public PlayerConfig? Players { get; set; }

    [JsonPropertyName("teams")]
    public JsonElement Teams { get; set; }

    [JsonPropertyName("zones")]
    public List<ZoneDefinition> Zones { get; set; } = [];

    [JsonPropertyName("phases")]
    public List<PhaseDefinition> Phases { get; set; } = [];

    [JsonPropertyName("scoring")]
    public ScoringDefinition? Scoring { get; set; }

    [JsonPropertyName("win_condition")]
    public WinCondition? WinCondition { get; set; }

    [JsonPropertyName("house_rules")]
    public List<HouseRule> HouseRules { get; set; } = [];

    [JsonPropertyName("help")]
    public string? Help { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("ui")]
    public GameUiConfig? Ui { get; set; }

    // Helpers
    public string DeckType => Deck.ValueKind == JsonValueKind.String
        ? Deck.GetString() ?? "standard-52"
        : "standard-52";

    public int MinPlayers => Players?.Min ?? 2;
    public int MaxPlayers => Players?.Max ?? 4;

    public string PlayerRangeText => MinPlayers == MaxPlayers
        ? $"{MinPlayers} players"
        : $"{MinPlayers}–{MaxPlayers} players";

    public bool HasTeams => Teams.ValueKind == JsonValueKind.Object;

    /// <summary>Scale factor applied to the base card size (1.0 = default).</summary>
    public float CardScale => Ui?.CardScale ?? 1.0f;
}

public class PlayerConfig
{
    [JsonPropertyName("min")]
    public int Min { get; set; } = 2;

    [JsonPropertyName("max")]
    public int Max { get; set; } = 4;
}

public class ZoneDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "all";
}

public class PhaseDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("next")]
    public JsonElement Next { get; set; }

    // Phase-specific properties are read from the raw JSON element as needed
    // by the corresponding logic module.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public string? NextPhase => Next.ValueKind == JsonValueKind.String
        ? Next.GetString()
        : null;
}

public class ScoringDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class WinCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("threshold")]
    public int? Threshold { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("winner")]
    public string? Winner { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }
}

public class GameUiConfig
{
    /// <summary>Multiplier on the base card width (default 1.0).</summary>
    [JsonPropertyName("card_scale")]
    public float CardScale { get; set; } = 1.0f;

    /// <summary>
    /// Automatically sort the player's hand after every action.
    /// "none" (default) — no auto-sort.
    /// "rank"           — group same ranks; Ace low (A 2 3 … K).
    /// "rank_ace_high"  — group same ranks; Ace high (2 3 … K A).
    /// "suit"           — sort by suit, then rank within suit.
    /// </summary>
    [JsonPropertyName("auto_sort_hand")]
    public string AutoSortHand { get; set; } = "none";

    /// <summary>
    /// When true a Sort button appears in the HUD so the player can manually sort
    /// their hand at any time.  Defaults to true.
    /// </summary>
    [JsonPropertyName("allow_sort")]
    public bool AllowSort { get; set; } = true;

    /// <summary>
    /// When true a Log button appears in the HUD so the player can review the
    /// full game event history.  Defaults to true.
    /// </summary>
    [JsonPropertyName("show_game_log")]
    public bool ShowGameLog { get; set; } = true;
}

public class DealDefinition
{
    /// <summary>
    /// Cards dealt per player.  Either a plain integer, or an array of
    /// <c>{ "max_players": N, "cards": N }</c> rules evaluated top-to-bottom —
    /// the first rule whose max_players is ≥ the actual player count wins.
    /// Omit max_players on the last entry to serve as a catch-all default.
    /// </summary>
    [JsonPropertyName("cards_per_player")]
    public JsonElement CardsPerPlayer { get; set; }

    [JsonPropertyName("remainder_to")]
    public string? RemainderTo { get; set; }

    [JsonPropertyName("face")]
    public string? Face { get; set; }

    public int GetCardsPerPlayer(int playerCount)
    {
        if (CardsPerPlayer.ValueKind == JsonValueKind.Number)
            return CardsPerPlayer.GetInt32();

        if (CardsPerPlayer.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in CardsPerPlayer.EnumerateArray())
            {
                bool hasMax = item.TryGetProperty("max_players", out var mp);
                int cards   = item.GetProperty("cards").GetInt32();
                if (!hasMax || playerCount <= mp.GetInt32())
                    return cards;
            }
        }

        return 5; // fallback
    }
}

public class HouseRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("default")]
    public bool Default { get; set; }

    // Runtime state — not from JSON
    [JsonIgnore]
    public bool IsEnabled { get; set; }
}
