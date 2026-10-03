using System.Text.Json;

namespace Cards.Hosting;

/// <summary>
/// Which games a server offers. Every game is on until a manager turns it off; an off game
/// is left off the home page and cannot be opened as a shared table here. Kept as one
/// small file beside the accounts.
/// </summary>
public sealed class GameCatalog
{
    private readonly string? _path;
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _write = new();

    /// <summary>A catalog kept in <paramref name="path"/>; null keeps it in memory only.</summary>
    public GameCatalog(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            foreach (var id in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [])
                _off.Add(id);
        }
        catch (Exception) { /* a damaged file offers every game, which is the safe way to fail */ }
    }

    public IReadOnlyList<string> Off
    {
        get { lock (_write) return [.. _off.Order()]; }
    }

    public bool IsOffered(string gameId)
    {
        lock (_write) return !_off.Contains(gameId);
    }

    public void SetOffered(string gameId, bool offered)
    {
        lock (_write)
        {
            if (offered) _off.Remove(gameId); else _off.Add(gameId);
            if (_path is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_off.Order().ToList()));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
    }
}
