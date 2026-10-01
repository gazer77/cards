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
    /// <param name="Cell">
    /// On a phone held upright, the part of the table an opponent's seat is drawn into, in
    /// table fractions. Null at a desktop table, and for the person at this screen.
    /// </param>
    /// <param name="Compass">
    /// Where this seat would sit at a desktop table — where its tricks are played to,
    /// around the middle, whatever its cell.
    /// </param>
    private sealed record Seat(int Index, string Side, float Rotation, int Slot = 0, int Slots = 1,
                               SKRect? Cell = null, Seat? Compass = null);

    /// <summary>
    /// A table taller than it is wide — a phone held upright. Laid out as a desktop, its
    /// side seats came out as slivers down the edges and everything else crowded the
    /// middle of a screen whose height went unused.
    /// </summary>
    public static bool IsPortrait(SKImageInfo info) => info.Height > info.Width * 1.15f;

    public static IReadOnlyList<ZoneLayout> Compute(GameState state, SKImageInfo info)
    {
        float W = info.Width, H = info.Height;
        var table = new SKRect(0, 0, W, H);

        float baseCardW = MathF.Min(W * 0.11f, 96f) * state.Definition.CardScale;
        bool  portrait  = IsPortrait(info);
        var (seats, teamSeats) = portrait
            ? PortraitSeatMap(state, W, H, baseCardW)
            : (SeatMap(state.Players.Count), new Dictionary<string, Seat>());

        // Every zone gets a spot: declared, or the default for its kind.
        var spots = new List<Spot>();
        foreach (var zone in state.Zones.Values)
        {
            var seat = zone.OwnerId is { } owner
                ? teamSeats.GetValueOrDefault(owner) ?? SeatOf(state, seats, owner)
                : null;
            var (place, region) = ResolvePlace(zone, seat is not null, portrait);
            spots.Add(new Spot(zone, place, seat ?? seats[0], region));
        }

        // What each opponent's cell must hold: the run of its seat its zones use.
        var extents = CellExtents(spots);

        // Zones sharing a region (and, for owned ones, a seat) divide it between them.
        var placed = new List<(Spot Spot, SKRect Bounds)>();
        foreach (var group in spots.GroupBy(s => (s.Region, s.Region is null ? s.Zone.Id : "", s.Seat.Index)))
        {
            var members = group.ToList();
            for (int i = 0; i < members.Count; i++)
            {
                var spot   = members[i];
                var seatPx = spot.Seat.Cell is { } cell && !PlayedToTheMiddle(spot)
                    ? ToCellSpace(spot.Place, cell, extents[spot.Seat.Index])
                    : ToSeatSpace(spot.Place, spot.Seat.Compass ?? spot.Seat);

                // Upright, the compass of trick spots closes in on the middle, giving the
                // opponents' rows above it the height a tall screen has to spare.
                if (portrait && spot.Zone.OwnerId is not null && PlayedToTheMiddle(spot))
                    seatPx = PullTowardMiddle(seatPx, 0.7f);
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
                // Only what is really in the way. A clipped corner — a pile whose edge
                // laps a few pixels into this column — used to count as fully blocking,
                // and Golf's discard, overlapping a side grid by 7% of its width, took
                // the whole side of the table away from two of its four players.
                float overlap = MathF.Min(ob.Right, bounds.Right) - MathF.Max(ob.Left, bounds.Left);
                if (overlap <= MathF.Min(ob.Width, bounds.Width) * 0.25f) continue;

                // An obstacle across the middle of the table cannot be gone round by
                // moving up or down: taking it as either would leave a sliver. The side
                // seat keeps the larger of the two gaps it leaves instead.
                if (ob.Top < H / 2f && ob.Bottom > H / 2f)
                {
                    if (ob.Top - yMin >= yMax - ob.Bottom) yMax = MathF.Min(yMax, ob.Top - 4f);
                    else                                   yMin = MathF.Max(yMin, ob.Bottom + 4f);
                    continue;
                }

                if (ob.MidY < H / 2f) yMin = MathF.Max(yMin, ob.Bottom + 4f);
                else                  yMax = MathF.Min(yMax, ob.Top - 4f);
            }

            // A band thinner than a card is not a squeeze, it is a disappearance. When
            // the obstacles leave no room, the seat keeps a card's worth of table and
            // overlaps rather than vanishing: a player can move a card off a pile they
            // can see, and can do nothing at all with one they cannot.
            float floor = baseCardW * 1.4f;
            if (yMax - yMin < floor)
            {
                float mid = (yMin + yMax) / 2f;
                (yMin, yMax) = (MathF.Max(0f, mid - floor / 2f), MathF.Min(H, mid + floor / 2f));
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
                // What the seat uses, squeezed into what it has. Scaling the whole
                // table height instead threw away the margin the seat was not using:
                // Golf's side player, whose column holds one grid and an empty slot,
                // got a grid a third the size of the one across the table from it.
                var used = Occupied(placed, spot.Seat.Index);
                float span = MathF.Max(1f, used.Bottom - used.Top);
                float k    = (band.Max - band.Min) / span;
                final = new SKRect(bounds.Left,
                                   band.Min + (bounds.Top    - used.Top) * k,
                                   bounds.Right,
                                   band.Min + (bounds.Bottom - used.Top) * k);
            }

            // The sliver the band tolerated: an obstacle lapping a little way into this
            // column is gone round sideways rather than by giving up the whole height.
            // Trimming the edge costs a side seat a few pixels of width; treating it as
            // a full block cost it the table.
            if (spot.Seat.Side is "left" or "right")
                foreach (var ob in fixedObstacles)
                {
                    if (ob.Bottom <= final.Top || ob.Top >= final.Bottom) continue;
                    if (ob.Right <= final.Left || ob.Left >= final.Right) continue;

                    if (spot.Seat.Side == "left") final.Right = MathF.Min(final.Right, ob.Left - 4f);
                    else                          final.Left  = MathF.Max(final.Left,  ob.Right + 4f);
                }

            layouts.Add(Describe(state, spot, final, baseCardW, info));
        }

        return layouts;
    }


    /// <summary>
    /// The vertical run a seat's zones actually cover, before any squeeze. A seat is
    /// compressed into the room it has by how much of the table it uses, not by how
    /// much of the table exists.
    /// </summary>
    private static (float Top, float Bottom) Occupied(
        List<(Spot Spot, SKRect Bounds)> placed, int seatIndex)
    {
        float top = float.MaxValue, bottom = float.MinValue;
        foreach (var (spot, bounds) in placed)
        {
            if (spot.Seat.Index != seatIndex) continue;
            top    = MathF.Min(top, bounds.Top);
            bottom = MathF.Max(bottom, bounds.Bottom);
        }
        return top <= bottom ? (top, bottom) : (0f, 1f);
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

    /// <summary>
    /// A phone held upright: the person at this screen at the bottom as ever, and every
    /// opponent across the top in a cell of their own — one full-width row each for up
    /// to three, two to a row beyond that. The sides of a portrait screen are too narrow
    /// to seat anyone; its height is what there is to spare.
    ///
    /// The cells stop short of the middle: of the centre band (40% down) where nothing is
    /// played to it, and of the compass of trick spots around it (24% down) where
    /// something is. One opponent is the desktop table already — across, at the top —
    /// and is left as it is.
    /// </summary>
    private static (List<Seat> Seats, Dictionary<string, Seat> Teams) PortraitSeatMap(
        GameState state, float W, float H, float baseCardW)
    {
        int players = state.Players.Count;
        var desk    = SeatMap(players);
        if (players <= 2) return (desk, []);

        bool tricks = state.Zones.Values.Any(z => z.OwnerId is not null && PlayedToTheMiddle(z, z.Definition?.Layout?.Region));
        float depth = tricks ? 0.29f : 0.38f;

        // Who goes where, by seat round the table from the person at this screen. A seat
        // with a role — Blackjack's dealer — is who everyone plays against, and gets a
        // row of its own nearest the middle rather than a corner cell.
        int viewer = Math.Max(0, state.Players.FindIndex(p => p.Id == state.Viewer));
        var people = new List<int>();
        var roles  = new List<int>();
        for (int k = 1; k < players; k++)
            (state.Players[(viewer + k) % players].Role is null ? people : roles).Add(k);

        // A team's zones — Hand and Foot's melds — are the whole team's, and a strip of
        // thirteen slots squeezed into one player's cell is unreadable. Each other team
        // gets a row across the full width, between the players and the middle.
        var myTeam = state.Teams.FirstOrDefault(t => t.PlayerIds.Contains(state.Viewer));
        // Only a team that lays something out to be read: a team's pile of won tricks sits
        // beside its first player as it does at a desktop.
        var teams  = state.Teams
            .Where(t => t != myTeam && state.Zones.Values.Any(z => z.OwnerId == t.Id && z.Type is "spread" or "grid"))
            .ToList();
        // Rows are shared out by weight: the dealer is what everyone plays against and gets
        // twice a player's height; another team's melds, which are read, get two and a
        // half — a player's own row is mostly a hand of backs.
        const float RoleWeight = 2f, TeamWeight = 2.5f;
        float fullRows = teams.Count * TeamWeight + roles.Count * RoleWeight;

        // As many columns as give the players' cards the most room. Full-width rows suit
        // a seat that is only a hand; a hand with a meld strip under it is squashed flat
        // in a row, and two to a row gives it the height back.
        var places = state.Zones.Values
            .Where(z => z.OwnerId is { } o && teams.All(t => t.Id != o) && !PlayedToTheMiddle(z, z.Definition?.Layout?.Region))
            .DistinctBy(z => z.Id.Split(':')[0])
            .Select(z => ResolvePlace(z, owned: true, portrait: true).Place)
            .ToList();
        float RowsFor(int c) => (people.Count + c - 1) / c + fullRows;
        int cols = people.Count == 0 ? 1 : Enumerable.Range(1, Math.Min(3, people.Count))
            .Select(c => (Cols: c, Card: SmallestCard(places, c, RowsFor(c), depth, W, H, baseCardW)))
            .Aggregate((best, next) => next.Card > best.Card + 2f ? next : best)
            .Cols;
        float rowH = depth / RowsFor(cols);

        static SKRect Padded(SKRect cell)
        {
            // A margin inside each cell, so neighbours' cards and name plates do not touch.
            cell.Inflate(-cell.Width * 0.02f, -cell.Height * 0.07f);
            return cell;
        }

        var seats = new Seat[players];
        seats[0] = desk[0];
        for (int i = 0; i < people.Count; i++)
        {
            int r = i / cols, c = i % cols;
            // A short last row is centred rather than left-aligned.
            int inRow   = Math.Min(cols, people.Count - r * cols);
            float width = 1f / cols;
            float left  = (1f - inRow * width) / 2f + c * width;
            int k       = people[i];
            seats[k] = new Seat(k, "top", 180f, Cell: Padded(new SKRect(left, r * rowH, left + width, (r + 1) * rowH)),
                                Compass: desk[k]);
        }

        float y = (people.Count + cols - 1) / cols * rowH;
        var teamSeats = new Dictionary<string, Seat>();
        foreach (var team in teams)
        {
            teamSeats[team.Id] = new Seat(players + teamSeats.Count, "top", 180f,
                                          Cell: Padded(new SKRect(0f, y, 1f, y + rowH * TeamWeight)));
            y += rowH * TeamWeight;
        }
        foreach (int k in roles)
        {
            seats[k] = new Seat(k, "top", 180f, Cell: Padded(new SKRect(0f, y, 1f, y + rowH * RoleWeight)),
                                Compass: desk[k]);
            y += rowH * RoleWeight;
        }

        return ([.. seats], teamSeats);
    }

    /// <summary>
    /// The width of the smallest card an opponent's zones would draw, seated in cells of
    /// <paramref name="cols"/> by <paramref name="rows"/> — what choosing the columns weighs.
    /// </summary>
    private static float SmallestCard(List<PlaceDefinition> places, int cols, float rows, float depth,
                                      float W, float H, float baseCardW)
    {
        if (places.Count == 0) return baseCardW;
        var unit  = new SKRect(0, 0, 1, 1);
        var rects = places.Select(p => ResolveTable(ToSeatSpace(p, new Seat(0, "top", 180f)), unit)).ToList();
        float top = MathF.Max(0f, rects.Min(r => r.Top)), bottom = rects.Max(r => r.Bottom);
        float k   = (depth / rows) * 0.86f / MathF.Max(0.01f, bottom - top);
        float cellW = 0.96f / cols;

        return rects.Min(r => MathF.Min(baseCardW, MathF.Min(r.Width * cellW * W, r.Height * k * H / 1.4f)));
    }

    /// <summary>
    /// A zone a seat plays into the middle of the table — a trick — rather than one it
    /// keeps in front of itself. On a phone it stays in the compass around the centre,
    /// where the cards meet, rather than going up into its owner's cell.
    /// </summary>
    private static bool PlayedToTheMiddle(Zone zone, string? region)
        => zone.Type == "trick" || region == "seat-play";

    private static bool PlayedToTheMiddle(Spot spot) => PlayedToTheMiddle(spot.Zone, spot.Region);

    /// <summary>
    /// For each opponent's cell, the run of the top edge's depth (0 at the edge) that its
    /// zones cover, as they would sit at a desktop table. The cell holds exactly that run,
    /// scaled to fit: a Hearts seat that is only a hand fills its row with the hand, where
    /// mapping the whole seat band in would leave two thirds of the row empty.
    /// </summary>
    private static Dictionary<int, (float Top, float Bottom)> CellExtents(List<Spot> spots)
    {
        var extents = new Dictionary<int, (float, float)>();
        foreach (var spot in spots)
        {
            if (spot.Seat.Cell is null || PlayedToTheMiddle(spot)) continue;

            // Turned to the top edge: y from the edge is (1 − y) of the bottom-seat place.
            var r = ResolveTable(ToSeatSpace(spot.Place, spot.Seat with { Slots = 1 }), new SKRect(0, 0, 1, 1));
            var (top, bottom) = extents.GetValueOrDefault(spot.Seat.Index, (float.MaxValue, float.MinValue));
            extents[spot.Seat.Index] = (MathF.Min(top, r.Top), MathF.Max(bottom, r.Bottom));
        }
        foreach (var (seat, (top, bottom)) in extents.ToList())
            extents[seat] = (MathF.Max(0f, top), MathF.Max(MathF.Max(0f, top) + 0.01f, bottom));
        return extents;
    }

    /// <summary>
    /// A bottom-seat place fitted into an opponent's cell: the seat's used run
    /// (<paramref name="extent"/>) spans the cell's height, and the table's width the
    /// cell's width. Turned end over end — the hand at the cell's outer edge, as a seat
    /// across the table has it — but not mirrored: a cell reads left to right like the
    /// person's own seat, so a split hand sits to the right of the hand in every cell.
    /// </summary>
    private static PlaceDefinition ToCellSpace(PlaceDefinition p, SKRect cell, (float Top, float Bottom) extent)
    {
        float x = Pct(p.X, 0.5f), y = 1f - Pct(p.Y, 0.5f);
        var (ax, ay) = AnchorFractions(p.Anchor);
        float k = cell.Height / (extent.Bottom - extent.Top);

        return new PlaceDefinition
        {
            X = Fmt(cell.Left + x * cell.Width),
            Y = Fmt(cell.Top + (y - extent.Top) * k),
            Anchor = AnchorName(ax, 1f - ay),
            Width  = p.Width  is { } w ? Fmt(Pct(w, 0.2f) * cell.Width) : null,
            Height = p.Height is { } h ? Fmt(Pct(h, 0.2f) * k) : null,
        };
    }

    private static PlaceDefinition PullTowardMiddle(PlaceDefinition p, float k) => new()
    {
        X = p.X, Y = Fmt(0.5f + (Pct(p.Y, 0.5f) - 0.5f) * k),
        Anchor = p.Anchor, Width = p.Width, Height = p.Height,
    };

    private static Seat SeatOf(GameState state, List<Seat> seats, string ownerId)
    {
        int idx = state.Players.FindIndex(p => p.Id == ownerId);
        if (idx < 0)
        {
            // A team-owned zone sits with the team's first player — the viewer, when
            // it is the viewer's team.
            var team = state.Teams.FirstOrDefault(t => t.Id == ownerId);
            if (team is not null)
                idx = team.PlayerIds.Contains(state.Viewer)
                    ? state.Players.FindIndex(p => p.Id == state.Viewer)
                    : state.Players.FindIndex(p => team.PlayerIds.Contains(p.Id));
        }

        // The table turns so the viewer sits at the bottom and everyone else keeps
        // their place around it. Seat 0 is the viewer unless a view says otherwise.
        int viewer = Math.Max(0, state.Players.FindIndex(p => p.Id == state.Viewer));
        if (idx >= 0 && state.Players.Count > 0)
            idx = (idx - viewer + state.Players.Count) % state.Players.Count;
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

    /// <summary>Regions that differ on a phone held upright; the rest are as above.</summary>
    private static readonly Dictionary<string, PlaceDefinition> PortraitRegions = new()
    {
        ["center"]       = P("50%", "50%", "center", "26%", "13%"),
        ["center-left"]  = P("21%", "50%", "center", "14%", "13%"),
        ["center-right"] = P("79%", "50%", "center", "14%", "13%"),
    };

    private static PlaceDefinition P(string x, string y, string anchor, string w, string h)
        => new() { X = x, Y = y, Anchor = anchor, Width = w, Height = h };

    private static (PlaceDefinition Place, string? Region) ResolvePlace(Zone zone, bool owned, bool portrait)
    {
        var layout = zone.Definition?.Layout;

        // A phone held upright takes the definition's portrait layout where it gives one.
        if (portrait && layout?.Portrait is { } tall) layout = tall;

        if (layout?.Place is { } exact) return (exact, null);

        string region = layout?.Region ?? DefaultRegion(zone, owned);

        // Upright, the centre band is shorter — still a card's height and more — so the
        // compass of trick spots can close in around it.
        if (portrait && PortraitRegions.TryGetValue(region, out var upright)) return (upright, region);
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

    private static ZoneLayout Describe(GameState state, Spot spot, SKRect bounds, float baseCardW, SKImageInfo table)
    {
        var  zone  = spot.Zone;
        var  seat  = spot.Seat;
        bool owned = zone.OwnerId is not null;
        bool mine  = owned && seat.Index == 0;

        // Cards are sized to the bounds they must fit, never larger than the table's base.
        // Hands stand upright at every seat, so a side seat's band — tall and narrow on
        // screen — is fitted as it is, and its hand closes up into a compact fan.
        //
        // A face-down pile (a hand arranged as a stack — Hand and Foot's foot) keeps being
        // turned to face its seat: backs read the same either way, and a spot declared
        // for the bottom seat and turned a quarter is wide and short, where an upright
        // card would shrink to a sliver.
        bool pile  = zone.Type == "hand" && zone.Definition?.Arrangement == "stack";
        bool turns = pile && seat.Side is "left" or "right";

        // A hand at a declared spot, at a side seat — Golf's slot for the card just drawn.
        // The spot is measured for the bottom seat; turned a quarter and squeezed into the
        // side column it came out wide and short, and an upright card fitted to it was a
        // third the size of the same slot at the top and bottom. Only its position turns:
        // it keeps the size the bottom seat's has, centred where the turned spot landed.
        // The same goes for a seat sharing the top edge, whose spot is narrowed to its slice.
        bool squeezed = (seat.Side is "left" or "right" && bounds.Width > bounds.Height) || seat.Slots > 1;
        if (!pile && zone.Type == "hand" && squeezed
            && zone.Definition?.Layout?.Place is { Width: { } pw, Height: { } ph })
        {
            float w = Pct(pw, 0.1f) * table.Width, h = Pct(ph, 0.1f) * table.Height;
            float left = Math.Clamp(bounds.MidX - w / 2f, 0f, MathF.Max(0f, table.Width  - w));
            float top  = Math.Clamp(bounds.MidY - h / 2f, 0f, MathF.Max(0f, table.Height - h));
            bounds = new SKRect(left, top, left + w, top + h);
        }
        float fitW = turns ? bounds.Height : bounds.Width;
        float fitH = turns ? bounds.Width  : bounds.Height;
        float cardW = MathF.Min(baseCardW, MathF.Min(fitW, fitH / 1.4f));
        float cardH = cardW * 1.4f;

        // Face-up if the zone lets everyone see, or lets its owner see and the owner is
        // the person at this screen, or a showdown has turned it over.
        var  owner    = state.Players.FirstOrDefault(p => p.Id == zone.OwnerId);
        bool revealed = owner is not null && IsShowdownRevealed(state, owner.Id);
        bool faceUp   = zone.Visibility is "all" or "top"
                     || (zone.Visibility is "owner" or "mixed" && mine)
                     // A pile only the dealer may look into. The mask already knew this
                     // word and the table did not, so such a zone drew face-down for
                     // everybody — including the dealer it was turned for.
                     || (zone.Visibility == "top_to_dealer" && state.DealerId == state.Viewer)
                     || revealed;

        // What the table says about a zone is the definition's to say. With no label
        // declared, a hand falls back to its owner's name — the seat is named once — and
        // every other zone to nothing. The renderer invents no words of its own.
        string? label = owned && zone.Type == "hand"
            ? (owner?.Name ?? state.Teams.FirstOrDefault(t => t.Id == zone.OwnerId)?.Name)
            : null;

        bool current = owner is not null && state.CurrentPlayer.Id == owner.Id;

        return new ZoneLayout(zone, bounds, cardW, cardH,
            zone.IsEmpty && !ZoneSlots.IsSlotted(zone.Definition) ? ZoneRenderHint.Empty : HintFor(zone),
            FaceUp: faceUp,
            // Upright at every seat but a face-down pile. Hands used to be turned to face
            // their players — side seats sideways, the top seat upside down — which made
            // them hard to read and turned the names with them, too small to read and
            // pushed off the table.
            RotationDegrees: pile && !revealed ? seat.Rotation : 0f,
            Label: label,
            IsCurrentPlayer: current && zone.Type == "hand",
            SeatQuarterTurns: owned ? (int)(seat.Rotation / 90f) : 0,
            SeatSide: owned ? seat.Side : null);
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
