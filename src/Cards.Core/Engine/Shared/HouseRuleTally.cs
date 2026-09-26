using Cards.Models;

namespace Cards.Engine.Shared;

/// <summary>
/// Whether a house rule is in, from the people's ballots and the game's own terms for
/// deciding (<see cref="GameDefinition.HouseRuleVote"/>).
///
/// The terms belong to the definition, like every other rule of the game: "majority"
/// (the default) needs more than half saying yes, so a tie is no; "unanimous" needs
/// everyone. A host's overruling counts only where the definition allows it — checked
/// where the host asks, so here a forced answer simply wins.
/// </summary>
public static class HouseRuleTally
{
    public static bool Carries(HouseRuleVoteDefinition terms, int yes, int voters, bool? forced = null)
    {
        if (forced is { } decided) return decided;
        if (voters <= 0) return false;

        return terms.DecideBy == "unanimous"
            ? yes == voters
            : yes * 2 > voters;
    }

    /// <summary>The terms in a sentence, for the lobby.</summary>
    public static string Describe(HouseRuleVoteDefinition terms)
        => (terms.DecideBy == "unanimous"
               ? "A rule is in only when everyone says yes."
               : "A rule is in when more than half say yes; a tie is a no.")
         + (terms.HostOverride ? " The host may overrule." : "");
}
