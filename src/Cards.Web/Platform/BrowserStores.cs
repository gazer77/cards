using Cards.Engine;
using Microsoft.JSInterop;

namespace Cards.Web.Platform;

/// <summary>
/// <see cref="ISettingsStore"/> over localStorage.
///
/// Loaded once into memory at startup and written through, because the engine's
/// settings surface is synchronous properties and JS interop is not. See
/// <see cref="LoadAsync"/>, which must run before the first read.
/// </summary>
public sealed class BrowserSettingsStore : ISettingsStore
{
    private const string StorageKey = "cards.settings";

    private readonly IJSRuntime _js;
    private Dictionary<string, string> _values = [];

    public BrowserSettingsStore(IJSRuntime js) => _js = js;

    public async Task LoadAsync()
    {
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(raw))
                _values = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(raw) ?? [];
        }
        catch
        {
            // Private browsing and blocked-storage modes throw on access. Settings are
            // a convenience; losing them must not stop the game from starting.
            _values = [];
        }
    }

    public string Get(string key, string defaultValue)
        => _values.TryGetValue(key, out var v) ? v : defaultValue;

    public bool Get(string key, bool defaultValue)
        => _values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : defaultValue;

    public void Set(string key, string value)
    {
        _values[key] = value;
        Flush();
    }

    public void Set(string key, bool value) => Set(key, value.ToString());

    private void Flush()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_values);
        // Fire and forget: the in-memory copy is authoritative for this session.
        _ = _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }
}

/// <summary>
/// <see cref="ISaveStore"/> over localStorage, one entry per game.
///
/// Saves are a few KB against a ~5MB quota, so this is comfortable. Existence is
/// tracked in memory because <see cref="Exists"/> is synchronous while interop is not.
/// </summary>
public sealed class BrowserSaveStore : ISaveStore
{
    private const string Prefix = "cards.save.";

    private readonly IJSRuntime _js;
    private readonly HashSet<string> _known = [];

    public BrowserSaveStore(IJSRuntime js) => _js = js;

    /// <summary>Populates the known-saves set. Must run before the first Exists call.</summary>
    public async Task LoadAsync(IEnumerable<string> gameIds)
    {
        foreach (var id in gameIds)
        {
            var key = $"save_{id}";
            try
            {
                var raw = await _js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);
                if (!string.IsNullOrEmpty(raw)) _known.Add(key);
            }
            catch { /* storage unavailable — treat as "no saves" */ }
        }
    }

    public bool Exists(string key) => _known.Contains(key);

    public void Delete(string key)
    {
        _known.Remove(key);
        _ = _js.InvokeVoidAsync("localStorage.removeItem", Prefix + key);
    }

    public async Task WriteAsync(string key, string contents)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", Prefix + key, contents);
            _known.Add(key);
        }
        catch { /* quota exceeded or storage blocked — the game continues unsaved */ }
    }

    public async Task<string?> ReadAsync(string key)
    {
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);
            return string.IsNullOrEmpty(raw) ? null : raw;
        }
        catch { return null; }
    }
}
