namespace Cards.Engine;

public class Zone
{
    public string Id { get; }
    public string Type { get; }         // deck, pile, hand, spread, trick, grid, pot
    public string? OwnerId { get; }     // player id, team id, or null for shared
    public string Visibility { get; }   // none, top, owner, all, count_only

    public List<Card> Cards { get; } = [];

    public Zone(string id, string type, string? ownerId = null, string visibility = "all")
    {
        Id = id;
        Type = type;
        OwnerId = ownerId;
        Visibility = visibility;
    }

    public int Count => Cards.Count;
    public bool IsEmpty => Cards.Count == 0;
    public Card? TopCard => Cards.Count > 0 ? Cards[^1] : null;

    public Card? Draw()
    {
        if (Cards.Count == 0) return null;
        var card = Cards[^1];
        Cards.RemoveAt(Cards.Count - 1);
        return card;
    }

    public void Add(Card card) => Cards.Add(card);

    public void AddRange(IEnumerable<Card> cards) => Cards.AddRange(cards);

    public bool Remove(Card card) => Cards.Remove(card);

    public void Clear() => Cards.Clear();

    /// <summary>
    /// Replaces the card ordering in-place with the given sequence.
    /// The sequence must contain exactly the same cards (same references).
    /// </summary>
    public void Reorder(IEnumerable<Card> newOrder)
    {
        var sorted = newOrder.ToList();
        Cards.Clear();
        Cards.AddRange(sorted);
    }
}
