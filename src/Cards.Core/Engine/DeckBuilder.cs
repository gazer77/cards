using Cards.Models;

namespace Cards.Engine;

public static class DeckBuilder
{
    /// <summary>
    /// Builds the deck a game asks for at a given table size.
    ///
    /// Composition comes from the definition (see <see cref="DeckSpec"/>), so a game can
    /// use any deck it can describe rather than one of a handful named in C#. Table size
    /// matters because a game may want more packs at a larger table.
    /// </summary>
    public static List<Card> Build(GameDefinition definition, int playerCount)
        => Build(DeckSpec.Parse(definition.Deck, playerCount));

    public static List<Card> Build(DeckSpec spec)
    {
        var cards = new List<Card>(spec.Size);

        for (int copy = 0; copy < spec.Copies; copy++)
            foreach (var suit in spec.Suits)
                foreach (var rank in spec.Ranks)
                    cards.Add(new Card(suit, rank) { Uid = cards.Count + 1 });

        // Jokers alternate red and black so a pair is visually distinguishable.
        for (int i = 0; i < spec.Jokers; i++)
        {
            var suit = (i % 2 == 0) ? Suit.Clubs : Suit.Hearts;
            cards.Add(new Card(suit, Rank.Joker) { IsWild = true, Uid = cards.Count + 1 });
        }

        return cards;
    }

    /// <summary>
    /// Builds one of the named decks.
    ///
    /// Throws on an unknown name rather than quietly substituting a standard 52. That
    /// fallback meant a typo produced a game dealing the wrong deck, which then failed
    /// somewhere unrelated and much later — and for multiplayer it is worse than a bug,
    /// since two clients on different builds would deal different decks from the same
    /// definition with no error on either side.
    /// </summary>
    public static List<Card> Build(string deckName)
        => Build(DeckSpec.FromName(deckName)
                 ?? throw new FormatException($"Unknown deck '{deckName}'."));

    public static void Shuffle(List<Card> cards, IShuffleStrategy? strategy = null)
        => (strategy ?? RandomShuffleStrategy.Instance).Shuffle(cards);

    /// <summary>
    /// Shuffles using the supplied randomness source, so a seeded game reshuffles
    /// reproducibly.  Engine callers that have a <see cref="GameState"/> should use
    /// this and pass <c>state.Rng</c>.
    /// </summary>
    public static void Shuffle(List<Card> cards, IRandomSource rng)
        => new RandomShuffleStrategy(rng).Shuffle(cards);
}
