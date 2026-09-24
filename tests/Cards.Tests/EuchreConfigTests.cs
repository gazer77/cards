using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Euchre as one game with a shape per seat count, where it used to be two definitions
/// and two entries in the picker.
///
/// The differences are real rules — partners at four and none at three, a maker scoring
/// one point for three tricks or two for five against one point for any of three, four or
/// five, and going alone skipping a partner that a three-player table does not have — so
/// these check the rules and not merely that the file loads.
/// </summary>
public sealed class EuchreConfigTests
{
    private static GameState Table(int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("euchre").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(3) };
        LogicRegistry.Create(definition).Initialize(state, seats, []);
        return state;
    }

    [Fact]
    public void One_entry_in_the_picker_covers_three_players_and_four()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var games  = loader.LoadAllAsync().GetAwaiter().GetResult();

        Assert.DoesNotContain(games, g => g.Id is "euchre-3p" or "euchre-4p");
        var euchre = Assert.Single(games.Where(g => g.Id == "euchre"));
        Assert.Equal(3, euchre.MinPlayers);
        Assert.Equal(4, euchre.MaxPlayers);
    }

    [Fact]
    public void Four_players_are_two_partnerships_and_three_players_are_three_rivals()
    {
        var four = Table(4);
        Assert.Equal(2, four.Teams.Count);
        Assert.All(four.Teams, t => Assert.Equal(2, t.PlayerIds.Count));

        Assert.Empty(Table(3).Teams);
    }

    [Fact]
    public void Each_shape_scores_by_its_own_rules()
    {
        var four  = Table(4).Definition.Scoring!;
        var three = Table(3).Definition.Scoring!;

        Assert.Equal("team",   GetString(four,  "count_by"));
        Assert.Equal("player", GetString(three, "count_by"));

        // Four: one point for three or four tricks, two for all five, four for a loner.
        Assert.True(four.Extra!["makers_win"].TryGetProperty("tricks_3_or_4", out _));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, four.Extra["loner_win"].ValueKind);

        // Three: one point for any of three, four or five, and no loner at all.
        Assert.True(three.Extra!["makers_win"].TryGetProperty("tricks_3_to_5", out _));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, three.Extra["loner_win"].ValueKind);

        static string? GetString(Cards.Models.ScoringDefinition s, string key)
            => s.Extra!.TryGetValue(key, out var v) ? v.GetString() : null;
    }

    [Fact]
    public void Going_alone_skips_a_partner_only_where_there_is_one()
    {
        // The rule lives inside the phase list, which a merge cannot reach into — the
        // three-player shape patches it by path, the way a definition extending another
        // patches its parent.
        var four  = Table(4).Definition.Phases.Single(p => p.Id == "play");
        var three = Table(3).Definition.Phases.Single(p => p.Id == "play");

        Assert.True(four.Extra!["loner_skips_partner"].GetBoolean());
        Assert.False(three.Extra!["loner_skips_partner"].GetBoolean());
    }

    [Fact]
    public void Both_shapes_deal_a_playable_table()
    {
        foreach (int seats in new[] { 3, 4 })
        {
            var state = Table(seats);
            Assert.Equal(seats, state.Players.Count);
            Assert.All(state.Players, p => Assert.Equal(5, state.Zones[$"hand:{p.Id}"].Count));
            Assert.Equal(24 - seats * 5, state.Zones["kitty"].Count + state.Zones["deck"].Count);
            Assert.True(state.Zones["kitty"].TopCard!.IsFaceUp);
        }
    }

    [Fact]
    public void House_rules_still_patch_the_shape_that_was_resolved()
    {
        // Resolution happens first, so a house rule patches the game being played.
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("euchre").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(3) };
        LogicRegistry.Create(definition).Initialize(state, 3, ["no_stick_dealer"]);

        Assert.Empty(state.Teams);                                     // the shape held
        var call = state.Definition.Phases.Single(p => p.Id == "call_trump");
        Assert.False(call.Extra!["stick_the_dealer"].GetBoolean());    // and the rule applied
    }
}
