using Cards.Engine;

namespace Cards.Services;

/// <summary>
/// <see cref="IGameAssetSource"/> over the MAUI app package, where
/// <c>games/*.json</c> and <c>games/help/*.md</c> are bundled as MauiAssets.
/// </summary>
public sealed class MauiGameAssetSource : IGameAssetSource
{
    public Task<Stream> OpenAsync(string logicalPath)
        => FileSystem.OpenAppPackageFileAsync(logicalPath);
}
