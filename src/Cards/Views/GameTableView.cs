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
    private IReadOnlyList<string> _dropZoneIds       = [];

    private string? _dragCardId;
    private string? _dragSourceZoneId;
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

    // Scratch lists — populated during draw, cleared after
    private readonly List<string> _finishedDealAnims    = [];
    private readonly List<string> _finishedFlipAnims    = [];
    private readonly List<string> _finishedReceiveAnims = [];

    private IDispatcherTimer? _animTimer;

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

            _state = value;

            if (value is not null)
            {
                var newIds = value.Zones.Values
                    .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet();
                var newFaceDown = value.Zones.Values
                    .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Id).ToHashSet();
                var newHandIds = value.Zones.Values
                    .Where(z => z.Type == "hand")
                    .SelectMany(z => z.Cards).Select(c => c.Id).ToHashSet();

                // Cards that just appeared → slide-up deal animation
                foreach (var id in newIds.Except(oldCardIds))
                    _dealAnims[id] = (now, 240f);

                // Cards that flipped face-up → scale-X flip animation
                foreach (var id in oldFaceDown.Except(newFaceDown))
                    if (newIds.Contains(id))
                        _flipAnims[id] = (now, 360f);

                // Cards that moved INTO a hand zone (received mid-game) → bump animation
                var justFlipped = oldFaceDown.Except(newFaceDown).ToHashSet();
                foreach (var id in newHandIds.Except(oldHandIds).Intersect(oldCardIds))
                    if (!justFlipped.Contains(id) && !_dealAnims.ContainsKey(id))
                        _receiveAnims[id] = (now, 1350f);
            }

            if (_dealAnims.Count > 0 || _flipAnims.Count > 0 || _receiveAnims.Count > 0)
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
        set { _selectedCardId = value; InvalidateSurface(); }
    }

    public IReadOnlyList<string> DropZoneIds
    {
        get => _dropZoneIds;
        set { _dropZoneIds = value; InvalidateSurface(); }
    }

    public void SetSkin(ICardSkin skin)     { _skin  = skin;  InvalidateSurface(); }
    public void SetTheme(ITableTheme theme) { _theme = theme; InvalidateSurface(); }

    /// <summary>
    /// Explicitly marks cards as having just arrived in a hand zone.
    /// Call this before assigning GameState when the same state object was mutated in-place,
    /// since the setter can't detect movements within the same object reference.
    /// </summary>
    public void MarkCardsReceivedInHand(IEnumerable<string> cardIds)
    {
        long now = NowMs();
        foreach (var id in cardIds)
            _receiveAnims[id] = (now, 1350f);
        if (_receiveAnims.Count > 0)
            EnsureAnimTimer();
    }

    // ── Animation timer ───────────────────────────────────────────────────────

    private void EnsureAnimTimer()
    {
        if (_animTimer?.IsRunning == true) return;

        _animTimer ??= Application.Current!.Dispatcher.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 fps
        _animTimer.Tick += (_, _) =>
        {
            InvalidateSurface();
            if (_dealAnims.Count == 0 && _flipAnims.Count == 0 && _receiveAnims.Count == 0)
                _animTimer.Stop();
        };
        _animTimer.Start();
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info   = e.Info;

        canvas.Clear();
        DrawFelt(canvas, info);

        if (_state is null)
        {
            if (!string.IsNullOrEmpty(_placeholderText))
                DrawPlaceholder(canvas, info, _placeholderText);
            return;
        }

        _cardRects.Clear();
        var layouts = ZoneLayoutEngine.Compute(_state, info);
        _lastLayouts = layouts;

        foreach (var layout in layouts)
        {
            DrawZone(canvas, layout);
            if (_dropZoneIds.Contains(layout.Zone.Id))
                DrawDropZoneHighlight(canvas, layout.Bounds);
        }

        // Clean up finished animations after all zones are drawn
        foreach (var id in _finishedDealAnims)    _dealAnims.Remove(id);
        foreach (var id in _finishedFlipAnims)    _flipAnims.Remove(id);
        foreach (var id in _finishedReceiveAnims) _receiveAnims.Remove(id);
        _finishedDealAnims.Clear();
        _finishedFlipAnims.Clear();
        _finishedReceiveAnims.Clear();

        if (_isDragging && _dragCardId is not null)
            DrawDragGhost(canvas);
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
            case ZoneRenderHint.Stack:     DrawStack(canvas, layout);     break;
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

    // ── Fan (hand) ────────────────────────────────────────────────────────────

    private void DrawFan(SKCanvas canvas, ZoneLayout layout)
    {
        var cards = layout.Zone.Cards;
        if (cards.Count == 0) return;

        float totalW = layout.Bounds.Width;
        float cardW  = layout.CardWidth;
        float cardH  = layout.CardHeight;
        float top    = layout.Bounds.MidY - cardH / 2f;

        float step = cards.Count == 1
            ? 0f
            : MathF.Min((totalW - cardW) / (cards.Count - 1), cardW * 0.75f);

        float startX = cards.Count == 1
            ? layout.Bounds.MidX - cardW / 2f
            : layout.Bounds.Left + (totalW - (step * (cards.Count - 1) + cardW)) / 2f;

        long now = NowMs();

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
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
        bool isSelected   = cardId == _selectedCardId;
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
                        CardTapped?.Invoke(cardId);
                    else
                    {
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
