using Cards.App;
using Cards.Services;
using Microsoft.JSInterop;

namespace Cards.Web.Platform;

/// <summary>
/// The table's sounds in the browser. Each is made once by <see cref="SoundGenerator"/>
/// and handed to <c>js/sounds.js</c>, which plays it through Web Audio. Playing is fire
/// and forget: a sound that cannot be played is not worth a table that stops.
/// </summary>
public sealed class BrowserTableSounds(IJSRuntime js, SettingsService settings) : ITableSounds
{
    private IJSObjectReference? _module;
    private Task? _loading;

    private static readonly Dictionary<TableCue, Func<byte[]>> Made = new()
    {
        [TableCue.Shuffle]  = SoundGenerator.Shuffle,
        [TableCue.Deal]     = SoundGenerator.Deal,
        [TableCue.Play]     = SoundGenerator.Play,
        [TableCue.Draw]     = SoundGenerator.Draw,
        [TableCue.Flip]     = SoundGenerator.Flip,
        [TableCue.Gather]   = SoundGenerator.Gather,
        [TableCue.Score]    = SoundGenerator.Score,
        [TableCue.YourTurn] = SoundGenerator.YourTurn,
        [TableCue.Win]      = SoundGenerator.Win,
        [TableCue.Lose]     = SoundGenerator.Lose,
    };

    /// <summary>Loads the sounds. Safe to call more than once; the page calls it as the table opens.</summary>
    public Task LoadAsync() => _loading ??= LoadOnceAsync();

    private async Task LoadOnceAsync()
    {
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/sounds.js");
            var sounds = Made.ToDictionary(kv => kv.Key.ToString(), kv => Convert.ToBase64String(kv.Value()));
            await _module.InvokeVoidAsync("init", sounds);
        }
        catch (JSException) { _module = null; }
    }

    public void Play(TableCue cue)
    {
        if (_module is null || !settings.SoundOn) return;
        _ = PlayAsync(cue, settings.SoundVolume);
    }

    private async Task PlayAsync(TableCue cue, double volume)
    {
        try { await _module!.InvokeVoidAsync("play", cue.ToString(), volume); }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
    }
}
