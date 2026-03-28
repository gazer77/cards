using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

public static class ZoneLayoutEngine
{
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
                    layouts.Add(new ZoneLayout(oppZone,
                        new SKRect(W * 0.05f, oppTop, W * 0.95f, oppTop + oppH),
                        cardW, cardH_local, ZoneHintFor(oppZone),
                        FaceUp: false, RotationDegrees: 180f,
                        Label: p1.Name,
                        IsCurrentPlayer: state.CurrentPlayerIndex == 1));
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
            centerTop = H * 0.30f;
            centerH   = H * 0.38f;
            handTop   = H * 0.76f;
            handH     = H * 0.22f;
        }

        float cardH = cardW * 1.4f;

        // Opponent hand (top, face direction from zone visibility, no rotation in 2-player)
        if (state.Players.Count > 1)
        {
            var oppZone = GetPlayerZone(state, "hand", p1.Id);
            if (oppZone is not null)
            {
                bool oppFaceUp = oppZone.Visibility is "all" or "top";
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
            bool plyFaceUp = playerZone.Visibility is "all" or "top" or "owner";
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
        float handH  = H * 0.22f;
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

        // Center zones
        PlaceCenterZones(state, layouts,
            new SKRect(centerX, H * 0.26f, centerX + centerW, H * 0.72f),
            cardW, cardH);

        return layouts;
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

        if (centerZones.Count == 0) return;

        float spacing = centerBounds.Width / (centerZones.Count + 1);
        float cy = centerBounds.MidY;

        for (int i = 0; i < centerZones.Count; i++)
        {
            var zone = centerZones[i];
            float cx = centerBounds.Left + spacing * (i + 1);
            var bounds = new SKRect(cx - cardW / 2f, cy - cardH / 2f, cx + cardW / 2f, cy + cardH / 2f);

            var hint  = ZoneHintFor(zone);
            bool faceUp = zone.Visibility is "top" or "all";

            layouts.Add(new ZoneLayout(zone, bounds, cardW, cardH, hint,
                FaceUp: faceUp, RotationDegrees: 0f,
                Label: ZoneLabelFor(zone.Id),
                IsCurrentPlayer: false));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddHandLayout(GameState state, List<ZoneLayout> layouts,
        int playerIndex, float rotation, SKRect bounds, float cardW, float cardH, bool faceUp)
    {
        if (playerIndex >= state.Players.Count) return;
        var player = state.Players[playerIndex];
        var zone = GetPlayerZone(state, "hand", player.Id)
                ?? GetPlayerZone(state, "foot", player.Id);
        if (zone is null) return;

        layouts.Add(new ZoneLayout(zone, bounds, cardW, cardH, ZoneHintFor(zone),
            FaceUp: faceUp, RotationDegrees: rotation,
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
