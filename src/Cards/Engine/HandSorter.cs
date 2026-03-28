namespace Cards.Engine;

/// <summary>
/// Sorts the cards in a hand zone in-place according to a named sort mode.
/// Only applies to zones where cards are face-up (i.e. the player can see them).
/// </summary>
public static class HandSorter
{
    /// <summary>
    /// Sort <paramref name="zone"/> using the mode string from <see cref="Models.GameUiConfig.AutoSortHand"/>.
    /// No-ops when <paramref name="mode"/> is null, empty, or "none".
    /// </summary>
    public static void Sort(Zone zone, string? mode)
    {
        if (string.IsNullOrEmpty(mode) || mode == "none") return;

        var sorted = mode switch
        {
            "rank"          => ByRank(zone.Cards, aceHigh: false),
            "rank_ace_high" => ByRank(zone.Cards, aceHigh: true),
            "suit"          => BySuit(zone.Cards),
            _               => null,
        };

        if (sorted is not null)
            zone.Reorder(sorted);
    }

    // ── Sort strategies ───────────────────────────────────────────────────────

    /// <summary>
    /// Group same ranks together; order groups by rank value.
    /// Ace low:  A 2 3 4 5 6 7 8 9 10 J Q K
    /// Ace high: 2 3 4 5 6 7 8 9 10 J Q K A
    /// Within each rank group, cards are ordered by suit (C D H S).
    /// </summary>
    private static List<Card> ByRank(IEnumerable<Card> cards, bool aceHigh)
        => cards
            .OrderBy(c => RankKey(c.Rank, aceHigh))
            .ThenBy(c => (int)c.Suit)
            .ToList();

    /// <summary>
    /// Sort by suit (C D H S), then by rank within each suit (Ace low).
    /// </summary>
    private static List<Card> BySuit(IEnumerable<Card> cards)
        => cards
            .OrderBy(c => (int)c.Suit)
            .ThenBy(c => RankKey(c.Rank, aceHigh: false))
            .ToList();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int RankKey(Rank rank, bool aceHigh) => rank switch
    {
        Rank.Ace => aceHigh ? 14 : 1,
        _        => (int)rank,          // Two=2 … King=13
    };
}
