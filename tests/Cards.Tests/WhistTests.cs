using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Whist: partners across the table, the last card dealt turned up for trumps, and a
/// point a side for every trick over its book of six.
/// </summary>
public sealed class WhistTests
{
    private static (GameState State, IGameLogic Logic) Start(ulong seed)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "whist")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 4, []);
        foreach (var p in state.Players) state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);
        return (state, logic);
    }

    [Theory]
    [InlineData(1UL)] [InlineData(5UL)] [InlineData(9UL)]
    public void The_last_card_dealt_sets_trumps_and_is_said_at_the_table(ulong seed)
    {
        var (state, logic) = Start(seed);
        logic.GetValidActions(state);

        // The last card dealt is still in the hand it was dealt to.
        string to   = state.Metadata["deal_last_player"];
        var    card = state.Zones[$"hand:{to}"].Cards.Single(c => c.Id == state.Metadata["deal_last_card"]);

        Assert.Contains($"Trump: {card.Suit.ToString().ToLowerInvariant()}", state.Metadata["status"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(state.Announcements, a => a.PlayerId == to && a.Text.Contains(GameText.CardName(card)) && a.Text.Contains("trumps"));
    }

    [Fact]
    public void Each_deal_turns_up_its_own_trumps()
    {
        var (state, logic) = Start(3UL);
        var turned = new HashSet<string>();

        for (int step = 0; step < 2000 && !logic.IsGameOver(state) && state.RoundNumber <= 4; step++)
        {
            if (state.Metadata.GetValueOrDefault("deal_last_card") is { } id) turned.Add($"{state.RoundNumber}:{id}");
            logic.Apply(state, logic.GetAutoAction(state));
        }

        // A new card for every deal: the trumps are not the first deal's all game.
        Assert.True(turned.Select(t => t.Split(':')[1]).Distinct().Count() >= 3, string.Join(", ", turned));
    }

    [Fact]
    public void Only_tricks_over_six_score_and_the_game_goes_to_seven()
    {
        var (state, logic) = Start(11UL);
        int rounds = 0;
        for (int step = 0; step < 5000 && !logic.IsGameOver(state); step++)
        {
            int before = state.ScoreHistory.Count;
            logic.Apply(state, logic.GetAutoAction(state));
            if (state.ScoreHistory.Count == before) continue;
            rounds++;

            // Thirteen tricks between two sides: one side scores the odd tricks, the other none.
            var round = state.ScoreHistory[^1].Scores;
            Assert.Equal(2, round.Count);
            Assert.Contains(0, round.Values);
            Assert.InRange(round.Values.Max(), 1, 7);
        }

        Assert.True(logic.IsGameOver(state));
        Assert.Contains(state.Teams, t => state.GetTeamScore(t.Id) >= 7);
        Assert.True(rounds >= 1);
    }
}
