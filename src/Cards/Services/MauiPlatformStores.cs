using Cards.Engine;

namespace Cards.Services;

/// <summary><see cref="ISaveStore"/> over files in the app data directory.</summary>
public sealed class MauiSaveStore : ISaveStore
{
    public bool Exists(string key) => File.Exists(Path(key));

    public void Delete(string key)
    {
        var path = Path(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public Task WriteAsync(string key, string contents)
        => File.WriteAllTextAsync(Path(key), contents);

    public async Task<string?> ReadAsync(string key)
    {
        var path = Path(key);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    private static string Path(string key)
        => System.IO.Path.Combine(FileSystem.AppDataDirectory, $"{key}.json");
}

/// <summary><see cref="ISettingsStore"/> over MAUI Preferences.</summary>
public sealed class MauiSettingsStore : ISettingsStore
{
    public string Get(string key, string defaultValue) => Preferences.Get(key, defaultValue);
    public bool   Get(string key, bool   defaultValue) => Preferences.Get(key, defaultValue);
    public void   Set(string key, string value)        => Preferences.Set(key, value);
    public void   Set(string key, bool   value)        => Preferences.Set(key, value);
}
