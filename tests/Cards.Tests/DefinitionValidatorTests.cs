using System.Text.Json;
using Cards.Engine;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// Definitions are checked when they load, and a game with an unreadable rule does not
/// appear at all.
///
/// That is deliberately harsh, because the alternative is not a loud failure — it is a
/// silent one. A condition the engine does not recognise never holds, so the game deals
/// and plays with one of its rules simply missing, which is how Hand and Foot shipped for
/// a long time, and how an unknown deck name used to quietly deal a standard 52.
/// </summary>
public sealed class DefinitionValidatorTests
{
    private static GameDefinition Parse(string json)
        => JsonSerializer.Deserialize<GameDefinition>(json, GameLoader.JsonOptions)!;

    /// <summary>A minimal definition with one draw_discard phase carrying the given JSON.</summary>
    private static GameDefinition WithPhase(string phaseJson) => Parse($$"""
    {
      "id": "probe", "name": "Probe", "version": "1.0",
      "deck": "standard-52",
      "players": { "min": 2, "max": 4 },
      "zones": [ { "id": "deck", "type": "deck" } ],
      "phases": [ {{phaseJson}} ]
    }
    """);

    [Fact]
    public void A_sound_definition_has_nothing_to_report()
    {
        var definition = WithPhase("""
        {
          "id": "play", "type": "draw_discard",
          "draw_from": [
            { "zone": "deck" },
            { "zone": "discard",
              "requires": { "all": [ "team_has_melded",
                                     { "hand_count_of_rank": "top_discard", "at_least": 2 } ] } }
          ]
        }
        """);

        Assert.Empty(DefinitionValidator.Validate(definition));
    }

    [Fact]
    public void A_misspelled_condition_is_reported()
    {
        var definition = WithPhase("""
        {
          "id": "play", "type": "draw_discard",
          "draw_from": [ { "zone": "discard", "requires": "team_has_meldded" } ]
        }
        """);

        var problems = DefinitionValidator.Validate(definition);

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("play"));   // says where
    }

    [Fact]
    public void A_draw_source_with_no_zone_is_reported()
    {
        var definition = WithPhase("""
        { "id": "play", "type": "draw_discard", "draw_from": [ { "count": 2 } ] }
        """);

        Assert.NotEmpty(DefinitionValidator.Validate(definition));
    }

    [Fact]
    public void An_unreadable_deck_expression_is_reported()
    {
        var definition = Parse("""
        {
          "id": "probe", "name": "Probe", "version": "1.0",
          "deck": { "ranks": "2-A", "copies": "seats + 1" },
          "players": { "min": 2, "max": 4 },
          "zones": [ { "id": "deck", "type": "deck" } ],
          "phases": []
        }
        """);

        var problems = DefinitionValidator.Validate(definition);

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("deck"));
    }

    [Fact]
    public void An_unknown_deck_name_is_reported()
    {
        var definition = Parse("""
        {
          "id": "probe", "name": "Probe", "version": "1.0",
          "deck": "standard-260",
          "players": { "min": 2, "max": 4 },
          "zones": [ { "id": "deck", "type": "deck" } ],
          "phases": []
        }
        """);

        Assert.NotEmpty(DefinitionValidator.Validate(definition));
    }

    /// <summary>
    /// The whole point: a broken definition must not reach the table. It is recorded in
    /// LoadErrors so the reason is visible rather than the game merely being absent.
    /// </summary>
    [Fact]
    public async Task A_game_with_an_unreadable_rule_does_not_load()
    {
        var loader = new GameLoader(new BrokenRuleAssetSource());

        var definition = await loader.LoadAsync("broken");

        Assert.Null(definition);
        Assert.True(loader.LoadErrors.ContainsKey("broken"));
        Assert.Contains("team_has_meldded", loader.LoadErrors["broken"]);
    }

    [Fact]
    public async Task Every_shipped_definition_reads_cleanly()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var games = await loader.LoadAllAsync();

        Assert.Empty(loader.LoadErrors);
        Assert.NotEmpty(games);
    }

    /// <summary>Serves one deliberately broken definition.</summary>
    private sealed class BrokenRuleAssetSource : IGameAssetSource
    {
        public Task<Stream> OpenAsync(string logicalPath)
        {
            const string json = """
            {
              "id": "broken", "name": "Broken", "version": "1.0",
              "deck": "standard-52",
              "players": { "min": 2, "max": 2 },
              "zones": [ { "id": "deck", "type": "deck" } ],
              "phases": [
                { "id": "play", "type": "draw_discard",
                  "draw_from": [ { "zone": "discard", "requires": "team_has_meldded" } ] }
              ]
            }
            """;

            return Task.FromResult<Stream>(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        }
    }

    [Fact]
    public void An_unknown_zone_arrangement_is_reported()
    {
        var definition = Parse("""
        {
          "id": "probe", "name": "Probe", "version": "1.0",
          "deck": "standard-52",
          "players": { "min": 2, "max": 4 },
          "zones": [ { "id": "meld", "type": "spread", "arrangement": "sideways" } ],
          "phases": []
        }
        """);

        var problems = DefinitionValidator.Validate(definition);

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("meld") && p.Contains("sideways"));
    }

    private static GameDefinition WithMeldZone(string zoneExtras) => Parse($$"""
    {
      "id": "probe", "name": "Probe", "version": "1.0",
      "deck": "standard-52",
      "players": { "min": 2, "max": 4 },
      "zones": [ { "id": "meld", "type": "spread", "visibility": "all", {{zoneExtras}} } ],
      "phases": []
    }
    """);

    [Fact]
    public void A_well_formed_label_passes()
    {
        var definition = WithMeldZone("""
        "label": { "text": "{owner}", "placement": "left", "orientation": "vertical" },
        "group_label": { "text": "{rank}s", "placement": "top", "when": "team_has_melded" }
        """);

        Assert.Empty(DefinitionValidator.Validate(definition));
    }

    [Fact]
    public void An_unknown_label_placement_is_reported()
    {
        var definition = WithMeldZone("""
        "label": { "text": "x", "placement": "sideways" }
        """);

        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("sideways"));
    }

    /// <summary>{rank} names a group's rank; a zone as a whole has none.</summary>
    [Fact]
    public void Rank_placeholder_outside_a_group_label_is_reported()
    {
        var definition = WithMeldZone("""
        "label": { "text": "{rank}s" }
        """);

        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("{rank}"));
    }

    [Fact]
    public void A_label_condition_is_validated_like_any_other()
    {
        var definition = WithMeldZone("""
        "group_label": { "text": "{rank}s", "when": "team_has_meldded" }
        """);

        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("team_has_meldded"));
    }
}
