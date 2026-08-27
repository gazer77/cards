using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Views;

/// <summary>
/// MAUI host for <see cref="CardTableRenderer"/>.
///
/// All the drawing, animation and hit-testing lives in the renderer, which knows
/// nothing about MAUI; this class only bridges it to an SKCanvasView — surface in,
/// touch out, plus a dispatcher-timer animation driver. The Blazor client hosts the
/// same renderer through an equivalent shell.
/// </summary>
public class GameTableView : SKCanvasView
{
    private sealed class DispatcherAnimationDriver : IAnimationDriver
    {
        private IDispatcherTimer? _timer;

        public event Action? Tick;

        public void RequestFrames()
        {
            if (_timer is null)
            {
                _timer = Application.Current!.Dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromMilliseconds(16);   // ~60fps
                _timer.Tick += (_, _) => Tick?.Invoke();
            }
            if (!_timer.IsRunning) _timer.Start();
        }

        public void StopFrames() => _timer?.Stop();
    }

    private readonly CardTableRenderer _renderer;

    public GameTableView()
    {
        _renderer = new CardTableRenderer(new DispatcherAnimationDriver());
        _renderer.RedrawRequested += InvalidateSurface;

        _renderer.CardTapped          += id      => CardTapped?.Invoke(id);
        _renderer.ZoneTapped          += id      => ZoneTapped?.Invoke(id);
        _renderer.CanvasTapped        += ()      => CanvasTapped?.Invoke();
        _renderer.CardDropped         += (c, z)  => CardDropped?.Invoke(c, z);
        _renderer.CardReorderedInHand += (c, i)  => CardReorderedInHand?.Invoke(c, i);

        EnableTouchEvents = true;
        Touch += OnTouch;
    }

    // ── Events (unchanged contract for GameTablePage) ─────────────────────────

    public event Action<string>?         CardTapped;
    public event Action<string>?         ZoneTapped;
    public event Action?                 CanvasTapped;
    public event Action<string, string>? CardDropped;
    public event Action<string, int>?    CardReorderedInHand;

    // ── Pass-through surface ──────────────────────────────────────────────────

    public GameState? GameState
    {
        get => _renderer.GameState;
        set => _renderer.GameState = value;
    }

    public string PlaceholderText
    {
        get => _renderer.PlaceholderText;
        set => _renderer.PlaceholderText = value;
    }

    public IReadOnlyList<string> SelectableCardIds
    {
        get => _renderer.SelectableCardIds;
        set => _renderer.SelectableCardIds = value;
    }

    public string? SelectedCardId
    {
        get => _renderer.SelectedCardId;
        set => _renderer.SelectedCardId = value;
    }

    public IReadOnlyList<string> DropZoneIds
    {
        get => _renderer.DropZoneIds;
        set => _renderer.DropZoneIds = value;
    }

    public void SetSkin(ICardSkin skin)     => _renderer.SetSkin(skin);
    public void SetTheme(ITableTheme theme) => _renderer.SetTheme(theme);

    public void QueueFlyIns(
        IReadOnlyList<(string CardId, SKPoint From, SKPoint To)> entries,
        int delayBetweenMs = 0)
        => _renderer.QueueFlyIns(entries, delayBetweenMs);

    public Dictionary<string, SKPoint> ComputeHandSlotCenters(
        GameState state, IEnumerable<string> cardIds)
        => _renderer.ComputeHandSlotCenters(state, cardIds);

    public Task WaitForFlyInsAsync()                    => _renderer.WaitForFlyInsAsync();
    public Task WaitForNextPaintAsync()                 => _renderer.WaitForNextPaintAsync();
    public Task TriggerShuffleAnimationAsync(string id) => _renderer.TriggerShuffleAnimationAsync(id);
    public SKRect? GetLastCardRect(string cardId)       => _renderer.GetLastCardRect(cardId);
    public SKPoint? GetZoneCenter(string zoneId)        => _renderer.GetZoneCenter(zoneId);

    // ── Surface and input bridging ────────────────────────────────────────────

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        => _renderer.Paint(e.Surface.Canvas, e.Info);

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        e.Handled = true;

        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:   _renderer.OnPointerDown(e.Location); break;
            case SKTouchAction.Moved:     _renderer.OnPointerMove(e.Location); break;
            case SKTouchAction.Released:  _renderer.OnPointerUp(e.Location);   break;
            case SKTouchAction.Cancelled: _renderer.OnPointerCancel();         break;
        }
    }
}
