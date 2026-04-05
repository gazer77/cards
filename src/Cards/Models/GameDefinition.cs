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

    /// <summary>
    /// Selects the C# logic module for this game.
    /// Looked up in <see cref="Cards.Engine.LogicRegistry"/> by key.
    /// Omit for definition-only games that run entirely on the engine defaults.
    /// Defaults to <see cref="Id"/> when absent.
    /// </summary>
    [JsonPropertyName("implementation")]
    public string? Implementation { get; set; }

    [JsonPropertyName("deck")]
    public JsonElement Deck { get; set; }

    [JsonPropertyName("deal")]
    public DealDefinition? Deal { get; set; }

    [JsonPropertyName("players")]
    public PlayerConfig? Players { get; set; }

    [JsonPropertyName("teams")]
    public JsonElement Teams { get; set; }

    [JsonPropertyName("rounds")]
    public RoundsDefinition? Rounds { get; set; }

    [JsonPropertyName("zones")]
    public List<ZoneDefinition> Zones { get; set; } = [];

    [JsonPropertyName("phases")]
    public List<PhaseDefinition> Phases { get; set; } = [];

    /// <summary>
    /// Path-based overrides applied after merging with the parent definition.
    /// Only present in child definitions that use <c>"extends"</c>.
    /// Keys follow the same format as <c>house_rules[].affects</c>:
    ///   "players", "teams", "scoring", "win_condition" — full object replacements.
    ///   "scoring.field", "phaseId.param" — targeted field patches.
    /// </summary>
    [JsonPropertyName("overrides")]
    public Dictionary<string, JsonElement>? Overrides { get; set; }

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

    private TeamsDefinition? _teamsCache;

    /// <summary>
    /// Parsed teams configuration, or <c>null</c> when <c>"teams": false</c>.
    /// Cached after the first call.
    /// </summary>
    public TeamsDefinition? TeamsConfig
    {
        get
        {
            if (!HasTeams) return null;
            return _teamsCache ??= JsonSerializer.Deserialize<TeamsDefinition>(Teams.GetRawText());
        }
    }

    /// <summary>Clears the cached teams config (call after patching the Teams field).</summary>
    public void InvalidateTeamsCache() => _teamsCache = null;

    /// <summary>Scale factor applied to the base card size (1.0 = default).</summary>
    public float CardScale => Ui?.CardScale ?? 1.0f;
}

public class PlayerConfig
{
    [JsonPropertyName("min")]
    public int Min { get; set; } = 2;

    [JsonPropertyName("max")]
    public int Max { get; set; } = 4;

    /// <summary>
    /// Display names for each player seat, indexed by player position.
    /// <c>names[0]</c> is the human player; subsequent entries are opponents or AI seats.
    /// Defaults to "Player 1", "Player 2", … when absent.
    /// </summary>
    [JsonPropertyName("names")]
    public List<string>? Names { get; set; }
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

    /// <summary>Grid zone row count (type = "grid" only).</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 2;

    /// <summary>Grid zone column count (type = "grid" only).</summary>
    [JsonPropertyName("cols")]
    public int Cols { get; set; } = 3;

    /// <summary>How many cards the player may peek at on initial deal (type = "grid" only).</summary>
    [JsonPropertyName("peek_count")]
    public int PeekCount { get; set; } = 0;
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

public class TeamsDefinition
{
    /// <summary>Number of teams (default 2).</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 2;

    /// <summary>Players per team (default 2).</summary>
    [JsonPropertyName("size")]
    public int Size { get; set; } = 2;

    /// <summary>
    /// How players are assigned to teams by seat index.
    /// "alternating" (default) — even seats = team 0, odd seats = team 1 (e.g. 0,2 vs 1,3).
    /// "sequential"            — first N seats = team 0, next N = team 1 (e.g. 0,1 vs 2,3).
    /// </summary>
    [JsonPropertyName("arrangement")]
    public string Arrangement { get; set; } = "alternating";

    /// <summary>
    /// When set, teams are only used for games with these exact player counts.
    /// Outside these counts the game plays as individual (no team grouping).
    /// </summary>
    [JsonPropertyName("only_when_players")]
    public List<int>? OnlyWhenPlayers { get; set; }
}

public class DealDefinition
{
    /// <summary>
    /// Cards dealt per player.  Either a plain integer, or an array of
    /// <c>{ "max_players": N, "cards": N }</c> rules evaluated top-to-bottom —
    /// the first rule whose max_players is ≥ the actual player count wins.
    /// Omit max_players on the last entry to serve as a catch-all default.
    /// Ignored when <c>pattern</c> is set.
    /// </summary>
    [JsonPropertyName("cards_per_player")]
    public JsonElement CardsPerPlayer { get; set; }

    /// <summary>
    /// Group-size deal pattern, e.g. <c>"3-2"</c> for Euchre.
    /// Each number is the batch size dealt to every player in one clockwise pass.
    /// "3-2" → pass 1: 3 cards each, pass 2: 2 cards each (5 total per player).
    /// When present, takes precedence over <c>cards_per_player</c>.
    /// </summary>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("remainder_to")]
    public string? RemainderTo { get; set; }

    [JsonPropertyName("face")]
    public string? Face { get; set; }

    /// <summary>
    /// After dealing, flip the top card of the deck face-up and place it in
    /// this zone.  Typically used to seed a discard pile, e.g. Crazy Eights,
    /// Rummy.  Ignored when absent.
    /// </summary>
    [JsonPropertyName("then_flip_top_to")]
    public string? ThenFlipTopTo { get; set; }

    /// <summary>
    /// Optional explicit animation deal sequence.
    /// Each element is [playerIndex, cardCount] — cards are dealt to that player
    /// in that group size, in sequence.  When absent the animation defaults to
    /// one card clockwise per player per round.
    /// </summary>
    [JsonPropertyName("anim_deal_steps")]
    public List<int[]>? AnimDealSteps { get; set; }

    /// <summary>
    /// Milliseconds between each individual card animation when dealing.
    /// Defaults to 130 when absent.  Set lower (e.g. 20) for games that deal
    /// many cards (War) so the animation doesn't take too long.
    /// </summary>
    [JsonPropertyName("anim_delay_ms")]
    public int? AnimDelayMs { get; set; }

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

public class RoundsDefinition
{
    /// <summary>
    /// When the round loop ends.
    /// <c>"win_condition"</c> (default) — stop when the win condition is satisfied.
    /// <c>"fixed:N"</c> — stop after exactly N rounds (overrides win_condition.count).
    /// </summary>
    [JsonPropertyName("repeat_until")]
    public string RepeatUntil { get; set; } = "win_condition";

    /// <summary>
    /// How the dealer seat rotates between rounds.
    /// <c>"rotates_left"</c> | <c>"rotates_right"</c> | <c>"winner"</c> |
    /// <c>"loser"</c> | <c>"alternates"</c>
    /// </summary>
    [JsonPropertyName("dealer")]
    public string Dealer { get; set; } = "rotates_left";

    /// <summary>
    /// How the initial dealer is chosen.
    /// <c>"random"</c> (default) | <c>"high_card"</c>
    /// </summary>
    [JsonPropertyName("first_dealer")]
    public string FirstDealer { get; set; } = "random";
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

    /// <summary>
    /// Map of JSON-path-style keys to override values.
    /// Applied to the <see cref="GameDefinition"/> when this rule is enabled.
    /// Examples:
    ///   "battle.tie_resolution": "split"     — sets a phase parameter
    ///   "deal.cards_per_player": 7            — changes the deal count
    ///   "win_condition.count": 9              — adjusts win-condition field
    ///   "deck": "standard-52-jokers"          — swaps the deck
    ///   "scoring.bag_penalty": null           — clears a scoring parameter
    /// </summary>
    [JsonPropertyName("affects")]
    public Dictionary<string, JsonElement>? Affects { get; set; }

    // Runtime state — not from JSON
    [JsonIgnore]
    public bool IsEnabled { get; set; }
}
