using SkiaSharp;
using Cards.Engine;
using Cards.Models;

namespace Cards.Rendering;

/// <summary>
/// Lays out a table from what its definition declares, instead of from a hand-tuned
/// arrangement per player count.
///
/// Every zone resolves to a place in table percentages — a well-known region, an exact
/// place, or the default for its kind — and zones sharing a region flow side by side.
/// Owned zones are declared once, as if for the seat at the bottom of the screen, and
/// turned to each other seat: one declaration serves every player, and a six-seat table
/// needs no more thought than a two-seat one.
///
/// Every shipped game declares its layout; the hand-tuned engine that preceded this
/// is gone. A definition that declares nothing still gets a table, from the defaults
/// for each zone's kind.
/// </summary>
public static class DeclaredLayoutEngine
{
    /// <summary>A place in table percentages, plus what the zone is for on screen.</summary>
    private sealed record Spot(Zone Zone, PlaceDefinition Place, Seat Seat, string? Region);

    /// <summary>Which edge a player sits at, and how their content is turned to face them.</summary>
    /// <param name="Slot">Which share of the edge, of <paramref name="Slots"/>, when several seats sit along it.</param>
    private sealed record Seat(int Index, string Side, float Rotation, int Slot = 0, int Slots = 1);

    public static IReadOnlyList<ZoneLayout> Compute(GameState state, SKImageInfo info)
    {
        float W = info.Width, H = info.Height;
        var table = new SKRect(0, 0, W, H);

        float baseCardW = MathF.Min(W * 0.11f, 96f) * state.Definition.CardScale;
        var   seats     = SeatMap(state.Players.Count);

        // Every zone gets a spot: declared, or the default for its kind.
        var spots = new List<Spot>();
        foreach (var zone in state.Zones.Values)
        {
            var seat = zone.OwnerId is { } owner ? SeatOf(state, seats, owner) : null;
            var (place, region) = ResolvePlace(zone, seat is not null);
            spots.Add(new Spot(zone, place, seat ?? seats[0], region));
        }

        // Zones sharing a region (and, for owned ones, a seat) divide it between them.
        var placed = new List<(Spot Spot, SKRect Bounds)>();
        foreach (var group in spots.GroupBy(s => (s.Region, s.Region is null ? s.Zone.Id : "", s.Seat.Index)))
        {
            var members = group.ToList();
            for (int i = 0; i < members.Count; i++)
            {
                var spot   = members[i];
                var seatPx = ToSeatSpace(spot.Place, spot.Seat);
                var bounds = ResolveTable(seatPx, table);

                // Sharing: split the region along its longer axis, in declaration order.
                if (members.Count > 1)
                {
                    bool horizontal = bounds.Width >= bounds.Height;
                    float share = (horizontal ? bounds.Width : bounds.Height) / members.Count;
                    bounds = horizontal
                        ? new SKRect(bounds.Left + share * i, bounds.Top, bounds.Left + share * (i + 1), bounds.Bottom)
                        : new SKRect(bounds.Left, bounds.Top + share * i, bounds.Right, bounds.Top + share * (i + 1));
                    bounds.Inflate(-4f, -4f);
                }

                placed.Add((spot, bounds));
            }
        }

        // A side seat's zones, turned, run the table's full height, and would cross the
        // bottom and top seats' zones in the corners — four fronts around one centre
        // cannot each span their whole side. Each side zone is squeezed only as far as
        // what is actually in its column requires: a hand at the very edge, with
        // nothing above or below it but a small pile, keeps nearly its full run; a
        // meld strip in the front band stops at the bottom seat's melds. The same
        // treatment for regions and declared places, since the collision is the same.
        var fixedObstacles = placed.Where(p => p.Spot.Seat.Side is "bottom" or "top").Select(p => p.Bounds).ToList();

        // One band per side seat — the tightest any of its zones needs — so the seat's
        // own zones keep their arrangement relative to each other. Per-zone bands let a
        // hand and its foot land on one another.
        var bands = new Dictionary<int, (float Min, float Max)>();
        foreach (var (spot, bounds) in placed.Where(p => p.Spot.Seat.Side is "left" or "right"))
        {
            var (yMin, yMax) = bands.GetValueOrDefault(spot.Seat.Index, (0f, H));
            foreach (var ob in fixedObstacles)
            {
                if (ob.Right <= bounds.Left || ob.Left >= bounds.Right) continue;   // not in this column
                if (ob.MidY < H / 2f) yMin = MathF.Max(yMin, ob.Bottom + 4f);
                else                  yMax = MathF.Min(yMax, ob.Top - 4f);
            }
            bands[spot.Seat.Index] = (yMin, yMax);
        }

        var layouts = new List<ZoneLayout>();
        foreach (var (spot, bounds) in placed)
        {
            var final = bounds;
            if (bands.TryGetValue(spot.Seat.Index, out var band)
                && spot.Seat.Side is "left" or "right"
                && placed.Where(p => p.Spot.Seat.Index == spot.Seat.Index)
                         .Any(p => p.Bounds.Top < band.Min || p.Bounds.Bottom > band.Max))
            {
                float k = (band.Max - band.Min) / H;
                final = new SKRect(bounds.Left, band.Min + bounds.Top * k, bounds.Right, band.Min + bounds.Bottom * k);
            }
            layouts.Add(Describe(state, spot, final, baseCardW));
        }

        return layouts;
    }

    // ── Seats ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seat 0 is always the bottom edge. Opponents go right, top, left in turn; extra
    /// seats share the top, which is the edge with room to spare.
    /// </summary>
    private static List<Seat> SeatMap(int players)
    {
        var seats = new List<Seat> { new(0, "bottom", 0f) };
        string[] order = players switch
        {
            <= 1 => [],
            2    => ["top"],
            3    => ["right", "left"],
            4    => ["right", "top", "left"],
            _    => ["right", .. Enumerable.Repeat("top", players - 3), "left"],
        };
        int tops = order.Count(s => s == "top"), topSlot = 0;
        for (int i = 0; i < order.Length; i++)
        {
            string side = order[i];
            float rotation = side switch { "top" => 180f, "right" => 90f, "left" => 270f, _ => 0f };
            // Extra seats share the top edge, each taking its slice of it — in seating
            // order from the right, which is how they come round the table.
            seats.Add(side == "top"
                ? new Seat(i + 1, side, rotation, Slot: tops - 1 - topSlot++, Slots: tops)
                : new Seat(i + 1, side, rotation));
        }
        return seats;
    }

    private static Seat SeatOf(GameState state, List<Seat> seats, string ownerId)
    {
        int idx = state.Players.FindIndex(p => p.Id == ownerId);
        if (idx < 0)
        {
            // A team-owned zone sits with the team's first player.
            var team = state.Teams.FirstOrDefault(t => t.Id == ownerId);
            if (team is not null)
                idx = state.Players.FindIndex(p => team.PlayerIds.Contains(p.Id));
        }
        return idx >= 0 && idx < seats.Count ? seats[idx] : seats[0];
    }

    // ── Places ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The regions a definition may name, as places in bottom-seat space. Owned regions
    /// are turned to the owner's seat; shared ones stay put.
    ///
    /// The bands are disjoint by construction: seat 1–13% and 87–99%, seat-front 15–35%
    /// and 65–85%, center 40–60%. Seats are 72% wide so a side seat's band (turned, it
    /// runs 14–86% tall) clears the corners the bottom and top seats occupy, and the
    /// centre is 37–63% wide so side seats' fronts (turned, x 15–35% and 65–85%) and the
    /// compass of play spots around it (x 64–76% and 24–36%) clear it, and center-left
    /// and center-right (14–28%, 72–86%) clear the side seats' bands (1–13%, 87–99%). They do not clear side fronts: a game with side seats
    /// AND front zones should not use them, and the layout test says so per game.
    /// </summary>
    private static readonly Dictionary<string, PlaceDefinition> Regions = new()
    {
        // Shared
        ["center"]        = P("50%", "50%", "center", "26%", "20%"),
        ["center-left"]   = P("21%", "50%", "center", "14%", "20%"),
        ["center-right"]  = P("79%", "50%", "center", "14%", "20%"),
        ["center-top"]    = P("50%", "45%", "center", "60%", "10%"),
        ["center-bottom"] = P("50%", "55%", "center", "60%", "10%"),
        // Owned (bottom-seat space)
        ["seat"]          = P("50%", "93%", "center", "72%", "12%"),
        ["seat-front"]    = P("50%", "75%", "center", "56%", "20%"),
        ["seat-side"]     = P("89%", "75%", "center", "18%", "20%"),
        ["seat-corner"]   = P("6%",  "93%", "center", "10%", "12%"),
        ["seat-play"]     = P("50%", "70%", "center", "12%", "12%"),
    };

    private static PlaceDefinition P(string x, string y, string anchor, string w, string h)
        => new() { X = x, Y = y, Anchor = anchor, Width = w, Height = h };

    private static (PlaceDefinition Place, string? Region) ResolvePlace(Zone zone, bool owned)
    {
        var layout = zone.Definition?.Layout;

        if (layout?.Place is { } exact) return (exact, null);

        string region = layout?.Region ?? DefaultRegion(zone, owned);
        return (Regions.TryGetValue(region, out var p) ? p : Regions["center"], region);
    }

    /// <summary>Where a zone goes when its definition does not say — the old engine's habits.</summary>
    private static string DefaultRegion(Zone zone, bool owned)
    {
        string baseId = zone.Id.Split(':')[0];
        if (!owned) return "center";
        return baseId switch
        {
            "hand" or "hand2" or "hand3"           => "seat",
            "foot"                                 => "seat-corner",
            "meld" or "table" or "grid"           => "seat-front",
            "play" or "trick"                      => "seat-play",
            _                                       => "seat-side",
        };
    }

    /// <summary>
    /// Turns a bottom-seat place to face another seat: across the table it is upside
    /// down, on the sides it is on its side, and width and height trade places.
    /// </summary>
    private static PlaceDefinition ToSeatSpace(PlaceDefinition p, Seat seat)
    {
        float x = Pct(p.X, 0.5f), y = Pct(p.Y, 0.5f);
        float? w = p.Width  is { } pw ? Pct(pw, 1f) : null;
        float? h = p.Height is { } ph ? Pct(ph, 1f) : null;
        string anchor = p.Anchor;

        switch (seat.Side)
        {
            case "top":
                (x, y) = (1f - x, 1f - y);
                anchor = FlipAnchor(anchor);
                // Several seats along the top each get a slice of the edge.
                if (seat.Slots > 1)
                {
                    float slice = 1f / seat.Slots;
                    x = seat.Slot * slice + x * slice;
                    if (w is { } sliceW) w = sliceW * slice;
                }
                break;
            case "right":
                // Seated at the right edge, facing left: the bottom seat's near edge
                // (y = 100%) becomes x = 100%, and its left hand (x = 0%) points down
                // the screen (y = 100%).
                (x, y, w, h) = (y, 1f - x, h, w);
                anchor = RotateAnchor(anchor, quarterTurns: 3);
                break;
            case "left":
                (x, y, w, h) = (1f - y, x, h, w);
                anchor = RotateAnchor(anchor, quarterTurns: 1);
                break;
        }

        return new PlaceDefinition
        {
            X = Fmt(x), Y = Fmt(y), Anchor = anchor,
            Width = w is { } fw ? Fmt(fw) : null, Height = h is { } fh ? Fmt(fh) : null,
        };
    }

    private static SKRect ResolveTable(PlaceDefinition p, SKRect table)
    {
        float px = table.Left + table.Width  * Pct(p.X, 0.5f);
        float py = table.Top  + table.Height * Pct(p.Y, 0.5f);
        float w  = table.Width  * Pct(p.Width  ?? "20%", 0.2f);
        float h  = table.Height * Pct(p.Height ?? "20%", 0.2f);

        (float ax, float ay) = AnchorFractions(p.Anchor);
        float left = px - w * ax, top = py - h * ay;
        return new SKRect(left, top, left + w, top + h);
    }

    private static (float, float) AnchorFractions(string anchor) => anchor switch
    {
        "top-left"    => (0f, 0f),   "top"    => (0.5f, 0f), "top-right"    => (1f, 0f),
        "left"        => (0f, 0.5f),                          "right"        => (1f, 0.5f),
        "bottom-left" => (0f, 1f),   "bottom" => (0.5f, 1f), "bottom-right" => (1f, 1f),
        _             => (0.5f, 0.5f),
    };

    private static string FlipAnchor(string anchor)
    {
        var (ax, ay) = AnchorFractions(anchor);
        return AnchorName(1f - ax, 1f - ay);
    }

    private static string RotateAnchor(string anchor, int quarterTurns)
    {
        var (ax, ay) = AnchorFractions(anchor);
        for (int i = 0; i < quarterTurns; i++) (ax, ay) = (1f - ay, ax);
        return AnchorName(ax, ay);
    }

    private static string AnchorName(float ax, float ay)
    {
        string v  = ay < 0.25f ? "top"  : ay > 0.75f ? "bottom" : "";
        string hz = ax < 0.25f ? "left" : ax > 0.75f ? "right"  : "";
        if (v == "" && hz == "") return "center";
        if (v == "")  return hz;
        if (hz == "") return v;
        return $"{v}-{hz}";
    }

    private static float Pct(string text, float fallback)
        => float.TryParse(text.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out var v) ? v / 100f : fallback;

    private static string Fmt(float f)
        => (f * 100f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";

    // ── Zone description ──────────────────────────────────────────────────────

    private static ZoneLayout Describe(GameState state, Spot spot, SKRect bounds, float baseCardW)
    {
        var  zone  = spot.Zone;
        var  seat  = spot.Seat;
        bool owned = zone.OwnerId is not null;
        bool mine  = owned && seat.Index == 0;

        // Cards are sized to the bounds they must fit, never larger than the table's base.
        // A zone turned a quarter is drawn in its own frame, where its width and height
        // trade places — a side seat's band is tall on screen and wide to its player.
        bool onSide = zone.Type == "hand" && seat.Side is "left" or "right";
        float fitW = onSide ? bounds.Height : bounds.Width;
        float fitH = onSide ? bounds.Width  : bounds.Height;
        float cardW = MathF.Min(baseCardW, MathF.Min(fitW, fitH / 1.4f));
        float cardH = cardW * 1.4f;

        // Face-up if the zone lets everyone see, or lets its owner see and the owner is
        // the person at this screen, or a showdown has turned it over.
        var  owner    = state.Players.FirstOrDefault(p => p.Id == zone.OwnerId);
        bool revealed = owner is not null && IsShowdownRevealed(state, owner.Id);
        bool faceUp   = zone.Visibility is "all" or "top"
                     || (zone.Visibility is "owner" or "mixed" && mine)
                     || revealed;

        // What the table says about a zone is the definition's to say. With no label
        // declared, a hand falls back to its owner's name — the seat is named once — and
        // every other zone to nothing. The renderer invents no words of its own.
        string? label = owned && zone.Type == "hand"
            ? (owner?.Name ?? state.Teams.FirstOrDefault(t => t.Id == zone.OwnerId)?.Name)
            : null;

        bool current = owner is not null && state.CurrentPlayer.Id == owner.Id;

        return new ZoneLayout(zone, bounds, cardW, cardH,
            zone.IsEmpty && zone.Definition?.GroupLayout != "by_rank" ? ZoneRenderHint.Empty : HintFor(zone),
            FaceUp: faceUp,
            RotationDegrees: revealed ? 0f : (zone.Type == "hand" ? seat.Rotation : 0f),
            Label: label,
            IsCurrentPlayer: current && zone.Type == "hand",
            SeatQuarterTurns: owned ? (int)(seat.Rotation / 90f) : 0);
    }

    private static bool IsShowdownRevealed(GameState state, string playerId)
        => state.Metadata.GetValueOrDefault("showdown_revealed") == "true"
        && state.Metadata.GetValueOrDefault($"bet_folded:{playerId}") != "true";

    private static ZoneRenderHint HintFor(Zone zone) => zone.Type switch
    {
        "deck"  => ZoneRenderHint.Stack,
        "pile"  => zone.Visibility == "count_only" ? ZoneRenderHint.CountOnly
                 : zone.Definition?.Arrangement == "compact" ? ZoneRenderHint.Spread
                 : ZoneRenderHint.Stack,
        "hand"  => ZoneRenderHint.Fan,
        "trick" or "spread" or "grid" => ZoneRenderHint.Spread,
        _       => ZoneRenderHint.Stack,
    };
}
