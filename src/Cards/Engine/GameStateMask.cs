namespace Cards.Engine;

/// <summary>
/// Creates read-only masked snapshots of <see cref="GameState"/> for use by
/// <see cref="IPlayerAgent"/> implementations.
///
/// A masked snapshot is a shallow copy of the state in which zones not visible
/// to the specified viewer have their card lists cleared, preventing AI agents
/// from accessing information they would not have at a real table.
///
/// Zone visibility rules (from each zone's <c>Visibility</c> field):
///   all        — full card list visible to everyone
///   owner      — full card list visible only to the zone's owner
///   top        — only the top card is visible
///   none       — no cards visible
///   count_only — no cards visible
///
/// Regardless of visibility, the actual card count of every zone is written into
/// <c>Metadata["zone_count:{zoneId}"]</c> so agents can reason about pile sizes.
/// </summary>
public static class GameStateMask
{
    public static GameState CreateViewFor(GameState source, string viewerId)
    {
        var masked = new GameState
        {
            GameId     = source.GameId,
            Definition = source.Definition,
        };

        masked.CurrentPhaseId     = source.CurrentPhaseId;
        masked.CurrentPlayerIndex = source.CurrentPlayerIndex;
        masked.RoundNumber        = source.RoundNumber;
        masked.DealerId           = source.DealerId;

        masked.Players.AddRange(source.Players);

        foreach (var kv in source.Scores)           masked.Scores[kv.Key]            = kv.Value;
        foreach (var kv in source.Metadata)         masked.Metadata[kv.Key]          = kv.Value;
        foreach (var kv in source.EnabledHouseRules) masked.EnabledHouseRules[kv.Key] = kv.Value;

        foreach (var kv in source.Zones)
        {
            var src  = kv.Value;
            var dest = new Zone(src.Id, src.Type, src.OwnerId, src.Visibility);

            bool seeAll = src.Visibility switch
            {
                "all"          => true,
                "owner"        => src.OwnerId == viewerId,
                "top_to_dealer"=> source.DealerId == viewerId,
                _              => false,
            };

            if (seeAll)
                dest.AddRange(src.Cards);
            else if ((src.Visibility is "top" or "top_to_dealer") && src.TopCard is { } top)
                dest.Add(top);
            // else: dest stays empty (count_only / none / non-matching owner)

            masked.Zones[src.Id] = dest;

            // Always expose count so agents can reason about pile sizes
            masked.Metadata[$"zone_count:{src.Id}"] = src.Count.ToString();
        }

        return masked;
    }
}
