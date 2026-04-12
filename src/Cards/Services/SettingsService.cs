namespace Cards.Services;

public class SettingsService
{
    private const string KeyCardSkin        = "card_skin";
    private const string KeyTableTheme     = "table_theme";
    private const string KeyPlayerName     = "player_name";
    private const string KeyShowMessages   = "show_game_messages";
    private const string KeyAutoReady      = "auto_ready";

    public string CardSkinId
    {
        get => Preferences.Get(KeyCardSkin, "simple");
        set => Preferences.Set(KeyCardSkin, value);
    }

    public string TableThemeId
    {
        get => Preferences.Get(KeyTableTheme, "casino-green");
        set => Preferences.Set(KeyTableTheme, value);
    }

    public string PlayerName
    {
        get => Preferences.Get(KeyPlayerName, "Player");
        set => Preferences.Set(KeyPlayerName, value);
    }

    public bool ShowGameMessages
    {
        get => Preferences.Get(KeyShowMessages, true);
        set => Preferences.Set(KeyShowMessages, value);
    }

    public bool AutoReady
    {
        get => Preferences.Get(KeyAutoReady, false);
        set => Preferences.Set(KeyAutoReady, value);
    }
}
