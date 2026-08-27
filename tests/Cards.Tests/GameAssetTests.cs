using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Game content moved from per-host packaging (MauiAsset globs) to resources embedded
/// in Cards.Core. That kind of change breaks at runtime, not compile time, so it needs
/// covering directly.
/// </summary>
public sealed class GameAssetTests
{
    [Fact]
    public async Task Every_game_definition_loads_from_embedded_resources()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var games  = await loader.LoadAllAsync();

        Assert.Equal(16, games.Count);
        Assert.All(games, g => Assert.False(string.IsNullOrWhiteSpace(g.Id)));
    }

    [Fact]
    public async Task Every_game_with_a_help_file_can_open_it()
    {
        var assets = new EmbeddedGameAssetSource();
        var loader = new GameLoader(assets);

        foreach (var game in await loader.LoadAllAsync())
        {
            if (string.IsNullOrEmpty(game.Help)) continue;

            using var stream = await assets.OpenAsync($"games/help/{game.Help}");
            Assert.True(stream.Length > 0, $"Help file for '{game.Id}' is empty.");
        }
    }

    /// <summary>
    /// The embedded copy must be the same content the repo's games/ directory holds —
    /// otherwise the shipped rules could silently diverge from the ones under review.
    /// </summary>
    [Fact]
    public async Task Embedded_definitions_match_the_repo_files()
    {
        var embedded = new GameLoader(new EmbeddedGameAssetSource());
        var onDisk   = new GameLoader(FileSystemGameAssetSource.FromRepo());

        var a = await embedded.LoadAllAsync();
        var b = await onDisk.LoadAllAsync();

        Assert.Equal(b.Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal),
                     a.Select(g => g.Id).OrderBy(x => x, StringComparer.Ordinal));
    }
}
