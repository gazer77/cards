namespace Cards.Engine;

/// <summary>
/// Fisher-Yates shuffle using <see cref="Random.Shared"/>.
/// Produces a uniformly random permutation — every ordering equally likely.
/// </summary>
public sealed class RandomShuffleStrategy : IShuffleStrategy
{
    public static readonly RandomShuffleStrategy Instance = new();

    public void Shuffle(List<Card> cards)
    {
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }
}
