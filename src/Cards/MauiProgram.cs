using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using SkiaSharp.Views.Maui.Controls.Hosting;
using Cards.Engine;
using Cards.Pages;
using Cards.Services;
using Cards.ViewModels;

namespace Cards;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf",   "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf",  "OpenSansSemibold");
            });

        // Engine
        builder.Services.AddSingleton<IGameAssetSource, MauiGameAssetSource>();
        builder.Services.AddSingleton<GameLoader>();

        // Services
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<GameSaveService>();
        builder.Services.AddSingleton(AudioManager.Current);
        builder.Services.AddSingleton<SoundService>();
        builder.Services.AddSingleton<MultiplayerService>();

        // Pages
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<GameSetupPage>();
        builder.Services.AddTransient<GameTablePage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<HelpPage>();
        builder.Services.AddTransient<LobbyHostPage>();
        builder.Services.AddTransient<LobbyJoinPage>();

        // ViewModels
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<GameSetupViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<LobbyHostViewModel>();
        builder.Services.AddTransient<LobbyJoinViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
