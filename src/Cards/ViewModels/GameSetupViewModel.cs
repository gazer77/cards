using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Cards.Models;

namespace Cards.ViewModels;

public class GameSetupViewModel : INotifyPropertyChanged
{
    private GameDefinition? _game;
    private int _playerCount;
    private int _minPlayers;
    private int _maxPlayers;

    public GameSetupViewModel()
    {
        StartGameCommand = new Command(OnStartGame, CanStartGame);
    }

    public GameDefinition? Game
    {
        get => _game;
        set
        {
            _game = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameName));
            OnPropertyChanged(nameof(PlayerRangeText));
            OnPropertyChanged(nameof(HasHelp));

            if (value is not null)
            {
                _minPlayers = value.MinPlayers;
                _maxPlayers = value.MaxPlayers;
                PlayerCount = value.MinPlayers;
                HouseRules = new ObservableCollection<HouseRule>(value.HouseRules);
            }

            ((Command)StartGameCommand).ChangeCanExecute();
        }
    }

    public string GameName      => _game?.Name           ?? string.Empty;
    public string PlayerRangeText => _game?.PlayerRangeText ?? string.Empty;
    public bool   HasHelp       => !string.IsNullOrEmpty(_game?.Help);

    public int PlayerCount
    {
        get => _playerCount;
        set
        {
            _playerCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDecreasePlayers));
            OnPropertyChanged(nameof(CanIncreasePlayers));
            ((Command)StartGameCommand).ChangeCanExecute();
        }
    }

    public bool CanDecreasePlayers => _playerCount > _minPlayers;
    public bool CanIncreasePlayers => _playerCount < _maxPlayers;
    public bool HasHouseRules => HouseRules.Count > 0;

    public ObservableCollection<HouseRule> HouseRules { get; private set; } = [];

    public ICommand StartGameCommand { get; }
    public event Action<GameDefinition, int, List<string>>? StartGameRequested;

    public void DecreasePlayers() { if (CanDecreasePlayers) PlayerCount--; }
    public void IncreasePlayers() { if (CanIncreasePlayers) PlayerCount++; }

    private bool CanStartGame() => _game is not null && _playerCount >= _minPlayers;

    private void OnStartGame()
    {
        if (_game is null) return;
        var enabledRules = HouseRules
            .Where(r => r.IsEnabled)
            .Select(r => r.Id)
            .ToList();
        StartGameRequested?.Invoke(_game, _playerCount, enabledRules);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
