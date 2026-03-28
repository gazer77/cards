using Cards.Pages;

namespace Cards;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute("gamesetup", typeof(GameSetupPage));
        Routing.RegisterRoute("gametable", typeof(GameTablePage));
        Routing.RegisterRoute("settings",  typeof(SettingsPage));
        Routing.RegisterRoute("help",      typeof(HelpPage));
        Routing.RegisterRoute("lobbyhost", typeof(LobbyHostPage));
        Routing.RegisterRoute("lobbyjoin", typeof(LobbyJoinPage));
    }
}
