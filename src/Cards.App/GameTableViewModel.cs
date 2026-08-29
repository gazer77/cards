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

    private ITableAnimator _animator = NullTableAnimator.Instance;

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

    /// <summary>
    /// Plays card movement between turns. Defaults to doing nothing, so a host with no
    /// table attached still works; a client assigns its own once its canvas exists.
    /// Animation timing is not decoration here — it is most of what keeps the turn loop
    /// at a pace a person can follow.
    /// </summary>
    public ITableAnimator Animator
    {
        get => _animator;
        set => _animator = value ?? NullTableAnimator.Instance;
    }

    /// <summary>
    /// Stretches the pause the engine asks for between automatic turns. 1.0 is the
    /// engine's own timing; 2.0 is half speed.
    ///
    /// The engine reports a delay per step and those steps chain, so a run of AI turns
    /// resolves faster than a person can follow what happened. This is the single knob
    /// for that, kept here rather than in a client so every client paces alike.
    ///
    /// TODO: surface this as a user setting (a speed slider on the settings screen),
    /// persisted through ISettingsStore alongside the other preferences. Hard-coded
    /// defaults are a starting point, not the answer — how fast is "followable"
    /// depends on the game and the player.
    /// </summary>
    public double TurnPace { get; set; } = 1.0;

    /// <summary>
    /// Floor for an automatic turn's pause, so a step the engine considers instant is
    /// still visible when it changes the table.
    ///
    /// Applied only to steps that already ask for a non-zero delay. A zero delay is the
    /// engine saying "this is internal bookkeeping, not a move" — several phases chain
    /// those deliberately, and holding each one would turn scoring into a slideshow.
    /// </summary>
    public TimeSpan MinimumTurnPause { get; set; } = TimeSpan.Zero;

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

    /// <summary>
    /// Raised when something new happens, with the seat it concerns.
    ///
    /// The engine has no notion of events — it exposes a status line describing the
    /// position. Watching that line for changes is what turns it into a history, and
    /// it is also the only producer the game log has ever had: nothing in the engine
    /// writes to <see cref="GameState.GameLog"/>, so a client that does not do this
    /// has an empty log rather than a missing view.
    /// </summary>
    public event Action<string, string>? MessagePosted;

    /// <summary>Last status text turned into a log entry, so a steady state is not repeated.</summary>
    private string _lastLoggedStatus = string.Empty;

    /// <summary>
    /// Records the status line if it has changed since the last check.
    ///
    /// Attributed to <paramref name="actingPlayerId"/> — whoever was on turn when the
    /// action was applied, captured before it ran. Reading the current player afterwards
    /// gets it backwards on exactly the moves that matter: a handler sets its message
    /// and then passes the turn, so "the AI asked for aces and got none" ends up
    /// labelled as the human's, and the table appears to be asking the wrong player.
    /// </summary>
    private void CaptureStatusChange(string? actingPlayerId = null)
    {
        if (_logic is null || _state is null) return;

        string text = _logic.GetStatusText(_state);
        if (string.IsNullOrEmpty(text) || text == _lastLoggedStatus) return;

        _lastLoggedStatus = text;
        _state.GameLog.Add(text);

        if (_state.Players.Count == 0) return;

        MessagePosted?.Invoke(actingPlayerId ?? _state.CurrentPlayer.Id, text);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a game and begins play.
    ///
    /// With <paramref name="resumeSlotId"/> the named save is restored, and its own seat
    /// count and house rules take precedence over the arguments — a saved position
    /// carries its own table. Without one, a fresh game is dealt.
    /// </summary>
    public async Task<bool> StartAsync(
        string gameId,
        int playerCount,
        IReadOnlyList<string>? enabledRules = null,
        bool resume = true,
        string? resumeSlotId = null,
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

        await _saves.EnsureLoadedAsync();

        bool restored = false;
        if (resume && resumeSlotId is not null)
        {
            restored = await _saves.RestoreAsync(state, logic, resumeSlotId);

            // A resumed game keeps the seat count and house rules it was saved with,
            // whatever the caller asked for — those are properties of the position, not
            // of the request, and disagreeing about them is what strands cards in the
            // hands of players that no longer exist.
            if (restored && _saves.FindSlot(resumeSlotId) is { } slot)
            {
                _playerCount  = slot.PlayerCount;
                _enabledRules = slot.EnabledRules;
                SlotId        = resumeSlotId;
            }
        }

        if (!restored)
        {
            logic.Initialize(state, playerCount, _enabledRules);
            SlotId = null;   // a fresh game gets its own slot on first save
        }

        _state = state;
        _logic = logic;

        // A restored game keeps the log it was saved with; only the opening line of a
        // fresh game is new. Either way the baseline is set here so resuming does not
        // replay the whole history as bubbles.
        _lastLoggedStatus = string.Empty;
        CaptureStatusChange();

        Changed?.Invoke();

        // A fresh deal is choreographed — full deck, shuffle, cards dealt one at a
        // time. A resumed game is already mid-hand, so it simply appears.
        if (!restored) await _animator.PlayDealAsync(state);

        await RunAutoAdvanceLoopAsync();
        return true;
    }

    /// <summary>
    /// The save this game occupies, or null until it has been saved once.
    ///
    /// Held so repeated saves update one entry rather than filling the resume list with
    /// a row per turn.
    /// </summary>
    public string? SlotId { get; private set; }

    public async Task SaveAsync()
    {
        if (_state is null) return;
        SlotId = await _saves.SaveAsync(_state, _playerCount, _enabledRules, SlotId);
    }

    /// <summary>Discards this game's save, if it has one.</summary>
    public async Task DeleteSaveAsync()
    {
        if (SlotId is null) return;

        await _saves.DeleteAsync(SlotId);
        SlotId = null;
    }

    // ── Hand sorting ──────────────────────────────────────────────────────────

    /// <summary>Every sort a game may offer, in the order they are shown by default.</summary>
    private static readonly (string Mode, string Label)[] AllSortModes =
    [
        ("suit_value",    "By Suit & Value"),
        ("suit_stable",   "By Suit"),
        ("rank_ace_high", "By Value (Ace High)"),
        ("rank",          "By Value (Ace Low)"),
    ];

    /// <summary>The mode meaning "leave my hand alone, I arrange it myself".</summary>
    public const string CustomSortMode = "none";

    /// <summary>
    /// Sorts this game offers, most useful first.
    ///
    /// A definition may name its own modes (a trick-taking game wants suits grouped;
    /// a rummy game usually does not), in which case only those are offered, in the
    /// order given. Its default_sort is promoted to the top, and manual arrangement
    /// is always available last.
    /// </summary>
    public IReadOnlyList<(string Mode, string Label)> SortModes
    {
        get
        {
            var ui = _state?.Definition.Ui;

            var modes = ui?.SortModes is { Count: > 0 } configured
                ? configured
                    .Select(m => AllSortModes.FirstOrDefault(o => o.Mode == m))
                    .Where(o => o.Mode is not null)
                    .ToList()
                : AllSortModes.ToList();

            if (!string.IsNullOrEmpty(ui?.DefaultSort))
            {
                int i = modes.FindIndex(o => o.Mode == ui.DefaultSort);
                if (i > 0)
                {
                    var promoted = modes[i];
                    modes.RemoveAt(i);
                    modes.Insert(0, promoted);
                }
            }

            modes.Add((CustomSortMode, "Free"));
            return modes;
        }
    }

    /// <summary>
    /// The sort kept in effect as cards arrive, or null to leave the hand alone.
    ///
    /// Sorting once on request is not what a player means by choosing a sort: a card
    /// won from an opponent lands on the end of the hand, so an ordered hand comes
    /// apart over a game unless new cards are placed where they belong. Setting this
    /// keeps the order rather than restoring it on demand.
    ///
    /// <see cref="CustomSortMode"/> is a real choice, not the absence of one — it means
    /// "I arrange this myself", so it must not be re-sorted.
    /// </summary>
    public string? ActiveSortMode { get; set; }

    /// <summary>Re-applies <see cref="ActiveSortMode"/>, if one is in effect.</summary>
    private void MaintainSort()
    {
        if (ActiveSortMode is null || ActiveSortMode == CustomSortMode) return;
        SortHandInternal(ActiveSortMode);
    }

    /// <summary>
    /// Whether this game has a hand the player can see and therefore sort. Games whose
    /// hands are all face-down piles offer nothing to arrange.
    /// </summary>
    public bool CanSortHand =>
        _state is not null &&
        _state.Zones.Values.Any(z => z.Type == "hand" && z.Visibility is "owner" or "all");

    /// <summary>
    /// Reorders the player's own hands. Purely cosmetic — it never advances the game.
    /// Only zones the player can actually see are touched; sorting a hidden hand would
    /// do nothing visible and reorder an opponent's cards.
    /// </summary>
    public void SortHand(string mode)
    {
        // Choosing a sort also keeps it: the player is setting how their hand is
        // arranged, not asking for it to be tidied once.
        ActiveSortMode = mode;

        if (SortHandInternal(mode)) Changed?.Invoke();
    }

    /// <summary>Sorts without raising Changed. Returns whether anything was sorted.</summary>
    private bool SortHandInternal(string mode)
    {
        if (_state is null || string.IsNullOrEmpty(mode) || mode == CustomSortMode) return false;

        foreach (var zone in _state.Zones.Values)
            if (zone.Type == "hand" && zone.Visibility is "owner" or "all")
                HandSorter.Sort(zone, mode);

        return true;
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

    /// <summary>
    /// Applies <see cref="TurnPace"/> and <see cref="MinimumTurnPause"/> to one step's
    /// delay. A zero delay is passed through untouched — see
    /// <see cref="MinimumTurnPause"/> for why.
    /// </summary>
    private TimeSpan Pace(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return delay;

        var scaled = TurnPace > 0 ? delay * TurnPace : delay;
        return scaled < MinimumTurnPause ? MinimumTurnPause : scaled;
    }

    private bool CanAcceptInput()
        => _state is not null && _logic is not null && !_isAutoAdvancing && !IsGameOver;

    private async Task ApplyAsync(GameAction action)
    {
        await ApplyAnimatedAsync(action);
        await RunAutoAdvanceLoopAsync();
    }

    /// <summary>
    /// Applies one action and lets the resulting card movement finish before returning.
    ///
    /// Origins must be captured before Apply and destinations resolved after the view
    /// has the new state, so this is deliberately one indivisible step rather than
    /// something each caller assembles — the turn loop got that ordering wrong once
    /// already by simply not animating at all.
    /// </summary>
    private async Task ApplyAnimatedAsync(GameAction action)
    {
        // Whose move this is has to be read before it is applied: applying it is what
        // passes the turn on.
        string? actingPlayerId = _state!.Players.Count > 0 ? _state.CurrentPlayer.Id : null;

        _animator.CaptureBeforeMove(_state);
        _logic!.Apply(_state, action);

        // Sort before the view is told, so a newly won card is animated into the slot
        // it will actually occupy rather than flying to the end of the hand and then
        // jumping into place.
        MaintainSort();

        CaptureStatusChange(actingPlayerId);
        Changed?.Invoke();
        await _animator.PlayMoveAsync(_state!);
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

                await Task.Delay(Pace(delay.Value));

                var actions = _logic.GetValidActions(_state);
                var cards   = _logic.GetSelectableCardIds(_state);
                if (actions.Count == 0 && cards.Count == 0) break;

                // Pause on a lone "ready" so the player can look at what was revealed
                // before the next round wipes it.
                if (actions.Count == 1 && cards.Count == 0 && actions[0].Type == "ready")
                    break;

                await ApplyAnimatedAsync(_logic.GetAutoAction(_state));

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
