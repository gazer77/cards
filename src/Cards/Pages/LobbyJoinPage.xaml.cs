using Cards.Engine;
using Cards.ViewModels;

namespace Cards.Pages;

public partial class LobbyJoinPage : ContentPage
{
    private readonly LobbyJoinViewModel _viewModel;

    public LobbyJoinPage(LobbyJoinViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.GameStarted += OnGameStarted;
        BindingContext = viewModel;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.GameStarted -= OnGameStarted;
    }

    private async void OnJoinClicked(object? sender, EventArgs e)
        => await _viewModel.JoinAsync();

    private async void OnLeaveClicked(object? sender, EventArgs e)
    {
        await _viewModel.LeaveAsync();
        await Shell.Current.GoToAsync("//home");
    }

    private async void OnBackClicked(object? sender, EventArgs e)
        => await Shell.Current.GoToAsync("..");

    private async void OnGameStarted(GameState state)
    {
        await Shell.Current.GoToAsync("gametable", new Dictionary<string, object>
        {
            ["GameId"]        = state.GameId,
            ["PlayerCount"]   = state.Players.Count,
            ["HouseRules"]    = (List<string>)state.EnabledHouseRules.Keys.ToList(),
            ["IsMultiplayer"] = true,
            ["InitialState"]  = state,
        });
    }
}
