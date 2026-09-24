namespace Cards.Engine;

public enum PlayerType { Human, AI }

public class Player
{
    public string Id { get; }
    public string Name { get; set; }
    public PlayerType Type { get; }
    public int TeamIndex { get; set; } = -1;

    /// <summary>
    /// The role this seat fills — "dealer" — or null for a player. A role seat is the
    /// game's own: it holds no chips and is never ranked against the players.
    /// </summary>
    public string? Role { get; init; }

    public Player(string id, string name, PlayerType type = PlayerType.Human)
    {
        Id = id;
        Name = name;
        Type = type;
    }

    public bool IsHuman => Type == PlayerType.Human;

    public override string ToString() => Name;
}
