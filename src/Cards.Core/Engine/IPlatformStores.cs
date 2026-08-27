namespace Cards.Engine;

/// <summary>
/// Where saved games live. MAUI writes files under AppDataDirectory; the browser
/// uses localStorage; tests use memory.
/// </summary>
public interface ISaveStore
{
    bool Exists(string key);
    void Delete(string key);
    Task WriteAsync(string key, string contents);

    /// <summary>Returns null when the key is absent.</summary>
    Task<string?> ReadAsync(string key);
}

/// <summary>
/// Key/value settings storage. MAUI uses Preferences; the browser uses localStorage.
///
/// Deliberately synchronous: <see cref="Cards.Services.SettingsService"/> exposes plain
/// properties that ViewModels read inline, and making those async would ripple through
/// every screen. Browser implementations should load once at startup and write through.
/// </summary>
public interface ISettingsStore
{
    string Get(string key, string defaultValue);
    bool   Get(string key, bool   defaultValue);
    void   Set(string key, string value);
    void   Set(string key, bool   value);
}

/// <summary>In-memory settings, used by tests and as a safe fallback.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = [];

    public string Get(string key, string defaultValue)
        => _values.TryGetValue(key, out var v) ? v : defaultValue;

    public bool Get(string key, bool defaultValue)
        => _values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : defaultValue;

    public void Set(string key, string value) => _values[key] = value;
    public void Set(string key, bool value)   => _values[key] = value.ToString();
}
