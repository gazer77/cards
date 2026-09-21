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
    public void Each_grid_is_dealt_face_down_with_two_cards_peeked(int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        LogicRegistry.Create(definition).Initialize(state, seats, []);

        foreach (var p in state.Players)
        {
            var grid = state.Zones[$"grid:{p.Id}"];
            Assert.Equal(6, grid.Count);
            Assert.Equal(2, grid.Cards.Count(c => c.IsFaceUp));
        }

        // And the seat count is the seat count.
        Assert.Equal(seats, state.Players.Count);
        Assert.Equal(seats, state.Zones.Keys.Count(k => k.StartsWith("grid:")));
    }
}
