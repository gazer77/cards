using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// The fixed slots of a grouped zone, resolved from its definition — either the list it
/// wrote out, or the one <c>by_rank</c> stands for. One resolver, so the renderer that
/// draws a group in a slot and the intake rule that files a card into one agree on
/// which slot that is.
/// </summary>
public static class ZoneSlots
{
    /// <summary>A resolved slot: what it holds and what it says while empty.</summary>
    public sealed record Slot(CardMatch Match, string? Label);

    /// <summary>The zone's slots in order, or empty for a zone that flows.</summary>
    public static IReadOnlyList<Slot> For(Zone zone, GameState state)
    {
        var def = zone.Definition;
        if (def is null) return [];

        if (def.GroupLayout == "slots")
            return def.Slots.Select(s => new Slot(s.Match, s.Label)).ToList();

        if (def.GroupLayout != "by_rank") return [];

        // by_rank: every natural rank in the deck, in deck order, then a wild slot when
        // the game has wilds. The shorthand a definition reaches for when the strip is
        // simply "one of each".
        var wilds = MeldRules.WildRanks(state.Definition);
        IReadOnlyList<Rank> ranks;
        int jokers;
        try
        {
            var spec = DeckSpec.Parse(state.Definition.Deck, state.Players.Count);
            ranks  = spec.Ranks;
            jokers = spec.Jokers;
        }
        catch (FormatException) { return []; }

        var slots = ranks.Where(r => !wilds.Contains(r))
            .Select(r => new Slot(new CardMatch { Rank = RankToken(r) }, MeldRules.RankDisplayName(r)))
            .ToList();
        if (jokers > 0 || wilds.Count > 0)
            slots.Add(new Slot(new CardMatch { Wild = true }, "W"));
        return slots;
    }

    /// <summary>
    /// The slot a group belongs in: matched by its defining natural rank, or as wild
    /// when it has none. -1 when no slot claims it.
    /// </summary>
    public static int SlotOf(IReadOnlyList<Slot> slots, IReadOnlyList<Card> group, HashSet<Rank> wilds)
    {
        if (group.Count == 0) return -1;
        var natural = group.FirstOrDefault(c => !MeldRules.IsWild(c, wilds));
        var key     = natural ?? group[0];
        bool asWild = natural is null;

        for (int i = 0; i < slots.Count; i++)
            if (Fits(slots[i].Match, key, asWild, wilds)) return i;
        return -1;
    }

    /// <summary>The slot a single card would file into, for intake rules that add to a slotted zone.</summary>
    public static int SlotOf(IReadOnlyList<Slot> slots, Card card, HashSet<Rank> wilds)
        => SlotOf(slots, [card], wilds);

    private static bool Fits(CardMatch m, Card card, bool asWild, HashSet<Rank> wilds)
    {
        if (m.Wild is { } wantWild && wantWild != asWild) return false;
        if (asWild && m.Wild is null) return false;   // a wild-only group needs a wild slot
        if (m.Rank is { } rank && MeldRules.ParseRank(rank) != card.Rank) return false;
        if (m.Color is { } color && card.IsRed != color.Equals("red", StringComparison.OrdinalIgnoreCase)) return false;
        if (m.Suit is { } suit && !card.Suit.ToString().Equals(suit, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string RankToken(Rank r) => r switch
    {
        Rank.Ace => "A", Rank.Jack => "J", Rank.Queen => "Q", Rank.King => "K",
        _ => ((int)r).ToString(),
    };
}
