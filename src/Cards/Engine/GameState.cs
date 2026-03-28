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

    public void AddScore(string playerId, int points)
    {
        Scores[playerId] = GetScore(playerId) + points;
    }

    public void AdvancePlayer()
    {
        CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
    }
}
