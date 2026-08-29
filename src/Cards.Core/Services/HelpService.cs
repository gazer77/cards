using Cards.Engine;

namespace Cards.Services;

/// <summary>
/// Loads a game's rules text.
///
/// A game definition names its help file rather than embedding the text, so the rules
/// travel with the game content and reach MAUI, the browser and the server through the
/// same asset source.
/// </summary>
public sealed class HelpService
{
    private readonly GameLoader       _loader;
    private readonly IGameAssetSource _assets;

    public HelpService(GameLoader loader, IGameAssetSource assets)
    {
        _loader = loader;
        _assets = assets;
    }

    /// <summary>
    /// Returns the markdown rules for a game, or an explanatory line when there are
    /// none. Never throws: missing rules are a gap in the content, not a failure the
    /// player can act on, and should not take the table down.
    /// </summary>
    public async Task<string> LoadAsync(string gameId)
    {
        var definition = await _loader.LoadAsync(gameId);

        if (definition?.Help is not { } helpFile)
            return "No rules have been written for this game yet.";

        try
        {
            using var stream = await _assets.OpenAsync($"games/help/{helpFile}");
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch
        {
            return "Rules for this game are not available.";
        }
    }
}
