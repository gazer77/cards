namespace Cards.Engine;

/// <summary>
/// Fisher-Yates shuffle over an <see cref="IRandomSource"/>.
/// Produces a uniformly random permutation — every ordering equally likely.
/// </summary>
public sealed class RandomShuffleStrategy : IShuffleStrategy
{
    /// <summary>Non-deterministic shuffle, used when no seed is in play.</summary>
    public static readonly RandomShuffleStrategy Instance = new(SharedRandomSource.Instance);

    private readonly IRandomSource _rng;

    public RandomShuffleStrategy(IRandomSource rng) => _rng = rng;

    public void Shuffle(List<Card> cards)
    {
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
    }
}
