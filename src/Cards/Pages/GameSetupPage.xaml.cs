using Cards.Models;
using Cards.ViewModels;

namespace Cards.Pages;

[QueryProperty(nameof(Game), "Game")]
public partial class GameSetupPage : ContentPage
{
    private readonly GameSetupViewModel _viewModel;

    public GameSetupPage(GameSetupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.StartGameRequested += OnStartGameRequested;
        BindingContext = viewModel;
    }

    public GameDefinition? Game
    {
        set => _viewModel.Game = value;
    }

    private void OnDecreasePlayersClicked(object? sender, EventArgs e)
        => _viewModel.DecreasePlayers();

    private void OnIncreasePlayersClicked(object? sender, EventArgs e)
        => _viewModel.IncreasePlayers();

    private async void OnStartGameRequested(GameDefinition game, int playerCount, List<string> enabledRules)
    {
        await Shell.Current.GoToAsync("gametable", new Dictionary<string, object>
        {
            ["GameId"]      = game.Id,
            ["PlayerCount"] = playerCount,
            ["HouseRules"]  = enabledRules,
        });
    }

    private async void OnViewRulesClicked(object? sender, EventArgs e)
    {
        if (_viewModel.Game is null) return;
        await Shell.Current.GoToAsync("help", new Dictionary<string, object>
        {
            ["GameId"]   = _viewModel.Game.Id,
            ["GameName"] = _viewModel.Game.Name,
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.StartGameRequested -= OnStartGameRequested;
    }
}
