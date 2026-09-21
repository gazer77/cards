using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Golf's grids are dealt face-down and two cards are peeked. initial_face sat in the
/// zone definition unread, so "face": "owner" into a zone everyone may see dealt every
/// card face-up and the peek turned two that were already up: the game was played with
/// every grid exposed, and only a debug flag being switched off made that visible.
/// </summary>
public sealed class GolfDealTests
{
    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)]
    public void Each_grid_is_dealt_face_down_and_each_player_turns_two_of_their_choosing(int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);

        // Dealt face-down, every card. The choice of what to turn is the player's.
        foreach (var p in state.Players)
            Assert.Equal(0, state.Zones[$"grid:{p.Id}"].Cards.Count(c => c.IsFaceUp));
        Assert.Equal("peek", state.CurrentPhaseId);

        // Each seat turns the two it picks — here the LAST two, which the deal's old
        // "first two" could never have produced.
        for (int seat = 0; seat < seats; seat++)
        {
            Assert.Equal(seat, state.CurrentPlayerIndex);
            var grid = state.Zones[$"grid:{state.CurrentPlayer.Id}"];
            foreach (var pick in new[] { grid.Cards[5], grid.Cards[4] })
                logic.Apply(state, new GameAction("select_card", CardId: pick.Id, CardUid: pick.Uid));
            Assert.Equal(2, grid.Cards.Count(c => c.IsFaceUp));
            Assert.True(grid.Cards[4].IsFaceUp && grid.Cards[5].IsFaceUp);
        }

        Assert.Equal("play", state.CurrentPhaseId);
        Assert.Equal(0, state.CurrentPlayerIndex);

        // And the seat count is the seat count.
        Assert.Equal(seats, state.Players.Count);
        Assert.Equal(seats, state.Zones.Keys.Count(k => k.StartsWith("grid:")));
    }
}
