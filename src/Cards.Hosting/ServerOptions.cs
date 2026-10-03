using System.Text.Json;
using Cards.Services;

namespace Cards.Hosting;

/// <summary>
/// The choices an admin makes for the whole server — today, whether players may give
/// their account a username and password. Kept as one small file beside the accounts;
/// in memory only where there is nowhere to keep it.
/// </summary>
public sealed class ServerOptions
{
    private readonly string? _path;
    private readonly object _write = new();
    private ServerOptionsView _current = new();

    public ServerOptions(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try { _current = JsonSerializer.Deserialize<ServerOptionsView>(File.ReadAllText(path)) ?? new(); }
        catch (Exception) { /* a damaged file falls back to the defaults */ }
    }

    public ServerOptionsView Current
    {
        get { lock (_write) return new() { UsernamesOffered = _current.UsernamesOffered }; }
    }

    public void Set(ServerOptionsView options)
    {
        lock (_write)
        {
            _current = new() { UsernamesOffered = options.UsernamesOffered };
            if (_path is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_current));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
    }
}
