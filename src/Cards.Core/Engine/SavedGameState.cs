namespace Cards.Engine;

/// <summary>
/// Serialisable snapshot of a running game.  Stored as JSON in AppDataDirectory.
/// On restore the logic's Initialize is called first (to re-apply private fields
/// like _dealerHitsHard17), then all runtime state is overwritten from here.
/// </summary>
public sealed class SavedGameState
{
    public string         GameId       { get; set; } = string.Empty;
    public int            PlayerCount  { get; set; }
    public List<string>   EnabledRules { get; set; } = [];
    public string         PhaseId      { get; set; } = string.Empty;
    public int            PlayerIndex  { get; set; }
    public int            RoundNumber  { get; set; } = 1;
    public string?        DealerId     { get; set; }
    public Dictionary<string, int>    Scores   { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
    public List<string>   GameLog  { get; set; } = [];
    public List<SavedZone> Zones   { get; set; } = [];
}

public sealed class SavedZone
{
    public string       Id         { get; set; } = string.Empty;
    public string       Type       { get; set; } = string.Empty;
    public string?      OwnerId    { get; set; }
    public string       Visibility { get; set; } = "all";
    public List<SavedCard> Cards   { get; set; } = [];
}

public sealed class SavedCard
{
    public int  Suit     { get; set; }
    public int  Rank     { get; set; }
    public bool IsFaceUp { get; set; }
    public bool IsWild   { get; set; }

    /// <summary>
    /// Physical identity. Persisted so a resumed game keeps the same cards rather than
    /// re-labelling them, which would read to the renderer as every card being new.
    /// Zero on saves written before uids existed; harmless, since duplicates only
    /// matter in multi-deck games.
    /// </summary>
    public int  Uid      { get; set; }
}
