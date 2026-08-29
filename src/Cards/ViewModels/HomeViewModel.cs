using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cards.Engine;
using Cards.Models;
using Cards.Services;

namespace Cards.ViewModels;

public class HomeViewModel : INotifyPropertyChanged
{
    private readonly GameLoader      _loader;
    private readonly GameSaveService _saves;

    private List<GameDefinition>              _allGames  = [];
    private ObservableCollection<GameDefinition> _games  = [];
    private bool             _isLoading;
    private bool             _hasLoaded;
    private string           _filterText    = string.Empty;
    private GameDefinition?  _selectedGame;

    public HomeViewModel(GameLoader loader, GameSaveService saves)
    {
        _loader = loader;
        _saves  = saves;
    }

    // ── Games list (filtered) ─────────────────────────────────────────────────

    public ObservableCollection<GameDefinition> Games
    {
        get => _games;
        private set { _games = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotLoading)); }
    }

    public bool IsNotLoading => !_isLoading;

    // ── Filter ────────────────────────────────────────────────────────────────

    public string FilterText
    {
        get => _filterText;
        set { _filterText = value; OnPropertyChanged(); ApplyFilter(); }
    }

    // ── Selected game / preview ───────────────────────────────────────────────

    public GameDefinition? SelectedGame
    {
        get => _selectedGame;
        set
        {
            _selectedGame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPreviewVisible));
            OnPropertyChanged(nameof(SelectedGameHasHelp));
            OnPropertyChanged(nameof(SelectedGameHasSave));
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(SelectedGameName));
            OnPropertyChanged(nameof(SelectedGamePlayerRange));
        }
    }

    public bool IsPreviewVisible      => _selectedGame is not null;
    public bool SelectedGameHasHelp   => !string.IsNullOrEmpty(_selectedGame?.Help);
    public bool SelectedGameHasSave   => _selectedGame is not null && _saves.HasSave(_selectedGame.Id);
    public string StartButtonText     => SelectedGameHasSave ? "Resume" : "Start Game";
    public string SelectedGameName        => _selectedGame?.Name           ?? string.Empty;
    public string SelectedGamePlayerRange => _selectedGame?.PlayerRangeText ?? string.Empty;

    /// <summary>
    /// Deletes every save for the selected game so the next start is fresh.
    ///
    /// Saves now live in their own slots, so a game can have more than one. MAUI has no
    /// resume list yet and plays one table at a time, so "start over" means all of them
    /// — see the web client for the per-save version.
    /// </summary>
    public async Task DeleteSelectedSaveAsync()
    {
        if (_selectedGame is not null)
            await _saves.DeleteAllForAsync(_selectedGame.Id);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        if (_isLoading || _hasLoaded) return;
        IsLoading = true;
        try
        {
            _allGames = await _loader.LoadAllAsync();

            // SelectedGameHasSave is read while binding, so the save index has to be in
            // memory before the list is shown or every game claims to have no save.
            await _saves.EnsureLoadedAsync();

            _hasLoaded = true;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var source = string.IsNullOrWhiteSpace(_filterText)
            ? _allGames
            : _allGames.Where(g => MatchesFilter(g, _filterText));
        Games = new ObservableCollection<GameDefinition>(source);
    }

    private static bool MatchesFilter(GameDefinition game, string filter)
    {
        if (game.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        // Normalize ID hyphens to spaces so "poker-wilds" matches "poker"
        if (game.Id.Replace('-', ' ').Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        return game.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
