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

    /// <summary>
    /// Cards grouped into distinct sets, for zones where the grouping is part of the
    /// game rather than a way of laying cards out — melds, principally.
    ///
    /// Empty for every other zone, which then behaves exactly as before. Groups hold
    /// card uids rather than ids because a multi-deck game has several cards answering
    /// to the same rank and suit, and a meld needs to name the ones it actually
    /// contains.
    ///
    /// Without this, melds pile into one zone and their structure has to be guessed
    /// back: canasta scoring regroups by rank and distributes wilds greedily, which is
    /// an approximation that happens to be right often enough to look correct.
    /// </summary>
    public List<List<int>> Groups { get; } = [];

    public bool HasGroups => Groups.Count > 0;

    public int Count => Cards.Count;
    public bool IsEmpty => Cards.Count == 0;
    public Card? TopCard => Cards.Count > 0 ? Cards[^1] : null;

    /// <summary>Adds cards as a new group, keeping them in the zone's card list too.</summary>
    public void AddGroup(IEnumerable<Card> cards)
    {
        var group = cards.ToList();
        if (group.Count == 0) return;

        foreach (var card in group) Cards.Add(card);
        Groups.Add(group.Select(c => c.Uid).ToList());
    }

    /// <summary>Adds one card to an existing group, or does nothing if the index is unknown.</summary>
    public void AddToGroup(int groupIndex, Card card)
    {
        if (groupIndex < 0 || groupIndex >= Groups.Count) return;

        Cards.Add(card);
        Groups[groupIndex].Add(card.Uid);
    }

    /// <summary>The cards of one group, in the order they were laid.</summary>
    public IReadOnlyList<Card> GroupCards(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= Groups.Count) return [];

        var byUid = Cards.ToDictionary(c => c.Uid);
        return Groups[groupIndex]
            .Where(byUid.ContainsKey)
            .Select(uid => byUid[uid])
            .ToList();
    }

    public Card? Draw()
    {
        if (Cards.Count == 0) return null;
        var card = Cards[^1];
        Cards.RemoveAt(Cards.Count - 1);
        return card;
    }

    public void Add(Card card) => Cards.Add(card);

    public void AddRange(IEnumerable<Card> cards) => Cards.AddRange(cards);

    public bool Remove(Card card)
    {
        // A card leaving takes its place in any group with it, or the group would name
        // a card the zone no longer holds.
        if (Groups.Count > 0)
        {
            foreach (var group in Groups) group.Remove(card.Uid);
            Groups.RemoveAll(g => g.Count == 0);
        }

        return Cards.Remove(card);
    }

    public void Clear()
    {
        Cards.Clear();
        Groups.Clear();
    }

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
