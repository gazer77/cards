using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cards.Engine;
using Cards.Engine.Multiplayer;
using Cards.Models;
using Cards.Services;

namespace Cards.ViewModels;

public sealed class LobbyHostViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MultiplayerService _mp;
    private readonly SettingsService    _settings;
    private readonly GameLoader         _loader;

    private string _roomCode    = string.Empty;
    private string _statusText  = "Starting server…";
    private bool   _canStart;
    private bool   _isBusy;

    public LobbyHostViewModel(MultiplayerService mp, SettingsService settings, GameLoader loader)
    {
        _mp       = mp;
        _settings = settings;
        _loader   = loader;
    }

    // ── Inputs (set by page before HostAsync is called) ───────────────────────

    public string         GameId       { get; set; } = string.Empty;
    public int            PlayerCount  { get; set; } = 2;
    public List<string>   EnabledRules { get; set; } = [];

    // ── Bindable properties ───────────────────────────────────────────────────

    public string RoomCode
    {
        get => _roomCode;
        private set { _roomCode = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public bool CanStart
    {
        get => _canStart;
        private set { _canStart = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public ObservableCollection<LobbyPlayer> Players { get; } = [];

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when the host presses Start and the state is ready.</summary>
    public event Action<GameState>? StartRequested;

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task HostAsync()
    {
        IsBusy = true;
        StatusText = "Starting server…";

        // Add the host as the first player
        var hostName = _settings.PlayerName;
        var hostId   = Guid.NewGuid().ToString("N")[..8];
        Players.Add(new LobbyPlayer(hostId, hostName, IsHost: true, IsYou: true));

        _mp.PlayerConnectionChanged += OnPlayerConnectionChanged;

        try
        {
            var code = await _mp.HostAsync(
                hostPlayerId: hostId,
                logic:        LogicRegistry.Create(GameId)
                              ?? throw new InvalidOperationException($"No logic for '{GameId}'"),
                playerCount:  PlayerCount,
                enabledRules: EnabledRules);

            RoomCode   = code;
            StatusText = "Waiting for players…";
            RefreshCanStart();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StartGameAsync()
    {
        if (!CanStart) return;
        IsBusy = true;
        StatusText = "Starting game…";

        var definition = await _loader.LoadAsync(GameId);
        if (definition is null) { StatusText = "Game definition not found."; IsBusy = false; return; }

        var state = new GameState { GameId = definition.Id, Definition = definition };
        await _mp.StartGameAsync(state);

        StartRequested?.Invoke(state);
    }

    public async Task CancelAsync()
    {
        _mp.PlayerConnectionChanged -= OnPlayerConnectionChanged;
        await _mp.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void OnPlayerConnectionChanged(string playerId, bool connected)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (connected)
            {
                if (!Players.Any(p => p.PlayerId == playerId))
                    Players.Add(new LobbyPlayer(playerId, playerId, IsHost: false, IsYou: false));
            }
            else
            {
                var existing = Players.FirstOrDefault(p => p.PlayerId == playerId);
                if (existing is not null) Players.Remove(existing);
            }
            RefreshCanStart();
            StatusText = Players.Count >= PlayerCount
                ? "All players connected."
                : $"{Players.Count} / {PlayerCount} players";
        });
    }

    private void RefreshCanStart() =>
        CanStart = Players.Count >= 2 && !string.IsNullOrEmpty(RoomCode);

    public void Dispose()
    {
        _mp.PlayerConnectionChanged -= OnPlayerConnectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record LobbyPlayer(string PlayerId, string Name, bool IsHost, bool IsYou)
{
    public string DisplayName => IsYou ? $"{Name} (You)" : Name;
    public string Badge       => IsHost ? "HOST" : string.Empty;
}
