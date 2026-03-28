using Cards.Engine;
using Cards.Models;

namespace Cards.Pages;

[QueryProperty(nameof(GameId),   "GameId")]
[QueryProperty(nameof(GameName), "GameName")]
public partial class HelpPage : ContentPage
{
    private readonly GameLoader _loader;

    public HelpPage(GameLoader loader)
    {
        InitializeComponent();
        _loader = loader;
    }

    public string GameId   { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        GameTitleLabel.Text = GameName;
        await LoadHelpAsync();
    }

    private async Task LoadHelpAsync()
    {
        LoadingIndicator.IsVisible = true;
        ContentLabel.IsVisible     = false;

        try
        {
            var definition = await _loader.LoadAsync(GameId);
            string? helpFile = definition?.Help;
            string content   = string.Empty;

            if (helpFile is not null)
            {
                try
                {
                    using var stream = await FileSystem.OpenAppPackageFileAsync($"games/help/{helpFile}");
                    using var reader = new StreamReader(stream);
                    content = await reader.ReadToEndAsync();
                }
                catch
                {
                    content = "Help file not yet available for this game.";
                }
            }
            else
            {
                content = "No help file defined for this game.";
            }

            ContentLabel.Text = content;
        }
        finally
        {
            LoadingIndicator.IsVisible = false;
            ContentLabel.IsVisible     = true;
        }
    }
}
