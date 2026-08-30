using System.Text.Json;

namespace Cards.Engine;

/// <summary>
/// What a deck is made of: which ranks, which suits, how many copies, how many jokers.
///
/// Games differ in composition, not merely in size — Euchre uses nine through Ace,
/// Pinochle uses that doubled, Hand and Foot uses several full packs. Previously each
/// combination needed a name blessed in C# (<c>euchre-24</c>, <c>pinochle-48</c>), which
/// meant a game could only use a deck someone had already thought of, and the largest
/// anyone had written down was two packs. Declaring the composition removes the menu.
///
/// The old names still work as shorthand, so no definition had to change.
/// </summary>
public sealed class DeckSpec
{
    private static readonly Suit[] AllSuits =
        [Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades];

    public required IReadOnlyList<Rank> Ranks  { get; init; }
    public required IReadOnlyList<Suit> Suits  { get; init; }
    public required int                 Copies { get; init; }
    public int                          Jokers { get; init; }

    /// <summary>Cards this spec produces, without building them.</summary>
    public int Size => Ranks.Count * Suits.Count * Copies + Jokers;

    // ── Named shorthands ──────────────────────────────────────────────────────

    private static readonly Rank[] StandardRanks =
    [
        Rank.Two, Rank.Three, Rank.Four, Rank.Five, Rank.Six, Rank.Seven,
        Rank.Eight, Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace
    ];

    private static readonly Rank[] ShortRanks =
        [Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace];

    /// <summary>
    /// The deck names that existed before composition could be declared. Kept so every
    /// existing definition keeps working, and because they read well for the common
    /// cases.
    /// </summary>
    public static DeckSpec? FromName(string name) => name switch
    {
        "standard-52"        => new() { Ranks = StandardRanks, Suits = AllSuits, Copies = 1 },
        "standard-104"       => new() { Ranks = StandardRanks, Suits = AllSuits, Copies = 2 },
        "standard-52-jokers" => new() { Ranks = StandardRanks, Suits = AllSuits, Copies = 1, Jokers = 2 },
        "euchre-24"          => new() { Ranks = ShortRanks,    Suits = AllSuits, Copies = 1 },
        "pinochle-48"        => new() { Ranks = ShortRanks,    Suits = AllSuits, Copies = 2 },
        _                    => null,
    };

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a <c>deck</c> declaration, which is either a shorthand name or an object:
    /// <code>
    /// "deck": "standard-52"
    /// "deck": { "ranks": "9-A", "copies": 2 }
    /// "deck": { "ranks": "2-A", "jokers": 12,
    ///           "copies": [ { "max_players": 4, "count": 5 }, { "count": 6 } ] }
    /// </code>
    /// Copies may scale with the table, in the same shape <c>cards_per_player</c>
    /// already uses, because how many packs a game wants usually depends on how many
    /// people are playing.
    /// </summary>
    /// <exception cref="FormatException">
    /// The declaration names a deck that does not exist, or describes one with no cards
    /// in it. Deliberately loud: the previous behaviour was to quietly substitute a
    /// standard 52, so a typo produced a game that dealt the wrong deck and failed much
    /// later, somewhere unrelated.
    /// </exception>
    public static DeckSpec Parse(JsonElement deck, int playerCount)
    {
        if (deck.ValueKind == JsonValueKind.String)
        {
            string name = deck.GetString() ?? "";
            return FromName(name)
                ?? throw new FormatException($"Unknown deck '{name}'.");
        }

        if (deck.ValueKind != JsonValueKind.Object)
            throw new FormatException(
                $"A deck must be a name or an object, not {deck.ValueKind}.");

        var ranks = deck.TryGetProperty("ranks", out var r) ? ParseRanks(r) : StandardRanks;
        var suits = deck.TryGetProperty("suits", out var s) ? ParseSuits(s) : AllSuits;

        int copies = deck.TryGetProperty("copies", out var c) ? ParseScaled(c, playerCount, 1) : 1;

        // Jokers scale alongside copies: a game wanting one pack per player wants that
        // pack's jokers too.
        int jokers = deck.TryGetProperty("jokers", out var j) ? ParseScaled(j, playerCount, 0) : 0;

        var spec = new DeckSpec
        {
            Ranks  = ranks,
            Suits  = suits,
            Copies = copies,
            Jokers = jokers,
        };

        if (spec.Size == 0)
            throw new FormatException("Deck declaration produces no cards.");

        return spec;
    }

    /// <summary>
    /// Ranks as a range (<c>"2-A"</c>), a list (<c>["9","10","J","Q","K","A"]</c>), or a
    /// shorthand name. A range is the common case and reads closest to how a person
    /// would describe the deck.
    /// </summary>
    /// <summary>
    /// What a deck expression may refer to. Only the table size: a deck is built before a
    /// hand exists, so nothing about the position is known yet.
    /// </summary>
    public static IReadOnlyDictionary<string, int> NamedValues(int playerCount)
        => new Dictionary<string, int> { ["players"] = playerCount };

    private static IReadOnlyList<Rank> ParseRanks(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray()
                .Select(x => ParseRank(x.GetString() ?? ""))
                .ToList();

        string text = element.GetString() ?? "";

        if (text.Equals("standard", StringComparison.OrdinalIgnoreCase)) return StandardRanks;
        if (text.Equals("short", StringComparison.OrdinalIgnoreCase))    return ShortRanks;

        int dash = text.IndexOf('-');
        if (dash <= 0)
            throw new FormatException($"Ranks must be a range like '2-A', a list, or a name; got '{text}'.");

        var from = ParseRank(text[..dash]);
        var to   = ParseRank(text[(dash + 1)..]);

        var range = StandardRanks.SkipWhile(x => x != from).ToList();
        int end   = range.IndexOf(to);
        if (end < 0)
            throw new FormatException($"Rank range '{text}' does not run low to high.");

        return range.Take(end + 1).ToList();
    }

    private static Rank ParseRank(string token) => token.Trim().ToUpperInvariant() switch
    {
        "A" or "ACE"   => Rank.Ace,
        "K" or "KING"  => Rank.King,
        "Q" or "QUEEN" => Rank.Queen,
        "J" or "JACK"  => Rank.Jack,
        "T" or "10"    => Rank.Ten,
        var n when int.TryParse(n, out int v) && v is >= 2 and <= 10 => (Rank)v,
        _ => throw new FormatException($"Unknown rank '{token}'."),
    };

    /// <summary>
    /// Suits, defaulting to the usual four. Restricting them is supported; inventing new
    /// ones is not — <see cref="Suit"/> is an enum the renderer draws by hand, so a
    /// fifth suit needs artwork before it needs parsing.
    /// </summary>
    private static IReadOnlyList<Suit> ParseSuits(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return AllSuits;

        return element.EnumerateArray()
            .Select(x => (x.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "clubs"    or "c" => Suit.Clubs,
                "diamonds" or "d" => Suit.Diamonds,
                "hearts"   or "h" => Suit.Hearts,
                "spades"   or "s" => Suit.Spades,
                var other => throw new FormatException($"Unknown suit '{other}'."),
            })
            .ToList();
    }

    /// <summary>
    /// A fixed count, or entries selected by table size — the first whose
    /// <c>max_players</c> the table does not exceed, with an entry lacking one acting as
    /// the default. Mirrors the shape <c>cards_per_player</c> already uses.
    ///
    /// Verbose for a rule as simple as "one pack per player, plus one" — an expression
    /// form is on the backlog, and this is the case that argues for it.
    /// </summary>
    private static int ParseScaled(JsonElement element, int playerCount, int fallback)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return Math.Max(0, element.GetInt32());

        // An expression says the rule outright — "players + 1" — rather than enumerating
        // a tier per table size and leaving the reader to infer the pattern.
        if (element.ValueKind == JsonValueKind.String)
            return Math.Max(0, RuleExpression.Evaluate(
                element.GetString() ?? "", NamedValues(playerCount)));

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                bool hasMax = item.TryGetProperty("max_players", out var mp);
                int count   = item.TryGetProperty("count", out var cv) ? cv.GetInt32() : fallback;

                if (!hasMax || playerCount <= mp.GetInt32())
                    return Math.Max(0, count);
            }
        }

        return fallback;
    }
}
