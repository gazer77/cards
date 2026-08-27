namespace Cards.Engine;

/// <summary>
/// Converts a live <see cref="GameState"/> to and from the flat
/// <see cref="SavedGameState"/> DTO.
///
/// Pure — no IO, no platform. Disk persistence (Cards.Services.GameSaveService)
/// and the multiplayer state-sync path both build on this, so the two can never
/// drift into producing different shapes for the same state.
/// </summary>
public static class GameStateSerializer
{
    /// <summary>Captures a live state as a serializable DTO.</summary>
    public static SavedGameState Snapshot(
        GameState state, int playerCount, IReadOnlyList<string> enabledRules)
        => new()
        {
            GameId       = state.GameId,
            PlayerCount  = playerCount,
            EnabledRules = enabledRules.ToList(),
            PhaseId      = state.CurrentPhaseId,
            PlayerIndex  = state.CurrentPlayerIndex,
            RoundNumber  = state.RoundNumber,
            DealerId     = state.DealerId,
            Scores       = new Dictionary<string, int>(state.Scores),
            Metadata     = new Dictionary<string, string>(state.Metadata),
            GameLog      = [.. state.GameLog],
            Zones        = state.Zones.Values.Select(z => new SavedZone
            {
                Id         = z.Id,
                Type       = z.Type,
                OwnerId    = z.OwnerId,
                Visibility = z.Visibility,
                Cards      = z.Cards.Select(c => new SavedCard
                {
                    Suit     = (int)c.Suit,
                    Rank     = (int)c.Rank,
                    IsFaceUp = c.IsFaceUp,
                    IsWild   = c.IsWild,
                }).ToList(),
            }).ToList(),
        };

    /// <summary>
    /// Overlays a DTO onto <paramref name="state"/>.
    ///
    /// <paramref name="logic"/>.Initialize runs first: the DTO carries no Players,
    /// Teams or logic-private fields, so Initialize is what rebuilds them. The
    /// zones it creates are then replaced by the saved ones, except any the save
    /// predates — those are kept, so a save written before a zone existed still loads.
    /// </summary>
    public static void Restore(
        GameState state,
        IGameLogic logic,
        SavedGameState dto,
        int playerCount,
        IReadOnlyList<string> enabledRules)
    {
        logic.Initialize(state, playerCount, enabledRules);

        var initZones = new Dictionary<string, Zone>(state.Zones);

        state.Zones.Clear();
        foreach (var sz in dto.Zones)
        {
            var zone = new Zone(sz.Id, sz.Type, sz.OwnerId, sz.Visibility);
            foreach (var sc in sz.Cards)
                zone.Add(new Card((Suit)sc.Suit, (Rank)sc.Rank, sc.IsFaceUp) { IsWild = sc.IsWild });
            state.Zones[sz.Id] = zone;
        }

        // Forward-compatibility: keep zones Initialize created that the save predates.
        foreach (var (id, zone) in initZones)
            state.Zones.TryAdd(id, zone);

        state.CurrentPhaseId     = dto.PhaseId;
        state.CurrentPlayerIndex = dto.PlayerIndex;
        state.RoundNumber        = dto.RoundNumber;
        state.DealerId           = dto.DealerId;

        state.Scores.Clear();
        foreach (var (k, v) in dto.Scores) state.Scores[k] = v;

        state.Metadata.Clear();
        foreach (var (k, v) in dto.Metadata) state.Metadata[k] = v;

        state.GameLog.Clear();
        state.GameLog.AddRange(dto.GameLog);
    }
}
