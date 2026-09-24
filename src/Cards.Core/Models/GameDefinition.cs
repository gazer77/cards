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

    /// <summary>
    /// Seats the game fills itself, after the players' — see <see cref="RoleDefinition"/>.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<RoleDefinition> Roles { get; set; } = [];

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

    [JsonPropertyName("blinds")]
    public BlindsDefinition? Blinds { get; set; }

    [JsonPropertyName("ante")]
    public AnteDefinition? Ante { get; set; }

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

    /// <summary>
    /// The words on the table — every message and button label, by key. Anything not
    /// declared keeps its default wording. See <see cref="TextDefinition"/>.
    /// </summary>
    [JsonPropertyName("text")]
    public TextDefinition? Text { get; set; }

    /// <summary>
    /// The score card on the table, if this game shows one. See <see cref="ScoreCardDefinition"/>.
    /// </summary>
    [JsonPropertyName("score_card")]
    public ScoreCardDefinition? ScoreCard { get; set; }

    /// <summary>
    /// The shapes this game can take — by seat count, or by a name the player picks.
    /// See <see cref="ConfigurationDefinition"/>. Empty for a game with one shape.
    /// </summary>
    [JsonPropertyName("configurations")]
    public List<ConfigurationDefinition> Configurations { get; set; } = [];

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

    /// <summary>
    /// Initial score (chip count) each player starts with.
    /// Used by chip-based games (Texas Hold'em, Stud).
    /// Default 0 means no starting chips.
    /// </summary>
    [JsonPropertyName("starting_score")]
    public int StartingScore { get; set; } = 0;
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

    /// <summary>
    /// Which way dealt cards land in this zone — "up" or "down" — overriding the deal's
    /// own <c>face</c>. Golf's grid is dealt face-down whatever the deal says, then
    /// <c>peek_count</c> turns two. Omitted, the deal decides.
    /// </summary>
    [JsonPropertyName("initial_face")]
    public string? InitialFace { get; set; }

    /// <summary>
    /// How the cards sit: "full" (side by side), "compact" (overlapped, index showing),
    /// or "stack" (top card only, with a count). Geometry, not information — what may
    /// be SEEN stays visibility's job. Null takes the default for the zone's type.
    /// </summary>
    [JsonPropertyName("arrangement")]
    public string? Arrangement { get; set; }

    /// <summary>A declared caption for the zone. Null keeps the renderer's default.</summary>
    [JsonPropertyName("label")]
    public ZoneLabelDefinition? Label { get; set; }

    /// <summary>
    /// A caption for each group in a grouped zone — what names a meld when a wild on
    /// top hides which rank it is of.
    /// </summary>
    [JsonPropertyName("group_label")]
    public ZoneLabelDefinition? GroupLabel { get; set; }

    /// <summary>
    /// Small coloured counters beside each group — cards held, books completed. Each
    /// shows one quantity; several may sit side by side.
    /// </summary>
    [JsonPropertyName("group_badges")]
    public List<BadgeDefinition> GroupBadges { get; set; } = [];

    /// <summary>
    /// Card size in this zone, relative to the table's base card: 1.5 draws them half
    /// again as large, 0.75 three-quarters. Melds are read while a deck is merely
    /// recognised, and a definition knows which is which.
    /// </summary>
    [JsonPropertyName("card_scale")]
    public float CardScale { get; set; } = 1.0f;

    /// <summary>
    /// Where the zone sits on the table — see <see cref="ZoneLayoutDefinition"/>. Any
    /// zone declaring one switches the whole definition to declared layout; zones
    /// without one then take the default region for their kind.
    /// </summary>
    [JsonPropertyName("layout")]
    public ZoneLayoutDefinition? Layout { get; set; }

    /// <summary>
    /// Rules that fire as cards arrive here — see <see cref="OnReceiveRule"/>. Settled
    /// after any arrival: dealt, drawn, or picked up.
    /// </summary>
    [JsonPropertyName("on_receive")]
    public List<OnReceiveRule> OnReceive { get; set; } = [];

    /// <summary>
    /// How groups are placed within the zone. "flow" (default) lays them in the order
    /// laid, wrapping; "by_rank" gives every rank in the deck a fixed slot, wild slot
    /// last — shorthand for a <see cref="Slots"/> list the definition did not want to
    /// write out; "slots" uses the list.
    /// </summary>
    [JsonPropertyName("group_layout")]
    public string GroupLayout { get; set; } = "flow";

    /// <summary>
    /// The fixed slots of a grouped zone, in order, when <c>group_layout</c> is
    /// "slots": which cards each holds and what it says while empty. Written out, a
    /// definition controls the whole strip — order, which ranks exist, where the wilds
    /// go, a slot for cards set aside like red threes.
    /// </summary>
    [JsonPropertyName("slots")]
    public List<SlotDefinition> Slots { get; set; } = [];

    /// <summary>Where every empty slot's label sits unless the slot says otherwise. Omitted, centred.</summary>
    [JsonPropertyName("slot_label_place")]
    public PlaceDefinition? SlotLabelPlace { get; set; }
}

/// <summary>
/// Where something sits relative to the cards it decorates, in the cards' own
/// proportions so it scales with them.
///
/// <c>x</c>/<c>y</c> name a point on the cards as percentages of their width and
/// height — 0% is the left or top edge, 100% the right or bottom, 50% the middle, and
/// values outside 0–100 land outside the cards. <c>anchor</c> says which point of the
/// box lands there. So a badge over the bottom-left corner is
/// <c>{ "x": "0%", "y": "100%" }</c> (anchor defaults to center); tucked inside the
/// corner is the same with <c>"anchor": "bottom-left"</c>; a strip across the bottom
/// edge adds <c>"width": "100%"</c>.
/// </summary>
public class PlaceDefinition
{
    /// <summary>Percent of the cards' width; "0%" is the left edge.</summary>
    [JsonPropertyName("x")]
    public string X { get; set; } = "50%";

    /// <summary>Percent of the cards' height; "0%" is the top edge.</summary>
    [JsonPropertyName("y")]
    public string Y { get; set; } = "50%";

    /// <summary>
    /// Which point of the box lands on (x, y): "center" (default), "top-left", "top",
    /// "top-right", "left", "right", "bottom-left", "bottom", "bottom-right".
    /// </summary>
    [JsonPropertyName("anchor")]
    public string Anchor { get; set; } = "center";

    /// <summary>Box width as a percent of the cards' width. Omitted, the box fits its text.</summary>
    [JsonPropertyName("width")]
    public string? Width { get; set; }

    /// <summary>Box height as a percent of the cards' height. Omitted, the box fits its text.</summary>
    [JsonPropertyName("height")]
    public string? Height { get; set; }

    /// <summary>"left" | "center" (default) | "right" — text within the box.</summary>
    [JsonPropertyName("text_align")]
    public string TextAlign { get; set; } = "center";

    /// <summary>"top" | "middle" (default) | "bottom" — text within the box.</summary>
    [JsonPropertyName("vertical_align")]
    public string VerticalAlign { get; set; } = "middle";
}

/// <summary>
/// One counter beside a group. Placement, orientation and condition work as they do
/// for a <see cref="ZoneLabelDefinition"/>; several badges on the same side sit in a
/// row, in declaration order. A <see cref="Place"/> overrides <c>placement</c> with an
/// exact position.
/// </summary>
public class BadgeDefinition
{
    /// <summary>Exact position, in the cards' proportions. Overrides <c>placement</c>.</summary>
    [JsonPropertyName("place")]
    public PlaceDefinition? Place { get; set; }

    /// <summary>
    /// What it shows: "cards" (in the group), "books" (complete sets of book_size),
    /// "loose" (cards beyond the last complete book — the working stack), or "score"
    /// (what the zone would score if the round ended now, by this game's own scoring —
    /// not a count, so a zero is shown rather than hidden).
    /// </summary>
    [JsonPropertyName("shows")]
    public string Shows { get; set; } = "cards";

    /// <summary>Badge background, as #RRGGBB. The theme's default when omitted.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>Badge text colour, as #RRGGBB. Chosen for contrast when omitted.</summary>
    [JsonPropertyName("text_color")]
    public string? TextColor { get; set; }

    /// <summary>
    /// What to do when the quantity is zero: "hide" the badge, or show this text
    /// instead of "0" — a dash, typically.
    /// </summary>
    [JsonPropertyName("zero")]
    public string Zero { get; set; } = "hide";

    [JsonPropertyName("placement")]
    public string Placement { get; set; } = "bottom";

    [JsonPropertyName("orientation")]
    public string Orientation { get; set; } = "horizontal";

    [JsonPropertyName("when")]
    public JsonElement? When { get; set; }
}

/// <summary>
/// A caption a definition places beside a zone or each of its groups.
///
/// <c>text</c> may use placeholders: <c>{rank}</c> (a group's natural rank — group
/// labels only), <c>{count}</c> (cards held), <c>{owner}</c> (the owning player or
/// team's name). Pluralisation stays in the text ("{rank}s"), not in code.
/// </summary>
public class ZoneLabelDefinition
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>"top" | "bottom" (default) | "left" | "right", relative to the cards.</summary>
    [JsonPropertyName("placement")]
    public string Placement { get; set; } = "bottom";

    /// <summary>"horizontal" (default) | "vertical" | "angled".</summary>
    [JsonPropertyName("orientation")]
    public string Orientation { get; set; } = "horizontal";

    /// <summary>A condition; the label shows only while it holds. Absent means always.</summary>
    [JsonPropertyName("when")]
    public JsonElement? When { get; set; }

    /// <summary>Exact position, in the cards' proportions. Overrides <c>placement</c>.</summary>
    [JsonPropertyName("place")]
    public PlaceDefinition? Place { get; set; }
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
    /// Ordered list of sort modes offered to the player in the Sort Hand picker.
    /// Allowed values: "suit_value", "rank_ace_high", "rank", "suit_stable", "suit".
    /// Null / empty means all standard modes are shown.
    /// The first entry is presented as the recommended default.
    /// </summary>
    [JsonPropertyName("sort_modes")]
    public List<string>? SortModes { get; set; }

    /// <summary>
    /// The sort mode pre-selected in the picker (appears first in the list).
    /// Falls back to the first entry of SortModes, or "rank_ace_high" if both are unset.
    /// </summary>
    [JsonPropertyName("default_sort")]
    public string? DefaultSort { get; set; }

    /// <summary>
    /// Whether the bubble that names a tapped card also says what it is worth —
    /// "Jack of Clubs — 10".
    ///
    /// What a card is worth is a rule of the game, not a property of the card: a King
    /// is nothing in Golf, ten in Hand and Foot, and neither in Hearts, and none of
    /// that is on the face. So a game that scores its cards says it here and the value
    /// is part of the card's name from then on, for every player, rather than waiting
    /// on a setting each of them would have to find.
    ///
    /// Left unset it is true for any game whose scoring states card values, which is
    /// exactly the set of games where a card means more than its rank and suit. Set it
    /// false for a game that would rather not say.
    /// </summary>
    [JsonPropertyName("show_card_values")]
    public bool? ShowCardValues { get; set; }

    /// <summary>
    /// Whether the table marks which seat is dealing, the way a card table puts a
    /// button in front of the dealer.
    ///
    /// Unset means "when the game has a dealer at all" — any game whose <c>rounds</c>
    /// names a dealer or a first dealer. In Euchre it decides who bids first and who
    /// takes the turned card up; in Hearts it decides nothing at all, which is why the
    /// games that rotate one are exactly the games that show one.
    /// </summary>
    [JsonPropertyName("show_dealer")]
    public bool? ShowDealer { get; set; }

    /// <summary>What the dealer's mark says. One or two characters; "D" by default.</summary>
    [JsonPropertyName("dealer_label")]
    public string DealerLabel { get; set; } = "D";

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

    /// <summary>
    /// Hand and Foot-style deal: deal this many stacks per player.
    /// First stack → <c>hand:{playerId}</c>, second → <c>foot:{playerId}</c>, etc.
    /// Use with <c>cards_per_stack</c>.
    /// </summary>
    [JsonPropertyName("stacks_per_player")]
    public int StacksPerPlayer { get; set; } = 0;

    /// <summary>Cards per stack when <c>stacks_per_player</c> is set.</summary>
    [JsonPropertyName("cards_per_stack")]
    public int CardsPerStack { get; set; } = 13;

    /// <summary>
    /// Zone type to deal into.  Defaults to "hand"; set to "grid" for Golf-style games
    /// where cards are dealt face-down into a player layout instead of a hand zone.
    /// </summary>
    [JsonPropertyName("target_zone")]
    public string TargetZone { get; set; } = "hand";

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
    /// After dealing (and moving the remainder), flip the top card of this
    /// named zone face-up in place.  Used for Euchre's kitty turn-up.
    /// </summary>
    [JsonPropertyName("then_flip_top_of")]
    public string? ThenFlipTopOf { get; set; }

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

public class BlindsDefinition
{
    [JsonPropertyName("small")]
    public BlindEntry? Small { get; set; }

    [JsonPropertyName("big")]
    public BlindEntry? Big { get; set; }
}

public class BlindEntry
{
    /// <summary>"left_of_dealer" | "two_left_of_dealer"</summary>
    [JsonPropertyName("position")]
    public string Position { get; set; } = "left_of_dealer";

    [JsonPropertyName("amount")]
    public int Amount { get; set; } = 1;
}

public class AnteDefinition
{
    [JsonPropertyName("amount")]
    public int Amount { get; set; } = 1;
}

/// <summary>
/// What a card must be for an <see cref="OnReceiveRule"/> to apply. Every field given
/// must hold; a field omitted does not care.
/// </summary>
public class CardMatch
{
    [JsonPropertyName("rank")]
    public string? Rank { get; set; }

    /// <summary>"red" or "black".</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("suit")]
    public string? Suit { get; set; }

    /// <summary>Whether the game treats the card as wild.</summary>
    [JsonPropertyName("wild")]
    public bool? Wild { get; set; }
}

/// <summary>
/// A rule that fires when a matching card arrives in a zone: move it to another zone
/// and, optionally, draw a replacement.
/// </summary>
public class OnReceiveRule
{
    [JsonPropertyName("card")]
    public CardMatch? Card { get; set; }

    /// <summary>Base id of the zone the card goes to, resolved for the receiving zone's owner.</summary>
    [JsonPropertyName("move_to")]
    public string MoveTo { get; set; } = string.Empty;

    /// <summary>Zone to draw one replacement from, per card moved. Omitted, no replacement.</summary>
    [JsonPropertyName("replace_from")]
    public string? ReplaceFrom { get; set; }
}

/// <summary>
/// Where a zone sits on the table, one of two ways.
///
/// A <c>region</c> is a well-known area. Shared zones use <c>center</c>,
/// <c>center-left</c>, <c>center-right</c>, <c>center-top</c>, <c>center-bottom</c>;
/// owned zones use <c>seat</c> (the owner's edge, where a hand goes) or
/// <c>seat-front</c> (the strip inboard of it, where melds go). Zones sharing a region
/// sit side by side in declaration order.
///
/// A <c>place</c> is exact, in percentages of the table — the same model badges and
/// labels use for the cards. For an owned zone it is written as if for the seat at the
/// bottom of the screen and turned to each other seat, so one declaration serves every
/// player: a meld area at <c>y: 70%</c> for the bottom seat is at <c>y: 30%</c>, upside
/// down, for the seat across the table.
/// </summary>
public class ZoneLayoutDefinition
{
    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("place")]
    public PlaceDefinition? Place { get; set; }
}

/// <summary>
/// One fixed slot in a grouped zone, and which cards belong in it.
///
/// A group is matched by its defining natural rank — a meld of 4s with two wilds in it
/// is a meld of 4s, and goes where 4s go. A group with no natural card is matched by
/// <c>wild: true</c>; that is how a wilds-only slot is declared, and why a slot for
/// "4s" needs no mention of wilds. The first slot whose match fits wins.
/// </summary>
public class SlotDefinition
{
    [JsonPropertyName("match")]
    public CardMatch Match { get; set; } = new();

    /// <summary>Shown in the slot while it is empty. Omitted, the slot shows nothing.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// Where the empty-slot label sits, in the slot's proportions. Omitted, centred.
    /// Set once on the zone's <c>slot_label_place</c> to position every slot's label
    /// the same way; a slot's own place overrides it.
    /// </summary>
    [JsonPropertyName("place")]
    public PlaceDefinition? Place { get; set; }
}

/// <summary>
/// Overrides for what the table says. Keys are named by the engine — the schema lists
/// them with their default wording — and a definition replaces any it likes:
///
/// <code>
/// "text": {
///   "actions":  { "meld": "Lay Books", "draw_from_deck": "Draw" },
///   "messages": { "turn_draw": "{player} to draw", "turn_draw_you": "Draw two" }
/// }
/// </code>
///
/// A message key may have a <c>_you</c> variant, used when the player concerned is the
/// one at this screen. Placeholders: <c>{player}</c>, <c>{card}</c>, <c>{rank}</c>,
/// <c>{count}</c>, <c>{required}</c>, <c>{offered}</c>, <c>{zone}</c>.
/// </summary>
public class TextDefinition
{
    [JsonPropertyName("actions")]
    public Dictionary<string, string> Actions { get; set; } = [];

    [JsonPropertyName("messages")]
    public Dictionary<string, string> Messages { get; set; } = [];
}

/// <summary>
/// A score card on the table: what each side has scored, and optionally what each round
/// scored on the way there.
///
/// Placed like everything else — a <see cref="PlaceDefinition"/> in table percentages —
/// so a game decides where its scores belong rather than the renderer deciding for it.
///
/// Two views of the same figures, because both are wanted at different moments:
///   "total"  — one number per side. Small, and always readable.
///   "detail" — a column per round with the total at the end. Golf's nine holes.
/// A game names the view it opens in; the player switches with a tap when
/// <c>collapsible</c> is left true, and that choice is theirs and not saved into the game.
/// </summary>
public class ScoreCardDefinition
{
    /// <summary>Heading above the rows. Empty for none.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "Scores";

    /// <summary>"total" (default) or "detail" — the view it opens in.</summary>
    [JsonPropertyName("view")]
    public string View { get; set; } = "total";

    /// <summary>Whether a tap switches between the two views. Default true.</summary>
    [JsonPropertyName("collapsible")]
    public bool Collapsible { get; set; } = true;

    /// <summary>"player" (default) or "team" — one row per seat, or per side.</summary>
    [JsonPropertyName("by")]
    public string By { get; set; } = "player";

    /// <summary>
    /// How many rounds the detail view shows, most recent last. A game of many short
    /// rounds would otherwise write columns until they were too thin to read.
    /// </summary>
    [JsonPropertyName("max_rounds")]
    public int MaxRounds { get; set; } = 9;

    /// <summary>What to call a round in the detail view's header — "Hole", "Round".</summary>
    [JsonPropertyName("round_label")]
    public string RoundLabel { get; set; } = "R";

    /// <summary>Where it sits. Required: a score card with no place has nowhere to go.</summary>
    [JsonPropertyName("place")]
    public PlaceDefinition? Place { get; set; }
}

/// <summary>
/// One shape a game can take, on top of what every shape of it shares.
///
/// Two games in the catalogue were really one game twice: Euchre at three players and
/// at four, differing in four rules; and poker, where Hold'em and Stud share a deck, a
/// betting vocabulary and nothing else. A configuration is the difference written where
/// the difference is, rather than as a second copy of the game.
///
/// A configuration is chosen one of two ways, and may use both:
///   • <c>when</c> — matched against what is known when the table sits down. Every
///     configuration that matches is applied, in declaration order, and nobody is asked
///     anything. Euchre's three-player rules work this way.
///   • <c>name</c> — offered to the player at setup. Exactly one named configuration
///     applies: the one chosen, or the one marked <c>default</c>. Poker's variants work
///     this way, because no fact about the table says whether you meant Stud.
///
/// Everything else in the object is a fragment of a game definition, merged onto the
/// base: objects merge key by key, and anything else replaces. So a configuration says
/// only what it changes.
/// </summary>
public class ConfigurationDefinition
{
    /// <summary>What to call this shape where a player picks one. Null: never offered.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>A sentence for the picker, saying what this shape is.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>What must be true for this shape to apply, or to be offered.</summary>
    [JsonPropertyName("when")]
    public ConfigurationMatch? When { get; set; }

    /// <summary>The named shape chosen when the player has not chosen one.</summary>
    [JsonPropertyName("default")]
    public bool Default { get; set; }

    /// <summary>The definition fragment this configuration merges in.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// What a configuration matches against. Only the player count today — the one fact a
/// table knows about itself before a card is dealt.
/// </summary>
public class ConfigurationMatch
{
    /// <summary>An exact seat count.</summary>
    [JsonPropertyName("players")]
    public int? Players { get; set; }

    /// <summary>The fewest seats this applies to, inclusive.</summary>
    [JsonPropertyName("min_players")]
    public int? MinPlayers { get; set; }

    /// <summary>The most seats this applies to, inclusive.</summary>
    [JsonPropertyName("max_players")]
    public int? MaxPlayers { get; set; }

    /// <summary>Whether a table of this size is described by this match.</summary>
    public bool Matches(int players)
        => (Players is null    || players == Players)
        && (MinPlayers is null || players >= MinPlayers)
        && (MaxPlayers is null || players <= MaxPlayers);

    /// <summary>Whether the match says anything at all.</summary>
    public bool IsEmpty => Players is null && MinPlayers is null && MaxPlayers is null;
}

/// <summary>
/// A seat the game itself fills, beside the ones the players choose — Blackjack's
/// dealer. Chosen seat counts are players; a role seat is added on top of them and is
/// always driven by the engine, never by a person.
///
/// Before this, Blackjack counted the dealer as one of its "players", so the setup
/// screen offered one player and dealt a table with nobody at it but the dealer.
/// </summary>
public class RoleDefinition
{
    /// <summary>What the role is — used for the seat's name when none is given.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The seat's name on the table. Defaults to the id, capitalised.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>How many seats this role takes. Default one.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;
}
