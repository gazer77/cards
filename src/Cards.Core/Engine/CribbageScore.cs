namespace Cards.Engine;

/// <summary>
/// Cribbage's arithmetic, kept apart from the phases that use it so it can be checked
/// against hands whose counts every player knows (a 29, a 24 of fives and a jack, a
/// "nineteen"). Pure: no state, no side effects.
///
/// Point values come from the definition and are passed in; the defaults are the
/// standard ones.
/// </summary>
public static class CribbageScore
{
    /// <summary>What a card counts toward fifteen and thirty-one: ace one, faces ten.</summary>
    public static int Value(Card c) => c.Rank switch
    {
        Rank.Ace                              => 1,
        Rank.Jack or Rank.Queen or Rank.King  => 10,
        Rank.Joker                            => 0,
        var r                                 => (int)r,
    };

    /// <summary>Rank as a run sees it: ace low, one to thirteen.</summary>
    private static int Order(Card c) => c.Rank == Rank.Ace ? 1 : (int)c.Rank;

    // ── The show ──────────────────────────────────────────────────────────────

    public sealed record ShowPoints(int Fifteens, int Pairs, int Runs, int Flush, int Nobs)
    {
        public int Total => Fifteens + Pairs + Runs + Flush + Nobs;
    }

    public sealed record ShowValues(int Fifteen = 2, int Pair = 2, int RunCard = 1, int FlushCard = 1, int Nobs = 1);

    /// <summary>
    /// A hand counted with the starter: every combination making fifteen, every pair,
    /// every run (a double run counted twice), a flush, and his nobs — the jack of the
    /// starter's suit. A crib's flush must take in the starter too; a hand's needs only
    /// its own four.
    /// </summary>
    public static ShowPoints Show(IReadOnlyList<Card> hand, Card? starter, bool crib, ShowValues? values = null)
    {
        var v   = values ?? new ShowValues();
        var all = starter is null ? hand.ToList() : [.. hand, starter];

        // Fifteens: every subset of two or more that adds to fifteen.
        int fifteens = 0;
        for (int mask = 1; mask < 1 << all.Count; mask++)
        {
            int sum = 0;
            for (int i = 0; i < all.Count; i++) if ((mask & (1 << i)) != 0) sum += Value(all[i]);
            if (sum == 15) fifteens++;
        }

        // Pairs: every two cards of a rank.
        int pairs = 0;
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                if (all[i].Rank == all[j].Rank) pairs++;

        // Runs: the longest stretch of consecutive ranks, three or more, counted once for
        // each way of choosing one card of each rank in it.
        int runs = 0;
        var counts = new int[15];
        foreach (var c in all) counts[Order(c)]++;
        for (int start = 1; start <= 13; start++)
        {
            if (counts[start] == 0 || (start > 1 && counts[start - 1] > 0)) continue;
            int len = 0, ways = 1;
            while (start + len <= 13 && counts[start + len] > 0) { ways *= counts[start + len]; len++; }
            if (len >= 3) runs += len * ways;
        }

        // Flush: the four in hand one suit; the starter too for the fifth. A crib needs all five.
        int flush = 0;
        if (hand.Count >= 4 && hand.All(c => c.Suit == hand[0].Suit))
        {
            bool five = starter is not null && starter.Suit == hand[0].Suit;
            if (five)       flush = hand.Count + 1;
            else if (!crib) flush = hand.Count;
        }

        // His nobs: the jack in hand of the starter's suit.
        int nobs = starter is not null && hand.Any(c => c.Rank == Rank.Jack && c.Suit == starter.Suit) ? 1 : 0;

        return new ShowPoints(fifteens * v.Fifteen, pairs * v.Pair, runs * v.RunCard, flush * v.FlushCard, nobs * v.Nobs);
    }

    // ── The play ──────────────────────────────────────────────────────────────

    public sealed record PegPoints(int Fifteen, int ThirtyOne, int Pairs, int PairCount, int Run, int RunLength)
    {
        public int Total => Fifteen + ThirtyOne + Pairs + Run;
    }

    public sealed record PegValues(int Fifteen = 2, int ThirtyOne = 2, int Pair = 2, int PairRoyal = 6,
                                   int DoublePairRoyal = 12, int RunCard = 1);

    /// <summary>
    /// What the card just played scores, given the cards played since the count was last
    /// reset (it included, last): fifteen or thirty-one on the count; a pair, pair royal or
    /// double pair royal with the cards straight before it; and a run, if the last three
    /// or more cards, in any order, are consecutive ranks — the longest such.
    /// </summary>
    public static PegPoints Peg(IReadOnlyList<Card> sequence, PegValues? values = null)
    {
        var v = values ?? new PegValues();
        if (sequence.Count == 0) return new PegPoints(0, 0, 0, 0, 0, 0);

        int count     = sequence.Sum(Value);
        int fifteen   = count == 15 ? v.Fifteen : 0;
        int thirtyOne = count == 31 ? v.ThirtyOne : 0;

        // Same rank straight back from the last card.
        var last  = sequence[^1];
        int same  = 1;
        for (int i = sequence.Count - 2; i >= 0 && sequence[i].Rank == last.Rank; i--) same++;
        int pairs = same switch { 2 => v.Pair, 3 => v.PairRoyal, >= 4 => v.DoublePairRoyal, _ => 0 };

        // The longest run ending with the last card.
        int run = 0;
        for (int k = sequence.Count; k >= 3; k--)
        {
            var tail = sequence.Skip(sequence.Count - k).Select(Order).ToList();
            if (tail.Distinct().Count() == k && tail.Max() - tail.Min() == k - 1) { run = k; break; }
        }

        return new PegPoints(fifteen, thirtyOne, pairs, same >= 2 ? same : 0, run * v.RunCard, run);
    }
}
