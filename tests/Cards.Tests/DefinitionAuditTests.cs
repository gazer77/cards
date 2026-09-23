using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The audit that finds definition properties nothing reads.
///
/// A definition that states something the engine ignores is the failure this codebase
/// keeps producing: "rows": 2 drew one row, "initial_face": "down" dealt face-up, a
/// deck badge showed nothing. Each was right in the file and wrong on the table, with
/// no error anywhere. The audit turns that silence into a line of text.
///
/// The shipped games are not yet clean, so this pins exactly which properties are
/// ignored today, with why. A new one fails this test the moment it is written, which
/// is the whole point; clearing one means deleting a line from the list below and from
/// the backlog it belongs to.
/// </summary>
public sealed class DefinitionAuditTests
{
    /// <summary>
    /// Ignored today, by game and path. Each is a rule the definition states and the
    /// engine does not keep — listed in plan.md under "declared but not implemented".
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownGaps = new()
    {
        // A top-level "dealer": "random" — who deals first. The engine always starts
        // the deal at seat 0 and rotates from there.
        ["blackjack.json"] =
        [
            "deal.note",                    // a note to the reader, addressed to nobody
            "phases[0].allow_double_down",  // none of the four are offered as actions
            "phases[0].allow_split",
            "phases[0].allow_surrender",
            "phases[0].blackjack_pays",     // a natural pays even money today
            "roles",                        // a dealer seat with fixed rules; every seat is alike
        ],
        ["euchre-4p.json"]     = ["dealer", "phases[0].order", "phases[0].prompt", "phases[1].order"],
        ["gin-rummy.json"]     = ["dealer"],
        ["go-fish.json"]       = ["dealer"],
        ["golf.json"]          = ["dealer"],
        // locked_until: the foot opens when the hand empties, which the engine does by
        // rule rather than by reading this.
        ["hand-and-foot.json"] = ["dealer", "scoring.meld_types", "zones[3].locked_until"],
        ["hearts.json"]        = ["deal.order", "dealer"],
        ["high-card.json"]     = ["deal.order"],
        // meld_table: pinochle's meld values are a table in the scoring engine.
        ["pinochle.json"]      = ["deal.order", "dealer", "phases[0].order", "scoring.meld_table"],
        ["poker-stud.json"]    = ["dealer"],
        ["spades.json"]        = ["deal.order", "dealer", "phases[0].order"],
        ["texas-holdem.json"]  = ["dealer"],
        // deal.order / phase order: "clockwise", "left_of_dealer". The deal and the
        // bidding both go clockwise from the dealer's left already, so these describe
        // what happens without being what decides it.
        ["war.json"]           = ["deal.order"],
    };

    public static TheoryData<string> Games
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(GamesDir(), "*.json")) data.Add(path);
            return data;
        }
    }

    private static string GamesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "games")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "games");
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void No_definition_says_anything_new_that_nothing_reads(string path)
    {
        string file  = Path.GetFileName(path);
        var    found = DefinitionAudit.UnreadProperties(File.ReadAllText(path))
            .Select(line => line[..line.IndexOf(':')])
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var expected = (KnownGaps.TryGetValue(file, out var gaps) ? gaps : [])
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, found);
    }

    [Fact]
    public void A_misspelt_property_is_reported_rather_than_dropped()
    {
        // The exact shape of the Golf bug: a zone states how it should be dealt, spells
        // it wrong by one letter, and the deal reads nothing.
        const string json = """
            { "id": "x", "name": "X",
              "zones": [ { "id": "grid", "type": "grid", "initial_faces": "down" } ] }
            """;

        var problems = DefinitionAudit.UnreadProperties(json);
        Assert.Contains(problems, p => p.StartsWith("zones[0].initial_faces:"));

        // And the correct spelling is not reported.
        Assert.DoesNotContain(DefinitionAudit.UnreadProperties(json.Replace("initial_faces", "initial_face")),
                              p => p.Contains("initial_face"));
    }

    [Fact]
    public void A_parameter_meant_for_another_phase_type_is_reported()
    {
        // flip_after_discard is a draw_discard rule. On a trick-taking phase it is a
        // line that reads like a rule and is not one.
        const string json = """
            { "id": "x", "name": "X",
              "phases": [ { "id": "play", "type": "trick_taking", "flip_after_discard": true } ] }
            """;

        Assert.Contains(DefinitionAudit.UnreadProperties(json),
                        p => p.StartsWith("phases[0].flip_after_discard:") && p.Contains("trick_taking"));
    }

    [Fact]
    public void A_loaded_game_carries_its_warnings_without_being_stopped_by_them()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var game   = loader.LoadAsync("blackjack").GetAwaiter().GetResult();

        Assert.NotNull(game);                                   // a warning never blocks a game
        Assert.True(loader.LoadWarnings.ContainsKey("blackjack"));
        Assert.Contains("allow_split", loader.LoadWarnings["blackjack"]);
        Assert.False(loader.LoadErrors.ContainsKey("blackjack"));
    }
}

/// <summary>
/// The audit's table of what each phase type reads, held against the handlers' own
/// source. A handler that learns a new parameter without the table learning it would
/// report every game using that parameter as broken — a guard that cries wolf is worse
/// than no guard, so the table is checked rather than trusted.
/// </summary>
public sealed class PhaseKeyTableTests
{
    /// <summary>Handler file → the phase types it serves.</summary>
    private static readonly (string File, string[] Types)[] Handlers =
    [
        ("BiddingHandler.cs",         ["bidding"]),
        ("BlackjackRoundHandler.cs",  ["blackjack_round"]),
        ("DrawDiscardHandler.cs",     ["draw_discard"]),
        ("FreePlayHandler.cs",        ["free_play"]),
        ("GoFishHandler.cs",          ["go_fish"]),
        ("MeldHandler.cs",            ["meld"]),
        ("PassCardsHandler.cs",       ["pass_cards"]),
        ("PokerBettingHandler.cs",    ["poker_betting"]),
        ("RevealHandler.cs",          ["reveal"]),
        ("ShowdownHandler.cs",        ["showdown"]),
        ("TrickTakingHandler.cs",     ["trick_taking"]),
        ("WarHandler.cs",             ["war"]),
    ];

    /// <summary>
    /// Literals that match the shape of a definition read but are not one — a key read
    /// from metadata, or a value compared against.
    /// </summary>
    private static readonly string[] NotDefinitionKeys = ["false", "true", "trump"];

    [Fact]
    public void Every_parameter_a_handler_reads_is_in_the_table()
    {
        var engineDir = EngineDir();
        var missing   = new List<string>();

        foreach (var (file, types) in Handlers)
        {
            string source = File.ReadAllText(Path.Combine(engineDir, file));
            foreach (var key in KeysReadIn(source))
            {
                if (NotDefinitionKeys.Contains(key)) continue;
                foreach (var type in types)
                    if (!DefinitionAudit.PhaseKeys[type].Contains(key, StringComparer.OrdinalIgnoreCase))
                        missing.Add($"{type}: {file} reads '{key}', which the audit does not know.");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void Every_scoring_key_the_engine_reads_is_in_the_table()
    {
        string source  = File.ReadAllText(Path.Combine(EngineDir(), "ScoringEngine.cs"));
        var    missing = KeysReadIn(source)
            .Where(k => !NotDefinitionKeys.Contains(k))
            .Where(k => !DefinitionAudit.ScoringKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(missing.Count == 0,
            "ScoringEngine reads " + string.Join(", ", missing) + ", which the audit does not know.");
    }

    /// <summary>
    /// The definition keys a handler's source reads: the accessor helpers every handler
    /// uses, and the extension bag itself.
    /// </summary>
    private static IEnumerable<string> KeysReadIn(string source)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            """(?:Get\w+|Parse\w+)\([\w.?]+, *"(?<k>[a-z_0-9]+)"|Extra\??[.\[]TryGetValue\("(?<k>[a-z_0-9]+)"|Extra\??\["(?<k>[a-z_0-9]+)"\]""");

        return pattern.Matches(source)
            .Select(m => m.Groups["k"].Value)
            .Where(k => k.Length > 0)
            .Distinct();
    }

    private static string EngineDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Cards.Core")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Cards.Core", "Engine");
    }
}
