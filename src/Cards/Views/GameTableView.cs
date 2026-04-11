using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Views;

public class GameTableView : SKCanvasView
{
    private ICardSkin   _skin  = new DefaultCardSkin();
    private ITableTheme _theme = new DefaultTableTheme();
    private GameState?  _state;
    private string      _placeholderText = string.Empty;

    // ── Interaction state ─────────────────────────────────────────────────────

    private IReadOnlyList<string> _selectableCardIds = [];
    private string?               _selectedCardId;
    private HashSet<string>       _selectedCardIds   = [];
    private IReadOnlyList<string> _dropZoneIds       = [];

    private string? _dragCardId;
    private string? _dragSourceZoneId;
    private string? _tooltipCardId;
    private SKPoint _touchStartPt;
    private SKPoint _dragCurrentPt;
    private bool    _isDragging;
    private bool    _recordCardRects;
    private const float DragThreshold = 14f;

    private readonly List<(string CardId, SKRect Rect)> _cardRects = [];
    private IReadOnlyList<ZoneLayout> _lastLayouts = [];

    // ── Animation state ───────────────────────────────────────────────────────

    // Per-card animations: cardId → (startTimeMs, durationMs)
    private readonly Dictionary<string, (long Start, float Duration)> _dealAnims    = [];
    private readonly Dictionary<string, (long Start, float Duration)> _flipAnims    = [];
    private readonly Dictionary<string, (long Start, float Duration)> _receiveAnims = [];
    // Fly-in: cardId → (source center, fixed destination center, startTimeMs, durationMs)
    // Both From and To are fixed at queue time — To is never recomputed at draw time.
    private readonly Dictionary<string, (SKPoint From, SKPoint To, long Start, float Duration)> _flyInAnims = [];

    private SKImageInfo _lastInfo;
    private const float FlyInSpeedPxMs = 1.2f; // pixels per millisecond
    // Shuffle: zoneId → (startTimeMs, durationMs)
    private readonly Dictionary<string, (long Start, float Duration)> _shuffleAnims = [];
    private TaskCompletionSource? _shuffleCompletion;
    private TaskCompletionSource? _flyInCompletion;

    // Scratch lists — populated during draw, cleared after
    private readonly List<string> _finishedDealAnims    = [];
    private readonly List<string> _finishedFlipAnims    = [];
    private readonly List<string> _finishedReceiveAnims = [];
    private readonly List<string> _finishedFlyInAnims   = [];
    private readonly List<string> _finishedShuffleAnims = [];

    private IDispatcherTimer? _animTimer;
    private TaskCompletionSource? _nextPaintCompletion;

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<string>?         CardTapped;
    public event Action<string>?         ZoneTapped;
    public event Action?                 CanvasTapped;
    public event Action<string, string>? CardDropped;
    /// <summary>Card was dragged to a new position within its own hand zone.</summary>
    public event Action<string, int>?    CardReorderedInHand;

    // ── Constructor ───────────────────────────────────────────────────────────

    public GameTableView()
    {
        EnableTouchEvents = true;
        Touch += OnTouch;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public GameState? GameState
    {
        get => _state;
        set
        {
            long now = NowMs();

            // Collect card states before the swap
            var oldCardIds = _state?.Zones.Values
                .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet() ?? [];
            var oldFaceDown = _state?.Zones.Values
                .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet() ?? [];
            var oldHandIds = _state?.Zones.Values
                .Where(z => z.Type == "hand")
                .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet() ?? [];

            _state         = value;
            _tooltipCardId = null;   // dismiss tooltip whenever the game state advances

            if (value is not null)
            {
                // Drop any shuffle anims that were running on a zone that is now empty
                // (e.g. the preDeal deck after transitioning to the real game state).
                // Without this cleanup the entry would linger forever because
                // DrawShufflingStack is never called on an empty zone, so t never
                // reaches 1 and the timer never stops.
                foreach (var zoneId in _shuffleAnims.Keys.ToList())
                {
                    if (!value.Zones.TryGetValue(zoneId, out var sz) || sz.IsEmpty)
                    {
                        _shuffleAnims.Remove(zoneId);
                        _shuffleCompletion?.TrySetResult();
                        _shuffleCompletion = null;
                    }
                }

                var newIds = value.Zones.Values
                    .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet();
                var newFaceDown = value.Zones.Values
                    .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
                var newHandIds = value.Zones.Values
                    .Where(z => z.Type == "hand")
                    .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet();

                // Cards that just appeared in a fan/spread zone → slide-up deal animation.
                // Deliberately excludes deck/pile zones: DrawStack never processes _dealAnims,
                // so entries for those cards would accumulate forever and block timer shutdown.
                // Skip if a fly-in is already queued for this card (e.g. deal fly-in set before GameState).
                var fanSpreadCardIds = value.Zones.Values
                    .Where(z => z.Type is "hand" or "spread" or "trick")
                    .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet();
                foreach (var id in newIds.Except(oldCardIds))
                    if (!_flyInAnims.ContainsKey(id) && fanSpreadCardIds.Contains(id))
                        _dealAnims[id] = (now, 240f);

                // Cards that flipped face-up → scale-X flip animation.
                // Skip cards with a pending fly-in: they are arriving for the first
                // time (initial deal) and haven't landed yet.  Showing a flip at the
                // destination before the card has even flown there looks wrong and can
                // briefly expose face values in zones that should render face-down.
                foreach (var id in oldFaceDown.Except(newFaceDown))
                    if (newIds.Contains(id) && !_flyInAnims.ContainsKey(id))
                        _flipAnims[id] = (now, 360f);

                // Cards that moved INTO a hand zone (received mid-game) → bump animation.
                // Skip cards that already have a fly-in queued: the fly-in IS the arrival
                // animation, so adding a receive bump on top produces erratic combined motion.
                var justFlipped = oldFaceDown.Except(newFaceDown).ToHashSet();
                foreach (var id in newHandIds.Except(oldHandIds).Intersect(oldCardIds))
                    if (!justFlipped.Contains(id) && !_dealAnims.ContainsKey(id)
                                                  && !_flyInAnims.ContainsKey(id))
                        _receiveAnims[id] = (now, 1350f);
            }

            if (_dealAnims.Count > 0 || _flipAnims.Count > 0 ||
                _receiveAnims.Count > 0 || _flyInAnims.Count > 0 || _shuffleAnims.Count > 0)
                EnsureAnimTimer();
            else
                InvalidateSurface();
        }
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set { _placeholderText = value; InvalidateSurface(); }
    }

    public IReadOnlyList<string> SelectableCardIds
    {
        get => _selectableCardIds;
        set { _selectableCardIds = value; InvalidateSurface(); }
    }

    public string? SelectedCardId
    {
        get => _selectedCardId;
        set
        {
            _selectedCardId = value;
            _selectedCardIds = value is null ? []
                : new HashSet<string>(value.Split(',', StringSplitOptions.RemoveEmptyEntries));
            InvalidateSurface();
        }
    }

    public IReadOnlyList<string> DropZoneIds
    {
        get => _dropZoneIds;
        set { _dropZoneIds = value; InvalidateSurface(); }
    }

    public void SetSkin(ICardSkin skin)     { _skin  = skin;  InvalidateSurface(); }
    public void SetTheme(ITableTheme theme) { _theme = theme; InvalidateSurface(); }

    /// <summary>
    /// Queues fly-in animations for a list of cards.  The page is responsible for
    /// computing both <c>From</c> (source screen position) and <c>To</c> (destination
    /// fan-slot center) using <see cref="ComputeHandSlotCenters"/> before calling this.
    /// <para>
    /// Each card's travel duration is computed as <c>distance / <see cref="FlyInSpeedPxMs"/></c>
    /// so cards travelling further naturally take longer.  When <paramref name="delayBetweenMs"/>
    /// is greater than zero, successive entries start that many ms apart (deal waterfall);
    /// when zero all entries start simultaneously (mid-game receive).
    /// </para>
    /// </summary>
    public void QueueFlyIns(
        IReadOnlyList<(string CardId, SKPoint From, SKPoint To)> entries,
        int delayBetweenMs = 0)
    {
        if (entries.Count == 0) return;
        long now    = NowMs();
        long offset = 0;

        foreach (var (id, from, to) in entries)
        {
            float dist = SKPoint.Distance(from, to);
            float dur  = MathF.Max(dist / FlyInSpeedPxMs, 80f);
            _flyInAnims[id] = (from, to, now + offset, dur);
            offset += delayBetweenMs;
        }

        EnsureAnimTimer();
    }

    /// <summary>
    /// Computes the exact fan-slot center for each card ID in <paramref name="cardIds"/>
    /// within <paramref name="state"/>, using the last known canvas dimensions.
    /// Call after setting <see cref="GameState"/> so the canvas size is available.
    /// Cards not currently in a hand zone are omitted from the result.
    /// </summary>
    public Dictionary<string, SKPoint> ComputeHandSlotCenters(
        GameState state, IEnumerable<string> cardIds)
    {
        if (_lastInfo.Width == 0) return [];
        return ComputeFanSlotCenters(state, cardIds, _lastInfo);
    }

    /// <summary>
    /// Returns a Task that completes once all queued fly-in animations have finished.
    /// Returns <see cref="Task.CompletedTask"/> immediately if none are active.
    /// Always combine with a safety timeout: <c>Task.WhenAny(WaitForFlyInsAsync(), Task.Delay(N))</c>.
    /// </summary>
    public Task WaitForFlyInsAsync()
    {
        if (!_flyInAnims.Any()) return Task.CompletedTask;
        _flyInCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _flyInCompletion.Task;
    }

    /// <summary>
    /// Computes the center pixel of every fan-slot for the given card IDs in
    /// <paramref name="state"/>, using <paramref name="info"/> for canvas dimensions.
    /// Only hand zones (rendered as fans) are considered; cards in other zone types
    /// are omitted from the result.
    /// </summary>
    private static Dictionary<string, SKPoint> ComputeFanSlotCenters(
        GameState state, IEnumerable<string> cardIds, SKImageInfo info)
    {
        var result = new Dictionary<string, SKPoint>();
        var idSet  = cardIds.ToHashSet();
        if (idSet.Count == 0) return result;

        var layouts = ZoneLayoutEngine.Compute(state, info);

        foreach (var layout in layouts)
        {
            if (layout.Hint != ZoneRenderHint.Fan) continue;

            var cards  = layout.Zone.Cards;
            float totalW = layout.Bounds.Width;
            float cardW  = layout.CardWidth;
            float cardH  = layout.CardHeight;

            float step = cards.Count == 1
                ? 0f
                : MathF.Min((totalW - cardW) / (cards.Count - 1), cardW * 0.75f);
            float startX = cards.Count == 1
                ? layout.Bounds.MidX - cardW / 2f
                : layout.Bounds.Left + (totalW - (step * (cards.Count - 1) + cardW)) / 2f;
            float midY = layout.Bounds.MidY;

            for (int i = 0; i < cards.Count; i++)
            {
                if (idSet.Contains(cards[i].Id))
                    result[cards[i].Id] = new SKPoint(startX + i * step + cardW / 2f, midY);
            }
        }
        return result;
    }

    /// <summary>Returns the last rendered screen rect for a card, or null if not rendered.</summary>
    public SKRect? GetLastCardRect(string cardId)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
            if (_cardRects[i].CardId == cardId) return _cardRects[i].Rect;
        return null;
    }

    /// <summary>Returns the center of a zone's last rendered bounds, or null.</summary>
    public SKPoint? GetZoneCenter(string zoneId)
    {
        var layout = _lastLayouts.FirstOrDefault(l => l.Zone.Id == zoneId);
        return layout is null ? null : new SKPoint(layout.Bounds.MidX, layout.Bounds.MidY);
    }

    /// <summary>
    /// Plays a riffle-shuffle animation on the named zone and returns a Task that
    /// completes when the animation finishes (driven by the render loop).
    /// </summary>
    public Task TriggerShuffleAnimationAsync(string zoneId)
    {
        _shuffleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shuffleAnims[zoneId] = (NowMs(), 1200f);
        EnsureAnimTimer();
        return _shuffleCompletion.Task;
    }

    /// <summary>
    /// Returns a Task that completes on the next <see cref="OnPaintSurface"/> call.
    /// Use after setting <see cref="GameState"/> to guarantee <see cref="_lastLayouts"/>
    /// has been populated with the new state before reading zone positions.
    /// </summary>
    public Task WaitForNextPaintAsync()
    {
        _nextPaintCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Force a repaint in case the view system hasn't scheduled one yet.
        InvalidateSurface();
        return _nextPaintCompletion.Task;
    }

    // ── Animation timer ───────────────────────────────────────────────────────

    private void EnsureAnimTimer()
    {
        // Create the timer and register the Tick handler exactly once.
        if (_animTimer is null)
        {
            _animTimer = Application.Current!.Dispatcher.CreateTimer();
            _animTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 fps
            _animTimer.Tick += OnAnimTimerTick;
        }
        if (!_animTimer.IsRunning)
            _animTimer.Start();
    }

    private void OnAnimTimerTick(object? sender, EventArgs e)
    {
        InvalidateSurface();
        if
        (
            !_dealAnims.Any() &&
            !_flipAnims.Any() &&
            !_receiveAnims.Any() &&
            !_flyInAnims.Any() &&
            !_shuffleAnims.Any()
        )
        {
            _animTimer!.Stop();
            // Queue one final repaint after stopping so the last clean frame
            // is guaranteed to be painted even if the platform coalesces or
            // drops the InvalidateSurface() call above.
            InvalidateSurface();
        }
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info   = e.Info;

        _lastInfo = info;
        canvas.Clear();
        DrawFelt(canvas, info);

        if (_state is null)
        {
            if (!string.IsNullOrEmpty(_placeholderText))
                DrawPlaceholder(canvas, info, _placeholderText);
            return;
        }

        // Remove fly-in entries for cards that are no longer in any hand zone
        // (e.g. moved to a books/spread zone mid-animation).  Without this the
        // entry would linger forever, the timer would never stop, and the card
        // would appear frozen at its mid-flight position.
        if (_flyInAnims.Count > 0)
        {
            var handCardIds = _state.Zones.Values
                .Where(z => z.Type == "hand")
                .SelectMany(z => z.Cards)
                .Select(c => c.Id)
                .ToHashSet();
            foreach (var orphanId in _flyInAnims.Keys.Where(id => !handCardIds.Contains(id)).ToList())
                _flyInAnims.Remove(orphanId);
        }

        _cardRects.Clear();
        var layouts = ZoneLayoutEngine.Compute(_state, info);
        _lastLayouts = layouts;

        // Signal any caller waiting for the canvas to render the current state.
        _nextPaintCompletion?.TrySetResult();
        _nextPaintCompletion = null;

        foreach (var layout in layouts)
        {
            DrawZone(canvas, layout);
            if (_dropZoneIds.Contains(layout.Zone.Id))
                DrawDropZoneHighlight(canvas, layout.Bounds);
        }

        DrawTrickDirectionIndicator(canvas, layouts);

        // Clean up finished animations after all zones are drawn
        foreach (var id in _finishedDealAnims)    _dealAnims.Remove(id);
        foreach (var id in _finishedFlipAnims)    _flipAnims.Remove(id);
        foreach (var id in _finishedReceiveAnims) _receiveAnims.Remove(id);
        foreach (var id in _finishedFlyInAnims)   _flyInAnims.Remove(id);
        foreach (var id in _finishedShuffleAnims) _shuffleAnims.Remove(id);

        _finishedDealAnims.Clear();
        _finishedFlipAnims.Clear();
        _finishedReceiveAnims.Clear();
        _finishedFlyInAnims.Clear();
        _finishedShuffleAnims.Clear();

        // Signal any awaiter that was waiting for fly-ins to complete.
        if (!_flyInAnims.Any() && _flyInCompletion is not null)
        {
            _flyInCompletion.TrySetResult();
            _flyInCompletion = null;
        }

        if (_isDragging && _dragCardId is not null)
            DrawDragGhost(canvas);

        DrawCardTooltip(canvas, info);
    }

    // ── Zone rendering ────────────────────────────────────────────────────────

    private void DrawZone(SKCanvas canvas, ZoneLayout layout)
    {
        bool rotated = layout.RotationDegrees != 0f;
        _recordCardRects = !rotated;

        if (rotated)
        {
            canvas.Save();
            canvas.RotateDegrees(layout.RotationDegrees, layout.Bounds.MidX, layout.Bounds.MidY);
        }

        if (layout.Zone.IsEmpty || layout.Hint == ZoneRenderHint.Empty)
            DrawEmptyZone(canvas, layout);
        else
            DrawFilledZone(canvas, layout);

        if (layout.Label is not null) DrawLabel(canvas, layout);
        if (layout.IsCurrentPlayer)   DrawHighlight(canvas, layout.Bounds);

        if (rotated) canvas.Restore();
    }

    private void DrawEmptyZone(SKCanvas canvas, ZoneLayout layout)
        => CardRenderer.DrawEmptySlot(canvas, CenterCardRect(layout), _theme, layout.Zone.Id.ToUpper());

    private void DrawFilledZone(SKCanvas canvas, ZoneLayout layout)
    {
        switch (layout.Hint)
        {
            case ZoneRenderHint.Stack:
                if (_shuffleAnims.ContainsKey(layout.Zone.Id))
                    DrawShufflingStack(canvas, layout);
                else
                    DrawStack(canvas, layout);
                break;
            case ZoneRenderHint.Fan:       DrawFan(canvas, layout);       break;
            case ZoneRenderHint.Spread:    DrawSpread(canvas, layout);    break;
            case ZoneRenderHint.CountOnly: DrawCountOnly(canvas, layout); break;
            default:
                CardRenderer.DrawEmptySlot(canvas, CenterCardRect(layout), _theme);
                break;
        }
    }

    // ── Stack ─────────────────────────────────────────────────────────────────

    private void DrawStack(SKCanvas canvas, ZoneLayout layout)
    {
        var baseRect = CenterCardRect(layout);
        int depth    = Math.Min(layout.Zone.Count, 3);

        for (int i = depth - 1; i >= 1; i--)
            CardRenderer.DrawCardBack(canvas, OffsetRect(baseRect, i * 2f, i * -1.5f), _skin);

        var topCard = layout.Zone.TopCard;
        if (topCard is not null && layout.FaceUp)
            CardRenderer.DrawCardFace(canvas, baseRect, topCard, _skin);
        else
            CardRenderer.DrawCardBack(canvas, baseRect, _skin);

        if (topCard is not null && _recordCardRects)
            _cardRects.Add((topCard.Id, baseRect));
    }

    // ── Shuffle animation ─────────────────────────────────────────────────────

    private void DrawShufflingStack(SKCanvas canvas, ZoneLayout layout)
    {
        var sa       = _shuffleAnims[layout.Zone.Id];
        long now     = NowMs();
        float t      = Math.Clamp((now - sa.Start) / sa.Duration, 0f, 1f);
        var baseRect = CenterCardRect(layout);
        float maxSplit = layout.CardWidth * 0.65f;
        int half1 = layout.Zone.Count / 2;
        int half2 = layout.Zone.Count - half1;

        if (t < 0.35f)
        {
            // Split: two halves spread apart
            float split = maxSplit * EaseOutCubic(t / 0.35f);
            DrawHalfStack(canvas, baseRect, -split, half1);
            DrawHalfStack(canvas, baseRect,  split, half2);
        }
        else if (t < 0.78f)
        {
            // Merge: halves come back together
            float merge  = EaseInOutSine((t - 0.35f) / 0.43f);
            float split  = maxSplit * (1f - merge);
            DrawHalfStack(canvas, baseRect, -split, half1);
            DrawHalfStack(canvas, baseRect,  split, half2);
        }
        else
        {
            // Settle: combined stack bounces up slightly
            float settleT  = (t - 0.78f) / 0.22f;
            float bounceY  = -layout.CardHeight * 0.07f * MathF.Sin(MathF.PI * settleT);
            var   settled  = OffsetRect(baseRect, 0f, bounceY);
            int   depth    = Math.Min(layout.Zone.Count, 3);
            for (int i = depth - 1; i >= 1; i--)
                CardRenderer.DrawCardBack(canvas, OffsetRect(settled, i * 2f, i * -1.5f), _skin);
            CardRenderer.DrawCardBack(canvas, settled, _skin);
        }

        if (t >= 1f)
        {
            _finishedShuffleAnims.Add(layout.Zone.Id);
            _shuffleCompletion?.TrySetResult();
            _shuffleCompletion = null;
        }
    }

    private void DrawHalfStack(SKCanvas canvas, SKRect baseRect, float offsetX, int count)
    {
        if (count <= 0) return;
        var rect  = OffsetRect(baseRect, offsetX, 0f);
        int depth = Math.Min(count, 3);
        for (int i = depth - 1; i >= 1; i--)
            CardRenderer.DrawCardBack(canvas, OffsetRect(rect, i * 2f, i * -1.5f), _skin);
        CardRenderer.DrawCardBack(canvas, rect, _skin);
    }

    // ── Fan (hand) ────────────────────────────────────────────────────────────

    private void DrawFan(SKCanvas canvas, ZoneLayout layout)
    {
        var cards = layout.Zone.Cards;
        if (cards.Count == 0) return;

        float totalW = layout.Bounds.Width;
        float cardW  = layout.CardWidth;
        float cardH  = layout.CardHeight;
        float top    = layout.Bounds.MidY - cardH / 2f;
        long  now    = NowMs();

        // Draw pending cards (fly-in not yet started) face-down at their source
        // position so the deck appears to still hold them.
        foreach (var card in cards)
        {
            if (_flyInAnims.TryGetValue(card.Id, out var pending) && now < pending.Start)
            {
                var deckRect = new SKRect(pending.From.X - cardW / 2f, pending.From.Y - cardH / 2f,
                                          pending.From.X + cardW / 2f, pending.From.Y + cardH / 2f);
                CardRenderer.DrawCardBack(canvas, deckRect, _skin);
            }
        }

        // Fan layout uses ALL cards so each card's fly-in destination is its final
        // resting position, not an intermediate slot in a partially-filled fan.
        float step = cards.Count == 1
            ? 0f
            : MathF.Min((totalW - cardW) / (cards.Count - 1), cardW * 0.75f);

        float startX = cards.Count == 1
            ? layout.Bounds.MidX - cardW / 2f
            : layout.Bounds.Left + (totalW - (step * (cards.Count - 1) + cardW)) / 2f;

        for (int i = 0; i < cards.Count; i++)
        {
            var card     = cards[i];
            bool hasFlyIn = _flyInAnims.TryGetValue(card.Id, out var fia);

            // Pending cards were already drawn at their source — skip them here.
            if (hasFlyIn && now < fia.Start)
                continue;

            var rect = new SKRect(startX + i * step, top, startX + i * step + cardW, top + cardH);

            // ── Flip animation ────────────────────────────────────────────────
            if (_flipAnims.TryGetValue(card.Id, out var fa))
            {
                float t = Math.Clamp((now - fa.Start) / fa.Duration, 0f, 1f);
                DrawFlippingCard(canvas, rect, card, t);
                if (_recordCardRects)
                {
                    _cardRects.Add((card.Id, rect));
                    DrawCardInteractiveHint(canvas, rect, card.Id);
                }
                if (t >= 1f) _finishedFlipAnims.Add(card.Id);
                continue;
            }

            // ── Fly-in animation (card slides from source to fixed destination) ──
            if (hasFlyIn)
            {
                float t    = Math.Clamp((now - fia.Start) / fia.Duration, 0f, 1f);
                float ease = EaseOutCubic(t);
                // Use fia.To (fixed at queue time) — not rect.Mid (live slot).
                // This prevents the destination from shifting when cards are sorted
                // or added mid-animation, which previously caused cards to get stuck.
                float cx   = fia.From.X + (fia.To.X - fia.From.X) * ease;
                float cy   = fia.From.Y + (fia.To.Y - fia.From.Y) * ease;
                rect = new SKRect(cx - cardW / 2f, cy - cardH / 2f,
                                  cx + cardW / 2f, cy + cardH / 2f);
                if (t >= 1f) _finishedFlyInAnims.Add(card.Id);
            }

            // ── Receive animation (bump up then settle) ───────────────────────
            if (_receiveAnims.TryGetValue(card.Id, out var ra))
            {
                float t    = Math.Clamp((now - ra.Start) / ra.Duration, 0f, 1f);
                float bump = -cardH * 0.4f * MathF.Sin(MathF.PI * t);
                rect = OffsetRect(rect, 0f, bump);
                if (t >= 1f) _finishedReceiveAnims.Add(card.Id);
            }

            // ── Deal animation (slide up) ─────────────────────────────────────
            if (_dealAnims.TryGetValue(card.Id, out var da))
            {
                float t    = Math.Clamp((now - da.Start) / da.Duration, 0f, 1f);
                float ease = EaseOutCubic(t);
                rect = OffsetRect(rect, 0f, (1f - ease) * cardH * 0.65f);
                if (t >= 1f) _finishedDealAnims.Add(card.Id);
            }

            // ── Normal draw ───────────────────────────────────────────────────
            if (layout.FaceUp)
                CardRenderer.DrawCard(canvas, rect, card, _skin);
            else
                CardRenderer.DrawCardBack(canvas, rect, _skin);

            if (_recordCardRects)
            {
                _cardRects.Add((card.Id, rect));
                DrawCardInteractiveHint(canvas, rect, card.Id);
            }
        }
    }

    // ── Spread ────────────────────────────────────────────────────────────────

    private void DrawSpread(SKCanvas canvas, ZoneLayout layout)
    {
        var cards = layout.Zone.Cards;
        if (cards.Count == 0) return;

        float totalW = layout.Bounds.Width;
        float cardW  = MathF.Min(layout.CardWidth, totalW / cards.Count - 4f);
        float cardH  = cardW * 1.4f;
        float top    = layout.Bounds.MidY - cardH / 2f;
        float gap    = 4f;
        float total  = cards.Count * cardW + (cards.Count - 1) * gap;
        float startX = layout.Bounds.MidX - total / 2f;

        long now = NowMs();

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var rect = new SKRect(
                startX + i * (cardW + gap), top,
                startX + i * (cardW + gap) + cardW, top + cardH);

            // Deal animation (slide up)
            if (_dealAnims.TryGetValue(card.Id, out var da))
            {
                float t    = Math.Clamp((now - da.Start) / da.Duration, 0f, 1f);
                float ease = EaseOutCubic(t);
                rect = OffsetRect(rect, 0f, (1f - ease) * cardH * 0.65f);
                if (t >= 1f) _finishedDealAnims.Add(card.Id);
            }

            CardRenderer.DrawCard(canvas, rect, card, _skin);

            if (_recordCardRects)
            {
                _cardRects.Add((card.Id, rect));
                DrawCardInteractiveHint(canvas, rect, card.Id);
            }
        }
    }

    // ── Count-only ────────────────────────────────────────────────────────────

    private void DrawCountOnly(SKCanvas canvas, ZoneLayout layout)
    {
        var baseRect = CenterCardRect(layout);
        int depth    = Math.Min(layout.Zone.Count / 4, 5);

        for (int i = Math.Max(depth - 1, 0); i >= 1; i--)
            CardRenderer.DrawCardBack(canvas, OffsetRect(baseRect, i * 2f, i * -1.5f), _skin);

        if (layout.Zone.Count > 0)
            CardRenderer.DrawCardBack(canvas, baseRect, _skin);

        DrawCountBadge(canvas, baseRect, layout.Zone.Count);
    }

    private void DrawCountBadge(SKCanvas canvas, SKRect cardRect, int count)
    {
        float badgeSz = cardRect.Width * 0.38f;
        float bx = cardRect.Right  - badgeSz * 0.3f;
        float by = cardRect.Bottom - badgeSz * 0.3f;

        using var bgPaint   = new SKPaint { Color = new SKColor(0xC8, 0xA9, 0x6E), IsAntialias = true };
        using var textPaint = new SKPaint { Color = new SKColor(0x0D, 0x25, 0x18), IsAntialias = true };
        using var font      = new SKFont(SKTypeface.Default, badgeSz * 0.55f);

        canvas.DrawCircle(bx, by, badgeSz / 2f, bgPaint);
        string text = count.ToString();
        float  tw   = font.MeasureText(text);
        canvas.DrawText(text, bx - tw / 2f, by + badgeSz * 0.2f, font, textPaint);
    }

    // ── Flip animation ────────────────────────────────────────────────────────

    private void DrawFlippingCard(SKCanvas canvas, SKRect rect, Card card, float t)
    {
        // Ease in-out: slow at start and end, fast through the middle
        float ease   = EaseInOutSine(t);
        float scaleX = ease < 0.5f ? (1f - ease * 2f) : ((ease - 0.5f) * 2f);
        bool  face   = ease >= 0.5f;

        canvas.Save();
        if (scaleX > 0.005f)
            canvas.Scale(scaleX, 1f, rect.MidX, rect.MidY);
        if (face)
            CardRenderer.DrawCardFace(canvas, rect, card, _skin);
        else
            CardRenderer.DrawCardBack(canvas, rect, _skin);
        canvas.Restore();
    }

    // ── Interactive card hints ────────────────────────────────────────────────

    private void DrawCardInteractiveHint(SKCanvas canvas, SKRect rect, string cardId)
    {
        bool isSelected   = _selectedCardIds.Contains(cardId);
        bool isSelectable = !isSelected && _selectableCardIds.Contains(cardId);
        if (!isSelected && !isSelectable) return;

        float r        = rect.Width * 0.08f;
        var   inflated = new SKRect(rect.Left - 3f, rect.Top - 3f, rect.Right + 3f, rect.Bottom + 3f);

        if (isSelected)
        {
            using var glow = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 7f,
                Color       = new SKColor(0xFF, 0xD7, 0x00, 0x55),
                MaskFilter  = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f),
            };
            canvas.DrawRoundRect(inflated, r + 3f, r + 3f, glow);

            using var border = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 3.5f,
                Color       = new SKColor(0xFF, 0xD7, 0x00, 0xFF),
            };
            canvas.DrawRoundRect(inflated, r + 3f, r + 3f, border);
        }
        else
        {
            using var border = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 2f,
                Color       = new SKColor(0xFF, 0xD7, 0x00, 0xAA),
            };
            canvas.DrawRoundRect(inflated, r + 3f, r + 3f, border);
        }
    }

    // ── Drop zone highlight ───────────────────────────────────────────────────

    private static void DrawDropZoneHighlight(SKCanvas canvas, SKRect bounds)
    {
        float r        = bounds.Width * 0.06f;
        var   inflated = new SKRect(bounds.Left - 5f, bounds.Top - 5f, bounds.Right + 5f, bounds.Bottom + 5f);

        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style       = SKPaintStyle.Fill,
            Color       = new SKColor(0x00, 0xFF, 0x88, 0x28),
        };
        canvas.DrawRoundRect(inflated, r, r, fill);

        using var border = new SKPaint
        {
            IsAntialias = true,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            Color       = new SKColor(0x00, 0xFF, 0x88, 0xCC),
        };
        canvas.DrawRoundRect(inflated, r, r, border);
    }

    // ── Drag ghost ────────────────────────────────────────────────────────────

    private void DrawDragGhost(SKCanvas canvas)
    {
        var card = FindCardById(_dragCardId!);
        if (card is null) return;

        float ghostW = _lastLayouts.Count > 0 ? _lastLayouts[0].CardWidth * 1.2f : 60f;
        float ghostH = ghostW * 1.4f;
        var ghostRect = new SKRect(
            _dragCurrentPt.X - ghostW / 2f,
            _dragCurrentPt.Y - ghostH * 0.65f,
            _dragCurrentPt.X + ghostW / 2f,
            _dragCurrentPt.Y + ghostH * 0.35f);

        CardRenderer.DrawCard(canvas, ghostRect, card, _skin);
    }

    private Card? FindCardById(string cardId)
    {
        if (_state is null) return null;
        foreach (var zone in _state.Zones.Values)
            foreach (var card in zone.Cards)
                if (card.Id == cardId) return card;
        return null;
    }

    // ── Touch handling ────────────────────────────────────────────────────────

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        e.Handled = true;

        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _touchStartPt  = e.Location;
                _dragCurrentPt = e.Location;
                _isDragging    = false;
                _dragCardId    = HitTestSelectableCard(e.Location)
                              ?? HitTestAnyHandCard(e.Location);
                _dragSourceZoneId = _dragCardId is not null ? FindZoneOfCard(_dragCardId) : null;
                break;

            case SKTouchAction.Moved:
                if (_dragCardId is not null)
                {
                    _dragCurrentPt = e.Location;
                    if (!_isDragging)
                    {
                        float dx = e.Location.X - _touchStartPt.X;
                        float dy = e.Location.Y - _touchStartPt.Y;
                        if (MathF.Sqrt(dx * dx + dy * dy) > DragThreshold)
                            _isDragging = true;
                    }
                    if (_isDragging) InvalidateSurface();
                }
                break;

            case SKTouchAction.Released:
                if (_isDragging && _dragCardId is not null)
                {
                    var zoneId = HitTestZone(e.Location);
                    if (zoneId is not null && zoneId == _dragSourceZoneId && IsHandZoneId(zoneId))
                    {
                        // Reorder within the same hand zone
                        int newIndex = CalcFanInsertIndex(zoneId, e.Location.X, _dragCardId);
                        CardReorderedInHand?.Invoke(_dragCardId, newIndex);
                    }
                    else if (zoneId is not null && _selectableCardIds.Contains(_dragCardId))
                    {
                        CardDropped?.Invoke(_dragCardId, zoneId);
                    }
                }
                else
                {
                    var cardId = HitTestCard(e.Location);
                    if (cardId is not null)
                    {
                        // Toggle info tooltip on face-up cards (any card, not just selectable ones)
                        if (IsCardFaceUp(cardId))
                            _tooltipCardId = (_tooltipCardId == cardId) ? null : cardId;
                        else
                            _tooltipCardId = null;

                        CardTapped?.Invoke(cardId);
                    }
                    else
                    {
                        _tooltipCardId = null;  // dismiss tooltip on empty-space tap
                        var zoneId = HitTestZone(e.Location);
                        if (zoneId is not null)
                            ZoneTapped?.Invoke(zoneId);
                        else
                            CanvasTapped?.Invoke();
                    }
                }
                _isDragging       = false;
                _dragCardId       = null;
                _dragSourceZoneId = null;
                InvalidateSurface();
                break;

            case SKTouchAction.Cancelled:
                _isDragging       = false;
                _dragCardId       = null;
                _dragSourceZoneId = null;
                InvalidateSurface();
                break;
        }
    }

    // ── Hit testing ───────────────────────────────────────────────────────────

    private string? HitTestCard(SKPoint pt)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
        {
            var (id, rect) = _cardRects[i];
            if (rect.Contains(pt)) return id;
        }
        return null;
    }

    private string? HitTestSelectableCard(SKPoint pt)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
        {
            var (id, rect) = _cardRects[i];
            if (rect.Contains(pt) && _selectableCardIds.Contains(id)) return id;
        }
        return null;
    }

    private string? HitTestZone(SKPoint pt)
    {
        for (int i = _lastLayouts.Count - 1; i >= 0; i--)
        {
            var layout = _lastLayouts[i];
            if (layout.Bounds.Contains(pt)) return layout.Zone.Id;
        }
        return null;
    }

    // Hits any card inside a non-rotated fan (hand) zone — for drag-reorder.
    private string? HitTestAnyHandCard(SKPoint pt)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
        {
            var (id, rect) = _cardRects[i];
            if (!rect.Contains(pt)) continue;
            var zoneId = FindZoneOfCard(id);
            if (zoneId is not null && IsHandZoneId(zoneId)) return id;
        }
        return null;
    }

    private bool IsHandZoneId(string zoneId)
        => _lastLayouts.Any(l => l.Zone.Id == zoneId && l.Hint == ZoneRenderHint.Fan);

    private string? FindZoneOfCard(string cardId)
    {
        if (_state is null) return null;
        foreach (var (id, zone) in _state.Zones)
            if (zone.Cards.Any(c => c.Id == cardId))
                return id;
        return null;
    }

    // Calculates the insertion index when a card is dropped at dropX within a fan zone.
    private int CalcFanInsertIndex(string zoneId, float dropX, string dragCardId)
    {
        var layout = _lastLayouts.FirstOrDefault(l => l.Zone.Id == zoneId);
        if (layout is null) return 0;

        var cards = layout.Zone.Cards;
        int count = cards.Count;
        if (count <= 1) return 0;

        float totalW = layout.Bounds.Width;
        float cardW  = layout.CardWidth;
        float step   = MathF.Min((totalW - cardW) / (count - 1), cardW * 0.75f);
        float startX = layout.Bounds.Left + (totalW - (step * (count - 1) + cardW)) / 2f;

        // Find the slot whose center is nearest to the drop X
        int   best     = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            float dist = MathF.Abs(dropX - (startX + i * step + cardW / 2f));
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    // ── Labels and highlights ─────────────────────────────────────────────────

    private void DrawLabel(SKCanvas canvas, ZoneLayout layout)
    {
        float labelSz = layout.CardWidth * 0.18f;
        float y       = layout.Bounds.Bottom + labelSz * 1.4f;

        using var paint = new SKPaint { Color = _theme.PlayerNameColor, IsAntialias = true };
        using var font  = new SKFont(SKTypeface.Default, labelSz);
        float w = font.MeasureText(layout.Label!);
        canvas.DrawText(layout.Label!, layout.Bounds.MidX - w / 2f, y, font, paint);
    }

    private void DrawHighlight(SKCanvas canvas, SKRect bounds)
    {
        float r        = bounds.Width * 0.08f;
        var   inflated = new SKRect(bounds.Left - 4f, bounds.Top - 4f, bounds.Right + 4f, bounds.Bottom + 4f);
        using var paint = new SKPaint
        {
            Color       = _theme.CurrentPlayerHighlight,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            IsAntialias = true,
        };
        canvas.DrawRoundRect(inflated, r + 4f, r + 4f, paint);
    }

    // ── Trick direction indicator ─────────────────────────────────────────────

    /// <summary>
    /// Draws a circular arrow (↻ or ↺) next to the trick leader's card zone to show
    /// the direction of play.  Only active when per-player trick zones are in use and
    /// trick_direction / trick_leader metadata are present.
    /// </summary>
    private void DrawTrickDirectionIndicator(SKCanvas canvas, IReadOnlyList<ZoneLayout> layouts)
    {
        if (_state is null) return;
        if (!_state.Metadata.TryGetValue("trick_direction", out var direction)) return;
        if (!_state.Metadata.TryGetValue("trick_leader",    out var leaderId))  return;

        // Find the leader's per-player trick zone layout
        var trickLayout = layouts.FirstOrDefault(l =>
            l.Zone.Type == "trick" && l.Zone.OwnerId == leaderId);
        if (trickLayout is null) return;

        // Screen Y-axis is down, so the seating order P0(bottom)→P1(right)→P2(top)→P3(left)
        // traces a counter-clockwise arc on screen even though it is "clockwise" by card-game
        // convention.  Invert so the icon matches what the player sees visually.
        bool clockwise = string.Equals(direction, "counter_clockwise", StringComparison.OrdinalIgnoreCase);
        float size = trickLayout.CardWidth * 0.42f;

        // Place icon at upper-right corner of the trick zone, slightly outside the bounds
        float cx = trickLayout.Bounds.Right  + size * 0.55f;
        float cy = trickLayout.Bounds.Top    - size * 0.55f;

        DrawCircularArrow(canvas, cx, cy, size, clockwise);
    }

    /// <summary>
    /// Draws a semi-transparent dark background circle and a ↻/↺ arc-plus-arrowhead icon.
    /// CW version has its arrowhead at the upper-right of the arc; CCW is a mirror image.
    /// </summary>
    private static void DrawCircularArrow(SKCanvas canvas, float cx, float cy, float size, bool clockwise)
    {
        float arcR    = size * 0.52f;   // radius of the circular arc
        float strokeW = size * 0.19f;

        // Semi-transparent background circle
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };
        canvas.DrawCircle(cx, cy, size * 0.72f, bgPaint);

        using var arcPaint = new SKPaint
        {
            Color       = new SKColor(255, 255, 255, 220),
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = strokeW,
            IsAntialias = true,
            StrokeCap   = SKStrokeCap.Round,
        };

        // CW:  arc from 30° (lower-right) sweeping +300° CW → ends at 330° (upper-right).
        // CCW: arc from 150° (lower-left) sweeping -300° CCW → ends at 210° (upper-left).
        // The two are mirror images around the vertical axis.
        float startAngle = clockwise ? 30f  : 150f;
        float sweepAngle = clockwise ? 300f : -300f;
        float endAngle   = startAngle + sweepAngle;   // 330° CW, 210° (= -150°) CCW

        var oval = new SKRect(cx - arcR, cy - arcR, cx + arcR, cy + arcR);
        using var arcPath = new SKPath();
        arcPath.AddArc(oval, startAngle, sweepAngle);
        canvas.DrawPath(arcPath, arcPaint);

        // Arrowhead at end of arc
        float endRad = endAngle * MathF.PI / 180f;
        float tipX   = cx + arcR * MathF.Cos(endRad);
        float tipY   = cy + arcR * MathF.Sin(endRad);

        // Tangent direction at tip (direction of travel along the arc)
        float tangentDeg = clockwise ? endAngle + 90f : endAngle - 90f;
        float tangentRad = tangentDeg * MathF.PI / 180f;

        // Wing lines point backward from tip (±140° from tangent = 40° open V)
        float arrowLen  = size * 0.28f;
        float wing1 = tangentRad + 2.44f;  // ~+140°
        float wing2 = tangentRad - 2.44f;  // ~-140°

        using var arrowPaint = new SKPaint
        {
            Color       = new SKColor(255, 255, 255, 220),
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = strokeW,
            IsAntialias = true,
            StrokeCap   = SKStrokeCap.Round,
        };
        canvas.DrawLine(tipX, tipY,
            tipX + arrowLen * MathF.Cos(wing1), tipY + arrowLen * MathF.Sin(wing1), arrowPaint);
        canvas.DrawLine(tipX, tipY,
            tipX + arrowLen * MathF.Cos(wing2), tipY + arrowLen * MathF.Sin(wing2), arrowPaint);
    }

    // ── Card info tooltip ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the card with the given ID was drawn face-up in the last paint.
    /// Only non-rotated zone layouts record card rects, so rotated opponent hands are
    /// naturally excluded.
    /// </summary>
    private bool IsCardFaceUp(string cardId)
    {
        foreach (var layout in _lastLayouts)
        {
            if (!layout.FaceUp) continue;
            if (layout.Zone.Cards.Any(c => c.Id == cardId)) return true;
        }
        return false;
    }

    /// <summary>
    /// Draws a chat-bubble tooltip showing the display name of the tapped card.
    /// The bubble appears above the card (or below if near the top edge) with a
    /// triangular tail pointing at the card.
    /// </summary>
    private void DrawCardTooltip(SKCanvas canvas, SKImageInfo info)
    {
        if (_tooltipCardId is null || _state is null) return;

        // Find the card's last-drawn screen rect
        var entry = _cardRects.LastOrDefault(r => r.CardId == _tooltipCardId);
        if (entry.CardId is null) return;

        // Find the card for its display name
        Card? card = _state.Zones.Values
            .SelectMany(z => z.Cards)
            .FirstOrDefault(c => c.Id == _tooltipCardId);
        if (card is null) return;

        string text     = card.DisplayName;
        float  cardW    = entry.Rect.Width;
        float  fontSize = Math.Clamp(cardW * 0.26f, 13f, 22f);

        using var font  = new SKFont(SKTypeface.Default, fontSize);
        float textW     = font.MeasureText(text);

        float padX    = fontSize * 0.85f;
        float padY    = fontSize * 0.60f;
        float tailH   = fontSize * 0.55f;
        float tailW   = fontSize * 0.80f;
        float bubbleW = textW + padX * 2f;
        float bubbleH = fontSize + padY * 2f;
        float cornerR = bubbleH / 2f;   // full pill shape

        // Prefer showing above the card; fall back to below if too close to top edge
        bool aboveCard = (entry.Rect.Top - tailH - bubbleH - 4f) > 8f;

        float bubbleTop = aboveCard
            ? entry.Rect.Top  - tailH - bubbleH - 4f
            : entry.Rect.Bottom + tailH + 4f;

        // Centre bubble on the card, clamped to screen
        float midX = Math.Clamp(entry.Rect.MidX,
            bubbleW / 2f + 8f,
            info.Width - bubbleW / 2f - 8f);

        float bubbleLeft   = midX - bubbleW / 2f;
        float bubbleRight  = midX + bubbleW / 2f;
        float bubbleBottom = bubbleTop + bubbleH;
        var   bubbleRect   = new SKRect(bubbleLeft, bubbleTop, bubbleRight, bubbleBottom);

        // Tail horizontal anchor: card centre, clamped inside the bubble's curved area
        float tailCX = Math.Clamp(entry.Rect.MidX, bubbleLeft + cornerR, bubbleRight - cornerR);

        // ── Shadow ────────────────────────────────────────────────────────────
        using var shadowPaint = new SKPaint
        {
            Color      = new SKColor(0, 0, 0, 70),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f),
        };
        canvas.DrawRoundRect(
            new SKRect(bubbleLeft + 3f, bubbleTop + 3f, bubbleRight + 3f, bubbleBottom + 3f),
            cornerR, cornerR, shadowPaint);

        // ── Bubble + tail fill ────────────────────────────────────────────────
        using var fillPaint = new SKPaint
        {
            Color       = new SKColor(0xF8, 0xF2, 0xE4),   // warm cream
            IsAntialias = true,
        };

        // Draw tail first so the bubble paints over its base and hides the seam
        using var tailPath = new SKPath();
        if (aboveCard)
        {
            tailPath.MoveTo(tailCX - tailW / 2f, bubbleBottom - 1f);
            tailPath.LineTo(tailCX + tailW / 2f, bubbleBottom - 1f);
            tailPath.LineTo(tailCX,               bubbleBottom + tailH);
        }
        else
        {
            tailPath.MoveTo(tailCX - tailW / 2f, bubbleTop + 1f);
            tailPath.LineTo(tailCX + tailW / 2f, bubbleTop + 1f);
            tailPath.LineTo(tailCX,               bubbleTop - tailH);
        }
        tailPath.Close();
        canvas.DrawPath(tailPath, fillPaint);
        canvas.DrawRoundRect(bubbleRect, cornerR, cornerR, fillPaint);

        // ── Border ────────────────────────────────────────────────────────────
        using var borderPaint = new SKPaint
        {
            Color       = new SKColor(0xC8, 0xA9, 0x6E, 0xBB),
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };
        canvas.DrawRoundRect(bubbleRect, cornerR, cornerR, borderPaint);

        // ── Text ──────────────────────────────────────────────────────────────
        using var textPaint = new SKPaint { Color = new SKColor(0x1C, 0x1C, 0x1C), IsAntialias = true };
        canvas.DrawText(text,
            midX - textW / 2f,
            bubbleTop + padY + fontSize * 0.82f,
            font, textPaint);
    }

    // ── Felt background ───────────────────────────────────────────────────────

    private void DrawFelt(SKCanvas canvas, SKImageInfo info)
    {
        using var feltPaint = new SKPaint { Color = _theme.FeltColor, IsAntialias = true };
        canvas.DrawRect(0, 0, info.Width, info.Height, feltPaint);

        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(info.Width / 2f, info.Height / 2f),
            MathF.Max(info.Width, info.Height) * 0.7f,
            [_theme.FeltColor, _theme.FeltEdgeColor],
            null,
            SKShaderTileMode.Clamp);
        using var vignette = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(0, 0, info.Width, info.Height, vignette);
    }

    // ── Placeholder ───────────────────────────────────────────────────────────

    private static void DrawPlaceholder(SKCanvas canvas, SKImageInfo info, string text)
    {
        using var paint = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x35), IsAntialias = true };
        using var font  = new SKFont(SKTypeface.Default, 20f);
        float w = font.MeasureText(text);
        canvas.DrawText(text, (info.Width - w) / 2f, info.Height / 2f, font, paint);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static SKRect CenterCardRect(ZoneLayout layout)
    {
        float hw = layout.CardWidth  / 2f;
        float hh = layout.CardHeight / 2f;
        return new SKRect(layout.Bounds.MidX - hw, layout.Bounds.MidY - hh,
                          layout.Bounds.MidX + hw, layout.Bounds.MidY + hh);
    }

    private static SKRect OffsetRect(SKRect r, float dx, float dy)
        => new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

    private static long NowMs() => Environment.TickCount64;

    private static float EaseOutCubic(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u;
    }

    private static float EaseInOutSine(float t)
        => -(MathF.Cos(MathF.PI * t) - 1f) / 2f;
}
