using System.Reflection;

namespace Cards.Engine;

/// <summary>
/// Reads game content from resources embedded in Cards.Core.
///
/// This is the default for every host — phone, server, browser and tests all load
/// byte-identical definitions from the same assembly, so a client and a server can
/// never disagree about what a game's rules are.
/// </summary>
public sealed class EmbeddedGameAssetSource : IGameAssetSource
{
    private static readonly Assembly Owner = typeof(EmbeddedGameAssetSource).Assembly;

    public Task<Stream> OpenAsync(string logicalPath)
    {
        var stream = Owner.GetManifestResourceStream(logicalPath)
            ?? throw new FileNotFoundException(
                $"Embedded game asset '{logicalPath}' not found in {Owner.GetName().Name}. " +
                $"Available: {string.Join(", ", Owner.GetManifestResourceNames())}",
                logicalPath);

        return Task.FromResult(stream);
    }

    /// <summary>Logical paths of every embedded game definition, e.g. "games/war.json".</summary>
    public static IEnumerable<string> GameDefinitionPaths()
        => Owner.GetManifestResourceNames()
                .Where(n => n.StartsWith("games/", StringComparison.Ordinal)
                         && n.EndsWith(".json", StringComparison.Ordinal));
}
