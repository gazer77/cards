namespace Cards.Engine;

/// <summary>
/// Supplies the packaged game content (definitions under <c>games/</c> and the
/// help markdown under <c>games/help/</c>) to the engine.
///
/// Exists so the engine does not depend on any one host's packaging model:
/// MAUI serves these as app-package assets, the server and tests read them from
/// disk or embedded resources, and the browser reads them from embedded resources.
/// </summary>
public interface IGameAssetSource
{
    /// <summary>
    /// Opens a packaged asset by its logical path, e.g. <c>"games/war.json"</c>.
    /// Throws when the asset does not exist.
    /// </summary>
    Task<Stream> OpenAsync(string logicalPath);
}
