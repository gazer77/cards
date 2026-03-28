using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cards.Engine;
using Cards.Engine.Multiplayer;
using Cards.Models;
using Cards.Services;

namespace Cards.ViewModels;

public sealed class LobbyJoinViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MultiplayerService _mp;
    private readonly SettingsService    _settings;
    private readonly GameLoader         _loader;

    private string _roomCode   = string.Empty;
    private string _statusText = string.Empty;
    private bool   _isBusy;
    private bool   _isWaiting;   // joined and waiting for host to start
    private string _gameId      = string.Empty;

    public LobbyJoinViewModel(MultiplayerService mp, SettingsService settings, GameLoader loader)
    {
        _mp       = mp;
        _settings = settings;
        _loader   = loader;
    }

    // ── Bindable properties ───────────────────────────────────────────────────

    public string RoomCode
    {
        get => _roomCode;
        set { _roomCode = value.Trim().ToUpperInvariant(); OnPropertyChanged(); OnPropertyChanged(nameof(CanJoin)); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanJoin)); }
    }

    public bool IsWaiting
    {
        get => _isWaiting;
        private set { _isWaiting = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotWaiting)); }
    }

    public bool IsNotWaiting => !_isWaiting;
    public bool CanJoin      => !IsBusy && RoomCode.Length >= 5;
    public bool HasError     => !IsBusy && !IsWaiting && !string.IsNullOrEmpty(_statusText);

    public ObservableCollection<LobbyPlayer> Players { get; } = [];

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when the server starts the game; supplies the ready GameState.</summary>
    public event Action<GameState>? GameStarted;

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task JoinAsync()
    {
        if (!CanJoin) return;
        IsBusy     = true;
        StatusText = "Connecting…";

        var playerId   = Guid.NewGuid().ToString("N")[..8];
        var playerName = _settings.PlayerName;

        _mp.PlayerConnectionChanged += OnPlayerConnectionChanged;
        _mp.StateSynced             += OnStateSynced;

        try
        {
            // We need a placeholder GameState to give to the client.
            // The real state arrives via StateSynced once the host starts.
            // Use a dummy definition for now; it will be replaced by StateSync.
            var definition = new GameDefinition
            {
                Id   = "pending",
                Name = "Multiplayer Game",
            };
            var placeholderState = new GameState { GameId = "pending", Definition = definition };

            var logic = LogicRegistry.Create(RoomCode) ?? new PendingGameLogic();

            bool accepted = await _mp.JoinAsync(
                roomCode:   RoomCode,
                playerId:   playerId,
                playerName: playerName,
                logic:      logic,
                state:      placeholderState);

            if (accepted)
            {
                Players.Add(new LobbyPlayer(playerId, playerName, IsHost: false, IsYou: true));
                IsWaiting  = true;
                StatusText = "Waiting for host to start…";
            }
            else
            {
                StatusText = "Could not join. Check the room code and try again.";
                _mp.PlayerConnectionChanged -= OnPlayerConnectionChanged;
                _mp.StateSynced             -= OnStateSynced;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            _mp.PlayerConnectionChanged -= OnPlayerConnectionChanged;
            _mp.StateSynced             -= OnStateSynced;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LeaveAsync()
    {
        _mp.PlayerConnectionChanged -= OnPlayerConnectionChanged;
        _mp.StateSynced             -= OnStateSynced;
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
        });
    }

    private void OnStateSynced(GameState state)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _mp.StateSynced -= OnStateSynced;

            // Load the real game definition for the received game ID
            var definition = await _loader.LoadAsync(state.GameId);
            if (definition is not null)
            {
                // Patch in the loaded definition (state.Definition was a placeholder)
                var realState = new GameState { GameId = definition.Id, Definition = definition };
                // Copy over all runtime state from the synced state
                foreach (var (k, v) in state.Zones)   realState.Zones[k]    = v;
                foreach (var (k, v) in state.Scores)  realState.Scores[k]   = v;
                foreach (var (k, v) in state.Metadata) realState.Metadata[k] = v;
                realState.CurrentPhaseId     = state.CurrentPhaseId;
                realState.CurrentPlayerIndex = state.CurrentPlayerIndex;
                realState.RoundNumber        = state.RoundNumber;
                foreach (var p in state.Players) realState.Players.Add(p);

                _mp.Client?.AttachState(realState);
                GameStarted?.Invoke(realState);
            }
            else
            {
                StatusText = "Unknown game received from host.";
            }
        });
    }

    public void Dispose()
    {
        _mp.PlayerConnectionChanged -= OnPlayerConnectionChanged;
        _mp.StateSynced             -= OnStateSynced;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Placeholder logic used by the client while waiting for the server to send the real state.
/// </summary>
file sealed class PendingGameLogic : IGameLogic
{
    public void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules) { }
    public IReadOnlyList<GameAction> GetValidActions(GameState state) => [];
    public void Apply(GameState state, GameAction action) { }
    public bool IsGameOver(GameState state) => false;
    public string GetStatusText(GameState state) => "Waiting…";
}
