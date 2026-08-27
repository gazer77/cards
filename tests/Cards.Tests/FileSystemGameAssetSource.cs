using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Serves game content straight out of the repo's <c>games/</c> directory, so tests
/// run against the same JSON the app ships rather than a copy that can drift.
/// </summary>
public sealed class FileSystemGameAssetSource : IGameAssetSource
{
    private readonly string _root;

    /// <param name="root">Repo root — the directory containing <c>games/</c>.</param>
    public FileSystemGameAssetSource(string root) => _root = root;

    public Task<Stream> OpenAsync(string logicalPath)
    {
        // logicalPath is "games/war.json" — already repo-relative.
        string full = Path.Combine(_root, logicalPath.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult<Stream>(File.OpenRead(full));
    }

    /// <summary>
    /// Walks up from the test binary until it finds the directory holding <c>games/</c>.
    /// Avoids hard-coding a relative depth that breaks when the build output path changes.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "games")) &&
                File.Exists(Path.Combine(dir.FullName, "games", "war.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate the repo root (a directory containing games/war.json) " +
            $"walking up from {AppContext.BaseDirectory}.");
    }

    public static FileSystemGameAssetSource FromRepo() => new(FindRepoRoot());
}
