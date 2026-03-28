using Plugin.Maui.Audio;

namespace Cards.Services;

/// <summary>
/// Plays short sound effects for card events.
/// Sounds are synthesised in-memory — no audio files required.
/// Call InitializeAsync() once before using; safe to call Play* before init completes.
/// </summary>
public sealed class SoundService
{
    private readonly IAudioManager _audio;
    private IAudioPlayer? _deal, _flip, _win, _lose;
    private bool _initializing;

    public SoundService(IAudioManager audio)
    {
        _audio = audio;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initializing || _deal is not null) return;
        _initializing = true;
        try
        {
            _deal = _audio.CreatePlayer(new MemoryStream(SoundGenerator.Deal()));
            _flip = _audio.CreatePlayer(new MemoryStream(SoundGenerator.Flip()));
            _win  = _audio.CreatePlayer(new MemoryStream(SoundGenerator.Win()));
            _lose = _audio.CreatePlayer(new MemoryStream(SoundGenerator.Lose()));
        }
        catch
        {
            // Audio unavailable on this device / simulator — silence is fine.
        }
        finally
        {
            _initializing = false;
        }
    }

    // ── Play methods ──────────────────────────────────────────────────────────

    public void PlayDeal() => PlayOne(_deal);
    public void PlayFlip() => PlayOne(_flip);
    public void PlayWin()  => PlayOne(_win);
    public void PlayLose() => PlayOne(_lose);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void PlayOne(IAudioPlayer? player)
    {
        if (player is null) return;
        try
        {
            if (player.IsPlaying) player.Stop();
            player.Seek(0);
            player.Play();
        }
        catch { /* ignore playback errors */ }
    }
}
