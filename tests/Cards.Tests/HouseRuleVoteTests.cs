using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// How a shared table settles house rules is the game's to say: a simple majority by
/// default, a tie counting as no; everyone, where the definition asks for unanimity; and
/// the host overruling only where the definition allows it.
/// </summary>
public sealed class HouseRuleVoteTests
{
    private static readonly HouseRuleVoteDefinition Majority  = new();
    private static readonly HouseRuleVoteDefinition Unanimous = new() { DecideBy = "unanimous" };

    [Theory]
    [InlineData(2, 3, true)]
    [InlineData(1, 2, false)]   // a tie is no
    [InlineData(2, 4, false)]
    [InlineData(3, 4, true)]
    [InlineData(0, 1, false)]
    public void A_majority_is_more_than_half(int yes, int voters, bool carries)
        => Assert.Equal(carries, HouseRuleTally.Carries(Majority, yes, voters));

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(2, 3, false)]
    public void Unanimous_means_everyone(int yes, int voters, bool carries)
        => Assert.Equal(carries, HouseRuleTally.Carries(Unanimous, yes, voters));

    [Fact]
    public void A_ruling_wins_over_the_count()
    {
        Assert.True(HouseRuleTally.Carries(Majority, 0, 3, forced: true));
        Assert.False(HouseRuleTally.Carries(Majority, 3, 3, forced: false));
    }

    [Fact]
    public void A_game_that_says_nothing_gets_the_defaults()
    {
        var hearts = new GameLoader(new EmbeddedGameAssetSource()).LoadAsync("hearts").GetAwaiter().GetResult()!;
        Assert.Equal("majority", hearts.HouseRuleVote.DecideBy);
        Assert.False(hearts.HouseRuleVote.HostOverride);
    }

    [Fact]
    public void The_terms_are_read_from_the_definition_and_checked()
    {
        var def = System.Text.Json.JsonSerializer.Deserialize<GameDefinition>(
            """{ "id": "x", "name": "X", "house_rule_vote": { "decide_by": "unanimous", "host_override": true } }""")!;
        Assert.Equal("unanimous", def.HouseRuleVote.DecideBy);
        Assert.True(def.HouseRuleVote.HostOverride);
        Assert.Empty(DefinitionAudit.UnreadProperties(
            """{ "id": "x", "name": "X", "house_rule_vote": { "decide_by": "unanimous", "host_override": true } }"""));

        def.HouseRuleVote.DecideBy = "whoever shouts";
        Assert.Contains(DefinitionValidator.Validate(def), p => p.Contains("decide_by"));
    }
}
