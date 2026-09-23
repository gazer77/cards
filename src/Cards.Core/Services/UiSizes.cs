namespace Cards.Services;

/// <summary>
/// The sizes a player may choose for what the table draws: cards, speech bubbles and
/// the status line.
///
/// Five steps, each a quarter smaller than the one above it, with extra-large being
/// the size the table has always drawn — so a player who never opens the menu sees no
/// change, and every other choice is a deliberate step down. Eyesight and screen size
/// vary more than any single default can cover, and a phone held close wants something
/// quite different from a tablet on a table.
/// </summary>
public static class UiSizes
{
    /// <summary>What a size is called and what it multiplies by. Largest first.</summary>
    public static readonly (string Id, string Label, double Scale)[] All =
    [
        ("xl", "Extra large", 1.00),
        ("l",  "Large",       0.75),
        ("m",  "Medium",      0.5625),
        ("s",  "Small",       0.4219),
        ("xs", "Extra small", 0.3164),
    ];

    public const string Default = "xl";

    /// <summary>The elements a size may be set for, and what to call each on screen.</summary>
    public static readonly (string Id, string Label)[] Targets =
    [
        ("cards",   "Cards"),
        ("bubbles", "Speech bubbles"),
        ("status",  "Status line"),
    ];

    public static double ScaleOf(string? id)
    {
        foreach (var (sizeId, _, scale) in All)
            if (sizeId == id) return scale;
        return 1.0;
    }

    public static string LabelOf(string? id)
    {
        foreach (var (sizeId, label, _) in All)
            if (sizeId == id) return label;
        return LabelOf(Default);
    }
}
