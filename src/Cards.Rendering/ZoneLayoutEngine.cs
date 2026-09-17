using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

public static class ZoneLayoutEngine
{
    /// <summary>
    /// How much larger meld cards are drawn than the deck and discard beside them.
    /// Melds are the zone a player reads rather than merely recognises. A placeholder
    /// for per-zone, player-settable card size — see plan.md.
    /// </summary>
    private const float MeldCardScale = 1.5f;

    /// <summary>
    /// Share of the center band given over to meld areas. Melds are where a game like
    /// Hand and Foot is actually read, and they only grow; the deck and discard never
    /// need more than one card of width each.
    /// </summary>
    private const float MeldBandShare = 0.55f;

    /// <summary>Breathing room between zones, so they read as distinct places.</summary>
    private const float ZonePadding = 10f;

    public static IReadOnlyList<ZoneLayout> Compute(GameState state, SKImageInfo canvasInfo)
    {
        int playerCount = state.Players.Count;
        return playerCount <= 2
            ? ComputeTwoPlayer(state, canvasInfo)
            : ComputeFourPlayer(state, canvasInfo);
    }

    // ── 2-player layout ───────────────────────────────────────────────────────

    private static IReadOnlyList<ZoneLayout> ComputeTwoPlayer(GameState state, SKImageInfo info)
    {
        float W = info.Width;
        float H = info.Height;
        var layouts = new List<ZoneLayout>();

        float scale = state.Definition.CardScale;
        float cardW = MathF.Min(W * 0.14f, 110f) * scale;
        cardW = MathF.Min(cardW, W * 0.35f); // never wider than 35 % of screen

        var p0 = state.Players[0];
        var p1 = state.Players.Count > 1 ? state.Players[1] : p0;

        var oppPlayZone = state.FindZone($"play:{p1.Id}");
        var plyPlayZone = state.FindZone($"play:{p0.Id}");
        bool hasPlayZones = oppPlayZone is not null || plyPlayZone is not null;

        float oppTop, oppH, centerTop, centerH, handTop, handH;

        if (hasPlayZones)
        {
            // ── Dynamic layout: stack 5 rows of card-height zones ─────────────
            // Rows: opp-hand | opp-play | pot | ply-play | ply-hand
            float gap     = MathF.Max(H * 0.015f, 8f);
            float potFrac = 0.65f; // pot is shorter than a full card

            // Cap cardW so everything fits vertically with a small margin
            float maxCardW = (H * 0.96f - 4f * gap) / ((4f + potFrac) * 1.4f);
            cardW = MathF.Min(cardW, maxCardW);
            float cardH_local = cardW * 1.4f;
            float potH  = cardH_local * potFrac;

            float totalH = cardH_local + gap + cardH_local + gap + potH + gap + cardH_local + gap + cardH_local;
            float startY = MathF.Max((H - totalH) / 2f, gap);

            oppTop         = startY;
            oppH           = cardH_local;
            float oppPlayTop = oppTop  + oppH + gap;
            float oppPlayH   = cardH_local;
            centerTop      = oppPlayTop + oppPlayH + gap;
            centerH        = potH;
            float plyPlayTop = centerTop + centerH + gap;
            float plyPlayH   = cardH_local;
            handTop        = plyPlayTop + plyPlayH + gap;
            handH          = cardH_local;

            // Opponent play zone
            if (oppPlayZone is not null)
            {
                float cx = W / 2f;
                var hint = oppPlayZone.IsEmpty ? ZoneRenderHint.Empty : ZoneHintFor(oppPlayZone);
                layouts.Add(new ZoneLayout(oppPlayZone,
                    new SKRect(cx - cardW * 0.7f, oppPlayTop, cx + cardW * 0.7f, oppPlayTop + oppPlayH),
                    cardW, cardH_local, hint,
                    FaceUp: true, RotationDegrees: 0f, Label: null, IsCurrentPlayer: false));
            }

            // Player play zone
            if (plyPlayZone is not null)
            {
                float cx = W / 2f;
                var hint = plyPlayZone.IsEmpty ? ZoneRenderHint.Empty : ZoneHintFor(plyPlayZone);
                layouts.Add(new ZoneLayout(plyPlayZone,
                    new SKRect(cx - cardW * 0.7f, plyPlayTop, cx + cardW * 0.7f, plyPlayTop + plyPlayH),
                    cardW, cardH_local, hint,
                    FaceUp: true, RotationDegrees: 0f, Label: null, IsCurrentPlayer: false));
            }

            // Opponent hand
            if (state.Players.Count > 1)
            {
                var oppZone = GetPlayerZone(state, "hand", p1.Id);
                if (oppZone is not null)
                {
                    bool oppRevealed = IsShowdownRevealed(state, p1.Id);
                    layouts.Add(new ZoneLayout(oppZone,
                        new SKRect(W * 0.05f, oppTop, W * 0.95f, oppTop + oppH),
                        cardW, cardH_local, ZoneHintFor(oppZone),
                        FaceUp: oppRevealed, RotationDegrees: oppRevealed ? 0f : 180f,
                        Label: p1.Name,
                        IsCurrentPlayer: state.CurrentPlayerIndex == 1));
                }
            }

            // Center zones (pot, etc.)
            PlaceCenterZones(state, layouts,
                new SKRect(W * 0.1f, centerTop, W * 0.9f, centerTop + centerH),
                cardW, cardH_local);

            // Player hand
            var playerZone2 = GetPlayerZone(state, "hand", p0.Id);
            if (playerZone2 is not null)
                layouts.Add(new ZoneLayout(playerZone2,
                    new SKRect(W * 0.02f, handTop, W * 0.98f, handTop + handH),
                    cardW, cardH_local, ZoneHintFor(playerZone2),
                    FaceUp: false, RotationDegrees: 0f,
                    Label: p0.Name,
                    IsCurrentPlayer: state.CurrentPlayerIndex == 0));

            return layouts;
        }
        else
        {
            oppTop    = H * 0.02f;
            oppH      = H * 0.20f;
            centerTop = H * 0.26f;
            // Down to the hand. The band used to stop at 0.68 and leave a wide empty
            // stripe above the hand while the melds inside it were squeezed.
            centerH   = H * 0.58f;
            handTop   = H * 0.87f;
            handH     = H * 0.11f;
        }

        float cardH = cardW * 1.4f;

        // Opponent hand (top, face direction from zone visibility, no rotation in 2-player)
        if (state.Players.Count > 1)
        {
            var oppZone = GetPlayerZone(state, "hand", p1.Id);
            if (oppZone is not null)
            {
                bool oppFaceUp = oppZone.Visibility is "all" or "top"
                              || IsShowdownRevealed(state, p1.Id);
                layouts.Add(new ZoneLayout(oppZone,
                    new SKRect(W * 0.05f, oppTop, W * 0.95f, oppTop + oppH),
                    cardW, cardH, ZoneHintFor(oppZone),
                    FaceUp: oppFaceUp, RotationDegrees: 0f,
                    Label: p1.Name,
                    IsCurrentPlayer: state.CurrentPlayerIndex == 1));
            }
        }

        // Center zones
        PlaceCenterZones(state, layouts,
            new SKRect(W * 0.1f, centerTop, W * 0.9f, centerTop + centerH),
            cardW, cardH);

        // Player hand (bottom, face direction from zone visibility)
        var playerZone = GetPlayerZone(state, "hand", p0.Id);
        if (playerZone is not null)
        {
            bool plyFaceUp = playerZone.Visibility is "all" or "top" or "owner" or "mixed";
            layouts.Add(new ZoneLayout(playerZone,
                new SKRect(W * 0.02f, handTop, W * 0.98f, handTop + handH),
                cardW, cardH, ZoneHintFor(playerZone),
                FaceUp: plyFaceUp, RotationDegrees: 0f,
                Label: p0.Name,
                IsCurrentPlayer: state.CurrentPlayerIndex == 0));
        }

        return layouts;
    }

    // ── 4-player layout ───────────────────────────────────────────────────────

    private static IReadOnlyList<ZoneLayout> ComputeFourPlayer(GameState state, SKImageInfo info)
    {
        float W = info.Width;
        float H = info.Height;
        var layouts = new List<ZoneLayout>();

        float cardW = MathF.Min(W * 0.13f, 100f) * state.Definition.CardScale;
        cardW = MathF.Min(cardW, W * 0.20f);
        float cardH = cardW * 1.4f;

        float sideW  = W * 0.15f;
        float sideH  = H * 0.42f;
        float topH   = H * 0.18f;
        float handH  = H * 0.11f;
        float centerX = W * 0.18f;
        float centerW = W * 0.64f;

        // Player 0 — bottom, face-up
        AddHandLayout(state, layouts, 0, 0f,
            new SKRect(W * 0.02f, H - handH - H * 0.02f, W * 0.98f, H - H * 0.02f),
            cardW, cardH, faceUp: true);

        // Player 1 — right
        if (state.Players.Count > 1)
            AddHandLayout(state, layouts, 1, 90f,
                new SKRect(W - sideW - W * 0.01f, H * 0.28f, W - W * 0.01f, H * 0.28f + sideH),
                cardW, cardH, faceUp: false);

        // Player 2 — top
        if (state.Players.Count > 2)
            AddHandLayout(state, layouts, 2, 180f,
                new SKRect(centerX, H * 0.02f, centerX + centerW, H * 0.02f + topH),
                cardW, cardH, faceUp: false);

        // Player 3 — left
        if (state.Players.Count > 3)
            AddHandLayout(state, layouts, 3, 270f,
                new SKRect(W * 0.01f, H * 0.28f, W * 0.01f + sideW, H * 0.28f + sideH),
                cardW, cardH, faceUp: false);

        // Per-player trick zones in compass positions (if this game uses them)
        PlaceTrickZonesCompass(state, layouts, W, H, cardW, cardH);

        // Center zones
        PlaceCenterZones(state, layouts,
            new SKRect(centerX, H * 0.26f, centerX + centerW, H * 0.72f),
            cardW, cardH);

        return layouts;
    }

    // ── Meld placement ────────────────────────────────────────────────────────

    /// <summary>
    /// Meld areas in seating order — team-owned where the game has teams, per-player
    /// otherwise, with one shared zone as the fallback.
    /// </summary>
    private static List<Zone> MeldZones(GameState state)
    {
        var zones = new List<Zone>();

        foreach (var team in state.Teams)
            if (state.FindZone($"meld:{team.Id}") is { } z) zones.Add(z);

        foreach (var player in state.Players)
            if (state.FindZone($"meld:{player.Id}") is { } z) zones.Add(z);

        if (zones.Count == 0 && state.FindZone("meld") is { } shared) zones.Add(shared);

        return zones;
    }

    /// <summary>
    /// Splits the meld band between the sides that own melds, each getting an equal
    /// share. Every side keeps its area whether or not it has melded yet, so a meld
    /// appearing does not shove the other side's melds sideways mid-game.
    /// </summary>
    private static void PlaceMeldZones(
        GameState state, List<ZoneLayout> layouts, List<Zone> meldZones,
        SKRect band, float cardW)
    {
        float shareW = (band.Width - ZonePadding * (meldZones.Count - 1)) / meldZones.Count;
        float x      = band.Left;

        foreach (var zone in meldZones)
        {
            var bounds = new SKRect(x + ZonePadding, band.Top + ZonePadding,
                                    x + shareW - ZonePadding, band.Bottom - ZonePadding);
            x += shareW + ZonePadding;

            layouts.Add(new ZoneLayout(zone, bounds, cardW, cardW * 1.4f,
                zone.IsEmpty ? ZoneRenderHint.Empty : ZoneRenderHint.Spread,
                FaceUp: true, RotationDegrees: 0f,
                Label: MeldLabelFor(state, zone),
                IsCurrentPlayer: false));
        }
    }

    /// <summary>Whose melds these are, in the player's own words rather than a zone id.</summary>
    private static string MeldLabelFor(GameState state, Zone zone)
    {
        var team = state.Teams.FirstOrDefault(t => t.Id == zone.OwnerId);
        if (team is not null) return team.Name.ToUpperInvariant();

        var owner = state.Players.FirstOrDefault(p => p.Id == zone.OwnerId);
        return (owner?.Name ?? "MELDS").ToUpperInvariant();
    }

    // ── Center zone placement ─────────────────────────────────────────────────

    private static void PlaceCenterZones(GameState state, List<ZoneLayout> layouts,
        SKRect centerBounds, float cardW, float cardH)
    {
        var centerOrder = new[] { "community", "trick", "shared_table", "deck", "discard", "won_tricks", "pot" };

        var centerZones = centerOrder
            .Select(id => state.FindZone(id))
            .Where(z => z is not null)
            .Cast<Zone>()
            .ToList();

        // Per-player spread zones (e.g. books) are placed in the center alongside shared zones
        foreach (var player in state.Players)
        {
            var z = state.FindZone($"books:{player.Id}");
            if (z is not null) centerZones.Add(z);
        }

        // Meld areas get a band of their own further down, so they are collected
        // separately rather than joining the row the deck and discard share.
        var meldZones = MeldZones(state);
        if (meldZones.Count > 0)
        {
            // A meld zone was weighted by its card count, so twenty melded cards left
            // the deck and discard a twentieth of the width each and drawn on top of
            // one another. Melds outgrow every other zone, so they cannot share a row
            // proportioned by contents.
            float meldTop = centerBounds.Top + centerBounds.Height * (1f - MeldBandShare);
            PlaceMeldZones(state, layouts, meldZones,
                new SKRect(centerBounds.Left, meldTop, centerBounds.Right, centerBounds.Bottom),
                cardW * MeldCardScale);

            centerBounds = new SKRect(centerBounds.Left, centerBounds.Top,
                                      centerBounds.Right, meldTop - ZonePadding);
        }

        if (centerZones.Count == 0) return;

        float cy = centerBounds.MidY;

        // Spread zones (e.g. community cards) need room for multiple cards side-by-side.
        // Weight them by expected card slots so they get proportionally more width;
        // deck/pile/pot zones each get weight 1.
        const int spreadMinSlots = 5;
        float[] weights = centerZones.Select(z =>
            ZoneHintFor(z) == ZoneRenderHint.Spread
                ? (float)Math.Max(z.Cards.Count, spreadMinSlots)
                : 1f).ToArray();
        float totalWeight = weights.Sum();
        float unitW = centerBounds.Width / totalWeight;

        // compact flag is used only for non-spread label decisions (e.g. books labels)
        float stackSpacing = centerBounds.Width / (centerZones.Count + 1);
        bool compact = stackSpacing < cardW * 2.5f;

        float xLeft = centerBounds.Left;
        for (int i = 0; i < centerZones.Count; i++)
        {
            var zone   = centerZones[i];
            float zoneW = weights[i] * unitW;
            float zCardH = cardH;

            // Padded so neighbours read as distinct places rather than one long strip —
            // and so the deck and discard stop touching.
            var bounds  = new SKRect(xLeft + ZonePadding, cy - zCardH / 2f,
                                     xLeft + zoneW - ZonePadding, cy + zCardH / 2f);
            xLeft += zoneW;

            // Spread zones always render as spread (they now have sufficient bounds width).
            // Non-spread zones collapse to stack only when compact.
            var hint = ZoneHintFor(zone);
            if (compact && hint != ZoneRenderHint.Spread)
                hint = ZoneRenderHint.Stack;

            bool faceUp = zone.Visibility is "top" or "all";
            string? label = ZoneLabelFor(zone.Id)
                         ?? (zone.Id.StartsWith("books:") ? BooksLabelFor(state, zone, compact) : null);

            layouts.Add(new ZoneLayout(zone, bounds, cardW, zCardH, hint,
                FaceUp: faceUp, RotationDegrees: 0f,
                Label: label,
                IsCurrentPlayer: false));
        }
    }

    private static string BooksLabelFor(GameState state, Zone zone, bool compact = false)
    {
        var owner = state.Players.FirstOrDefault(p => p.Id == zone.OwnerId);
        string name = owner?.Name.ToUpperInvariant() ?? "BOOKS";
        if (compact && owner is not null)
        {
            int score = state.GetScore(owner.Id);
            return score > 0 ? $"{name}: {score}" : $"{name} BOOKS";
        }
        return $"{name} BOOKS";
    }

    // ── Per-player trick zone compass layout ──────────────────────────────────

    /// <summary>
    /// Places each player's trick zone in a compass pattern (bottom / right / top / left)
    /// within the center play area.  Does nothing if the game uses a shared trick zone.
    /// </summary>
    private static void PlaceTrickZonesCompass(GameState state, List<ZoneLayout> layouts,
        float W, float H, float cardW, float cardH)
    {
        // Compass positions as fractions of screen (cx, cy) per player index.
        // P0 = bottom, P1 = right, P2 = top, P3 = left — matches the seating layout.
        ReadOnlySpan<(float fx, float fy)> compass =
        [
            (0.50f, 0.635f),  // Player 0 — bottom
            (0.715f, 0.490f), // Player 1 — right
            (0.50f, 0.345f),  // Player 2 — top
            (0.285f, 0.490f), // Player 3 — left
        ];

        for (int i = 0; i < state.Players.Count && i < compass.Length; i++)
        {
            var player = state.Players[i];
            var zone   = state.FindZone($"trick:{player.Id}");
            if (zone is null) continue;

            float cx = W * compass[i].fx;
            float cy = H * compass[i].fy;
            var   bounds = new SKRect(cx - cardW / 2f, cy - cardH / 2f,
                                      cx + cardW / 2f, cy + cardH / 2f);
            var hint = zone.IsEmpty ? ZoneRenderHint.Empty : ZoneRenderHint.Spread;

            layouts.Add(new ZoneLayout(zone, bounds, cardW, cardH, hint,
                FaceUp: true, RotationDegrees: 0f, Label: null, IsCurrentPlayer: false));
        }
    }

    /// <summary>
    /// Returns true when a showdown has been revealed and the given player did not fold,
    /// meaning their hand should be shown face-up to all players.
    /// </summary>
    private static bool IsShowdownRevealed(GameState state, string playerId)
        => state.Metadata.GetValueOrDefault("showdown_revealed") == "true"
        && state.Metadata.GetValueOrDefault($"bet_folded:{playerId}") != "true";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddHandLayout(GameState state, List<ZoneLayout> layouts,
        int playerIndex, float rotation, SKRect bounds, float cardW, float cardH, bool faceUp)
    {
        if (playerIndex >= state.Players.Count) return;
        var player = state.Players[playerIndex];
        var zone = GetPlayerZone(state, "hand", player.Id)
                ?? GetPlayerZone(state, "foot", player.Id);
        if (zone is null) return;

        // During showdown reveal, show non-folded opponents face-up (rotation cleared so cards read correctly).
        bool revealed = !faceUp && IsShowdownRevealed(state, player.Id);
        layouts.Add(new ZoneLayout(zone, bounds, cardW, cardH, ZoneHintFor(zone),
            FaceUp: faceUp || revealed,
            RotationDegrees: revealed ? 0f : rotation,
            Label: player.Name,
            IsCurrentPlayer: state.CurrentPlayerIndex == playerIndex));
    }

    private static Zone? GetPlayerZone(GameState state, string zoneId, string playerId)
        => state.FindZone($"{zoneId}:{playerId}") ?? state.FindZone(zoneId);

    private static ZoneRenderHint ZoneHintFor(Zone zone) => zone.Type switch
    {
        "deck"   => ZoneRenderHint.Stack,
        "pile"   => zone.Visibility == "count_only" ? ZoneRenderHint.CountOnly : ZoneRenderHint.Stack,
        "spread" => ZoneRenderHint.Spread,
        "trick"  => ZoneRenderHint.Spread,
        "hand"   => ZoneRenderHint.Fan,
        _        => zone.IsEmpty ? ZoneRenderHint.Empty : ZoneRenderHint.Stack,
    };

    private static string? ZoneLabelFor(string zoneId) => zoneId switch
    {
        "deck"       => "DECK",
        "discard"    => "DISCARD",
        "community"  => null,
        "trick"      => null,
        "won_tricks" => "TRICKS",
        _            => null,
    };
}
