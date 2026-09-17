using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// What counts as a meld, shared between the engine (which enforces it) and the UI
/// (which previews it). One implementation, or the highlight on screen and the refusal
/// underneath it would eventually disagree.
/// </summary>
public static class MeldRules
{
    /// <summary>
    /// The ranks the definition declares wild, from <c>scoring.wild_cards</c>. Jokers
    /// are wild by construction (<see cref="Card.IsWild"/>) whatever the list says.
    /// </summary>
    public static HashSet<Rank> WildRanks(GameDefinition? definition)
    {
        var ranks = new HashSet<Rank>();

        if (definition?.Scoring?.Extra?.TryGetValue("wild_cards", out var el) == true
            && el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String
                    && ParseRank(item.GetString() ?? "") is { } rank)
                    ranks.Add(rank);

        return ranks;
    }

    public static bool IsWild(Card card, HashSet<Rank> wildRanks)
        => card.IsWild || wildRanks.Contains(card.Rank);

    /// <summary>"3" → Three, "J"/"jack" → Jack, "A"/"ace" → Ace; null for anything else.</summary>
    public static Rank? ParseRank(string text) => text.Trim().ToLowerInvariant() switch
    {
        "a" or "ace"     or "1"  => Rank.Ace,
        "2" or "two"             => Rank.Two,
        "3" or "three"           => Rank.Three,
        "4" or "four"            => Rank.Four,
        "5" or "five"            => Rank.Five,
        "6" or "six"             => Rank.Six,
        "7" or "seven"           => Rank.Seven,
        "8" or "eight"           => Rank.Eight,
        "9" or "nine"            => Rank.Nine,
        "10" or "ten" or "t"     => Rank.Ten,
        "j" or "jack"            => Rank.Jack,
        "q" or "queen"           => Rank.Queen,
        "k" or "king"            => Rank.King,
        "joker"                  => Rank.Joker,
        _                        => null,
    };

    /// <summary>
    /// A meld is three or more cards of one rank, wilds allowed as stand-ins but never
    /// outnumbering the real cards. Returns the rank the meld is of; a selection that is
    /// all wilds has no rank and is not a meld.
    /// </summary>
    public static bool IsValidMeld(IReadOnlyList<Card> cards, HashSet<Rank> wildRanks, out Rank rank)
    {
        rank = Rank.Joker;
        if (cards.Count < 3) return false;

        var naturals = cards.Where(c => !IsWild(c, wildRanks)).ToList();
        if (naturals.Count == 0) return false;

        rank = naturals[0].Rank;
        if (naturals.Any(c => c.Rank != naturals[0].Rank)) return false;

        return cards.Count - naturals.Count <= naturals.Count;
    }

    /// <summary>
    /// Splits a selection into melds by rank, or null when no split works.
    ///
    /// Naturals sort themselves — each rank present is one meld. Wilds are the choice:
    /// each is dealt to whichever meld is furthest from being legal, which fills every
    /// deficit before padding, so a distribution is found whenever one exists.
    /// </summary>
    public static List<List<Card>>? PartitionIntoMelds(IReadOnlyList<Card> cards, HashSet<Rank> wildRanks)
    {
        var wilds = cards.Where(c => IsWild(c, wildRanks)).ToList();
        var melds = cards.Where(c => !IsWild(c, wildRanks))
                         .GroupBy(c => c.Rank)
                         .Select(g => g.ToList())
                         .ToList();
        if (melds.Count == 0) return null;   // all wilds is a pile of substitutes

        foreach (var wild in wilds)
        {
            var target = melds.Where(m => CanTakeWild(m, wildRanks))
                              .OrderByDescending(m => m.Count < 3 ? 3 - m.Count : 0)
                              .FirstOrDefault();
            if (target is null) return null;
            target.Add(wild);
        }

        return melds.All(m => IsValidMeld(m, wildRanks, out _)) ? melds : null;
    }

    public static Rank MeldRankOf(IReadOnlyList<Card> meld, HashSet<Rank> wildRanks)
        => meld.First(c => !IsWild(c, wildRanks)).Rank;

    private static bool CanTakeWild(List<Card> meld, HashSet<Rank> wildRanks)
        => meld.Count(c => IsWild(c, wildRanks)) < meld.Count(c => !IsWild(c, wildRanks));
}
