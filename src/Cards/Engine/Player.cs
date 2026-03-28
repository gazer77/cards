namespace Cards.Engine;

public enum PlayerType { Human, AI }

public class Player
{
    public string Id { get; }
    public string Name { get; set; }
    public PlayerType Type { get; }
    public int TeamIndex { get; set; } = -1;

    public Player(string id, string name, PlayerType type = PlayerType.Human)
    {
        Id = id;
        Name = name;
        Type = type;
    }

    public bool IsHuman => Type == PlayerType.Human;

    public override string ToString() => Name;
}
