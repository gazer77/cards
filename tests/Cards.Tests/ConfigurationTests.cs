using System.Text.Json;
using Cards.Engine;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// One game, several shapes.
///
/// Euchre at three players and at four was two definitions differing in four rules, and
/// poker is three entries in the picker for one game. A configuration is that difference
/// written where the difference is: matched against the table when the table decides it
/// (how many are playing), or offered by name when nothing about the table does (which
/// variant of poker you meant).
/// </summary>
public sealed class ConfigurationTests
{
    private static GameDefinition Parse(string json)
        => JsonSerializer.Deserialize<GameDefinition>(json, GameLoader.JsonOptions)!;

    private const string Euchreish = """
    {
      "id": "x", "name": "X",
      "players": { "min": 3, "max": 4 },
      "teams": false,
      "scoring": { "type": "euchre", "makers_win": { "tricks_5": 2 } },
      "configurations": [
        { "when": { "players": 4 },
          "teams": { "count": 2, "size": 2 },
          "scoring": { "makers_win": { "tricks_3_or_4": 1 } } }
      ]
    }
    """;

    [Fact]
    public void A_shape_matched_by_the_seat_count_applies_without_asking()
    {
        var definition = Parse(Euchreish);

        var three = GameConfiguration.Resolve(definition, 3);
        var four  = GameConfiguration.Resolve(definition, 4);

        Assert.Null(three.TeamsConfig);
        Assert.NotNull(four.TeamsConfig);
        Assert.Equal(2, four.TeamsConfig!.Count);
    }

    [Fact]
    public void A_shape_says_only_what_it_changes()
    {
        // The four-player shape mentions one key inside scoring; the rest of scoring —
        // and the whole of the game around it — comes through untouched.
        var four = GameConfiguration.Resolve(Parse(Euchreish), 4);

        Assert.Equal("euchre", four.Scoring!.Type);
        Assert.Equal("X", four.Name);
        Assert.True(four.Scoring.Extra!["makers_win"].TryGetProperty("tricks_5", out var kept));
        Assert.Equal(2, kept.GetInt32());
        Assert.True(four.Scoring.Extra["makers_win"].TryGetProperty("tricks_3_or_4", out var added));
        Assert.Equal(1, added.GetInt32());
    }

    [Fact]
    public void The_shapes_themselves_do_not_survive_into_the_game_being_played()
    {
        // Resolved once, before anything is dealt. Leaving them in would let a save
        // resolve twice and a validator check one game against another's rules.
        Assert.Empty(GameConfiguration.Resolve(Parse(Euchreish), 4).Configurations);
    }

    [Fact]
    public void A_game_with_one_shape_is_handed_back_exactly_as_it_was()
    {
        var plain = Parse("""{ "id": "x", "name": "X" }""");
        Assert.Same(plain, GameConfiguration.Resolve(plain, 4));
    }

    private const string Pokerish = """
    {
      "id": "p", "name": "Poker",
      "players": { "min": 2, "max": 8 },
      "configurations": [
        { "name": "Holdem", "description": "Two down, five shared.", "default": true,
          "deck": "standard-52" },
        { "name": "Stud", "description": "Seven each, four of them showing.",
          "deck": "standard-52-jokers" }
      ]
    }
    """;

    [Fact]
    public void A_named_shape_is_offered_and_the_default_is_what_nobody_chose()
    {
        var definition = Parse(Pokerish);

        Assert.Equal(["Holdem", "Stud"], GameConfiguration.Offered(definition, 4).Select(c => c.Name));
        Assert.Equal("Holdem", GameConfiguration.DefaultFor(definition, 4)!.Name);

        Assert.Equal("standard-52",        GameConfiguration.Resolve(definition, 4).DeckType);
        Assert.Equal("standard-52-jokers", GameConfiguration.Resolve(definition, 4, "Stud").DeckType);
    }

    [Fact]
    public void A_name_nobody_offers_falls_back_to_the_default_rather_than_no_game()
    {
        Assert.Equal("standard-52", GameConfiguration.Resolve(Parse(Pokerish), 4, "Omaha").DeckType);
    }

    [Fact]
    public void A_variant_may_be_offered_only_where_it_fits()
    {
        var definition = Parse("""
        {
          "id": "p", "name": "P", "players": { "min": 2, "max": 8 },
          "configurations": [
            { "name": "Two-handed", "when": { "max_players": 2 }, "deck": "standard-52" },
            { "name": "Full table", "default": true, "deck": "standard-52-jokers" }
          ]
        }
        """);

        Assert.Equal(["Two-handed", "Full table"], GameConfiguration.Offered(definition, 2).Select(c => c.Name));
        Assert.Equal(["Full table"],               GameConfiguration.Offered(definition, 5).Select(c => c.Name));
    }

    [Fact]
    public void A_matched_shape_and_a_chosen_one_both_apply_with_the_choice_last()
    {
        var definition = Parse("""
        {
          "id": "p", "name": "P", "players": { "min": 2, "max": 6 },
          "ui": { "card_scale": 1.0 },
          "configurations": [
            { "when": { "players": 2 }, "ui": { "card_scale": 1.4, "allow_sort": false } },
            { "name": "Loud", "default": true, "ui": { "card_scale": 2.0 } }
          ]
        }
        """);

        var resolved = GameConfiguration.Resolve(definition, 2);
        Assert.Equal(2.0f, resolved.Ui!.CardScale);      // the choice has the last word
        Assert.False(resolved.Ui.AllowSort);             // and the match still applies
    }
}

/// <summary>
/// What a definition may not say about its shapes. Each of these is a definition that
/// would otherwise load and play something other than what it looks like.
/// </summary>
public sealed class ConfigurationValidationTests
{
    private static IReadOnlyList<string> Check(string json)
        => DefinitionValidator.Validate(
            JsonSerializer.Deserialize<GameDefinition>(json, GameLoader.JsonOptions)!);

    [Fact]
    public void A_shape_that_is_neither_named_nor_matched_is_refused()
    {
        Assert.Contains(Check("""
            { "id": "x", "name": "X", "configurations": [ { "teams": false } ] }
            """), p => p.StartsWith("configurations[0]:"));
    }

    [Fact]
    public void Two_shapes_of_the_same_name_are_refused()
    {
        Assert.Contains(Check("""
            { "id": "x", "name": "X", "configurations": [
                { "name": "Stud", "teams": false },
                { "name": "stud", "teams": false } ] }
            """), p => p.Contains("already called"));
    }

    [Fact]
    public void Two_defaults_are_refused()
    {
        Assert.Contains(Check("""
            { "id": "x", "name": "X", "configurations": [
                { "name": "A", "default": true, "teams": false },
                { "name": "B", "default": true, "teams": false } ] }
            """), p => p.Contains("only one shape"));
    }

    [Fact]
    public void A_shape_that_would_not_load_as_a_game_is_refused_when_it_is_written()
    {
        // The point of checking every shape at load: this one is only wrong at three
        // players, and a table of three might not sit down for months.
        var problems = Check("""
            {
              "id": "x", "name": "X", "players": { "min": 3, "max": 4 },
              "zones": [ { "id": "hand", "type": "hand", "owner": "each_player" } ],
              "configurations": [
                { "when": { "players": 3 },
                  "zones": [ { "id": "hand", "type": "hand", "owner": "each_player",
                               "layout": { "region": "nowhere-in-particular" } } ] }
              ]
            }
            """);

        Assert.Contains(problems, p => p.Contains("configurations at 3 players")
                                    && p.Contains("nowhere-in-particular"));
    }
}
