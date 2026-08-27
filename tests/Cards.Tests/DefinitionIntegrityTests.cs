using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Guards the two paths that deep-clone a GameDefinition through a JSON round-trip:
/// inheritance ("extends") and house-rule application. Both used to throw on any
/// definition holding an unset JsonElement, and both failed silently — euchre-3p and
/// poker-wilds never loaded at all, and nothing surfaced it.
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

        // euchre-3p extends euchre-4p and narrows the seat count to exactly 3.
        var euchre3 = await loader.LoadAsync("euchre-3p");
        Assert.NotNull(euchre3);
        Assert.Equal(3, euchre3!.MinPlayers);
        Assert.Equal(3, euchre3.MaxPlayers);

        // poker-wilds extends texas-holdem and adds deuces wild.
        var wilds = await loader.LoadAsync("poker-wilds");
        Assert.NotNull(wilds);
        Assert.Equal("Wilds", wilds!.Name);
        Assert.Contains(wilds.HouseRules, r => r.Id == "one_eyed_jacks");
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
}
