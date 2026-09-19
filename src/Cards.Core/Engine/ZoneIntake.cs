using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Rules that fire when cards arrive in a zone — Hand and Foot's red threes, which
/// leave the hand for the threes pile the moment they are dealt, drawn, or revealed
/// from the foot, and are replaced from the deck.
///
/// Declared on the receiving zone as <c>on_receive</c>, and settled at one chokepoint
/// after any arrival, so dealt, drawn and picked-up cards all behave alike. That is the
/// point: the rule is "when this card reaches your hand", not "when it is drawn", and a
/// rule that fires on two of the three ways in is a rule that is sometimes wrong.
/// </summary>
public static class ZoneIntake
{
    /// <summary>
    /// Applies the zone's <c>on_receive</c> rules until nothing more moves. Replacement
    /// cards are themselves subject to the rules, so a red three drawn to replace a red
    /// three leaves too — bounded by the deck running out.
    /// </summary>
    public static void Settle(GameState state, Zone zone)
    {
        var rules = zone.Definition?.OnReceive;
        if (rules is null || rules.Count == 0) return;

        var wilds = MeldRules.WildRanks(state.Definition);

        // Bounded: each pass either moves a card out or stops.
        for (int guard = 0; guard < 200; guard++)
        {
            bool moved = false;

            foreach (var rule in rules)
            {
                var card = zone.Cards.FirstOrDefault(c => Matches(rule.Card, c, wilds));
                if (card is null) continue;

                var to = SideZone(state, rule.MoveTo, zone.OwnerId);
                if (to is null) continue;   // validated at load; belt and braces here

                zone.Remove(card);
                card.IsFaceUp = true;
                to.Add(card);
                moved = true;

                if (rule.ReplaceFrom is { } fromId
                    && state.FindZone(fromId) is { } from && !from.IsEmpty)
                {
                    var replacement = from.Draw()!;
                    replacement.IsFaceUp = zone.Type == "hand";
                    zone.Add(replacement);
                }

                state.GameLog.Add($"{OwnerName(state, zone)}: {Describe(card)} to {rule.MoveTo}");
                break;   // re-scan from the top, the replacement may match too
            }

            if (!moved) return;
        }
    }

    /// <summary>
    /// The zone a rule names, for this owner: <c>threes</c> resolves to the owner's
    /// team's, then the owner's own, then a shared one — the same lookup melds use.
    /// </summary>
    public static Zone? SideZone(GameState state, string baseId, string? ownerId)
    {
        if (ownerId is not null)
        {
            var team = state.GetPlayerTeam(ownerId);
            if (team is not null && state.FindZone($"{baseId}:{team.Id}") is { } teamZone)
                return teamZone;
            if (state.FindZone($"{baseId}:{ownerId}") is { } own)
                return own;
        }
        return state.FindZone(baseId);
    }

    private static bool Matches(CardMatch? match, Card card, HashSet<Rank> wilds)
    {
        if (match is null) return false;

        if (match.Rank is { } rank && MeldRules.ParseRank(rank) != card.Rank) return false;

        if (match.Color is { } color)
        {
            bool wantRed = color.Equals("red", StringComparison.OrdinalIgnoreCase);
            if (card.IsRed != wantRed) return false;
        }

        if (match.Suit is { } suit
            && !card.Suit.ToString().Equals(suit, StringComparison.OrdinalIgnoreCase))
            return false;

        if (match.Wild is { } wild && MeldRules.IsWild(card, wilds) != wild) return false;

        return true;
    }

    private static string OwnerName(GameState state, Zone zone)
        => state.Players.FirstOrDefault(p => p.Id == zone.OwnerId)?.Name ?? zone.Id;

    private static string Describe(Card card)
        => card.Rank == Rank.Joker ? "Joker" : $"{MeldRules.RankDisplayName(card.Rank)} of {card.Suit}";
}
