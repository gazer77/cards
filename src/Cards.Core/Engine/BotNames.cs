namespace Cards.Engine;

/// <summary>
/// Names for the computer's seats. "Bot 1" read as a placeholder; a name reads as
/// someone at the table. Short, so they fit a name plate on a phone; varied, so a table
/// of six is six different people; and never one already sitting down.
/// </summary>
public static class BotNames
{
    public static readonly IReadOnlyList<string> Pool =
    [
        "Ada", "Alma", "Amir", "Ana", "Arlo", "Asha", "Bea", "Ben", "Cal", "Cleo",
        "Dev", "Dina", "Eli", "Emi", "Esme", "Ezra", "Finn", "Gus", "Hana", "Hugo",
        "Ida", "Ines", "Ivo", "Jade", "Jai", "Juno", "Kai", "Kira", "Lars", "Leo",
        "Lila", "Lou", "Mae", "Mara", "Milo", "Nia", "Nico", "Noor", "Omar", "Otto",
        "Pia", "Quinn", "Raj", "Remy", "Rosa", "Sami", "Sana", "Theo", "Tova", "Uma",
        "Vera", "Wren", "Yara", "Yusuf", "Zane", "Zoe",
    ];

    /// <summary>
    /// <paramref name="count"/> different names, none of them in <paramref name="taken"/>
    /// (compared without case). Shuffled by <paramref name="rng"/>, so a seeded table
    /// names its computer the same way every time.
    /// </summary>
    public static IReadOnlyList<string> Pick(int count, IEnumerable<string?> taken, Random rng)
    {
        var avoid = taken.Where(n => n is not null).Select(n => n!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free  = Pool.Where(n => !avoid.Contains(n)).ToList();

        for (int i = free.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (free[i], free[j]) = (free[j], free[i]);
        }
        return free.Take(Math.Max(0, count)).ToList();
    }
}
