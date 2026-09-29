using Cards.Engine;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// Loading a definition in a test. The asset sources tests use (embedded, or the repo's
/// files) complete at once, so this reads the result directly — here, rather than in
/// each test method, where xUnit rightly flags blocking on a task (xUnit1031).
/// </summary>
internal static class TestGames
{
    public static GameDefinition? Load(GameLoader loader, string id)
        => loader.LoadAsync(id).GetAwaiter().GetResult();

    public static List<GameDefinition> LoadAll(GameLoader loader)
        => loader.LoadAllAsync().GetAwaiter().GetResult();
}
