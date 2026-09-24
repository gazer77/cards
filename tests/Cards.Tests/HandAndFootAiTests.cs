using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The computer players at Hand and Foot lay melds. They used to draw and discard and
/// never put a card down, because the only actions they were shown were discards —
/// so a round against them ended only when the stock ran out.
/// </summary>
public sealed class HandAndFootAiTests
{
    private static (GameState State, IGameLogic Logic) Table(int seats, ulong seed = 11)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        foreach (var p in state.Players)
            state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);
        return (state, logic);
    }

    private static int RealMelds(GameState state)
    {
        var wilds = MeldRules.WildRanks(state.Definition);
        var three = new HashSet<Rank> { Rank.Three };
        return state.Zones.Values.Where(z => z.Id.StartsWith("meld"))
            .Sum(z => Enumerable.Range(0, z.Groups.Count)
                .Count(i => MeldRules.IsMeldGroup(z.GroupCards(i), wilds, three)));
    }

    [Fact]
    public void A_computer_player_opens_when_it_holds_enough()
    {
        var (state, logic) = Table(2);
        state.RoundNumber = 1;   // an opening of 50
        logic.Apply(state, logic.GetAutoAction(state));   // draws
        Assert.Equal("discard", state.Metadata["dd_turn_state"]);

        var me   = state.CurrentPlayer.Id;
        var hand = state.Zones[$"hand:{me}"];
        hand.Clear();
        int uid = 8800;
        foreach (var (rank, suit) in new[] { (Rank.Ace, Suit.Clubs), (Rank.Ace, Suit.Hearts), (Rank.Ace, Suit.Spades),
                                             (Rank.Six, Suit.Clubs), (Rank.Nine, Suit.Hearts) })
            hand.Add(new Card(suit, rank, isFaceUp: true) { Uid = uid++ });
        foreach (var z in state.Zones.Values.Where(z => z.Id.StartsWith("meld"))) z.Clear();

        for (int i = 0; i < 10 && state.CurrentPlayer.Id == me; i++)
            logic.Apply(state, logic.GetAutoAction(state));

        var team = state.GetPlayerTeam(me);
        var melds = state.Zones[team is null ? $"meld:{me}" : $"meld:{team.Id}"];
        Assert.Equal(3, melds.Cards.Count(c => c.Rank == Rank.Ace));
        Assert.Single(hand.Cards);   // one of the odd cards kept, the other discarded
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void A_table_of_computer_players_melds_and_finishes(int seats)
    {
        var (state, logic) = Table(seats);
        int mostMelds = 0;

        for (int i = 0; i < 20000 && !logic.IsGameOver(state); i++)
        {
            logic.Apply(state, logic.GetAutoAction(state));
            mostMelds = Math.Max(mostMelds, RealMelds(state));
        }

        Assert.True(logic.IsGameOver(state));
        Assert.True(mostMelds >= 3, $"only {mostMelds} melds were ever on the table");
    }
}
