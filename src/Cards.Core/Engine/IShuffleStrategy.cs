namespace Cards.Engine;

/// <summary>
/// Shuffles a list of cards in place.
///
/// Current implementation: <see cref="RandomShuffleStrategy"/> (Fisher-Yates, Random.Shared).
///
/// Planned: RiffleShuffleStrategy — simulates a physical riffle by splitting the deck into
/// two roughly equal halves (with random variance) then interleaving cards with occasional
/// runs of 2–5 from one half.  Requires card play-order tracking so the deck can be
/// assembled from played/discard piles in a meaningful order before shuffling.
/// The number of riffle passes should be configurable (7 approaches uniform distribution).
/// </summary>
public interface IShuffleStrategy
{
    void Shuffle(List<Card> cards);
}
