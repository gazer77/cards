using Cards.Engine;
using Cards.Engine.Multiplayer;
using Cards.Models;
using Cards.Rendering;
using Cards.Services;

namespace Cards.Pages;

[QueryProperty(nameof(GameId),        "GameId")]
[QueryProperty(nameof(PlayerCount),   "PlayerCount")]
[QueryProperty(nameof(HouseRules),    "HouseRules")]
[QueryProperty(nameof(IsMultiplayer), "IsMultiplayer")]
[QueryProperty(nameof(InitialState),  "InitialState")]
public partial class GameTablePage : ContentPage
{
    private readonly GameLoader         _loader;
    private readonly SettingsService    _settings;
    private readonly GameSaveService    _saves;
    private readonly SoundService       _sounds;
    private readonly MultiplayerService _mp;
    private GameState?  _state;
    private IGameLogic? _logic;

    /// <summary>
    /// Card movement, shared with the browser client.
    ///
    /// This page used to work out fly-in geometry itself, against a MAUI control, which
    /// is precisely why the browser had no animations until it was extracted. Keeping
    /// one copy means a fix to how cards move is a fix on both clients.
    /// </summary>
    private readonly RendererTableAnimator _animator;

    public GameTablePage(GameLoader loader, SettingsService settings, GameSaveService saves, SoundService sounds, MultiplayerService mp)
    {
        InitializeComponent();
        _animator = new RendererTableAnimator(TableCanvas.Renderer);
        _loader   = loader;
        _settings = settings;
        _saves    = saves;
        _sounds   = sounds;
        _mp       = mp;

        TableCanvas.CardTapped          += OnCardTapped;
        TableCanvas.CanvasTapped        += OnCanvasTapped;
        TableCanvas.ZoneTapped          += OnZoneTapped;
        TableCanvas.CardDropped         += OnCardDropped;
        TableCanvas.CardReorderedInHand += OnCardReorderedInHand;
        TableCanvas.SizeChanged         += OnCanvasSizeChanged;
    }

    public string        GameId        { get; set; } = string.Empty;
    public int           PlayerCount   { get; set; } = 2;
    public List<string>? HouseRules    { get; set; }
    public bool          IsMultiplayer { get; set; }
    public GameState?    InitialState  { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_state is null)
            await InitializeGameAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (IsMultiplayer)
        {
            UnsubscribeMultiplayerEvents();
            if (_logic is null || _state is null || _logic.IsGameOver(_state))
                await _mp.DisposeAsync();
        }
        else if (_state is not null && _logic is not null && !_logic.IsGameOver(_state))
        {
            var rules = (IReadOnlyList<string>)(HouseRules ?? []);
            _ = _saves.SaveAsync(_state, PlayerCount, rules, _saveSlotId);
        }
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private async Task InitializeGameAsync()
    {
        GameOverOverlay.IsVisible  = false;
        GameLogOverlay.IsVisible   = false;
        GearMenuPanel.IsVisible    = false;
        GearMenuBackdrop.IsVisible = false;
        StatusMessagesPanel.Children.Clear();
        _lastShownStatus = string.Empty;

        if (IsMultiplayer && InitialState is not null)
        {
            await InitializeMultiplayerAsync(InitialState);
            return;
        }

        var definition = await _loader.LoadAsync(GameId);
        if (definition is null) { await Shell.Current.GoToAsync(".."); return; }

        Title = definition.Name;
        TableCanvas.SetSkin(SkinFactory.Create(_settings.CardSkinId));

        // Configure HUD buttons from game definition
        var ui = definition.Ui;
        GearSortButton.IsVisible = ui?.AllowSort  ?? true;
        GearLogButton.IsVisible  = ui?.ShowGameLog ?? true;

        var state  = new GameState { GameId = definition.Id, Definition = definition };
        var rules  = (IReadOnlyList<string>)(HouseRules ?? []);
        _logic     = LogicRegistry.Create(definition);

        bool restored = false;
        await _saves.EnsureLoadedAsync();

        // MAUI has one table at a time, so it resumes the most recent save for this
        // game at this size. The resume list is a web-client feature for now.
        _saveSlotId = _saves.SavesFor(definition.Id)
            .FirstOrDefault(s => s.PlayerCount == PlayerCount)?.Id;

        if (_logic is not null && _saveSlotId is not null)
            restored = await _saves.RestoreAsync(state, _logic, _saveSlotId);

        if (!restored)
        {
            if (_logic is not null)
                _logic.Initialize(state, PlayerCount, rules);
            else
                BuildFallbackState(state, definition);
        }

        _state = state;
        MaybeSortHands();

        // A fresh deal is choreographed — full deck, shuffle, cards dealt one at a time.
        // A resumed game is already mid-hand, so it simply appears.
        if (!restored) await _animator.PlayDealAsync(state);

        TableCanvas.GameState = _state;
        RefreshStatus();
        RefreshActionButtons();
        RefreshInteractionState();

        _ = _sounds.InitializeAsync();

        // If the restored (or freshly initialized) state immediately wants to
        // auto-advance (e.g. AI's turn, or player has no cards after restore),
        // kick off the loop without waiting for user input.
        if (_logic is not null && _logic.GetAutoAdvanceDelay(_state!) is not null)
            _ = RunAutoAdvanceLoopAsync();
    }

    private async Task InitializeMultiplayerAsync(GameState state)
    {
        var definition = await _loader.LoadAsync(state.GameId);
        if (definition is null) { await Shell.Current.GoToAsync(".."); return; }

        Title = definition.Name;
        TableCanvas.SetSkin(SkinFactory.Create(_settings.CardSkinId));

        var ui = definition.Ui;
        GearSortButton.IsVisible = ui?.AllowSort  ?? true;
        GearLogButton.IsVisible  = ui?.ShowGameLog ?? true;

        _logic = LogicRegistry.Create(state.Definition);
        _state = state;

        _mp.ActionApplied += OnMultiplayerActionApplied;
        _mp.StateSynced   += OnMultiplayerStateSynced;
        _mp.Disconnected  += OnMultiplayerDisconnected;

        MaybeSortHands();
        TableCanvas.GameState = _state;
        RefreshStatus();
        RefreshActionButtons();
        RefreshInteractionState();

        _ = _sounds.InitializeAsync();
    }

    private void UnsubscribeMultiplayerEvents()
    {
        _mp.ActionApplied -= OnMultiplayerActionApplied;
        _mp.StateSynced   -= OnMultiplayerStateSynced;
        _mp.Disconnected  -= OnMultiplayerDisconnected;
    }


    private void BuildFallbackState(GameState state, GameDefinition definition)
    {
        SetupEngine.Instance.Setup(state, PlayerCount, []);

        if (state.Zones.TryGetValue("deck", out var deck))
        {
            var cards = DeckBuilder.Build(definition, PlayerCount);
            DeckBuilder.Shuffle(cards);
            deck.AddRange(cards);
        }

        state.CurrentPhaseId = definition.Phases.FirstOrDefault()?.Id ?? string.Empty;
    }

    // ── Auto-sort ──────────────────────────────────────────────────────────────
    /// <summary>Applies the sort a game asks for by default, if it asks for one.</summary>
    private void MaybeSortHands()
        => HandSortOptions.Apply(_state, _state?.Definition.Ui?.AutoSortHand);

    private void SortPlayerHand(string mode)
    {
        if (HandSortOptions.Apply(_state, mode))
            TableCanvas.GameState = _state;
    }

    // ── Touch / interaction handlers ──────────────────────────────────────────

    private bool _isAutoAdvancing;

    /// <summary>
    /// The save this table is writing to, so repeated autosaves update one entry rather
    /// than adding a row per departure. Null until the game has been saved once.
    /// </summary>
    private string? _saveSlotId;

    private void OnCardTapped(string cardId)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || GameLogOverlay.IsVisible || _isAutoAdvancing) return;

        var selectables = _logic.GetSelectableCardIds(_state);
        if (!selectables.Contains(cardId)) return;

        _ = ApplyAndRefreshAsync(new GameAction("select_card", CardId: cardId));
    }

    private void OnCanvasTapped()
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || GameLogOverlay.IsVisible || _isAutoAdvancing) return;

        var actions = _logic.GetValidActions(_state);
        if (actions.Count != 1) return;
        _ = ApplyAndRefreshAsync(actions[0]);
    }

    private void OnZoneTapped(string zoneId)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || GameLogOverlay.IsVisible || _isAutoAdvancing) return;

        string? selectedCard = _state.Metadata.GetValueOrDefault("selected_card");
        if (selectedCard is null || selectedCard.Contains(',')) return;

        var dropZones = _logic.GetDropZoneIds(_state, selectedCard);
        if (!dropZones.Contains(zoneId)) return;

        _ = ApplyAndRefreshAsync(new GameAction("play_card", CardId: selectedCard, ZoneId: zoneId));
    }

    private void OnCardDropped(string cardId, string zoneId)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || GameLogOverlay.IsVisible || _isAutoAdvancing) return;

        var dropZones = _logic.GetDropZoneIds(_state, cardId);
        if (!dropZones.Contains(zoneId)) return;

        _ = ApplyAndRefreshAsync(new GameAction("play_card", CardId: cardId, ZoneId: zoneId));
    }

    private void OnCardReorderedInHand(string cardId, int newIndex)
    {
        if (_state is null) return;

        var zone = _state.Zones.Values.FirstOrDefault(z => z.Cards.Any(c => c.Id == cardId));
        if (zone is null) return;

        var cards = zone.Cards.ToList();
        var card  = cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null) return;

        cards.Remove(card);
        cards.Insert(Math.Clamp(newIndex, 0, cards.Count), card);
        zone.Reorder(cards);

        TableCanvas.GameState = _state;
    }

    private void OnActionClicked(GameAction action)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || GameLogOverlay.IsVisible || _isAutoAdvancing) return;
        _ = ApplyAndRefreshAsync(action);
    }

    private async Task ApplyAndRefreshAsync(GameAction action)
    {
        if (IsMultiplayer)
        {
            await ApplyAndRefreshMultiplayerAsync(action);
            return;
        }

        _animator.CaptureBeforeMove(_state!);
        ApplyWithSound(action);
        MaybeSortHands();
        TableCanvas.GameState = _state;
        await _animator.PlayMoveAsync(_state!);

        RefreshStatus();
        RefreshActionButtons();
        RefreshInteractionState();

        if (_logic!.IsGameOver(_state!)) { _sounds.PlayWin(); ShowGameOver(); return; }

        await RunAutoAdvanceLoopAsync();
    }

    /// <summary>
    /// Runs the auto-advance loop until the logic no longer requests it.
    /// Safe to call with no pending auto-advance (returns immediately).
    /// </summary>
    private async Task RunAutoAdvanceLoopAsync()
    {
        if (_isAutoAdvancing) return;

        _isAutoAdvancing = true;
        try
        {
            while (true)
            {
                var delay = _logic!.GetAutoAdvanceDelay(_state!);
                if (delay is null) break;

                await Task.Delay(delay.Value);

                var next = _logic.GetValidActions(_state!);
                if (next.Count == 0) break;

                // After the delay, if the only pending action is "ready", show the
                // ready-up dialog instead of auto-applying (gives players time to see
                // revealed hands before the next round starts).
                if (next.Count == 1 && next[0].Type == "ready")
                {
                    ShowReadyUp();
                    break;
                }

                _animator.CaptureBeforeMove(_state!);
                ApplyWithSound(_logic.GetAutoAction(_state!));
                MaybeSortHands();
                TableCanvas.GameState = _state;
                await _animator.PlayMoveAsync(_state!);

                RefreshStatus();
                RefreshActionButtons();
                RefreshInteractionState();

                if (_logic.IsGameOver(_state!)) { _sounds.PlayWin(); ShowGameOver(); return; }
            }
        }
        finally
        {
            _isAutoAdvancing = false;
        }
    }

    private void ShowReadyUp()
    {
        if (_state is null) return;

        if (_settings.AutoReady)
        {
            _ = ApplyAndRefreshAsync(new GameAction("ready"));
            return;
        }

        // Surface the "ready" action as a button in the action bar
        RefreshActionButtons();
    }

    private async Task ApplyAndRefreshMultiplayerAsync(GameAction action)
    {
        if (_mp.IsHost)
        {
            // Host: server applies the action synchronously before SendActionAsync returns,
            // so _state is already updated when we reach the refresh calls below.
            var faceDownBefore = _state!.Zones.Values
                .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
            var handCountBefore = _state.Zones
                .Where(kv => kv.Key.StartsWith("hand:")).Sum(kv => kv.Value.Count);

            await _mp.SendActionAsync(action);

            PlaySoundForStateChange(faceDownBefore, handCountBefore);
            MaybeSortHands();
            TableCanvas.GameState = _state;
            RefreshStatus();
            RefreshActionButtons();
            RefreshInteractionState();

            if (_logic!.IsGameOver(_state!)) { _sounds.PlayWin(); ShowGameOver(); return; }

            _isAutoAdvancing = true;
            try
            {
                while (true)
                {
                    var delay = _logic.GetAutoAdvanceDelay(_state!);
                    if (delay is null) break;

                    await Task.Delay(delay.Value);

                    var next = _logic.GetValidActions(_state!);
                    if (next.Count == 0) break;

                    if (next.Count == 1 && next[0].Type == "ready")
                    {
                        ShowReadyUp();
                        break;
                    }

                    faceDownBefore  = _state.Zones.Values
                        .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
                    handCountBefore = _state.Zones
                        .Where(kv => kv.Key.StartsWith("hand:")).Sum(kv => kv.Value.Count);

                    await _mp.SendActionAsync(_logic.GetAutoAction(_state!));

                    PlaySoundForStateChange(faceDownBefore, handCountBefore);
                    MaybeSortHands();
                    TableCanvas.GameState = _state;
                    RefreshStatus();
                    RefreshActionButtons();
                    RefreshInteractionState();

                    if (_logic.IsGameOver(_state!)) { _sounds.PlayWin(); ShowGameOver(); return; }
                }
            }
            finally
            {
                _isAutoAdvancing = false;
            }
        }
        else
        {
            // Client: send to server; state update + UI refresh happens via OnMultiplayerActionApplied.
            _ = _mp.SendActionAsync(action);
        }
    }

    private void PlaySoundForStateChange(HashSet<string> faceDownBefore, int handCountBefore)
    {
        var faceDownAfter = _state!.Zones.Values
            .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
        bool cardFlipped = faceDownBefore.Except(faceDownAfter).Any();

        int handCountAfter = _state.Zones
            .Where(kv => kv.Key.StartsWith("hand:")).Sum(kv => kv.Value.Count);
        bool cardDealt = handCountAfter > handCountBefore;

        if (cardFlipped)
            _sounds.PlayFlip();
        else if (cardDealt)
            _sounds.PlayDeal();
    }

    /// <summary>
    /// Applies an action and plays whatever sound the change calls for.
    ///
    /// Card movement is no longer worked out here. Deciding where each card flew from
    /// and where it lands is the same problem on every client, and it lived in this page
    /// written against a MAUI control — which is why the browser had no animations at
    /// all until that logic was moved to <see cref="RendererTableAnimator"/>. Sound
    /// stays, being the one part that genuinely differs.
    /// </summary>
    private void ApplyWithSound(GameAction action)
    {
        var faceDownBefore = _state!.Zones.Values
            .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
        var handCountBefore = _state.Zones
            .Where(kv => kv.Key.StartsWith("hand:")).Sum(kv => kv.Value.Count);

        _logic!.Apply(_state, action);

        PlaySoundForStateChange(faceDownBefore, handCountBefore);
    }

    private void RefreshInteractionState()
    {
        if (_logic is null || _state is null)
        {
            TableCanvas.SelectableCardIds = [];
            TableCanvas.SelectedCardId    = null;
            TableCanvas.DropZoneIds       = [];
            return;
        }

        TableCanvas.SelectableCardIds = _logic.GetSelectableCardIds(_state);

        string? selectedCard = _state.Metadata.GetValueOrDefault("selected_card");
        TableCanvas.SelectedCardId = selectedCard;

        // In multi-select mode (comma-separated IDs, e.g. Hand-and-Foot meld assembly),
        // drop zones don't apply — the meld is submitted via an action button, not drag-drop.
        bool isMultiSelect = selectedCard is not null && selectedCard.Contains(',');
        TableCanvas.DropZoneIds = !isMultiSelect && selectedCard is not null
            ? _logic.GetDropZoneIds(_state, selectedCard)
            : [];
    }

    // ── Multiplayer event handlers ────────────────────────────────────────────

    private void OnMultiplayerActionApplied(ActionAppliedMsg msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_state is null || _logic is null) return;
            if (GameOverOverlay.IsVisible) return;
            if (_isAutoAdvancing) return;   // host auto-advance loop handles its own refreshes

            MaybeSortHands();
            TableCanvas.GameState = _state;
            RefreshStatus();
            RefreshActionButtons();
            RefreshInteractionState();

            if (_logic.IsGameOver(_state)) { _sounds.PlayWin(); ShowGameOver(); }
        });
    }

    private void OnMultiplayerStateSynced(GameState newState)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _state = newState;
            _logic = LogicRegistry.Create(newState.Definition);

            MaybeSortHands();
            TableCanvas.GameState = _state;
            RefreshStatus();
            RefreshActionButtons();
            RefreshInteractionState();
        });
    }

    private void OnMultiplayerDisconnected(string reason)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            UnsubscribeMultiplayerEvents();
            await DisplayAlertAsync("Disconnected", $"Lost connection to host: {reason}", "OK");
            await Shell.Current.GoToAsync("//home");
        });
    }

    // ── Game-over overlay ─────────────────────────────────────────────────────

    private void ShowGameOver()
    {
        ActionButtonsPanel.Children.Clear();

        GameOverResultLabel.Text = _logic!.GetStatusText(_state!);

        string sub = _state!.Metadata.GetValueOrDefault("sub", "");
        if (!string.IsNullOrEmpty(sub))
        {
            GameOverSubLabel.Text      = sub;
            GameOverSubLabel.IsVisible = true;
        }
        else if (_state.RoundNumber > 1)
        {
            GameOverSubLabel.Text      = $"{_state.RoundNumber} rounds played";
            GameOverSubLabel.IsVisible = true;
        }
        else
        {
            GameOverSubLabel.IsVisible = false;
        }

        GameOverOverlay.IsVisible = true;
    }

    private async void OnPlayAgainClicked(object? sender, EventArgs e)
    {
        await _saves.DeleteAllForAsync(GameId);
        _saveSlotId = null;
        _state = null;
        await InitializeGameAsync();
    }

    // ── Status messages (stacking fade-out) ───────────────────────────────────

    private string _lastShownStatus = string.Empty;

    private void RefreshStatus()
    {
        if (_logic is null) return;
        string text = _logic.GetStatusText(_state!);
        if (string.IsNullOrEmpty(text) || text == _lastShownStatus) return;

        _lastShownStatus = text;
        AppendToLog(text);
        ShowMessage(text);
    }

    private void OnCanvasSizeChanged(object? sender, EventArgs e)
    {
        // Keep messages just above the player hand (bottom 13% of canvas = hand 11% + gap 2%)
        double margin = TableCanvas.Height * 0.13;
        StatusMessagesPanel.Margin = new Thickness(0, 0, 0, margin);
    }

    private void ShowMessage(string text)
    {
        if (!_settings.ShowGameMessages) return;

        // Limit visible stack to 3 — remove the oldest if we're at the cap
        while (StatusMessagesPanel.Children.Count >= 3)
            StatusMessagesPanel.Children.RemoveAt(0);

        var label = new Label
        {
            Text                  = text,
            TextColor             = Color.FromArgb("#FFD700"),
            FontSize              = 15,
            FontAttributes        = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        var bubble = new Border
        {
            Content         = label,
            BackgroundColor = Color.FromArgb("#CC0D2518"),
            StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 0,
            Padding         = new Thickness(18, 10),
            Opacity         = 0,
        };

        StatusMessagesPanel.Children.Add(bubble);

        // Fade in immediately, then fade out after a delay
        _ = AnimateMessageAsync(bubble);
    }

    private static async Task AnimateMessageAsync(View bubble)
    {
        await bubble.FadeToAsync(1.0, 180);
        await Task.Delay(3600);
        await bubble.FadeToAsync(0.0, 700);
        // Safe to remove from any thread since MAUI marshals Children mutations
        if (bubble.Parent is VerticalStackLayout panel)
            panel.Children.Remove(bubble);
    }

    // ── Game log ──────────────────────────────────────────────────────────────

    private void AppendToLog(string text)
    {
        if (_state is null) return;
        _state.GameLog.Add(text);
    }


    private void OnLogOverlayDismissed(object? sender, EventArgs e)
        => GameLogOverlay.IsVisible = false;

    // ── Gear menu ─────────────────────────────────────────────────────────────

    private void OnGearClicked(object? sender, EventArgs e)
    {
        bool show = !GearMenuPanel.IsVisible;
        GearMenuPanel.IsVisible    = show;
        GearMenuBackdrop.IsVisible = show;
    }

    private void OnGearMenuDismissed(object? sender, EventArgs e)
    {
        GearMenuPanel.IsVisible    = false;
        GearMenuBackdrop.IsVisible = false;
    }

    private async void OnGearSortClicked(object? sender, EventArgs e)
    {
        OnGearMenuDismissed(sender, e);
        if (_state is null) return;

        // Shared with the browser client: which sorts a game offers, in what order, is
        // a per-game rule and both clients had their own copy of it.
        var options = HandSortOptions.For(_state.Definition);

        string? chosen = await DisplayActionSheetAsync(
            "Sort Hand", "Cancel", null, options.Select(o => o.Label).ToArray());

        if (chosen is null || chosen == "Cancel") return;

        var mode = options.FirstOrDefault(o => o.Label == chosen).Mode;

        // Free means the player arranges the hand themselves, so there is deliberately
        // nothing to do.
        if (mode is null || mode == HandSortOptions.FreeMode) return;

        SortPlayerHand(mode);
    }

    private void OnGearLogClicked(object? sender, EventArgs e)
    {
        OnGearMenuDismissed(sender, e);
        if (_state is null) return;
        GameLogList.ItemsSource = _state.GameLog.AsReadOnly();
        GameLogOverlay.IsVisible = true;
        if (_state.GameLog.Count > 0)
            GameLogList.ScrollTo(_state.GameLog[^1], ScrollToPosition.End, animate: false);
    }

    private async void OnGearRulesClicked(object? sender, EventArgs e)
    {
        OnGearMenuDismissed(sender, e);
        if (_state is null) return;
        await Shell.Current.GoToAsync("help", new Dictionary<string, object>
        {
            ["GameId"]   = _state.GameId,
            ["GameName"] = _state.Definition.Name,
        });
    }

    private async void OnGearNewGameClicked(object? sender, EventArgs e)
    {
        OnGearMenuDismissed(sender, e);
        bool confirm = await DisplayAlertAsync(
            "New Game",
            "Start a new game? Your current progress will be lost.",
            "New Game", "Cancel");
        if (!confirm) return;

        if (_state is not null)
            await _saves.DeleteAllForAsync(_state.GameId);

        _saveSlotId = null;
        _state = null;
        await InitializeGameAsync();
    }

    private async void OnGearLeaveClicked(object? sender, EventArgs e)
    {
        OnGearMenuDismissed(sender, e);
        bool gameOver = _logic?.IsGameOver(_state!) ?? true;
        if (!gameOver)
        {
            bool confirm = await DisplayAlertAsync(
                "Leave Game",
                "Are you sure you want to leave this game?",
                "Leave", "Stay");
            if (!confirm) return;
        }
        await Shell.Current.GoToAsync("//home");
    }

    // Still used by the Game Over overlay's "Main Menu" button
    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//home");
    }

    // ── Action buttons ────────────────────────────────────────────────────────

    private void RefreshActionButtons()
    {
        ActionButtonsPanel.Children.Clear();
        if (_logic is null || _state is null) return;

        var actions = _logic.GetValidActions(_state);

        // Show buttons for multi-action choices, or for a single "ready" action.
        // Single "tap" actions are handled by canvas tap, not a button.
        bool showButtons = actions.Count > 1
            || (actions.Count == 1 && actions[0].Type == "ready");
        if (!showButtons) return;

        object? primaryStyle = null;
        object? hudStyle     = null;
        Application.Current?.Resources.TryGetValue("PrimaryButton", out primaryStyle);
        Application.Current?.Resources.TryGetValue("HudButton",     out hudStyle);

        foreach (var action in actions)
        {
            bool isReady = action.Type == "ready";
            var btn = new Button
            {
                Text  = action.Label ?? action.Type,
                Style = (isReady ? primaryStyle : hudStyle) as Style,
            };
            var captured = action;
            btn.Clicked += (_, _) => OnActionClicked(captured);
            ActionButtonsPanel.Children.Add(btn);
        }
    }
}
