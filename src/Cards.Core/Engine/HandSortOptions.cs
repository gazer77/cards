
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Which hand sorts a game offers, and how to apply one.
///
/// The rules are per-game and fiddly enough to be worth stating once: a definition may
/// name the sorts it wants and the order they appear in, its preferred sort is promoted
/// to the top, and arranging by hand is always available last. Both clients had their
/// own copy, agreeing by luck rather than by construction.
/// </summary>
public static class HandSortOptions
{
    /// <summary>The mode meaning "leave my hand alone, I arrange it myself".</summary>
    public const string FreeMode = "none";

    private static readonly (string Mode, string Label)[] All =
    [
        ("suit_value",    "By Suit & Value"),
        ("suit_stable",   "By Suit"),
        ("rank_ace_high", "By Value (Ace High)"),
        ("rank",          "By Value (Ace Low)"),
    ];

    /// <summary>Sorts this game offers, most useful first, with Free last.</summary>
    public static IReadOnlyList<(string Mode, string Label)> For(GameDefinition? definition)
    {
        var ui = definition?.Ui;

        // A game naming its own modes wants only those, in the order it gave.
        var modes = ui?.SortModes is { Count: > 0 } configured
            ? configured
                .Select(m => All.FirstOrDefault(o => o.Mode == m))
                .Where(o => o.Mode is not null)
                .ToList()
            : All.ToList();

        if (!string.IsNullOrEmpty(ui?.DefaultSort))
        {
            int i = modes.FindIndex(o => o.Mode == ui.DefaultSort);
            if (i > 0)
            {
                var preferred = modes[i];
                modes.RemoveAt(i);
                modes.Insert(0, preferred);
            }
        }

        modes.Add((FreeMode, "Free"));
        return modes;
    }

    /// <summary>
    /// Sorts the hands the player can actually see. Returns whether anything was sorted.
    ///
    /// Hidden hands are left alone: sorting one changes nothing visible and reorders an
    /// opponent's cards.
    /// </summary>
    public static bool Apply(GameState? state, string? mode)
    {
        if (state is null || string.IsNullOrEmpty(mode) || mode == FreeMode) return false;

        foreach (var zone in state.Zones.Values)
            if (zone.Type == "hand" && zone.Visibility is "owner" or "all")
                HandSorter.Sort(zone, mode);

        return true;
    }
}
