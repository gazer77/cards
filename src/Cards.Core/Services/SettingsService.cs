using System.Globalization;
using Cards.Engine;

namespace Cards.Services;

/// <summary>
/// Player-facing settings, over an <see cref="ISettingsStore"/> so the same service
/// backs MAUI Preferences on a phone and localStorage in a browser.
///
/// Properties stay synchronous on purpose — ViewModels read them inline.
/// </summary>
public class SettingsService
{
    private const string KeyCardSkin       = "card_skin";
    private const string KeyTableTheme     = "table_theme";
    private const string KeyPlayerName     = "player_name";
    private const string KeyShowMessages   = "show_game_messages";
    private const string KeyAutoReady      = "auto_ready";
    private const string KeyClientId       = "client_id";
    private const string KeyShowDiagnostics = "show_diagnostics";
    private const string KeyTurnPace       = "turn_pace";
    private const string KeyDefaultSort    = "default_hand_sort";

    private readonly ISettingsStore _store;

    public SettingsService(ISettingsStore store) => _store = store;

    public string CardSkinId
    {
        get => _store.Get(KeyCardSkin, "simple");
        set => _store.Set(KeyCardSkin, value);
    }

    public string TableThemeId
    {
        get => _store.Get(KeyTableTheme, "casino-green");
        set => _store.Set(KeyTableTheme, value);
    }

    public string PlayerName
    {
        get => _store.Get(KeyPlayerName, "Player");
        set => _store.Set(KeyPlayerName, value);
    }

    public bool ShowGameMessages
    {
        get => _store.Get(KeyShowMessages, true);
        set => _store.Set(KeyShowMessages, value);
    }

    public bool AutoReady
    {
        get => _store.Get(KeyAutoReady, false);
        set => _store.Set(KeyAutoReady, value);
    }

    /// <summary>
    /// How much to stretch the pause between automatic turns. 1.0 is the engine's own
    /// timing; higher is slower.
    ///
    /// The engine reports a delay per step and those steps chain, so a run of AI turns
    /// resolves faster than a person can follow. How fast is followable depends on the
    /// game and the player, which is why it is a setting rather than a constant.
    /// </summary>
    public double TurnPace
    {
        get => double.TryParse(_store.Get(KeyTurnPace, ""), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var v) && v > 0
            ? Math.Clamp(v, 0.25, 4.0)
            : 1.8;
        set => _store.Set(KeyTurnPace,
            Math.Clamp(value, 0.25, 4.0).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Hand sort applied to games the player has not set individually, or null to use
    /// whatever each game's definition prefers.
    /// </summary>
    public string? DefaultHandSort
    {
        get
        {
            var value = _store.Get(KeyDefaultSort, "");
            return string.IsNullOrEmpty(value) ? null : value;
        }
        set => _store.Set(KeyDefaultSort, value ?? "");
    }

    /// <summary>
    /// The hand sort the player last chose for a game, or null if they never have.
    ///
    /// Stored per game because the right answer differs by game — grouping by suit
    /// helps in a trick-taking game and gets in the way in a rummy game — and a player
    /// who sets it once should not have to set it again every deal.
    ///
    /// Null means "use the game's own default_sort", which is deliberately different
    /// from the player having explicitly chosen Free.
    /// </summary>
    public string? GetHandSort(string gameId)
    {
        var value = _store.Get($"sort:{gameId}", "");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void SetHandSort(string gameId, string mode)
        => _store.Set($"sort:{gameId}", mode);

    /// <summary>
    /// Shows the frame-timing overlay on the table. Off by default.
    ///
    /// Persisted rather than a launch flag so it survives the reload it takes to
    /// reproduce a rendering problem, and so it can be turned on from a device where
    /// setting a command-line switch is not an option — which is most of them.
    /// </summary>
    public bool ShowDiagnostics
    {
        get => _store.Get(KeyShowDiagnostics, false);
        set => _store.Set(KeyShowDiagnostics, value);
    }

    /// <summary>
    /// Stable per-install identity, created on first read.
    ///
    /// Multiplayer today mints a fresh GUID every session, which is why a dropped
    /// player can never be recognised on return. This is the anchor reconnect will
    /// hang off, so it is worth existing before the protocol work starts.
    /// </summary>
    public string ClientId
    {
        get
        {
            var existing = _store.Get(KeyClientId, "");
            if (!string.IsNullOrEmpty(existing)) return existing;

            var fresh = Guid.NewGuid().ToString("N");
            _store.Set(KeyClientId, fresh);
            return fresh;
        }
    }
}
