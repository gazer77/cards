namespace Cards.Engine;

public static class DeckBuilder
{
    private static readonly Suit[] AllSuits = [Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades];

    private static readonly Rank[] StandardRanks =
    [
        Rank.Two, Rank.Three, Rank.Four, Rank.Five, Rank.Six, Rank.Seven,
        Rank.Eight, Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace
    ];

    private static readonly Rank[] EuchreRanks =
    [
        Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace
    ];

    private static readonly Rank[] PinochleRanks =
    [
        Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace
    ];

    public static List<Card> Build(string deckType) => deckType switch
    {
        "standard-52"         => BuildWith(StandardRanks, copies: 1),
        "standard-104"        => BuildWith(StandardRanks, copies: 2),
        "standard-52-jokers"  => BuildWithJokers(StandardRanks, copies: 1, jokers: 2),
        "euchre-24"           => BuildWith(EuchreRanks,   copies: 1),
        "pinochle-48"         => BuildWith(PinochleRanks, copies: 2),
        _                     => BuildWith(StandardRanks, copies: 1)
    };

    public static void Shuffle(List<Card> cards, IShuffleStrategy? strategy = null)
        => (strategy ?? RandomShuffleStrategy.Instance).Shuffle(cards);

    private static List<Card> BuildWith(Rank[] ranks, int copies)
    {
        var cards = new List<Card>(ranks.Length * AllSuits.Length * copies);
        for (int c = 0; c < copies; c++)
            foreach (var suit in AllSuits)
                foreach (var rank in ranks)
                    cards.Add(new Card(suit, rank));
        return cards;
    }

    private static List<Card> BuildWithJokers(Rank[] ranks, int copies, int jokers)
    {
        var cards = BuildWith(ranks, copies);
        // Jokers represented as Ace of a special suit isn't ideal — placeholder until
        // a proper Joker card type is added.
        return cards;
    }
}
