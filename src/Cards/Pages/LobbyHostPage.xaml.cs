using Cards.Engine;
using Cards.ViewModels;

namespace Cards.Pages;

[QueryProperty(nameof(GameId),      "GameId")]
[QueryProperty(nameof(PlayerCount), "PlayerCount")]
[QueryProperty(nameof(HouseRules),  "HouseRules")]
public partial class LobbyHostPage : ContentPage
{
    private readonly LobbyHostViewModel _viewModel;

    public LobbyHostPage(LobbyHostViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.StartRequested += OnStartRequested;
        BindingContext = viewModel;
    }

    public string       GameId      { set => _viewModel.GameId      = value; }
    public int          PlayerCount { set => _viewModel.PlayerCount = value; }
    public List<string> HouseRules  { set => _viewModel.EnabledRules = value; }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.HostAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.StartRequested -= OnStartRequested;
    }

    private async void OnStartGameClicked(object? sender, EventArgs e)
        => await _viewModel.StartGameAsync();

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await _viewModel.CancelAsync();
        await Shell.Current.GoToAsync("..");
    }

    private async void OnCopyCodeClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_viewModel.RoomCode))
            await Clipboard.SetTextAsync(_viewModel.RoomCode);
    }

    private async void OnStartRequested(GameState state)
    {
        await Shell.Current.GoToAsync("gametable", new Dictionary<string, object>
        {
            ["GameId"]        = _viewModel.GameId,
            ["PlayerCount"]   = _viewModel.PlayerCount,
            ["HouseRules"]    = _viewModel.EnabledRules,
            ["IsMultiplayer"] = true,
            ["InitialState"]  = state,
        });
    }
}
