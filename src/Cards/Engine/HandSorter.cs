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
            "rank"          => ByRank(zone.Cards,       aceHigh: false),
            "rank_ace_high" => ByRank(zone.Cards,       aceHigh: true),
            "suit"          => BySuitValue(zone.Cards,  aceHigh: false),
            "suit_value"    => BySuitValue(zone.Cards,  aceHigh: true),
            "suit_stable"   => BySuitStable(zone.Cards),
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
    /// Sort by suit (C D H S), then by rank within each suit.
    /// </summary>
    private static List<Card> BySuitValue(IEnumerable<Card> cards, bool aceHigh)
        => cards
            .OrderBy(c => (int)c.Suit)
            .ThenBy(c => RankKey(c.Rank, aceHigh))
            .ToList();

    /// <summary>
    /// Group cards by suit (C D H S) while preserving the original relative order
    /// of cards within each suit (stable sort).  Useful when the player just wants
    /// their suits together without disturbing existing rank order within each group.
    /// </summary>
    private static List<Card> BySuitStable(IEnumerable<Card> cards)
    {
        // OrderBy in .NET is stable, so cards within the same suit keep their
        // original relative order.
        return cards.OrderBy(c => (int)c.Suit).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int RankKey(Rank rank, bool aceHigh) => rank switch
    {
        Rank.Ace => aceHigh ? 14 : 1,
        _        => (int)rank,          // Two=2 … King=13
    };
}
