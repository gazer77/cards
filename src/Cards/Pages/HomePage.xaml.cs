using Cards.Models;
using Cards.ViewModels;

namespace Cards.Pages;

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    // ── Game grid ─────────────────────────────────────────────────────────────

    private void OnGameSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GameDefinition game) return;

        // Clear selection so tapping the same game again re-opens the panel
        if (sender is CollectionView cv) cv.SelectedItem = null;

        _viewModel.SelectedGame = game;
    }

    // ── Preview panel ─────────────────────────────────────────────────────────

    private void OnPreviewDismissed(object? sender, TappedEventArgs e)
    {
        _viewModel.SelectedGame = null;
    }

    private async void OnStartGameClicked(object? sender, EventArgs e)
    {
        var game = _viewModel.SelectedGame;
        if (game is null) return;
        _viewModel.SelectedGame = null;

        await Shell.Current.GoToAsync("gametable", new Dictionary<string, object>
        {
            ["GameId"]      = game.Id,
            ["PlayerCount"] = game.MinPlayers,
            ["HouseRules"]  = new List<string>(),
        });
    }

    private async void OnNewGameClicked(object? sender, EventArgs e)
    {
        var game = _viewModel.SelectedGame;
        if (game is null) return;

        await _viewModel.DeleteSelectedSaveAsync();
        _viewModel.SelectedGame = null;

        await Shell.Current.GoToAsync("gametable", new Dictionary<string, object>
        {
            ["GameId"]      = game.Id,
            ["PlayerCount"] = game.MinPlayers,
            ["HouseRules"]  = new List<string>(),
        });
    }

    private async void OnHostMultiplayerClicked(object? sender, EventArgs e)
    {
        var game = _viewModel.SelectedGame;
        if (game is null) return;
        _viewModel.SelectedGame = null;

        await Shell.Current.GoToAsync("gamesetup", new Dictionary<string, object>
        {
            ["Game"] = game
        });
    }

    private async void OnViewRulesClicked(object? sender, EventArgs e)
    {
        var game = _viewModel.SelectedGame;
        if (game is null) return;
        _viewModel.SelectedGame = null;

        await Shell.Current.GoToAsync("help", new Dictionary<string, object>
        {
            ["GameId"]   = game.Id,
            ["GameName"] = game.Name,
        });
    }

    // ── Bottom bar ────────────────────────────────────────────────────────────

    private async void OnJoinGameClicked(object? sender, EventArgs e)
        => await Shell.Current.GoToAsync("lobbyjoin");

    private async void OnSettingsClicked(object? sender, EventArgs e)
        => await Shell.Current.GoToAsync("settings");
}
