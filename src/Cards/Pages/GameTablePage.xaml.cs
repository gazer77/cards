using Cards.Engine;
using Cards.Models;
using Cards.Rendering;
using Cards.Services;

namespace Cards.Pages;

[QueryProperty(nameof(GameId),      "GameId")]
[QueryProperty(nameof(PlayerCount), "PlayerCount")]
[QueryProperty(nameof(HouseRules),  "HouseRules")]
public partial class GameTablePage : ContentPage
{
    private readonly GameLoader       _loader;
    private readonly SettingsService  _settings;
    private readonly GameSaveService  _saves;
    private readonly SoundService     _sounds;
    private GameState?  _state;
    private IGameLogic? _logic;

    public GameTablePage(GameLoader loader, SettingsService settings, GameSaveService saves, SoundService sounds)
    {
        InitializeComponent();
        _loader   = loader;
        _settings = settings;
        _saves    = saves;
        _sounds   = sounds;

        TableCanvas.CardTapped   += OnCardTapped;
        TableCanvas.CanvasTapped += OnCanvasTapped;
        TableCanvas.ZoneTapped   += OnZoneTapped;
        TableCanvas.CardDropped  += OnCardDropped;
    }

    public string GameId { get; set; } = string.Empty;
    public int PlayerCount { get; set; } = 2;
    public List<string>? HouseRules { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Only initialize on first appearance; skip when returning from a sub-page (e.g. rules).
        if (_state is null)
            await InitializeGameAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Persist the game so the player can resume after navigating away.
        // Don't save if game is already over (no point restoring a finished game).
        if (_state is not null && _logic is not null && !_logic.IsGameOver(_state))
        {
            var rules = (IReadOnlyList<string>)(HouseRules ?? []);
            _ = _saves.SaveAsync(_state, PlayerCount, rules);
        }
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private async Task InitializeGameAsync()
    {
        GameOverOverlay.IsVisible = false;

        var definition = await _loader.LoadAsync(GameId);
        if (definition is null) { await Shell.Current.GoToAsync(".."); return; }

        GameTitleLabel.Text = definition.Name;
        TableCanvas.SetSkin(SkinFactory.Create(_settings.CardSkinId));

        var state  = new GameState { GameId = definition.Id, Definition = definition };
        var rules  = (IReadOnlyList<string>)(HouseRules ?? []);
        _logic     = LogicRegistry.Create(definition.Id);

        bool restored = false;
        if (_logic is not null && _saves.HasSave(definition.Id))
            restored = await _saves.RestoreAsync(state, _logic, PlayerCount, rules);

        if (!restored)
        {
            if (_logic is not null)
                _logic.Initialize(state, PlayerCount, rules);
            else
                BuildFallbackState(state, definition);
        }

        _state = state;
        TableCanvas.GameState = _state;
        RefreshStatus();
        RefreshActionButtons();
        RefreshInteractionState();

        // Initialise audio lazily so it doesn't block the UI on startup.
        _ = _sounds.InitializeAsync();
    }

    private static void BuildFallbackState(GameState state, GameDefinition definition)
    {
        for (int i = 0; i < 2; i++)
            state.Players.Add(new Player($"player{i}", $"Player {i + 1}"));

        foreach (var zoneDef in definition.Zones)
        {
            if (zoneDef.Owner == "each_player")
            {
                foreach (var p in state.Players)
                    state.Zones[$"{zoneDef.Id}:{p.Id}"] =
                        new Zone($"{zoneDef.Id}:{p.Id}", zoneDef.Type, p.Id, zoneDef.Visibility);
            }
            else
            {
                state.Zones[zoneDef.Id] =
                    new Zone(zoneDef.Id, zoneDef.Type, zoneDef.Owner, zoneDef.Visibility);
            }
        }

        if (state.Zones.TryGetValue("deck", out var deck))
        {
            var cards = DeckBuilder.Build(definition.DeckType);
            DeckBuilder.Shuffle(cards);
            deck.AddRange(cards);
        }

        state.CurrentPhaseId = definition.Phases.FirstOrDefault()?.Id ?? string.Empty;
    }

    // ── Touch / interaction handlers ──────────────────────────────────────────

    private bool _isAutoAdvancing;

    private void OnCardTapped(string cardId)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || _isAutoAdvancing) return;

        var selectables = _logic.GetSelectableCardIds(_state);
        if (!selectables.Contains(cardId)) return;

        _ = ApplyAndRefreshAsync(new GameAction("select_card", CardId: cardId));
    }

    private void OnCanvasTapped()
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || _isAutoAdvancing) return;

        // Single-action games (War, dealer tap, etc.) advance on any canvas tap.
        var actions = _logic.GetValidActions(_state);
        if (actions.Count != 1) return;
        _ = ApplyAndRefreshAsync(actions[0]);
    }

    private void OnZoneTapped(string zoneId)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || _isAutoAdvancing) return;

        string? selectedCard = _state.Metadata.GetValueOrDefault("selected_card");
        if (selectedCard is null) return;

        var dropZones = _logic.GetDropZoneIds(_state, selectedCard);
        if (!dropZones.Contains(zoneId)) return;

        _ = ApplyAndRefreshAsync(new GameAction("play_card", CardId: selectedCard, ZoneId: zoneId));
    }

    private void OnCardDropped(string cardId, string zoneId)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || _isAutoAdvancing) return;

        var dropZones = _logic.GetDropZoneIds(_state, cardId);
        if (!dropZones.Contains(zoneId)) return;

        _ = ApplyAndRefreshAsync(new GameAction("play_card", CardId: cardId, ZoneId: zoneId));
    }

    private void OnActionClicked(GameAction action)
    {
        if (_state is null || _logic is null) return;
        if (GameOverOverlay.IsVisible || _isAutoAdvancing) return;
        _ = ApplyAndRefreshAsync(action);
    }

    private async Task ApplyAndRefreshAsync(GameAction action)
    {
        ApplyWithSound(action);
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

                ApplyWithSound(next[0]);
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

    /// <summary>
    /// Applies an action then plays the appropriate sound effect based on what changed.
    /// </summary>
    private void ApplyWithSound(GameAction action)
    {
        // Snapshot what's face-down before the action
        var faceDownBefore = _state!.Zones.Values
            .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
        var handCountBefore = _state.Zones
            .Where(kv => kv.Key.StartsWith("hand:")).Sum(kv => kv.Value.Count);

        _logic!.Apply(_state, action);

        // Cards that flipped face-up → flip sound (e.g. Blackjack hole card reveal)
        var faceDownAfter = _state.Zones.Values
            .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
        bool cardFlipped = faceDownBefore.Except(faceDownAfter).Any();

        // More cards in hands than before → deal sound
        int handCountAfter = _state.Zones
            .Where(kv => kv.Key.StartsWith("hand:")).Sum(kv => kv.Value.Count);
        bool cardDealt = handCountAfter > handCountBefore;

        if (cardFlipped)
            _sounds.PlayFlip();
        else if (cardDealt)
            _sounds.PlayDeal();
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

        TableCanvas.DropZoneIds = selectedCard is not null
            ? _logic.GetDropZoneIds(_state, selectedCard)
            : [];
    }

    // ── Game-over overlay ─────────────────────────────────────────────────────

    private void ShowGameOver()
    {
        StatusLabel.IsVisible    = false;
        ActionButtonsPanel.Children.Clear();

        GameOverResultLabel.Text = _logic!.GetStatusText(_state!);

        // Prefer a game-supplied subtitle (e.g. hand totals), fall back to round count.
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
        _saves.DeleteSave(GameId); // clear save so Play Again starts fresh
        _state = null;             // force re-initialize
        await InitializeGameAsync();
    }

    // ── Status label ──────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        if (_logic is null) { StatusLabel.IsVisible = false; return; }
        string text = _logic.GetStatusText(_state!);
        StatusLabel.Text      = text;
        StatusLabel.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ── Action buttons ────────────────────────────────────────────────────────

    private void RefreshActionButtons()
    {
        ActionButtonsPanel.Children.Clear();
        if (_logic is null || _state is null) return;

        var actions = _logic.GetValidActions(_state);
        if (actions.Count <= 1) return; // single-action games use tap

        object? hudStyle = null;
        Application.Current?.Resources.TryGetValue("HudButton", out hudStyle);
        foreach (var action in actions)
        {
            var btn = new Button
            {
                Text  = action.Label ?? action.Type,
                Style = hudStyle as Style,
            };
            var captured = action;
            btn.Clicked += (_, _) => OnActionClicked(captured);
            ActionButtonsPanel.Children.Add(btn);
        }
    }

    // ── HUD buttons ───────────────────────────────────────────────────────────

    private async void OnRulesClicked(object? sender, EventArgs e)
    {
        if (_state is null) return;
        await Shell.Current.GoToAsync("help", new Dictionary<string, object>
        {
            ["GameId"]   = _state.GameId,
            ["GameName"] = _state.Definition.Name,
        });
    }

    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        // Skip confirmation if the game is already over
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
}
