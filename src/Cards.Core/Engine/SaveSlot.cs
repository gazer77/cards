namespace Cards.Engine;

/// <summary>
/// A saved game, as it appears in a list of games to resume.
///
/// Carries enough to describe the save without loading it, and — importantly — the
/// table it was written at. A save restored at a different player count leaves hands
/// belonging to seats that no longer exist, holding cards nobody can reach, so the
/// seat count travels with the save rather than being supplied by whoever loads it.
/// </summary>
public sealed class SaveSlot
{
    /// <summary>Stable id; also the storage key suffix.</summary>
    public string Id { get; set; } = string.Empty;

    public string GameId   { get; set; } = string.Empty;

    /// <summary>Display name, stored so the list needs no game definitions to render.</summary>
    public string GameName { get; set; } = string.Empty;

    public int PlayerCount { get; set; }

    public List<string> EnabledRules { get; set; } = [];

    public DateTimeOffset SavedAt { get; set; }

    /// <summary>One line describing the position, e.g. "Round 2 — You 3, AI 1".</summary>
    public string Summary { get; set; } = string.Empty;

    public string RulesText => EnabledRules.Count == 0 ? "" : string.Join(", ", EnabledRules);
}
