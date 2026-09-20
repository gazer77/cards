using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

/// <summary>
/// Draws a card table onto a Skia canvas and turns pointer input into table events.
///
/// Framework-neutral by design: it owns no control, no timer and no dispatcher. A host
/// supplies a surface (<see cref="Paint"/>), forwards pointer input
/// (<see cref="OnPointerDown"/> and friends), handles <see cref="RedrawRequested"/> by
/// invalidating its surface, and supplies an <see cref="IAnimationDriver"/> for frames.
/// That is the entire contract, and it is what lets the MAUI app and the browser share
/// this file rather than maintaining two renderers.
/// </summary>
public sealed class CardTableRenderer
{
    private ICardSkin   _skin  = new DefaultCardSkin();
    private ITableTheme _theme = new DefaultTableTheme();
    private GameState?  _state;
    private string      _placeholderText = string.Empty;

    // ── Interaction state ─────────────────────────────────────────────────────

    private IReadOnlyList<string> _selectableCardIds = [];
    private string?               _selectedCardId;
    private readonly HashSet<string> _selectedCardIds = [];
    private IReadOnlyList<string> _dropZoneIds       = [];

    private string? _dragCardId;
    private string? _dragSourceZoneId;
    private string? _tooltipCardId;
    private SKPoint _touchStartPt;
    private SKPoint _dragCurrentPt;
    private bool    _isDragging;
    private bool    _recordCardRects;
    private const float DragThreshold = 14f;

    private readonly List<(int Uid, string CardId, SKRect Rect)> _cardRects = [];
    private IReadOnlyList<ZoneLayout> _lastLayouts = [];

    // ── Animation state ───────────────────────────────────────────────────────

    // Per-card animations: cardId → (startTimeMs, durationMs)
    private readonly Dictionary<int, (long Start, float Duration)> _dealAnims    = [];
    private readonly Dictionary<int, (long Start, float Duration)> _flipAnims    = [];
    private readonly Dictionary<int, (long Start, float Duration)> _receiveAnims = [];
    // Fly-in: cardId → (source center, fixed destination center, startTimeMs, durationMs)
    // Both From and To are fixed at queue time — To is never recomputed at draw time.
    private readonly Dictionary<int, (SKPoint From, SKPoint To, long Start, float Duration)> _flyInAnims = [];

    private SKImageInfo _lastInfo;
    private const float FlyInSpeedPxMs = 1.2f; // pixels per millisecond
    // Shuffle: zoneId → (startTimeMs, durationMs)
    private readonly Dictionary<string, (long Start, float Duration)> _shuffleAnims = [];
    private TaskCompletionSource? _shuffleCompletion;
    private TaskCompletionSource? _flyInCompletion;

    // Scratch lists — populated during draw, cleared after
    private readonly List<int> _finishedDealAnims    = [];
    private readonly List<int> _finishedFlipAnims    = [];
    private readonly List<int> _finishedReceiveAnims = [];
    private readonly List<int> _finishedFlyInAnims   = [];
    private readonly List<string> _finishedShuffleAnims = [];

    private TaskCompletionSource? _nextPaintCompletion;

    // ── Speech bubbles ────────────────────────────────────────────────────────

    private readonly SpeechBubbles _bubbles = new();

    /// <summary>
    /// Shows a message beside a player's seat for a few seconds.
    ///
    /// Anchored to the seat rather than shown in a bar, because the status line
    /// describes one player's action and a shared bar leaves the reader working out
    /// whose. A seat that acts again before its bubble fades replaces it.
    /// </summary>
    public void PostMessage(string playerId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _bubbles.Post(playerId, text, NowMs());
        _driver.RequestFrames();   // bubbles fade, so they need frames to fade in
    }

    /// <summary>Removes every bubble — used when a new game replaces the table.</summary>
    public void ClearMessages()
    {
        _bubbles.Clear();
        RequestRedraw();
    }

    /// <summary>
    /// Counts every card on the table and how many ranks are short of a full four.
    ///
    /// A hand is drawn as overlapping slices once it grows, so counting cards from the
    /// screen is guesswork — a 28-card hand and a 16-card one look far more alike than
    /// they are. This settles "have cards gone missing?" without anyone having to
    /// squint at a screenshot.
    /// </summary>
    private string BuildCardCensus()
    {
        if (_state is null) return "cards —";

        var all = _state.Zones.Values.SelectMany(z => z.Cards).ToList();

        int deck  = _state.Zones.Values.Where(z => z.Type == "deck").Sum(z => z.Count);
        int hands = _state.Zones.Values.Where(z => z.Type == "hand").Sum(z => z.Count);
        int rest  = all.Count - deck - hands;

        // Jokers are deliberately excluded: they have no rank group to be short of.
        int shortRanks = all
            .Where(c => c.Rank != Rank.Joker)
            .GroupBy(c => c.Rank)
            .Count(g => g.Count() % 4 != 0);

        string ranks = shortRanks > 0 ? $"  ranks!={shortRanks}" : "";
        return $"cards {all.Count} (deck {deck}, hands {hands}, other {rest}){ranks}";
    }

    /// <summary>
    /// Where a player's bubble should point. Prefers the seat's hand, which is where
    /// a player "is" on the table; falls back to any zone they own.
    /// </summary>
    private SKRect? AnchorForPlayer(string playerId)
    {
        var hand = _lastLayouts.FirstOrDefault(
            l => l.Zone.OwnerId == playerId && l.Zone.Type == "hand");

        // The cards, not the band: a hand pinned to the band's bottom edge sits well
        // above the band's centre, and a bubble aimed at the band pointed at felt.
        if (hand is not null && hand.Hint == ZoneRenderHint.Fan && FanExtent(hand) is { } cards)
            return cards;

        var zone = hand ?? _lastLayouts.FirstOrDefault(l => l.Zone.OwnerId == playerId);
        return zone?.Bounds;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private readonly RenderDiagnostics        _diagnostics = new();
    private readonly System.Diagnostics.Stopwatch _paintClock  = new();

    /// <summary>
    /// Draws a frame-timing overlay on the table. Off by default.
    ///
    /// While on, the frame loop is held open so the numbers keep updating — which
    /// itself costs frames. It is a diagnostic, not a status bar.
    /// </summary>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Live frame timings. Only meaningful while <see cref="ShowDiagnostics"/> is on.</summary>
    public RenderDiagnostics Diagnostics => _diagnostics;

    /// <summary>
    /// Draws every card face-up, including opponents' hands and face-down piles.
    ///
    /// A debugging aid: most card-game bugs are about which cards are where, and that
    /// is exactly what the game is designed to hide. Off by default and gated behind a
    /// configuration flag, because it removes the point of the game — a player who
    /// turned this on by accident would see their opponent's hand.
    /// </summary>
    public bool RevealAllCards { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>A card was tapped: its description id, and the uid of the physical card hit.</summary>
    public event Action<string, int>?    CardTapped;
    public event Action<string>?         ZoneTapped;

    /// <summary>
    /// A zone was double-tapped — "do this zone's obvious thing": draw from the deck,
    /// claim the discard pile, discard the selection onto it.
    ///
    /// Separate from <see cref="ZoneTapped"/> because these acts are irreversible and a
    /// stray tap while reading the table should not spend a turn. It fires even when a
    /// card was hit, since the top card of a pile covers the zone it belongs to — which
    /// is why tapping the deck used to do nothing whatsoever.
    /// </summary>
    public event Action<string>?         ZoneActivated;

    public event Action?                 CanvasTapped;
    public event Action<string, string>? CardDropped;
    /// <summary>Card was dragged to a new position within its own hand zone.</summary>
    public event Action<string, int>?    CardReorderedInHand;

    // ── Constructor ───────────────────────────────────────────────────────────

    private readonly IAnimationDriver _driver;

    public CardTableRenderer(IAnimationDriver driver)
    {
        _driver = driver;
        _driver.Tick += OnAnimTimerTick;
    }

    /// <summary>
    /// Raised when the table needs repainting. The host answers by invalidating its
    /// surface, which eventually calls back into <see cref="Paint"/>.
    /// </summary>
    public event Action? RedrawRequested;

    private void RequestRedraw() => RedrawRequested?.Invoke();

    // ── Public API ────────────────────────────────────────────────────────────

    public GameState? GameState
    {
        get => _state;
        set
        {
            long now = NowMs();
            _wildRanks = null;   // a different game may have different wilds

            // Collect card states before the swap
            var oldUids = _state?.Zones.Values
                .SelectMany(z => z.Cards).Select(c => c.Uid).ToHashSet() ?? [];
            var oldFaceDownUids = _state?.Zones.Values
                .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Uid).ToHashSet() ?? [];
            var oldHandUids = _state?.Zones.Values
                .Where(z => z.Type == "hand")
                .SelectMany(z => z.Cards).Select(c => c.Uid).ToHashSet() ?? [];

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

                var newUids = value.Zones.Values
                    .SelectMany(z => z.Cards).Select(c => c.Uid).ToHashSet();
                var newFaceDownUids = value.Zones.Values
                    .SelectMany(z => z.Cards).Where(c => !c.IsFaceUp).Select(c => c.Uid).ToHashSet();
                var newHandUids = value.Zones.Values
                    .Where(z => z.Type == "hand")
                    .SelectMany(z => z.Cards).Select(c => c.Uid).ToHashSet();

                // Cards that just appeared in a fan/spread zone → slide-up deal animation.
                // Deliberately excludes deck/pile zones: DrawStack never processes _dealAnims,
                // so entries for those cards would accumulate forever and block timer shutdown.
                // Skip if a fly-in is already queued for this card (e.g. deal fly-in set before GameState).
                var fanSpreadUids = value.Zones.Values
                    .Where(z => z.Type is "hand" or "spread" or "trick")
                    .SelectMany(z => z.Cards).Select(c => c.Uid).ToHashSet();
                foreach (var id in newUids.Except(oldUids))
                    if (!_flyInAnims.ContainsKey(id) && fanSpreadUids.Contains(id))
                        _dealAnims[id] = (now, 240f);

                // Cards that flipped face-up → scale-X flip animation.
                // Skip cards with a pending fly-in: they are arriving for the first
                // time (initial deal) and haven't landed yet.  Showing a flip at the
                // destination before the card has even flown there looks wrong and can
                // briefly expose face values in zones that should render face-down.
                foreach (var id in oldFaceDownUids.Except(newFaceDownUids))
                    if (newUids.Contains(id) && !_flyInAnims.ContainsKey(id))
                        _flipAnims[id] = (now, 360f);

                // Cards that moved INTO a hand zone (received mid-game) → bump animation.
                // Skip cards that already have a fly-in queued: the fly-in IS the arrival
                // animation, so adding a receive bump on top produces erratic combined motion.
                var justFlipped = oldFaceDownUids.Except(newFaceDownUids).ToHashSet();
                foreach (var id in newHandUids.Except(oldHandUids).Intersect(oldUids))
                    if (!justFlipped.Contains(id) && !_dealAnims.ContainsKey(id)
                                                  && !_flyInAnims.ContainsKey(id))
                        _receiveAnims[id] = (now, 1350f);
            }

            if (_dealAnims.Count > 0 || _flipAnims.Count > 0 ||
                _receiveAnims.Count > 0 || _flyInAnims.Count > 0 || _shuffleAnims.Count > 0)
                EnsureAnimTimer();
            else
                RequestRedraw();
        }
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set { _placeholderText = value; RequestRedraw(); }
    }

    public IReadOnlyList<string> SelectableCardIds
    {
        get => _selectableCardIds;
        set { _selectableCardIds = value; _allSelectable = null; RequestRedraw(); }
    }

    public string? SelectedCardId
    {
        get => _selectedCardId;
        set
        {
            _selectedCardId = value;
            _selectedCardIds.Clear();
            _selectedUids.Clear();

            // A token is a uid (one physical card) or, from older callers, a card id.
            // Ids are why selecting one 4♥ used to light every 4♥ on the table,
            // the opponent's included.
            foreach (var token in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out int uid)) _selectedUids.Add(uid);
                else _selectedCardIds.Add(token);
            }
            RequestRedraw();
        }
    }

    private readonly HashSet<int> _selectedUids = [];

    private bool IsSelected(int uid, string cardId)
        => _selectedUids.Contains(uid) || _selectedCardIds.Contains(cardId);

    /// <summary>
    /// Card uid → meld index for a multi-meld selection. Selected cards borrow their
    /// meld's colour so a combined lay reads as the separate melds it will become.
    /// </summary>
    public IReadOnlyDictionary<int, int>? SelectedMeldGroups
    {
        get => _selectedMeldGroups;
        set { _selectedMeldGroups = value; RequestRedraw(); }
    }

    private IReadOnlyDictionary<int, int>? _selectedMeldGroups;

    /// <summary>
    /// One colour per meld in a combined lay, repeating past six. Picked to stay apart
    /// from each other, from the gold single-meld glow, and from the green felt.
    /// </summary>
    private static readonly SKColor[] MeldGroupColors =
    [
        new(0xFF, 0xD7, 0x00),   // gold — the familiar selection colour leads
        new(0x4F, 0xC3, 0xF7),   // sky blue
        new(0xFF, 0x8A, 0x65),   // coral
        new(0xBA, 0x68, 0xC8),   // orchid
        new(0xAE, 0xD5, 0x81),   // light green
        new(0xF0, 0x62, 0x92),   // pink
    ];

    public IReadOnlyList<string> DropZoneIds
    {
        get => _dropZoneIds;
        set { _dropZoneIds = value; RequestRedraw(); }
    }

    public void SetSkin(ICardSkin skin)     { _skin  = skin;  RequestRedraw(); }
    public void SetTheme(ITableTheme theme) { _theme = theme; RequestRedraw(); }

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
        IReadOnlyList<(int Uid, SKPoint From, SKPoint To)> entries,
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
            _dealAnims.Remove(id);  // suppress slide-up when a proper fly-in is queued
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
    public Dictionary<int, SKPoint> ComputeHandSlotCenters(
        GameState state, IEnumerable<int> cardUids)
    {
        if (_lastInfo.Width == 0) return [];
        return ComputeFanSlotCenters(state, cardUids, _lastInfo);
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
    private static Dictionary<int, SKPoint> ComputeFanSlotCenters(
        GameState state, IEnumerable<int> cardUids, SKImageInfo info)
    {
        var result = new Dictionary<int, SKPoint>();
        var idSet  = cardUids.ToHashSet();
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
                if (idSet.Contains(cards[i].Uid))
                    result[cards[i].Uid] = new SKPoint(startX + i * step + cardW / 2f, midY);
            }
        }
        return result;
    }

    /// <summary>
    /// The last rendered screen rect for one physical card, or null if it was not drawn.
    ///
    /// Keyed on uid, not on rank and suit: in a multi-deck game several cards answer to
    /// the same description, and animating from "a five of hearts" picks whichever was
    /// drawn last.
    /// </summary>
    public SKRect? GetLastCardRect(int uid)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
            if (_cardRects[i].Uid == uid) return _cardRects[i].Rect;
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
    /// Returns a Task that completes on the next <see cref="Paint"/> call.
    /// Use after setting <see cref="GameState"/> to guarantee <see cref="_lastLayouts"/>
    /// has been populated with the new state before reading zone positions.
    /// </summary>
    public Task WaitForNextPaintAsync()
    {
        _nextPaintCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Force a repaint in case the view system hasn't scheduled one yet.
        RequestRedraw();
        return _nextPaintCompletion.Task;
    }

    // ── Animation timer ───────────────────────────────────────────────────────

    private void EnsureAnimTimer() => _driver.RequestFrames();

    /// <summary>
    /// Drops animations whose time has passed.
    ///
    /// Each animation is normally retired by the code that draws it, on the frame it
    /// finishes — which only happens if that card is drawn at all. A card that lands
    /// somewhere its zone type does not process (or that is not drawn for any other
    /// reason) leaves an entry behind forever, and a single stale entry keeps the frame
    /// loop running for the rest of the session: the table never goes idle, repainting
    /// at full cost over a game where nothing is moving.
    ///
    /// Animations are driven by wall-clock time, so an entry past its duration has no
    /// effect on what is drawn. Removing it is free, and makes the loop's exit
    /// condition depend on elapsed time rather than on every card being reached.
    /// </summary>
    private void SweepExpiredAnimations()
    {
        long now = NowMs();

        Sweep(_dealAnims);
        Sweep(_flipAnims);
        Sweep(_receiveAnims);
        Sweep(_shuffleAnims);

        // Fly-ins are excluded: they are awaited by the turn loop, which is told they
        // are done through WaitForFlyInsAsync. Dropping one here would leave that wait
        // hanging until its safety timeout.

        void Sweep<TKey>(Dictionary<TKey, (long Start, float Duration)> anims) where TKey : notnull
        {
            if (anims.Count == 0) return;

            foreach (var key in anims
                         .Where(kv => now - kv.Value.Start > kv.Value.Duration + 250)
                         .Select(kv => kv.Key)
                         .ToList())
                anims.Remove(key);
        }
    }

    private void OnAnimTimerTick()
    {
        RequestRedraw();
        SweepExpiredAnimations();

        // Bubbles fade on the same clock as the animations, so they have to hold the
        // loop open too — otherwise a message posted on a quiet table paints once and
        // then sits there forever, never fading.
        _bubbles.Expire(NowMs());

        if
        (
            !_dealAnims.Any() &&
            !_flipAnims.Any() &&
            !_receiveAnims.Any() &&
            !_flyInAnims.Any() &&
            !_shuffleAnims.Any() &&
            !_bubbles.Any
        )
        {
            _driver.StopFrames();
            // Queue one final repaint after stopping so the last clean frame
            // is guaranteed to be painted even if the platform coalesces or
            // drops the RequestRedraw() call above.
            RequestRedraw();
        }
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Paints the whole table. The host calls this from its surface-paint callback.
    /// </summary>
    public void Paint(SKCanvas canvas, SKImageInfo info)
    {
        if (ShowDiagnostics)
        {
            _diagnostics.BeginFrame(NowMs());
            _paintClock.Restart();
        }

        PaintTable(canvas, info);

        if (!ShowDiagnostics) return;

        _paintClock.Stop();
        _diagnostics.EndFrame(_paintClock.Elapsed.TotalMilliseconds);
        _diagnostics.Info     = info;
        _diagnostics.FlyIns   = _flyInAnims.Count;
        _diagnostics.Deals    = _dealAnims.Count;
        _diagnostics.Flips    = _flipAnims.Count;
        _diagnostics.Receives = _receiveAnims.Count;
        _diagnostics.Shuffles = _shuffleAnims.Count;
        _diagnostics.CardCensus = BuildCardCensus();

        DrawDiagnosticsOverlay(canvas, info);

        // The overlay is only truthful if it keeps updating, so hold the frame loop
        // open while it is showing — otherwise it freezes at the last animated frame
        // and reports a frame rate that stopped being real.
        _driver.RequestFrames();
    }

    private void PaintTable(SKCanvas canvas, SKImageInfo info)
    {
        _lastInfo = info;
        // No Clear: the felt is opaque and covers the whole canvas, so clearing first
        // is a full-canvas write that every following pixel overwrites.
        DrawFelt(canvas, info);

        if (_state is null)
        {
            if (!string.IsNullOrEmpty(_placeholderText))
                DrawPlaceholder(canvas, info, _placeholderText);
            return;
        }

        // Remove fly-in entries for cards that are no longer in ANY zone at all
        // (e.g. card removed from game entirely).  We no longer restrict to hand
        // zones here because DrawFlyingCards handles non-hand fly-ins as an overlay.
        if (_flyInAnims.Count > 0)
        {
            var allUids = _state.Zones.Values
                .SelectMany(z => z.Cards)
                .Select(c => c.Uid)
                .ToHashSet();
            foreach (var orphanId in _flyInAnims.Keys.Where(id => !allUids.Contains(id)).ToList())
                _flyInAnims.Remove(orphanId);
        }

        _cardRects.Clear();
        _allSelectable = null;
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

        DrawFlyingCards(canvas);

        if (_isDragging && _dragCardId is not null)
            DrawDragGhost(canvas);

        // Bubbles sit above the table but below the tooltip, which is a direct response
        // to a touch and must never be covered by an incidental message.
        if (_bubbles.Any)
            _bubbles.Draw(canvas, info, NowMs(), AnchorForPlayer);

        DrawCardTooltip(canvas, info);
    }

    // ── Zone rendering ────────────────────────────────────────────────────────

    private void DrawZone(SKCanvas canvas, ZoneLayout layout)
    {
        bool rotated = layout.RotationDegrees != 0f;
        _recordCardRects = !rotated;
        _seatTurns       = layout.SeatQuarterTurns;

        if (rotated)
        {
            canvas.Save();
            canvas.RotateDegrees(layout.RotationDegrees, layout.Bounds.MidX, layout.Bounds.MidY);
        }

        // The seat to act is lit from behind its cards, not boxed. A stroked outline
        // around the ZONE traced a rectangle the cards did not fill and cut across
        // whatever labels lay in its path; it also read as an alert rather than a turn.
        // Drawn first so the cards sit on the light.
        if (layout.IsCurrentPlayer && layout.Hint == ZoneRenderHint.Fan)
            DrawTurnGlow(canvas, layout);

        // A by-rank zone is never "empty": its slots are the picture, melds or not.
        if (layout.Zone.Definition?.GroupLayout == "by_rank")
            DrawRankSlots(canvas, layout);
        else if (layout.Zone.IsEmpty || layout.Hint == ZoneRenderHint.Empty)
            DrawEmptyZone(canvas, layout);
        else
            DrawFilledZone(canvas, layout);

        if (layout.Label is not null) DrawLabel(canvas, layout);

        // Badges on a zone with no groups count the zone itself — the deck's cards
        // left, a pile's depth. They were drawn only per group, so a definition could
        // declare them on the deck, validate cleanly, and see nothing: accepted and
        // ignored, which is the one outcome the validator exists to prevent.
        // Keyed on the zone's kind, not on whether it happens to hold groups yet: a meld
        // zone before anyone has melded has no groups either, and must not report
        // "0 cards" about itself.
        if (layout.Zone.Type is "deck" or "pile" or "hand"
            && layout.Zone.Definition?.GroupBadges is { Count: > 0 })
            DrawGroupBadges(canvas, layout.Zone, ZoneCardsRect(layout), layout.Zone.Count, layout.CardWidth);

        if (rotated) canvas.Restore();
    }

    /// <summary>The rectangle a zone's cards occupy, for anchoring things beside them.</summary>
    private SKRect ZoneCardsRect(ZoneLayout layout)
        => layout.Hint == ZoneRenderHint.Fan && FanExtent(layout) is { } fan
            ? fan
            : CenterCardRect(layout);

    /// <summary>
    /// A soft pool of light under the cards of the seat to act — wider than the fan
    /// and faded at the edges, so it reads as light on the felt rather than a shape.
    /// </summary>
    private void DrawTurnGlow(SKCanvas canvas, ZoneLayout layout)
    {
        if (FanExtent(layout) is not { } cards) return;

        float padX = cards.Height * 0.35f;
        float padY = cards.Height * 0.25f;
        var   pool = new SKRect(cards.Left - padX, cards.Top - padY, cards.Right + padX, cards.Bottom + padY);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color       = _theme.CurrentPlayerHighlight.WithAlpha(0x38),
            MaskFilter  = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, cards.Height * 0.22f),
        };
        canvas.DrawRoundRect(pool, cards.Height * 0.3f, cards.Height * 0.3f, paint);
    }

    private void DrawEmptyZone(SKCanvas canvas, ZoneLayout layout)
        => CardRenderer.DrawEmptySlot(canvas, CenterCardRect(layout), _theme);   // the label, if any, is drawn with the zone

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
        long now     = NowMs();

        // The pile's face is the topmost card that has actually LANDED. The zone gains
        // its card the instant the move applies, but the fly-in is still en route — so
        // drawing the true top showed the discard on the pile while its animation was
        // also carrying it there, and the card appeared to arrive twice.
        var cards = layout.Zone.Cards;
        Card? topCard = null;
        int landed = 0;
        for (int i = cards.Count - 1; i >= 0; i--)
        {
            if (IsInFlight(cards[i].Uid, now)) continue;
            topCard ??= cards[i];
            landed++;
        }

        int depth = Math.Min(landed, 3);
        for (int i = depth - 1; i >= 1; i--)
            DrawCardBackCounted(canvas, OffsetRect(baseRect, i * 2f, i * -1.5f), _skin);

        if (topCard is not null)
            DrawCardForZone(canvas, baseRect, topCard, layout.FaceUp);
        else if (cards.Count == 0)
            DrawCardBackCounted(canvas, baseRect, _skin);
        // Every card in flight: leave the spot empty for them to land on.

        if (topCard is not null && _recordCardRects)
            _cardRects.Add((topCard.Uid, topCard.Id, baseRect));
    }

    /// <summary>Whether this card's fly-in is pending or still moving.</summary>
    private bool IsInFlight(int uid, long now)
        => _flyInAnims.TryGetValue(uid, out var f)
           && (now < f.Start || (now - f.Start) / f.Duration < 1f);

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
                DrawCardBackCounted(canvas, OffsetRect(settled, i * 2f, i * -1.5f), _skin);
            DrawCardBackCounted(canvas, settled, _skin);
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
            DrawCardBackCounted(canvas, OffsetRect(rect, i * 2f, i * -1.5f), _skin);
        DrawCardBackCounted(canvas, rect, _skin);
    }

    // ── Fan (hand) ────────────────────────────────────────────────────────────

    /// <summary>Where a fan's cards sit: the geometry the cards and anything drawn behind them share.</summary>
    private static (float StartX, float Step, float Top, float CardW, float CardH) FanGeometry(ZoneLayout layout)
    {
        int   count  = layout.Zone.Cards.Count;
        float totalW = layout.Bounds.Width;
        float cardW  = layout.CardWidth;
        float cardH  = layout.CardHeight;

        // Centred in the band, but never hanging past its bottom edge. The player's
        // band sits at the screen edge, and a card clipped there has no visible bottom
        // — so the selection lift read as the card growing taller instead of rising.
        float top = MathF.Min(layout.Bounds.MidY - cardH / 2f, layout.Bounds.Bottom - cardH);

        // Fan layout uses ALL cards so each card's fly-in destination is its final
        // resting position, not an intermediate slot in a partially-filled fan.
        float step = count <= 1
            ? 0f
            : MathF.Min((totalW - cardW) / (count - 1), cardW * 0.75f);

        float startX = count <= 1
            ? layout.Bounds.MidX - cardW / 2f
            : layout.Bounds.Left + (totalW - (step * (count - 1) + cardW)) / 2f;

        return (startX, step, top, cardW, cardH);
    }

    /// <summary>The rectangle a fan's cards actually cover, or null for an empty fan.</summary>
    private static SKRect? FanExtent(ZoneLayout layout)
    {
        int count = layout.Zone.Cards.Count;
        if (count == 0) return null;
        var (startX, step, top, cardW, cardH) = FanGeometry(layout);
        return new SKRect(startX, top, startX + step * (count - 1) + cardW, top + cardH);
    }

    private void DrawFan(SKCanvas canvas, ZoneLayout layout)
    {
        var cards = layout.Zone.Cards;
        if (cards.Count == 0) return;

        var (startX, step, top, cardW, cardH) = FanGeometry(layout);
        long now = NowMs();

        // Draw pending cards (fly-in not yet started) face-down at their source
        // position so the deck appears to still hold them.
        foreach (var card in cards)
        {
            if (_flyInAnims.TryGetValue(card.Uid, out var pending) && now < pending.Start)
            {
                var deckRect = new SKRect(pending.From.X - cardW / 2f, pending.From.Y - cardH / 2f,
                                          pending.From.X + cardW / 2f, pending.From.Y + cardH / 2f);
                DrawCardBackCounted(canvas, deckRect, _skin);
            }
        }

        for (int i = 0; i < cards.Count; i++)
        {
            var card     = cards[i];
            bool hasFlyIn = _flyInAnims.TryGetValue(card.Uid, out var fia);

            // Pending cards were already drawn at their source — skip them here.
            if (hasFlyIn && now < fia.Start)
                continue;

            var rect = new SKRect(startX + i * step, top, startX + i * step + cardW, top + cardH);

            // ── Flip animation ────────────────────────────────────────────────
            if (_flipAnims.TryGetValue(card.Uid, out var fa))
            {
                float t = Math.Clamp((now - fa.Start) / fa.Duration, 0f, 1f);
                DrawFlippingCard(canvas, rect, card, t);
                if (_recordCardRects)
                {
                    _cardRects.Add((card.Uid, card.Id, rect));
                    if (layout.FaceUp)
                        DrawCardInteractiveHint(canvas, rect, card.Id, card.Uid);
                }
                if (t >= 1f) _finishedFlipAnims.Add(card.Uid);
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
                if (t >= 1f) _finishedFlyInAnims.Add(card.Uid);
            }

            // ── Receive animation (bump up then settle) ───────────────────────
            if (_receiveAnims.TryGetValue(card.Uid, out var ra))
            {
                float t    = Math.Clamp((now - ra.Start) / ra.Duration, 0f, 1f);
                float bump = -cardH * 0.4f * MathF.Sin(MathF.PI * t);
                rect = OffsetRect(rect, 0f, bump);
                if (t >= 1f) _finishedReceiveAnims.Add(card.Uid);
            }

            // ── Deal animation (slide up) ─────────────────────────────────────
            if (_dealAnims.TryGetValue(card.Uid, out var da))
            {
                float t    = Math.Clamp((now - da.Start) / da.Duration, 0f, 1f);
                float ease = EaseOutCubic(t);
                rect = OffsetRect(rect, 0f, (1f - ease) * cardH * 0.65f);
                if (t >= 1f) _finishedDealAnims.Add(card.Uid);
            }

            // ── Selection lift ────────────────────────────────────────────────
            // Picked cards stand out of the fan, which in a heavily overlapped hand
            // says far more than an outline can: the raised edge is visible even where
            // the next card covers everything but a sliver.
            if (IsSelected(card.Uid, card.Id))
                rect = OffsetRect(rect, 0f, -cardH * SelectionLift);

            // ── Normal draw ───────────────────────────────────────────────────
            DrawCardForZone(canvas, rect, card, layout.FaceUp);

            if (_recordCardRects)
            {
                // The lifted rect, so a tap lands where the card actually is.
                _cardRects.Add((card.Uid, card.Id, rect));
                if (layout.FaceUp)
                    DrawCardInteractiveHint(canvas, rect, card.Id, card.Uid);
            }
        }
    }

    // ── Spread ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fraction of a card left showing under the next one in a compact run — enough to
    /// read the index and count the buried cards.
    /// </summary>
    private const float CompactOverlap = 0.28f;

    /// <summary>
    /// How far a selected card rises out of its fan, as a fraction of card height —
    /// about the height of the suit pip in the corner, which is enough to read as
    /// deliberate without breaking the line of the hand.
    /// </summary>
    private const float SelectionLift = 0.12f;

    /// <summary>
    /// The zone's declared arrangement, or the default for its shape. The declaration is
    /// a preference, not a promise: full degrades to compact when the cards will not
    /// fit, because a fifteen-card meld at full width is a broken table.
    /// </summary>
    private static string ArrangementFor(Zone zone)
        => zone.Arrangement ?? (zone.HasGroups ? "compact" : "full");

    private void DrawSpread(SKCanvas canvas, ZoneLayout layout)
    {
        if (layout.Zone.HasGroups)
        {
            DrawGroupedSpread(canvas, layout);
            return;
        }

        var cards = layout.Zone.Cards;
        if (cards.Count == 0) return;

        DrawCardRun(canvas, cards, layout.Bounds, layout.CardWidth, ArrangementFor(layout.Zone));
    }

    /// <summary>
    /// A spread that knows its melds draws each one separately — the canasta look — so
    /// two melds read as two melds and not one long row of cards. The arrangement
    /// applies to each meld, not to the zone as one row.
    /// </summary>
    private void DrawGroupedSpread(SKCanvas canvas, ZoneLayout layout)
    {
        var zone = layout.Zone;

        if (zone.Definition?.GroupLayout == "by_rank")
        {
            DrawRankSlots(canvas, layout);
            return;
        }

        if (zone.Groups.Count == 0) return;

        string arrangement = ArrangementFor(zone);
        const float groupGap = 10f;

        float cardW = layout.CardWidth;
        float WidthOf(int count, float w) => arrangement switch
        {
            "stack" => w,
            "full"  => w * count + 4f * (count - 1),
            _       => w * (1f + (count - 1) * CompactOverlap),
        };

        float TotalAt(string arr, float w)
        {
            float sum = 0f;
            foreach (var g in zone.Groups)
                sum += arr switch
                {
                    "stack" => w,
                    "full"  => w * g.Count + 4f * (g.Count - 1),
                    _       => w * (1f + (g.Count - 1) * CompactOverlap),
                };
            return sum + groupGap * (zone.Groups.Count - 1);
        }

        // Full that will not fit on one row becomes compact before anything shrinks.
        if (arrangement == "full" && TotalAt("full", cardW) > layout.Bounds.Width)
            arrangement = "compact";

        // Melds wrap onto as many rows as the zone's height allows. Squeezing every
        // meld a side owns into one row is what made them unreadable by mid-round —
        // and a side can end a round with a dozen of them.
        float cardH    = cardW * 1.4f;
        float rowGap   = 6f;
        // Room a caption above or below each row will take, counted before the row
        // count is fixed so captions never push the last row out of the zone.
        bool  captioned = zone.GroupLabel is { Placement: "top" or "bottom" } gl0 && LabelApplies(gl0);
        float rowH     = cardH + (captioned ? cardW * 0.16f * 1.6f : 0f);
        int   maxRows  = Math.Max(1, (int)((layout.Bounds.Height + rowGap) / (rowH + rowGap)));

        var rows = WrapIntoRows(zone, cardW, WidthOf, groupGap, layout.Bounds.Width, maxRows);

        // Still too wide on the last allowed row: shrink so everything fits.
        float widest = rows.Max(r => r.Width);
        if (widest > layout.Bounds.Width && widest > 0f)
        {
            float shrink = layout.Bounds.Width / widest;
            cardW *= shrink;
            cardH  = cardW * 1.4f;
            foreach (var row in rows) row.Width *= shrink;
        }

        // A caption per meld, when declared. It names the rank a wild on top would hide.
        var caption   = zone.GroupLabel is { } gl && LabelApplies(gl) ? gl : null;
        float capSz   = cardW * 0.16f;
        float capRoom = caption is null ? 0f
                      : caption.Placement is "top" or "bottom" ? capSz * 1.6f : 0f;
        rowGap += capRoom;

        float blockH = rows.Count * cardH + (rows.Count - 1) * rowGap
                     + (caption?.Placement == "top" ? capRoom : 0f)
                     + (caption?.Placement == "bottom" ? capRoom : 0f);
        float y      = layout.Bounds.MidY - blockH / 2f
                     + (caption?.Placement == "top" ? capRoom : 0f);

        var wilds = MeldRules.WildRanks(_state?.Definition);

        foreach (var row in rows)
        {
            float x = layout.Bounds.MidX - row.Width / 2f;
            foreach (var meld in row.Melds)
            {
                float w    = WidthOf(meld.Count, cardW);
                var   rect = new SKRect(x, y, x + w, y + cardH);
                DrawCardRun(canvas, meld, rect, cardW, arrangement);

                if (caption is not null && meld.Any(c => !MeldRules.IsWild(c, wilds)))
                {
                    string rank = MeldRules.RankDisplayName(MeldRules.MeldRankOf(meld, wilds));
                    DrawPlacedLabel(canvas, rect,
                        FillLabel(caption.Text, zone, rank, meld.Count),
                        capSz, caption);
                }

                DrawGroupBadges(canvas, zone, rect, meld.Count, cardW);

                x += w + groupGap;
            }
            y += cardH + rowGap;
        }
    }

    /// <summary>
    /// Every rank in the deck gets a fixed slot, in deck order, with a wild slot last
    /// when the game has wilds. A rank with no meld yet shows as its name in the slot,
    /// so "which melds does this side not have" is read at a glance — the way a
    /// physical table lays canastas out by rank. Slots wrap onto rows as the zone's
    /// width allows; the arrangement still governs how each slot's cards sit.
    /// </summary>
    private void DrawRankSlots(SKCanvas canvas, ZoneLayout layout)
    {
        var zone = layout.Zone;
        if (_state is null) return;

        var wilds = MeldRules.WildRanks(_state.Definition);

        // The strip is the definition's: which slots, in what order, holding what,
        // saying what while empty. by_rank is the shorthand for one of each.
        var slotDefs = ZoneSlots.For(zone, _state);
        int slots    = slotDefs.Count;
        if (slots == 0) return;

        // Which group sits in which slot — the same resolver intake files cards by.
        var bySlot = new Dictionary<int, IReadOnlyList<Card>>();
        for (int g = 0; g < zone.Groups.Count; g++)
        {
            var meld = zone.GroupCards(g);
            if (meld.Count == 0) continue;
            int slot = ZoneSlots.SlotOf(slotDefs, meld, wilds);
            if (slot >= 0 && !bySlot.ContainsKey(slot)) bySlot[slot] = meld;
        }

        string arrangement = ArrangementFor(zone);
        const float slotGap = 6f;

        // Badge and caption room per row, as in the flowing layout.
        var   caption = zone.GroupLabel is { } gl && LabelApplies(gl) ? gl : null;
        bool  capTB   = caption?.Placement is "top" or "bottom";
        bool  badgeTB = zone.Definition!.GroupBadges.Any(b => b.Placement is "top" or "bottom");

        // Fit as many slots per row as the width allows at the layout's card width,
        // shrinking only when even one row of them will not fit.
        float cardW  = layout.CardWidth;
        int   perRow = Math.Max(1, (int)((layout.Bounds.Width + slotGap) / (cardW + slotGap)));
        int   rows   = (slots + perRow - 1) / perRow;

        float cardH  = cardW * 1.4f;
        float extra  = (capTB ? cardW * 0.16f * 1.6f : 0f) + (badgeTB ? cardW * 0.15f * 1.8f : 0f);
        float rowH   = cardH + extra;
        float rowGap = 8f;

        float blockH = rows * rowH + (rows - 1) * rowGap;
        if (blockH > layout.Bounds.Height)
        {
            float shrink = layout.Bounds.Height / blockH;
            cardW *= shrink; cardH *= shrink; extra *= shrink; rowH = cardH + extra;
            perRow = Math.Max(1, (int)((layout.Bounds.Width + slotGap) / (cardW + slotGap)));
            rows   = (slots + perRow - 1) / perRow;
            blockH = rows * rowH + (rows - 1) * rowGap;
        }

        float y0 = layout.Bounds.MidY - blockH / 2f
                 + (caption?.Placement == "top" ? cardW * 0.16f * 1.6f : 0f);

        using var rankPaint = new SKPaint { Color = _theme.ZoneLabelColor, IsAntialias = true };
        using var rankFont  = new SKFont(SKTypeface.Default, cardW * 0.42f);

        for (int s = 0; s < slots; s++)
        {
            int   row  = s / perRow;
            int   col  = s % perRow;
            int   inRow = Math.Min(perRow, slots - row * perRow);
            float rowW = inRow * cardW + (inRow - 1) * slotGap;
            float x    = layout.Bounds.MidX - rowW / 2f + col * (cardW + slotGap);
            float y    = y0 + row * (rowH + rowGap);
            var   rect = new SKRect(x, y, x + cardW, y + cardH);

            string name = slotDefs[s].Label ?? "";

            if (bySlot.TryGetValue(s, out var meld))
            {
                DrawCardRun(canvas, meld, rect, cardW, arrangement);
                if (caption is not null)
                    DrawPlacedLabel(canvas, rect,
                        FillLabel(caption.Text, zone, slotDefs[s].Label ?? "", meld.Count),
                        cardW * 0.16f, caption);
                DrawGroupBadges(canvas, zone, rect, meld.Count, cardW);
            }
            else
            {
                // An empty slot names its rank, faintly: a promise of where the meld
                // will go, not a card.
                if (name.Length > 0)
                {
                    if (slotDefs[s].LabelPlace is { } lp)
                    {
                        // Positioned like any other label: in the slot's proportions,
                        // turned to the seat.
                        var place = TurnPlace(lp);
                        var box   = ResolvePlace(place, rect, rankFont.MeasureText(name) + cardW * 0.2f, cardW * 0.5f);
                        DrawTextInBox(canvas, name, box, rankFont, rankPaint, cardW * 0.42f,
                                      place.TextAlign, place.VerticalAlign, inset: cardW * 0.1f);
                    }
                    else
                    {
                        float tw = rankFont.MeasureText(name);
                        canvas.DrawText(name, rect.MidX - tw / 2f, rect.MidY + cardW * 0.15f, rankFont, rankPaint);
                    }
                }
                DrawGroupBadges(canvas, zone, rect, 0, cardW);
            }
        }
    }

    private sealed class MeldRow
    {
        public readonly List<IReadOnlyList<Card>> Melds = [];
        public float Width;
    }

    /// <summary>
    /// Packs melds into rows, breaking when the next one would overrun. The last
    /// allowed row takes whatever is left, so nothing is ever dropped — a meld that
    /// does not fit is drawn smaller, never not at all.
    /// </summary>
    private static List<MeldRow> WrapIntoRows(
        Zone zone, float cardW, Func<int, float, float> widthOf,
        float groupGap, float available, int maxRows)
    {
        var rows = new List<MeldRow> { new() };

        for (int g = 0; g < zone.Groups.Count; g++)
        {
            var meld = zone.GroupCards(g);
            float w  = widthOf(meld.Count, cardW);
            var row  = rows[^1];

            float withIt = row.Melds.Count == 0 ? w : row.Width + groupGap + w;
            if (row.Melds.Count > 0 && withIt > available && rows.Count < maxRows)
            {
                row = new MeldRow();
                rows.Add(row);
                withIt = w;
            }

            row.Melds.Add(meld);
            row.Width = withIt;
        }

        return rows;
    }

    /// <summary>
    /// One run of cards in the given arrangement: "full" side by side, "compact"
    /// overlapped, "stack" top card only with a count badge — a pile of six aces
    /// showing one must still say six.
    /// </summary>
    private void DrawCardRun(
        SKCanvas canvas, IReadOnlyList<Card> cards, SKRect bounds, float maxCardW, string arrangement)
    {
        if (cards.Count == 0) return;
        long now = NowMs();

        if (arrangement == "stack")
        {
            float w    = MathF.Min(maxCardW, bounds.Width);
            float h    = w * 1.4f;
            var   rect = new SKRect(bounds.MidX - w / 2f, bounds.MidY - h / 2f,
                                    bounds.MidX + w / 2f, bounds.MidY + h / 2f);
            DrawSpreadCard(canvas, cards[^1], rect, h, now);
            if (cards.Count > 1)
                DrawCountBadge(canvas, rect, cards.Count);
            return;
        }

        float gap     = arrangement == "full" ? 4f : 0f;
        float advance = arrangement == "full" ? maxCardW + gap : maxCardW * CompactOverlap;

        float cardW = maxCardW;
        float total = cardW + (cards.Count - 1) * advance;
        if (arrangement == "full" && total > bounds.Width)
        {
            // Preference, not promise: a full row that will not fit overlaps instead.
            advance = maxCardW * CompactOverlap;
            total   = cardW + (cards.Count - 1) * advance;
        }
        if (total > bounds.Width && total > 0f)
        {
            float scale = bounds.Width / total;
            cardW   *= scale;
            advance *= scale;
        }

        float cardH  = cardW * 1.4f;
        float top    = bounds.MidY - cardH / 2f;
        float startX = bounds.MidX - (cardW + (cards.Count - 1) * advance) / 2f;

        for (int i = 0; i < cards.Count; i++)
        {
            float left = startX + i * advance;
            DrawSpreadCard(canvas, cards[i], new SKRect(left, top, left + cardW, top + cardH), cardH, now);
        }
    }

    private void DrawSpreadCard(SKCanvas canvas, Card card, SKRect rect, float cardH, long now)
    {
        // Cards with an active fly-in are drawn by DrawFlyingCards overlay — skip here.
        if (_flyInAnims.TryGetValue(card.Uid, out var fia))
        {
            float ft = Math.Clamp((now - fia.Start) / fia.Duration, 0f, 1f);
            if (ft < 1f) return;
        }

        // Deal animation (slide up)
        if (_dealAnims.TryGetValue(card.Uid, out var da))
        {
            float t    = Math.Clamp((now - da.Start) / da.Duration, 0f, 1f);
            float ease = EaseOutCubic(t);
            rect = OffsetRect(rect, 0f, (1f - ease) * cardH * 0.65f);
            if (t >= 1f) _finishedDealAnims.Add(card.Uid);
        }

        DrawCardCounted(canvas, rect, card, _skin);

        if (_recordCardRects)
        {
            _cardRects.Add((card.Uid, card.Id, rect));
            DrawCardInteractiveHint(canvas, rect, card.Id, card.Uid);
        }
    }

    // ── Flying cards overlay ──────────────────────────────────────────────────

    /// <summary>
    /// Draws all cards currently in flight to non-hand destinations (trick zones,
    /// discard piles, won-tricks, etc.) at their interpolated screen positions.
    /// Hand-zone fly-ins are handled by <see cref="DrawFan"/> instead.
    /// </summary>
    private void DrawFlyingCards(SKCanvas canvas)
    {
        if (_flyInAnims.Count == 0 || _state is null) return;

        float cardW = _lastLayouts.Count > 0 ? _lastLayouts[0].CardWidth  : 50f;
        float cardH = _lastLayouts.Count > 0 ? _lastLayouts[0].CardHeight : 70f;
        long  now   = NowMs();

        foreach (var (uid, fia) in _flyInAnims)
        {
            // Hand-zone cards draw their own fly-ins inside DrawFan
            if (IsCardInHandZone(uid)) continue;

            if (now < fia.Start) continue;  // not yet started

            float t = Math.Clamp((now - fia.Start) / fia.Duration, 0f, 1f);
            if (t >= 1f) { _finishedFlyInAnims.Add(uid); continue; }

            float ease = EaseOutCubic(t);
            float cx   = fia.From.X + (fia.To.X - fia.From.X) * ease;
            float cy   = fia.From.Y + (fia.To.Y - fia.From.Y) * ease;
            var   rect = new SKRect(cx - cardW / 2f, cy - cardH / 2f,
                                    cx + cardW / 2f, cy + cardH / 2f);

            var card = FindCardByUid(uid);
            if (card is not null)
                DrawCardCounted(canvas, rect, card, _skin);
            else
                DrawCardBackCounted(canvas, rect, _skin);
        }
    }

    private bool IsCardInHandZone(int uid)
    {
        if (_state is null) return false;
        foreach (var zone in _state.Zones.Values)
            if (zone.Type == "hand" && zone.Cards.Any(c => c.Uid == uid))
                return true;
        return false;
    }

    // ── Count-only ────────────────────────────────────────────────────────────

    private void DrawCountOnly(SKCanvas canvas, ZoneLayout layout)
    {
        var baseRect = CenterCardRect(layout);
        int depth    = Math.Min(layout.Zone.Count / 4, 5);

        for (int i = Math.Max(depth - 1, 0); i >= 1; i--)
            DrawCardBackCounted(canvas, OffsetRect(baseRect, i * 2f, i * -1.5f), _skin);

        if (layout.Zone.Count > 0)
            DrawCardBackCounted(canvas, baseRect, _skin);

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
            DrawCardBackCounted(canvas, rect, _skin);
        canvas.Restore();
    }

    // ── Interactive card hints ────────────────────────────────────────────────

    /// <summary>
    /// Whether every card on the table is selectable, in which case marking them says
    /// nothing. During a discard every card in hand qualifies, so the whole hand lit up
    /// — twenty outlines conveying exactly as much as none. The selection highlight is
    /// unaffected; that one is always worth drawing.
    /// </summary>
    private bool EverythingIsSelectable()
    {
        if (_state is null || _selectableCardIds.Count == 0) return false;

        _allSelectable ??= _state.Zones.Values
            .Where(z => z.Type == "hand")
            .All(z => z.Cards.All(c => _selectableCardIds.Contains(c.Id)));

        return _allSelectable.Value;
    }

    private bool? _allSelectable;

    /// <summary>
    /// Quarter turns to the seat the zone being drawn faces. Placements and places are
    /// declared relative to the cards as their owner sees them, and turned by this on
    /// the way to the screen — so "bottom" is toward the player at every seat.
    /// </summary>
    private int _seatTurns;

    private string TurnSide(string side)
    {
        string[] ring = ["bottom", "left", "top", "right"];   // clockwise from the player
        int i = Array.IndexOf(ring, side);
        return i < 0 ? side : ring[(i + _seatTurns) % 4];
    }

    /// <summary>Turns a card-relative place the way the seat is turned. Percent maths only.</summary>
    private Cards.Models.PlaceDefinition TurnPlace(Cards.Models.PlaceDefinition p)
    {
        if (_seatTurns == 0) return p;
        float x = Percent(p.X, 0.5f), y = Percent(p.Y, 0.5f);
        string? w = p.Width, h = p.Height;
        string anchor = p.Anchor;
        for (int t = 0; t < _seatTurns; t++)
        {
            // One quarter turn clockwise about the card centre.
            (x, y) = (1f - y, x);
            (w, h) = (h, w);
            anchor = TurnAnchor(anchor);
        }
        return new Cards.Models.PlaceDefinition
        {
            X = Pct(x), Y = Pct(y), Anchor = anchor, Width = w, Height = h,
            TextAlign = p.TextAlign, VerticalAlign = p.VerticalAlign,
        };
    }

    private static string TurnAnchor(string anchor) => anchor switch
    {
        "top-left" => "top-right", "top" => "right", "top-right" => "bottom-right",
        "right" => "bottom", "bottom-right" => "bottom-left", "bottom" => "left",
        "bottom-left" => "top-left", "left" => "top", _ => anchor,
    };

    private static string Pct(float f)
        => (f * 100f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private void DrawCardInteractiveHint(SKCanvas canvas, SKRect rect, string cardId, int uid = -1)
    {
        bool isSelected   = IsSelected(uid, cardId);
        bool isSelectable = !isSelected && _selectableCardIds.Contains(cardId)
                                        && !EverythingIsSelectable();
        if (!isSelected && !isSelectable) return;

        // Drawn just inside the card rather than around it. Outlining an inflated rect
        // put a rounded border into the sliver of every card behind it in a fan, which
        // read as a row of little curls hooking over each card near the suit.
        float r        = rect.Width * 0.08f;
        var   inflated = new SKRect(rect.Left + 1.5f, rect.Top + 1.5f,
                                    rect.Right - 1.5f, rect.Bottom - 1.5f);

        if (isSelected)
        {
            var color = new SKColor(0xFF, 0xD7, 0x00);
            if (_selectedMeldGroups is { Count: > 0 } groups
                && groups.TryGetValue(uid, out int meld))
                color = MeldGroupColors[meld % MeldGroupColors.Length];

            using var glow = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 7f,
                Color       = color.WithAlpha(0x55),
                MaskFilter  = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f),
            };
            canvas.DrawRoundRect(inflated, r + 3f, r + 3f, glow);

            using var border = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 3.5f,
                Color       = color,
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

        // Show the card the same way the source zone was rendering it —
        // zone-level FaceUp overrides the per-card flag (e.g. hand zones
        // show all cards face-up even when IsFaceUp is false).
        bool faceUp = IsCardFaceUp(_dragCardId!);
        if (faceUp)
            CardRenderer.DrawCardFace(canvas, ghostRect, card, _skin);
        else
            DrawCardBackCounted(canvas, ghostRect, _skin);
    }

    private Card? FindCardByUid(int uid)
    {
        if (_state is null) return null;
        foreach (var zone in _state.Zones.Values)
            foreach (var card in zone.Cards)
                if (card.Uid == uid) return card;
        return null;
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

    /// <summary>
    /// Pointer pressed. <paramref name="location"/> must be in canvas pixels, not CSS
    /// pixels — a browser host has to scale by the device pixel ratio before calling in,
    /// or every hit test lands in the wrong place on a high-DPI screen.
    /// </summary>
    // Double-tap window and how far the second tap may wander — generous enough for a
    // finger on a phone, tight enough that two deliberate taps on different cards are
    // still two taps.
    private const long  DoubleTapMs   = 400;
    private const float DoubleTapSlop = 28f;

    private long    _lastTapMs;
    private SKPoint _lastTapPoint;

    public void OnPointerDown(SKPoint location)
    {
        _touchStartPt  = location;
        _dragCurrentPt = location;
        _isDragging    = false;
        _dragCardId    = HitTestSelectableCard(location)
                      ?? HitTestAnyHandCard(location);
        _dragSourceZoneId = _dragCardId is not null ? FindZoneOfCard(_dragCardId) : null;
    }

    public void OnPointerMove(SKPoint location)
    {
        if (_dragCardId is null) return;

        _dragCurrentPt = location;
        if (!_isDragging)
        {
            float dx = location.X - _touchStartPt.X;
            float dy = location.Y - _touchStartPt.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) > DragThreshold)
                _isDragging = true;
        }
        if (_isDragging) RequestRedraw();
    }

    public void OnPointerUp(SKPoint location)
    {
        if (_isDragging && _dragCardId is not null)
        {
            var zoneId = HitTestZone(location);
            if (zoneId is not null && zoneId == _dragSourceZoneId && IsHandZoneId(zoneId))
            {
                // Reorder within the same hand zone
                int newIndex = CalcFanInsertIndex(zoneId, location.X, _dragCardId);
                CardReorderedInHand?.Invoke(_dragCardId, newIndex);
            }
            else if (zoneId is not null && _selectableCardIds.Contains(_dragCardId))
            {
                CardDropped?.Invoke(_dragCardId, zoneId);
            }
        }
        else
        {
            // A second tap in the same place, soon after the first, activates the zone
            // under it. Checked before anything else so it works over the pile's top
            // card, which is exactly where a player aims when they mean "draw".
            var hitZone = HitTestZone(location);
            bool isDoubleTap =
                NowMs() - _lastTapMs <= DoubleTapMs
                && MathF.Abs(location.X - _lastTapPoint.X) <= DoubleTapSlop
                && MathF.Abs(location.Y - _lastTapPoint.Y) <= DoubleTapSlop;

            _lastTapMs    = isDoubleTap ? 0 : NowMs();   // a third tap starts over
            _lastTapPoint = location;

            if (isDoubleTap && hitZone is not null)
            {
                _tooltipCardId = null;
                ZoneActivated?.Invoke(hitZone);
                _isDragging       = false;
                _dragCardId       = null;
                _dragSourceZoneId = null;
                RequestRedraw();
                return;
            }

            var hit = HitTestCardWithUid(location);
            if (hit is { } tapped)
            {
                // Toggle info tooltip on face-up cards (any card, not just selectable ones)
                if (IsCardFaceUp(tapped.Id))
                    _tooltipCardId = (_tooltipCardId == tapped.Id) ? null : tapped.Id;
                else
                    _tooltipCardId = null;

                // The uid names the physical card under the finger, so a five-deck
                // game can tell which of its identical fours was meant.
                CardTapped?.Invoke(tapped.Id, tapped.Uid);
            }
            else
            {
                _tooltipCardId = null;  // dismiss tooltip on empty-space tap
                if (hitZone is not null)
                    ZoneTapped?.Invoke(hitZone);
                else
                    CanvasTapped?.Invoke();
            }
        }
        _isDragging       = false;
        _dragCardId       = null;
        _dragSourceZoneId = null;
        RequestRedraw();
    }

    public void OnPointerCancel()
    {
        _isDragging       = false;
        _dragCardId       = null;
        _dragSourceZoneId = null;
        RequestRedraw();
    }

    // ── Hit testing ───────────────────────────────────────────────────────────

    private string? HitTestCard(SKPoint pt) => HitTestCardWithUid(pt)?.Id;

    private (int Uid, string Id)? HitTestCardWithUid(SKPoint pt)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
        {
            var (uid, cardId, rect) = _cardRects[i];
            if (rect.Contains(pt)) return (uid, cardId);
        }
        return null;
    }

    private string? HitTestSelectableCard(SKPoint pt)
    {
        for (int i = _cardRects.Count - 1; i >= 0; i--)
        {
            var (_, cardId, rect) = _cardRects[i];
            if (rect.Contains(pt) && _selectableCardIds.Contains(cardId)) return cardId;
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
            var (_, cardId, rect) = _cardRects[i];
            if (!rect.Contains(pt)) continue;
            var zoneId = FindZoneOfCard(cardId);
            if (zoneId is not null && IsHandZoneId(zoneId)) return cardId;
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

        // A declared caption wins over the renderer's default, and may decline to show.
        if (layout.Zone.Label is { } declared)
        {
            // A declared empty caption is a way of saying "none": the definer chose no
            // label rather than forgetting one, and gets no fallback.
            if (declared.Text.Length == 0 || !LabelApplies(declared)) return;
            string text = FillLabel(declared.Text, layout.Zone, rank: null);
            // A zone label is placed against its cards, not its band — the band can be
            // far larger than the cards in it.
            DrawPlacedLabel(canvas, ZoneCardsRect(layout), text, labelSz, declared);
            return;
        }

        // The seat to act wears its name in the accent, with a pip beside it. This and
        // the glow under the cards are the whole turn indicator: quiet, and attached
        // to the player rather than drawn around a box.
        bool  active = layout.IsCurrentPlayer;
        var   color  = active ? _theme.CurrentPlayerHighlight : _theme.PlayerNameColor;
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        using var font  = new SKFont(SKTypeface.Default, labelSz);
        float w = font.MeasureText(layout.Label!);

        float x, y;
        if (layout.Hint == ZoneRenderHint.Fan && FanExtent(layout) is { } cards
            && cards.Bottom + labelSz * 1.4f > layout.Bounds.Bottom)
        {
            // A hand pinned to the band's bottom edge has no room below it, and a name
            // drawn there was covered by the cards. It sits above the fan instead, at
            // the left, where nothing else lives.
            x = cards.Left;
            y = cards.Top - labelSz * 0.6f;
        }
        else
        {
            x = layout.Bounds.MidX - w / 2f;
            y = layout.Bounds.Bottom + labelSz * 1.4f;
        }

        if (active)
        {
            float r = labelSz * 0.28f;
            canvas.DrawCircle(x - r * 2.2f, y - labelSz * 0.32f, r, paint);
        }
        canvas.DrawText(layout.Label!, x, y, font, paint);
    }

    /// <summary>
    /// Draws a caption against a rectangle, on the side and at the angle the definition
    /// asked for. "vertical" reads bottom-to-top like a book spine; "angled" leans 30°.
    /// </summary>
    private void DrawPlacedLabel(
        SKCanvas canvas, SKRect target, string text, float size,
        Cards.Models.ZoneLabelDefinition label)
    {
        string placement   = TurnSide(label.Placement);
        string orientation = label.Orientation;

        using var paint = new SKPaint { Color = _theme.PlayerNameColor, IsAntialias = true };
        using var font  = new SKFont(SKTypeface.Default, size);
        float w   = font.MeasureText(text);
        float gap = size * 0.5f;

        // An exact place wins over the four-sided placement.
        if (label.Place is { } declaredPlace)
        {
            var place = TurnPlace(declaredPlace);
            var box = ResolvePlace(place, target, w + size, size * 1.4f);
            float deg = orientation switch { "vertical" => -90f, "angled" => -30f, _ => 0f };
            canvas.Save();
            canvas.RotateDegrees(deg, box.MidX, box.MidY);
            DrawTextInBox(canvas, text, box, font, paint, size, place.TextAlign, place.VerticalAlign, inset: size * 0.5f);
            canvas.Restore();
            return;
        }

        float degrees = orientation switch
        {
            "vertical" => -90f,
            "angled"   => -30f,
            _          => 0f,
        };

        // Where the label's centre sits, then rotate the text about that point.
        (float cx, float cy) = placement switch
        {
            "top"   => (target.MidX, target.Top - gap - size * 0.4f),
            "left"  => (target.Left - gap - size * 0.4f, target.MidY),
            "right" => (target.Right + gap + size * 0.4f, target.MidY),
            _       => (target.MidX, target.Bottom + gap + size * 0.5f),
        };

        // A side label that stays horizontal needs room for its width, not its height.
        if (degrees == 0f && placement == "left")  cx -= w / 2f;
        if (degrees == 0f && placement == "right") cx += w / 2f;

        canvas.Save();
        canvas.RotateDegrees(degrees, cx, cy);
        canvas.DrawText(text, cx - w / 2f, cy + size * 0.35f, font, paint);
        canvas.Restore();
    }

    /// <summary>
    /// The definition's counters for one group — cards, books, loose — as small pills
    /// on the side each asks for. Badges sharing a side sit in a row, in declaration
    /// order, so "cards | books" reads left to right the way a player thinks of it.
    /// </summary>
    private void DrawGroupBadges(SKCanvas canvas, Zone zone, SKRect group, int cardCount, float cardW)
    {
        var badges = zone.Definition?.GroupBadges;
        if (badges is null || badges.Count == 0 || _state is null) return;

        int bookSize = ScoringEngine.BookSize(_state.Definition);
        float size   = MathF.Max(cardW * 0.15f, 10f);

        // Per side, how far along the row the next badge starts.
        var cursor = new Dictionary<string, float>();

        foreach (var badge in badges)
        {
            if (badge.When is { } when && !RuleCondition.Evaluate(when, _state)) continue;

            // Books are a property of a group. On a zone with none — the deck, a pile —
            // "books" and "loose" have nothing to divide by, so every kind honestly
            // means the cards that are there.
            int value = zone.Type == "spread"
                ? badge.Shows switch
                {
                    "books" => cardCount / bookSize,
                    "loose" => cardCount % bookSize,
                    _       => cardCount,
                }
                : cardCount;

            string text;
            if (value == 0)
            {
                if (badge.Zero == "hide") continue;
                text = badge.Zero;
            }
            else text = value.ToString();

            var fill = ParseColor(badge.Color) ?? _theme.PlayerNameColor.WithAlpha(0x66);
            var ink  = ParseColor(badge.TextColor) ?? ContrastingInk(fill);

            using var font = new SKFont(SKTypeface.Default, size);
            float textW = font.MeasureText(text);
            float pillW = MathF.Max(textW + size * 1.2f, size * 1.8f);
            float pillH = size * 1.4f;
            float gap   = size * 0.35f;

            SKRect pill;
            var    place = badge.Place is { } declaredPlace ? TurnPlace(declaredPlace) : null;
            string side  = TurnSide(badge.Placement);
            if (place is not null)
            {
                // An exact position in the cards' proportions. Explicit placement means
                // explicit: two badges placed on the same spot overlap, by request.
                pill = ResolvePlace(place, group, pillW, pillH);
            }
            else
            {
                float along = cursor.GetValueOrDefault(side, 0f);

                // Anchor: the badge row hugs the group's edge on the chosen side and grows
                // rightwards (top/bottom) or downwards (left/right) as badges accumulate.
                pill = side switch
                {
                    "top"   => new SKRect(group.Left + along, group.Top - pillH - gap,
                                          group.Left + along + pillW, group.Top - gap),
                    "left"  => new SKRect(group.Left - pillW - gap, group.Top + along,
                                          group.Left - gap, group.Top + along + pillH),
                    "right" => new SKRect(group.Right + gap, group.Top + along,
                                          group.Right + gap + pillW, group.Top + along + pillH),
                    _       => new SKRect(group.Left + along, group.Bottom + gap,
                                          group.Left + along + pillW, group.Bottom + gap + pillH),
                };
                cursor[side] = along + (side is "left" or "right" ? pillH : pillW) + gap;
            }

            float degrees = badge.Orientation switch { "vertical" => -90f, "angled" => -30f, _ => 0f };

            canvas.Save();
            canvas.RotateDegrees(degrees, pill.MidX, pill.MidY);
            using (var bg = new SKPaint { Color = fill, IsAntialias = true })
                canvas.DrawRoundRect(pill, pillH * 0.3f, pillH * 0.3f, bg);
            using (var fg = new SKPaint { Color = ink, IsAntialias = true })
                DrawTextInBox(canvas, text, pill, font, fg, size,
                              place?.TextAlign ?? "center", place?.VerticalAlign ?? "middle",
                              inset: size * 0.6f);
            canvas.Restore();
        }
    }

    // ── Placement ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns a declared place into a rectangle. (x, y) picks a point on the target in
    /// its own proportions; the anchor says which point of the box lands there; width
    /// and height, when given, are proportions of the target too. Everything scales
    /// with the cards because nothing here is in pixels.
    /// </summary>
    private static SKRect ResolvePlace(Cards.Models.PlaceDefinition place, SKRect target, float fitW, float fitH)
    {
        float px = target.Left + target.Width  * Percent(place.X, 0.5f);
        float py = target.Top  + target.Height * Percent(place.Y, 0.5f);

        float w = place.Width  is { } pw ? target.Width  * Percent(pw, 1f) : fitW;
        float h = place.Height is { } ph ? target.Height * Percent(ph, 1f) : fitH;

        // Which fraction of the box sits left of / above the anchor point.
        (float ax, float ay) = place.Anchor switch
        {
            "top-left"     => (0f,   0f),
            "top"          => (0.5f, 0f),
            "top-right"    => (1f,   0f),
            "left"         => (0f,   0.5f),
            "right"        => (1f,   0.5f),
            "bottom-left"  => (0f,   1f),
            "bottom"       => (0.5f, 1f),
            "bottom-right" => (1f,   1f),
            _              => (0.5f, 0.5f),
        };

        float left = px - w * ax;
        float top  = py - h * ay;
        return new SKRect(left, top, left + w, top + h);
    }

    /// <summary>"10%" → 0.10; a bare number is read as a percent too. Negative is allowed.</summary>
    private static float Percent(string text, float fallback)
    {
        var t = text.Trim().TrimEnd('%');
        return float.TryParse(t, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v / 100f
            : fallback;
    }

    /// <summary>Text laid within a box per the usual alignments, inset from its edges.</summary>
    private static void DrawTextInBox(
        SKCanvas canvas, string text, SKRect box, SKFont font, SKPaint paint, float size,
        string textAlign, string verticalAlign, float inset)
    {
        float textW = font.MeasureText(text);

        float x = textAlign switch
        {
            "left"  => box.Left + inset,
            "right" => box.Right - inset - textW,
            _       => box.MidX - textW / 2f,
        };

        // Baselines: a cap-height of roughly 0.7 em sits the glyphs where the eye
        // expects for each alignment.
        float y = verticalAlign switch
        {
            "top"    => box.Top + inset * 0.5f + size * 0.8f,
            "bottom" => box.Bottom - inset * 0.5f - size * 0.15f,
            _        => box.MidY + size * 0.35f,
        };

        canvas.DrawText(text, x, y, font, paint);
    }

    private static SKColor? ParseColor(string? hex)
        => hex is not null && SKColor.TryParse(hex, out var c) ? c : null;

    /// <summary>Black or white, whichever reads against the fill.</summary>
    private static SKColor ContrastingInk(SKColor fill)
    {
        float luma = (0.299f * fill.Red + 0.587f * fill.Green + 0.114f * fill.Blue) / 255f;
        return luma > 0.6f ? new SKColor(0x1A, 0x1A, 0x1A) : SKColors.White;
    }

    private bool LabelApplies(Cards.Models.ZoneLabelDefinition label)
        => _state is null || label.When is not { } when || RuleCondition.Evaluate(when, _state);

    /// <summary>Fills {rank}, {count} and {owner} in a declared caption.</summary>
    private string FillLabel(string template, Zone zone, string? rank, int? count = null)
    {
        string owner = "";
        if (_state is not null && zone.OwnerId is { } id)
            owner = _state.Teams.FirstOrDefault(t => t.Id == id)?.Name
                 ?? _state.Players.FirstOrDefault(p => p.Id == id)?.Name
                 ?? "";

        return template
            .Replace("{rank}",  rank ?? "")
            .Replace("{count}", (count ?? zone.Count).ToString())
            .Replace("{owner}", owner);
    }


    // ── Trick direction indicator ─────────────────────────────────────────────

    /// <summary>
    /// Draws a circular arrow (↻ or ↺) next to the current player's trick zone to show
    /// the direction of play.  Only active when per-player trick zones are in use and
    /// trick_direction metadata is present.
    /// </summary>
    private void DrawTrickDirectionIndicator(SKCanvas canvas, IReadOnlyList<ZoneLayout> layouts)
    {
        if (_state is null) return;
        if (!_state.Metadata.TryGetValue("trick_direction", out var direction)) return;
        if (_state.Players.Count == 0) return;
        var currentPlayerId = _state.Players[_state.CurrentPlayerIndex].Id;

        // Find the current player's per-player trick zone layout
        var trickLayout = layouts.FirstOrDefault(l =>
            l.Zone.Type == "trick" && l.Zone.OwnerId == currentPlayerId);
        if (trickLayout is null) return;

        // Match the player's expectation: "clockwise" game direction shows a ↻ icon.
        bool clockwise = string.Equals(direction, "clockwise", StringComparison.OrdinalIgnoreCase);
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

        // Both icons have the arrowhead at the top (270° in SkiaSharp = 12 o'clock):
        //   CW  (↻): arc from 330° (upper-right) sweeping +300° → tip at top pointing RIGHT.
        //   CCW (↺): arc from 210° (upper-left)  sweeping −300° → tip at top pointing LEFT.
        // Gap is ~60° wide and sits near the top in both cases, making the direction obvious.
        float startAngle = clockwise ? 330f : 210f;
        float sweepAngle = clockwise ? 300f : -300f;
        const float endAngle = 270f;  // always at 12 o'clock

        var oval = new SKRect(cx - arcR, cy - arcR, cx + arcR, cy + arcR);
        using var arcPath = new SKPath();
        arcPath.AddArc(oval, startAngle, sweepAngle);
        canvas.DrawPath(arcPath, arcPaint);

        // Arrowhead at top (270° = 12 o'clock position)
        float endRad = endAngle * MathF.PI / 180f;
        float tipX   = cx + arcR * MathF.Cos(endRad);  // = cx
        float tipY   = cy + arcR * MathF.Sin(endRad);  // = cy − arcR  (above centre)

        // Tangent at top: CW motion at 270° points RIGHT (0°); CCW points LEFT (180°)
        float tangentDeg = clockwise ? 0f : 180f;
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
        float  fontSize = Math.Clamp(cardW * 0.52f, 26f, 44f);

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

    // The felt is a full-canvas radial gradient. It only changes when the theme or the
    // canvas size changes, but it was being evaluated per pixel on every frame — on a
    // 1920x766 table that is 1.5 million shaded pixels per frame, twice over, before a
    // single card is drawn. Cached as an image and blitted instead.
    private SKImage?    _feltCache;
    private int         _feltWidth;
    private int         _feltHeight;
    private string?     _feltThemeId;

    private void DrawFelt(SKCanvas canvas, SKImageInfo info)
    {
        if (_feltCache is null || _feltWidth != info.Width || _feltHeight != info.Height
                               || _feltThemeId != _theme.Id)
        {
            _feltCache?.Dispose();
            _feltCache   = RenderFelt(info);
            _feltWidth   = info.Width;
            _feltHeight  = info.Height;
            _feltThemeId = _theme.Id;
        }

        if (_feltCache is not null) canvas.DrawImage(_feltCache, 0, 0);
        else                        PaintFelt(canvas, info.Width, info.Height);
    }

    private SKImage? RenderFelt(SKImageInfo info)
    {
        if (info.Width <= 0 || info.Height <= 0) return null;

        using var surface = SKSurface.Create(new SKImageInfo(
            info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null) return null;

        PaintFelt(surface.Canvas, info.Width, info.Height);
        return surface.Snapshot();
    }

    private void PaintFelt(SKCanvas canvas, int width, int height)
    {
        // The gradient is opaque and covers everything, so the flat base fill it used
        // to be painted over was a second full-canvas write for no visible effect.
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(width / 2f, height / 2f),
            MathF.Max(width, height) * 0.7f,
            [_theme.FeltColor, _theme.FeltEdgeColor],
            null,
            SKShaderTileMode.Clamp);
        using var vignette = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, width, height, vignette);
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

    // Card draws go through these so the overlay can report how many cards a frame
    // actually painted — the renderer's dominant cost, and the number that says
    // whether a fly-in is redrawing a whole stationary table to move one card.
    private void DrawCardCounted(SKCanvas canvas, SKRect rect, Card card, ICardSkin skin)
    {
        _diagnostics.CardsDrawn++;
        CardRenderer.DrawCard(canvas, rect, card, skin, IsWildHere(card));
    }

    /// <summary>
    /// Whether this game treats the card as wild. Read from the definition once per
    /// paint, since wildness is a rule of the game and the same 2 is ordinary elsewhere.
    /// </summary>
    private bool IsWildHere(Card card)
    {
        _wildRanks ??= MeldRules.WildRanks(_state?.Definition);
        return MeldRules.IsWild(card, _wildRanks);
    }

    private HashSet<Rank>? _wildRanks;

    private void DrawCardBackCounted(SKCanvas canvas, SKRect rect, ICardSkin skin)
    {
        _diagnostics.CardsDrawn++;
        CardRenderer.DrawCardBack(canvas, rect, skin);
    }

    /// <summary>
    /// Draws a card the way its zone says it should be seen, unless
    /// <see cref="RevealAllCards"/> overrides that.
    /// </summary>
    private void DrawCardForZone(SKCanvas canvas, SKRect rect, Card card, bool zoneFaceUp)
    {
        if (RevealAllCards)
        {
            // Forced, not merely permitted: an opponent's card is face-down on the card
            // itself as well as hidden by its zone, so honouring IsFaceUp here would
            // reveal nothing.
            _diagnostics.CardsDrawn++;
            CardRenderer.DrawCardFace(canvas, rect, card, _skin, IsWildHere(card));
            return;
        }

        if (zoneFaceUp) DrawCardCounted(canvas, rect, card, _skin);
        else            DrawCardBackCounted(canvas, rect, _skin);
    }

    /// <summary>
    /// Draws the frame-timing overlay. Plain ASCII in the default typeface, so it
    /// renders on every platform without depending on a font being present.
    /// </summary>
    private void DrawDiagnosticsOverlay(SKCanvas canvas, SKImageInfo info)
    {
        var lines = _diagnostics.Lines();

        const float pad = 8f;
        float size   = MathF.Max(11f, info.Width * 0.011f);
        float lineH  = size * 1.35f;
        float boxH   = lines.Count * lineH + pad * 2;
        float boxW   = MathF.Min(info.Width - 16f, size * 26f);

        var box = new SKRect(8f, 8f, 8f + boxW, 8f + boxH);

        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true };
        canvas.DrawRoundRect(box, 6f, 6f, bg);

        using var font = new SKFont(SKTypeface.Default, size);
        using var ink  = new SKPaint { Color = new SKColor(0x7C, 0xFF, 0xB2), IsAntialias = true };

        float y = box.Top + pad + size;
        foreach (var line in lines)
        {
            canvas.DrawText(line, box.Left + pad, y, SKTextAlign.Left, font, ink);
            y += lineH;
        }
    }

    private static long NowMs() => Environment.TickCount64;

    private static float EaseOutCubic(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u;
    }

    private static float EaseInOutSine(float t)
        => -(MathF.Cos(MathF.PI * t) - 1f) / 2f;
}
