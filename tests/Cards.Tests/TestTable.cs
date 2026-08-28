using Cards.Engine;

namespace Cards.Tests;

/// <summary>Builds a real dealt table for tests that need something to draw.</summary>
public static class TestTable
{
    public static GameState Build(string gameId = "war", int players = 2)
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        var definition = loader.LoadAsync(gameId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Could not load '{gameId}'.");

        var state = new GameState { GameId = definition.Id, Definition = definition };
        LogicRegistry.Create(definition).Initialize(state, players, []);
        return state;
    }
}
