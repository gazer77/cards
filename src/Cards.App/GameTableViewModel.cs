using Cards.Engine;
using Cards.Services;

namespace Cards.App;

/// <summary>
/// Drives a game table: owns the state and logic, maps player input to
/// <see cref="GameAction"/>s, and runs the auto-advance loop that plays AI turns.
///
/// Framework-neutral so the MAUI page and the Blazor page share one implementation.
/// GameTablePage currently keeps two hand-maintained copies of the turn loop (the
/// local one and an inline duplicate on the multiplayer path); this exists so a third
/// does not appear in the browser.
/// </summary>
public sealed class GameTableViewModel
{
    private readonly GameLoader      _loader;
    private readonly GameSaveService _saves;

    private GameState?  _state;
    private IGameLogic? _logic;
    private bool        _isAutoAdvancing;

    private int                   _playerCount;
    private IReadOnlyList<string> _enabledRules = [];

    public GameTableViewModel(GameLoader loader, GameSaveService saves)
    {
        _loader = loader;
        _saves  = saves;
    }

    // ── Observable surface ────────────────────────────────────────────────────

    /// <summary>Raised whenever anything a view renders has changed.</summary>
    public event Action? Changed;

    public GameState? State => _state;

    public bool IsBusy => _isAutoAdvancing;

    public bool IsGameOver => _logic is not null && _state is not null && _logic.IsGameOver(_state);

    public string StatusText =>
        _logic is not null && _state is not null ? _logic.GetStatusText(_state) : string.Empty;

    public IReadOnlyList<GameAction> Actions =>
        _logic is not null && _state is not null ? _logic.GetValidActions(_state) : [];

    /// <summary>
    /// Whether the action bar should be shown.
    ///
    /// A lone action is deliberately NOT given a button — it is triggered by tapping
    /// the table (see <see cref="TapTable"/>). "ready" is the exception, because after
    /// a showdown the player needs something explicit to press.
    /// </summary>
    public bool ShowActionButtons
    {
        get
        {
            var actions = Actions;
            return actions.Count > 1 || (actions.Count == 1 && actions[0].Type == "ready");
        }
    }

    public IReadOnlyList<string> SelectableCardIds =>
        _logic is not null && _state is not null ? _logic.GetSelectableCardIds(_state) : [];

    public string? SelectedCardId => _state?.Metadata.GetValueOrDefault("selected_card");

    public IReadOnlyList<string> DropZoneIds
    {
        get
        {
            if (_logic is null || _state is null) return [];

            var selected = SelectedCardId;
            if (selected is null) return [];

            // Multi-select (comma-separated, e.g. assembling a Hand-and-Foot meld) is
            // submitted with an action button, not by dropping onto a zone.
            if (selected.Contains(',')) return [];

            return _logic.GetDropZoneIds(_state, selected);
        }
    }

    public IReadOnlyList<string> GameLog => _state?.GameLog ?? [];

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a game and begins play. When <paramref name="resume"/> is set and a save
    /// exists, the saved position is restored instead of dealing a fresh hand.
    /// </summary>
    public async Task<bool> StartAsync(
        string gameId,
        int playerCount,
        IReadOnlyList<string>? enabledRules = null,
        bool resume = true,
        ulong? seed = null)
    {
        var definition = await _loader.LoadAsync(gameId);
        if (definition is null) return false;

        _playerCount  = playerCount;
        _enabledRules = enabledRules ?? [];

        var state = new GameState { GameId = definition.Id, Definition = definition };
        if (seed is not null)
        {
            state.Rng  = new SeededRandomSource(seed.Value);
            state.Seed = seed.Value;
        }

        var logic = LogicRegistry.Create(definition);

        bool restored = resume
                     && _saves.HasSave(definition.Id)
                     && await _saves.RestoreAsync(state, logic, playerCount, _enabledRules);

        if (!restored)
            logic.Initialize(state, playerCount, _enabledRules);

        _state = state;
        _logic = logic;

        Changed?.Invoke();
        await RunAutoAdvanceLoopAsync();
        return true;
    }

    public Task SaveAsync()
        => _state is null ? Task.CompletedTask
                          : _saves.SaveAsync(_state, _playerCount, _enabledRules);

    public void DeleteSave()
    {
        if (_state is not null) _saves.DeleteSave(_state.GameId);
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public Task TapCard(string cardId)
    {
        if (!CanAcceptInput()) return Task.CompletedTask;
        if (!_logic!.GetSelectableCardIds(_state!).Contains(cardId)) return Task.CompletedTask;

        return ApplyAsync(new GameAction("select_card", CardId: cardId));
    }

    public Task TapTable()
    {
        if (!CanAcceptInput()) return Task.CompletedTask;

        var actions = _logic!.GetValidActions(_state!);
        if (actions.Count != 1) return Task.CompletedTask;

        return ApplyAsync(actions[0]);
    }

    public Task TapZone(string zoneId)
    {
        if (!CanAcceptInput()) return Task.CompletedTask;

        var selected = SelectedCardId;
        if (selected is null || selected.Contains(',')) return Task.CompletedTask;
        if (!_logic!.GetDropZoneIds(_state!, selected).Contains(zoneId)) return Task.CompletedTask;

        return ApplyAsync(new GameAction("play_card", CardId: selected, ZoneId: zoneId));
    }

    public Task DropCard(string cardId, string zoneId)
    {
        if (!CanAcceptInput()) return Task.CompletedTask;
        if (!_logic!.GetDropZoneIds(_state!, cardId).Contains(zoneId)) return Task.CompletedTask;

        return ApplyAsync(new GameAction("play_card", CardId: cardId, ZoneId: zoneId));
    }

    public Task Invoke(GameAction action)
        => CanAcceptInput() ? ApplyAsync(action) : Task.CompletedTask;

    /// <summary>
    /// Moves a card within its own hand. Purely cosmetic — it never advances the game,
    /// so it does not go through Apply and does not trigger the auto-advance loop.
    /// </summary>
    public void ReorderInHand(string cardId, int newIndex)
    {
        if (_state is null) return;

        var zone = _state.Zones.Values.FirstOrDefault(z => z.Cards.Any(c => c.Id == cardId));
        var card = zone?.Cards.FirstOrDefault(c => c.Id == cardId);
        if (zone is null || card is null) return;

        var cards = zone.Cards.ToList();
        cards.Remove(card);
        cards.Insert(Math.Clamp(newIndex, 0, cards.Count), card);
        zone.Reorder(cards);

        Changed?.Invoke();
    }

    // ── Turn loop ─────────────────────────────────────────────────────────────

    private bool CanAcceptInput()
        => _state is not null && _logic is not null && !_isAutoAdvancing && !IsGameOver;

    private async Task ApplyAsync(GameAction action)
    {
        _logic!.Apply(_state!, action);
        Changed?.Invoke();
        await RunAutoAdvanceLoopAsync();
    }

    /// <summary>
    /// Plays out every turn that needs no human input.
    ///
    /// The engine never sleeps — it reports how long a step should take via
    /// GetAutoAdvanceDelay and leaves the waiting to whoever is driving. A null delay
    /// means "a human decides what happens next", which is the loop's exit condition.
    /// </summary>
    private async Task RunAutoAdvanceLoopAsync()
    {
        if (_isAutoAdvancing || _state is null || _logic is null) return;

        _isAutoAdvancing = true;
        try
        {
            while (true)
            {
                var delay = _logic.GetAutoAdvanceDelay(_state);
                if (delay is null) break;

                await Task.Delay(delay.Value);

                var actions = _logic.GetValidActions(_state);
                var cards   = _logic.GetSelectableCardIds(_state);
                if (actions.Count == 0 && cards.Count == 0) break;

                // Pause on a lone "ready" so the player can look at what was revealed
                // before the next round wipes it.
                if (actions.Count == 1 && cards.Count == 0 && actions[0].Type == "ready")
                    break;

                _logic.Apply(_state, _logic.GetAutoAction(_state));
                Changed?.Invoke();

                if (_logic.IsGameOver(_state)) break;
            }
        }
        finally
        {
            _isAutoAdvancing = false;
            Changed?.Invoke();
        }
    }
}
