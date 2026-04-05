namespace Cards.Engine;

/// <summary>
/// A group of players that share a score and bid/trick count in team games.
/// Teams are created by <see cref="SetupEngine"/> when the game definition
/// includes a <c>teams</c> configuration block.
///
/// Scores are tracked in <see cref="GameState.Scores"/> under the team's
/// <see cref="Id"/> (e.g. <c>"team0"</c>, <c>"team1"</c>).
/// </summary>
public sealed class Team
{
    public string   Id      { get; }
    public string   Name    { get; }
    public int      Index   { get; }
    public List<string> PlayerIds { get; } = [];

    public Team(int index)
    {
        Index = index;
        Id    = $"team{index}";
        Name  = $"Team {index + 1}";
    }

    public bool Contains(string playerId) => PlayerIds.Contains(playerId);
}
