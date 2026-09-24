using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Guards the two paths that deep-clone a GameDefinition through a JSON round-trip:
/// inheritance ("extends") and house-rule application. Both used to throw on any
/// definition holding an unset JsonElement, and both failed silently — a child definition and
/// a child definition never loaded at all, and nothing surfaced it.
/// </summary>
public sealed class DefinitionIntegrityTests
{
    private static GameLoader NewLoader() => new(new EmbeddedGameAssetSource());

    [Fact]
    public async Task Every_shipped_definition_loads_without_error()
    {
        var loader = NewLoader();
        await loader.LoadAllAsync();

        Assert.True(loader.LoadErrors.IsEmpty,
            "Definitions failed to load: " +
            string.Join(" | ", loader.LoadErrors.Select(kv => $"{kv.Key} -> {kv.Value}")));
    }

    [Fact]
    public async Task Inherited_definitions_resolve_their_parent()
    {
        var loader = NewLoader();

        // Euchre is one game with a shape per seat count now, and seats 3 and 4 both
        // resolve into games rather than into a definition with a hole in it.
        var euchre = await loader.LoadAsync("euchre");
        Assert.NotNull(euchre);
        Assert.Equal(3, euchre!.MinPlayers);
        Assert.Equal(4, euchre.MaxPlayers);

        // Poker is one game whose variants are named shapes inside it.
        var poker = await loader.LoadAsync("poker");
        Assert.NotNull(poker);
        Assert.Contains(poker!.Configurations, c => c.Name == "Deuces Wild");
    }

    /// <summary>
    /// Every house rule on every game must survive being switched on. This is the
    /// case a player hits from the setup screen, and it shares the broken clone path.
    /// </summary>
    [Fact]
    public async Task Every_house_rule_can_be_enabled()
    {
        var loader = NewLoader();

        foreach (var game in await loader.LoadAllAsync())
        {
            foreach (var rule in game.HouseRules)
            {
                var patched = HouseRuleEngine.Apply(game, [rule.Id]);
                Assert.NotNull(patched);
            }

            // And all of them at once, which is what an enthusiastic player does.
            var all = HouseRuleEngine.Apply(game, [.. game.HouseRules.Select(r => r.Id)]);
            Assert.NotNull(all);
        }
    }

    /// <summary>
    /// Every shipped definition's rules resolve at every table size it advertises, not
    /// just at the two the loader checks. A deck expression or a tier list can be sound
    /// at 2 and 6 seats and produce nothing at 4.
    /// </summary>
    [Fact]
    public async Task Every_definition_reads_cleanly_at_every_seat_count_it_offers()
    {
        var games = await NewLoader().LoadAllAsync();
        Assert.NotEmpty(games);

        foreach (var game in games)
            for (int seats = game.MinPlayers; seats <= game.MaxPlayers; seats++)
            {
                var deck = DeckSpec.Parse(game.Deck, seats);
                Assert.True(deck.Size > 0, $"{game.Id}: no cards at {seats} players.");
            }
    }

    /// <summary>
    /// House rules patch definition paths, so a rule can introduce a condition or an
    /// expression the base definition never had. Turning them all on must not produce a
    /// definition the engine cannot read.
    /// </summary>
    [Fact]
    public async Task House_rules_cannot_introduce_an_unreadable_rule()
    {
        var games = await NewLoader().LoadAllAsync();

        foreach (var game in games.Where(g => g.HouseRules.Count > 0))
        {
            var patched = HouseRuleEngine.Apply(game, [.. game.HouseRules.Select(r => r.Id)]);

            Assert.True(DefinitionValidator.Validate(patched) is { Count: 0 },
                $"{game.Id} with all house rules on: " +
                string.Join(" | ", DefinitionValidator.Validate(patched)));
        }
    }
}
