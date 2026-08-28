using SkiaSharp;
using Cards.App;
using Cards.Engine;

namespace Cards.Rendering;

/// <summary>
/// Drives <see cref="CardTableRenderer"/>'s fly-in animations from the turn loop.
///
/// This is the choreography that used to live inside the MAUI page, which is why the
/// browser client had none of it: the movement rules were written against a MAUI
/// control rather than against the renderer that actually owns the geometry. Nothing
/// here is platform-specific — it reads the renderer's last painted layout and asks it
/// to animate — so both clients can share one copy.
///
/// Every wait is paired with a safety timeout. A client that is slow, backgrounded, or
/// simply never paints must degrade to "the cards land instantly", never to a table
/// that has stopped taking turns.
/// </summary>
public sealed class RendererTableAnimator : ITableAnimator
{
    private readonly CardTableRenderer _renderer;

    /// <summary>Card id → zone id, as of the last <see cref="CaptureBeforeMove"/>.</summary>
    private Dictionary<string, string> _sourceZones = [];

    /// <summary>
    /// Card id → where it was painted, as of the last <see cref="CaptureBeforeMove"/>.
    ///
    /// Captured up front rather than read back after the move, because the renderer only
    /// keeps the most recent frame: once a repaint lands, a card's rect is its
    /// destination, and animating from there produces no visible motion at all. Whether
    /// that repaint has happened yet is a matter of host scheduling, so relying on it
    /// not to would make the animation silently host-dependent.
    /// </summary>
    private Dictionary<string, SKPoint> _sourcePoints = [];

    public RendererTableAnimator(CardTableRenderer renderer) => _renderer = renderer;

    // ── Mid-game moves ────────────────────────────────────────────────────────

    public void CaptureBeforeMove(GameState state)
    {
        _sourceZones  = new Dictionary<string, string>();
        _sourcePoints = new Dictionary<string, SKPoint>();

        foreach (var (zoneId, zone) in state.Zones)
            foreach (var card in zone.Cards)
            {
                _sourceZones[card.Id] = zoneId;

                var rect = _renderer.GetLastCardRect(card.Id);
                if (rect.HasValue)
                    _sourcePoints[card.Id] = new SKPoint(rect.Value.MidX, rect.Value.MidY);
            }
    }

    public async Task PlayMoveAsync(GameState state)
    {
        // Cards that changed zones. Deck arrivals are excluded: a reshuffle moves the
        // whole pack at once and animating it individually looks like a glitch.
        var moved = state.Zones.Values
            .Where(z => z.Type != "deck")
            .SelectMany(z => z.Cards.Select(c => (CardId: c.Id, DestZoneId: z.Id)))
            .Where(x => !_sourceZones.TryGetValue(x.CardId, out var prev) || prev != x.DestZoneId)
            .ToList();

        if (moved.Count == 0) return;

        var sources = ResolveSources(moved);
        var entries = BuildMoveEntries(state, moved, sources);
        if (entries.Count == 0) return;

        _renderer.QueueFlyIns(entries);
        await Task.WhenAny(_renderer.WaitForFlyInsAsync(), Task.Delay(2000));
    }

    /// <summary>
    /// Where each moving card should fly from, in descending order of precision:
    /// its captured rect from before the move, then its source zone's centre (which is
    /// all that is available for rotated zones, such as an opponent's hand along the
    /// side of the table), then the deck.
    /// </summary>
    private Dictionary<string, SKPoint> ResolveSources(
        IReadOnlyList<(string CardId, string DestZoneId)> moved)
    {
        var sources = new Dictionary<string, SKPoint>();
        foreach (var (cardId, _) in moved)
        {
            if (_sourcePoints.TryGetValue(cardId, out var pt))
            {
                sources[cardId] = pt;
                continue;
            }

            SKPoint? center = null;
            if (_sourceZones.TryGetValue(cardId, out var srcZoneId))
                center = _renderer.GetZoneCenter(srcZoneId);
            center ??= _renderer.GetZoneCenter("deck");

            if (center.HasValue) sources[cardId] = center.Value;
        }
        return sources;
    }

    /// <summary>
    /// Hand arrivals get their precise fan-slot centre, because a hand fans its cards
    /// and landing on the zone centre would drop every card on the same spot. Anything
    /// else lands on the zone centre.
    /// </summary>
    private List<(string CardId, SKPoint From, SKPoint To)> BuildMoveEntries(
        GameState state,
        IReadOnlyList<(string CardId, string DestZoneId)> moved,
        IReadOnlyDictionary<string, SKPoint> sources)
    {
        var handArrivals = moved
            .Where(m => state.Zones.TryGetValue(m.DestZoneId, out var z) && z.Type == "hand")
            .Select(m => m.CardId);
        var handDests = _renderer.ComputeHandSlotCenters(state, handArrivals);

        var entries = new List<(string, SKPoint, SKPoint)>(moved.Count);
        foreach (var (cardId, destZoneId) in moved)
        {
            if (!sources.TryGetValue(cardId, out var from)) continue;
            if (!state.Zones.TryGetValue(destZoneId, out var destZone)) continue;

            SKPoint? to = destZone.Type == "hand"
                ? handDests.TryGetValue(cardId, out var handPt) ? handPt : null
                : _renderer.GetZoneCenter(destZoneId);

            if (to.HasValue) entries.Add((cardId, from, to.Value));
        }
        return entries;
    }

    // ── Opening deal ──────────────────────────────────────────────────────────

    public async Task PlayDealAsync(GameState state)
    {
        var preDeal = BuildPreDealState(state);
        if (!preDeal.Zones.Values.Any(z => z.Type == "deck" && !z.IsEmpty)) return;

        // Show the undealt pack and wait for it to paint, so the layout holds real
        // pixel positions before anything is measured against it.
        _renderer.GameState = preDeal;
        await Task.WhenAny(_renderer.WaitForNextPaintAsync(), Task.Delay(500));

        // Read the deck centre before the shuffle: afterwards, layout cleanup can
        // race the reading. On a cold start the canvas may still be zero-sized, so
        // give it one more frame before giving up.
        var deckCenter = _renderer.GetZoneCenter("deck");
        if (!deckCenter.HasValue)
        {
            await Task.WhenAny(_renderer.WaitForNextPaintAsync(), Task.Delay(350));
            deckCenter = _renderer.GetZoneCenter("deck");
        }

        await Task.WhenAny(_renderer.TriggerShuffleAnimationAsync("deck"), Task.Delay(1600));

        _renderer.GameState = state;

        var deal = state.LastDealResult;
        if (deal is null || !deckCenter.HasValue) return;   // cards simply appear

        var entries = BuildDealEntries(deal, deckCenter.Value, state);
        if (entries.Count == 0) return;

        _renderer.QueueFlyIns(entries, delayBetweenMs: deal.AnimDelayMs);
        await Task.WhenAny(_renderer.WaitForFlyInsAsync(), Task.Delay(6000));
    }

    /// <summary>
    /// A display-only state with every card stacked face-down in the deck, so the
    /// shuffle plays on a full pack rather than on the stub left after dealing.
    /// </summary>
    private static GameState BuildPreDealState(GameState real)
    {
        var preview = new GameState
        {
            GameId         = real.GameId,
            Definition     = real.Definition,
            CurrentPhaseId = real.CurrentPhaseId,
        };

        foreach (var p in real.Players)
            preview.Players.Add(p);

        foreach (var (id, z) in real.Zones)
            preview.Zones[id] = new Zone(id, z.Type, z.OwnerId, z.Visibility);

        var deckZone = preview.Zones.Values.FirstOrDefault(z => z.Type == "deck");
        if (deckZone is not null)
            foreach (var card in real.Zones.Values.SelectMany(z => z.Cards))
                deckZone.Add(new Card(card.Suit, card.Rank, isFaceUp: false));

        return preview;
    }

    /// <summary>
    /// Deal entries in the engine's own deal order, so the stagger reproduces the
    /// round-the-table waterfall instead of an arbitrary sequence.
    /// </summary>
    private List<(string CardId, SKPoint From, SKPoint To)> BuildDealEntries(
        DealResult deal, SKPoint deckCenter, GameState finalState)
    {
        var destinations = _renderer.ComputeHandSlotCenters(
            finalState, deal.CardsByPlayerIndex.Values.SelectMany(ids => ids));

        var entries  = new List<(string, SKPoint, SKPoint)>();
        var assigned = new Dictionary<int, int>();

        foreach (var (playerIdx, count) in deal.Steps)
        {
            if (!deal.CardsByPlayerIndex.TryGetValue(playerIdx, out var cards)) continue;

            int start = assigned.GetValueOrDefault(playerIdx, 0);
            int end   = Math.Min(start + count, cards.Count);
            for (int i = start; i < end; i++)
                if (destinations.TryGetValue(cards[i], out var to))
                    entries.Add((cards[i], deckCenter, to));

            assigned[playerIdx] = end;
        }
        return entries;
    }
}
