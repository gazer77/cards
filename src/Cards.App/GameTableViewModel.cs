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

    /// <summary>
    /// The rules driving this table. Exposed so a test harness can play a game the way
    /// the client does rather than reimplementing the turn loop beside it.
    /// </summary>
    public IGameLogic? Logic => _logic;

    public bool IsBusy => _isAutoAdvancing;

    public bool IsGameOver => _logic is not null && _state is not null && _logic.IsGameOver(_state);

    public string StatusText =>
        _logic is not null && _state is not null ? _logic.GetStatusText(_state) : string.Empty;

    /// <summary>
    /// The actions a player can press — every legal action that has a label.
    ///
    /// An action with no label is not a button; it is the engine's way of saying "the
    /// table itself is the affordance", as a trick-taking phase does while it waits for
    /// a card. One of those reached the action bar and was drawn as a button reading
    /// "tap", which did nothing a player could see and meant nothing they could read.
    /// </summary>
    public IReadOnlyList<GameAction> Actions =>
        _logic is not null && _state is not null
            ? _logic.GetValidActions(_state).Where(a => a.Label is not null).ToList()
            : [];

    /// <summary>
    /// Whether the action bar should be shown.
    ///
    /// Every action a player can press gets a button. A lone action used to get none —
    /// it was reachable only by tapping bare felt, an affordance nothing on screen
    /// mentioned. The result was a table saying "Draw a card" with no way to draw one
    /// that a player could find.
    /// </summary>
    public bool ShowActionButtons => Actions.Count > 0;

    public IReadOnlyList<string> SelectableCardIds =>
        _logic is not null && _state is not null ? _logic.GetSelectableCardIds(_state) : [];

    public string? SelectedCardId => _state?.Metadata.GetValueOrDefault("selected_card");

    /// <summary>
    /// Which meld each selected card would land in, card uid → meld index, when the
    /// selection splits into more than one. Computed with the same rules the engine
    /// applies on Lay Meld, so the colours on screen are a preview and not a guess.
    /// Empty for a single meld, or when the selection is not yet a legal lay.
    /// </summary>
    public IReadOnlyDictionary<int, int> SelectedMeldGroups
    {
        get
        {
            var selected = SelectedCardId;
            if (_state is null || selected is null || !selected.Contains(','))
                return EmptyGroups;

            // Selection tokens are uids — each names one physical card, which is what
            // lets two identical fours be two entries here.
            var uids = selected.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => int.TryParse(t, out int u) ? u : -1)
                .ToHashSet();
            var hand = _state.FindZone($"hand:{_state.CurrentPlayer.Id}");
            var cards = hand?.Cards.Where(c => uids.Contains(c.Uid)).ToList();
            if (cards is null || cards.Count == 0) return EmptyGroups;

            var wilds = MeldRules.WildRanks(_state.Definition);
            var melds = MeldRules.PartitionIntoMelds(cards, wilds);
            if (melds is null || melds.Count < 2) return EmptyGroups;

            var groups = new Dictionary<int, int>();
            for (int i = 0; i < melds.Count; i++)
                foreach (var card in melds[i])
                    groups[card.Uid] = i;
            return groups;
        }
    }

    private static readonly IReadOnlyDictionary<int, int> EmptyGroups =
        new Dictionary<int, int>();

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
    /// A secondary line for the game-over screen — whatever the win condition wanted to
    /// add, or how long the game ran when it had nothing to say.
    /// </summary>
    public string GameOverDetail
    {
        get
        {
            if (_state is null) return string.Empty;

            var sub = _state.Metadata.GetValueOrDefault("sub", "");
            if (!string.IsNullOrEmpty(sub)) return sub;

            return _state.RoundNumber > 1 ? $"{_state.RoundNumber} rounds played" : string.Empty;
        }
    }

    /// <summary>
    /// Final standings, best first, with the winner flagged.
    ///
    /// Scoring differs by game — most count up, but a game like Hand and Foot can leave
    /// you deeply negative — so "best" follows whoever the engine declared the winner
    /// rather than assuming high scores win.
    /// </summary>
    public IReadOnlyList<(string Name, int Score, bool IsWinner)> FinalScores
    {
        get
        {
            if (_state is null || _state.Scores.Count == 0) return [];

            var winnerId = _state.Metadata.GetValueOrDefault("last_winner", "");

            var rows = _state.Players
                .Where(p => _state.Scores.ContainsKey(p.Id))
                .Select(p => (p.Name, Score: _state.GetScore(p.Id), IsWinner: p.Id == winnerId))
                .ToList();

            // Order by score, in whichever direction puts the declared winner on top.
            bool winnerHasLowest = rows.Any(r => r.IsWinner)
                                && rows.Where(r => r.IsWinner).Min(r => r.Score)
                                   <= rows.Min(r => r.Score);

            return winnerHasLowest
                ? rows.OrderBy(r => r.Score).ToList()
                : rows.OrderByDescending(r => r.Score).ToList();
        }
    }

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

        // Anything a player said on their own account — naming trump — is theirs, and
        // goes to their seat whoever is on turn by the time it is drawn. The engine
        // has already written these to the log; this is only the speaking.
        if (_state.Announcements.Count > 0)
        {
            foreach (var (speaker, said) in _state.Announcements.ToList())
                MessagePosted?.Invoke(speaker, said);
            _state.Announcements.Clear();
        }

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
        ulong? seed = null,
        string? initialSort = null,
        string? configuration = null)
    {
        var definition = await _loader.LoadAsync(gameId);
        if (definition is null) return false;

        _playerCount  = playerCount;
        _enabledRules = enabledRules ?? [];

        // The shape this table plays, set before anything is initialised: it decides what
        // the definition even says. A resumed game overwrites it from the save.
        var state = new GameState
        {
            GameId = definition.Id,
            Definition = definition,
            ConfigurationName = configuration,
        };
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

        // Sorted BEFORE the deal is choreographed, so cards fly straight to the slots
        // they will occupy. Sorting after — as the browser client used to — dealt the
        // hand in shuffle order and then snapped it into place the instant the
        // animation finished, which read as the table rearranging itself.
        if ((initialSort ?? definition.Ui?.DefaultSort) is { } sort)
        {
            ActiveSortMode = sort;
            SortHandInternal(sort);
        }

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

    /// <summary>The mode meaning "leave my hand alone, I arrange it myself".</summary>
    public const string CustomSortMode = HandSortOptions.FreeMode;

    /// <summary>
    /// Sorts this game offers, most useful first.
    ///
    /// A definition may name its own modes (a trick-taking game wants suits grouped;
    /// a rummy game usually does not), in which case only those are offered, in the
    /// order given. Its default_sort is promoted to the top, and manual arrangement
    /// is always available last.
    /// </summary>
    public IReadOnlyList<(string Mode, string Label)> SortModes
        => HandSortOptions.For(_state?.Definition);

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
    private bool SortHandInternal(string mode) => HandSortOptions.Apply(_state, mode);

    // ── Input ─────────────────────────────────────────────────────────────────

    public Task TapCard(string cardId, int uid = -1)
    {
        if (!CanAcceptInput()) return Task.CompletedTask;
        if (!_logic!.GetSelectableCardIds(_state!).Contains(cardId)) return Task.CompletedTask;

        // The uid says which physical copy was tapped; selectability is by description,
        // since the engine offers "a 4h may be selected", not one particular 4h.
        return ApplyAsync(new GameAction("select_card", CardId: cardId,
            CardUid: uid >= 0 ? uid : null));
    }

    /// <summary>
    /// A tap on bare felt. It carries the game forward only where the table itself is
    /// the affordance — "Tap to flip!" — which is what an action with no label is: one
    /// the action bar never draws a button for.
    ///
    /// A labelled action has a button, so the felt must not be a second, invisible copy
    /// of it. It was: a zone drawn turned records no card rectangles, so a click on an
    /// opponent's card is a click on nothing, and with a single action available that
    /// "nothing" discarded the card the player had just drawn.
    /// </summary>
    public Task TapTable()
    {
        if (!CanAcceptInput()) return Task.CompletedTask;

        var actions = _logic!.GetValidActions(_state!);
        if (actions.Count != 1 || actions[0].Label is not null) return Task.CompletedTask;

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

    /// <summary>
    /// Does the obvious thing for a zone — what a double-tap on it means.
    ///
    /// The binding is read from the game, never hardcoded: a zone offering
    /// <c>draw_from_{zone}</c> draws, and a zone the selection can be played to takes
    /// it. So a definition that adds a third pile gets the same gesture for free, and
    /// one that forbids drawing from the discard simply never offers the action.
    /// </summary>
    public Task ActivateZone(string zoneId)
    {
        if (!CanAcceptInput()) return Task.CompletedTask;

        var drawAction = _logic!.GetValidActions(_state!)
            .FirstOrDefault(a => a.Type == $"draw_from_{zoneId}");
        if (drawAction is not null) return ApplyAsync(drawAction);

        // Otherwise it is a destination: play the selection onto it.
        return TapZone(zoneId);
    }


    /// <summary>
    /// Does the obvious thing with a card — what a double tap on it means.
    ///
    /// The phase names it: turn this card now, discard the one just drawn. Where a
    /// phase names nothing, the double tap is just a tap, which is what it was before
    /// any of them named anything.
    /// </summary>
    public Task ActivateCard(string cardId, int uid = -1)
    {
        if (!CanAcceptInput()) return Task.CompletedTask;

        int? physical = uid >= 0 ? uid : null;
        var action = _logic!.GetDefaultCardAction(_state!, cardId, physical);

        // Still only for cards the phase is offering: a double tap is a shortcut past
        // the asking, never past the rules.
        if (action is not null && _logic.GetSelectableCardIds(_state!).Contains(cardId))
            return ApplyAsync(action);

        // Nothing to do with the card itself, so the gesture belongs to what it is
        // lying on. A deck and a discard pile are read through their top card, and a
        // double tap there has always meant "draw from here" — it kept meaning that
        // right up until cards learned to answer the gesture first.
        if (ZoneHolding(cardId) is { } zoneId) return ActivateZone(zoneId);

        return TapCard(cardId, uid);
    }

    /// <summary>The zone a card is lying in, or null if the table does not hold it.</summary>
    private string? ZoneHolding(string cardId)
        => _state?.Zones.Values.FirstOrDefault(z => z.Cards.Any(c => c.Id == cardId))?.Id;
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
