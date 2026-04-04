using Cards.Models;

namespace Cards.Engine;

public class GameState
{
    public required string GameId { get; init; }
    public required GameDefinition Definition { get; init; }

    public List<Player> Players { get; } = [];
    public Dictionary<string, Zone> Zones { get; } = [];
    public string CurrentPhaseId { get; set; } = string.Empty;
    public int CurrentPlayerIndex { get; set; }
    public int RoundNumber { get; set; } = 1;

    /// <summary>
    /// Player ID of the current dealer.  Used for zone visibility rules
    /// like <c>top_to_dealer</c> and for dealer-rotation logic between rounds.
    /// Null until the first dealer is assigned.
    /// </summary>
    public string? DealerId { get; set; }
    public Dictionary<string, int> Scores { get; } = [];
    public Dictionary<string, bool> EnabledHouseRules { get; } = [];

    /// <summary>Game-logic scratch space for phase state, results, etc.</summary>
    public Dictionary<string, string> Metadata { get; } = [];

    /// <summary>Ordered record of every notable event shown to the player.</summary>
    public List<string> GameLog { get; } = [];

    public Player CurrentPlayer => Players[CurrentPlayerIndex];

    public Zone GetZone(string id) => Zones[id];

    public Zone? FindZone(string id) => Zones.GetValueOrDefault(id);

    // Returns the zone owned by a specific player, e.g. "hand:player0"
    public Zone? GetPlayerZone(string zoneId, string playerId)
        => Zones.GetValueOrDefault($"{zoneId}:{playerId}");

    public int GetScore(string playerId) => Scores.GetValueOrDefault(playerId, 0);

    /// <summary>
    /// Set by <see cref="StandardDealEngine"/> (or custom deal logic via
    /// <see cref="StandardDealEngine.RecordResult"/>) after each deal.
    /// Consumed by the animation layer; not persisted to save files.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DealResult? LastDealResult { get; set; }

    /// <summary>
    /// AI agents registered for this game session, keyed by player ID.
    /// Not persisted to save files.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, IPlayerAgent> PlayerAgents { get; } = new();

    public void AddScore(string playerId, int points)
    {
        Scores[playerId] = GetScore(playerId) + points;
    }

    public void AdvancePlayer()
    {
        CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
    }
}
