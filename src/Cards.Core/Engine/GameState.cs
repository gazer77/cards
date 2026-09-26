using Cards.Models;

namespace Cards.Engine;

public class GameState
{
    public required string GameId { get; init; }
    public required GameDefinition Definition { get; set; }

    /// <summary>
    /// The named shape of the game this table is playing — a poker variant, say — or
    /// null for the default. Saved, so a resumed Stud game does not deal Hold'em.
    ///
    /// Set before <see cref="IGameLogic.Initialize"/>, which resolves the definition
    /// down to this shape; afterwards <see cref="Definition"/> IS the shape, and
    /// nothing downstream needs to know a choice was made.
    /// </summary>
    public string? ConfigurationName { get; set; }

    public List<Player> Players { get; } = [];
    public List<Team> Teams { get; } = [];
    public Dictionary<string, Zone> Zones { get; } = [];
    public string CurrentPhaseId { get; set; } = string.Empty;
    public int CurrentPlayerIndex { get; set; }
    public int RoundNumber { get; set; } = 1;

    /// <summary>
    /// Player ID of the current dealer.  Used for zone visibility rules
    /// like <c>top_to_dealer</c> and for dealer-rotation logic between rounds.
    /// Null until the first dealer is assigned.
    /// </summary>
    public string? DealerId { get; set; }
    public Dictionary<string, int> Scores { get; } = [];

    /// <summary>
    /// What each round scored, oldest first — the detail behind <see cref="Scores"/>.
    ///
    /// A running total answers "who is winning" and nothing else; a game of nine holes
    /// wants to show the holes. Recorded once, where every scoring type ends, and read
    /// by the score card a definition places on the table.
    /// </summary>
    public List<ScoreRound> ScoreHistory { get; } = [];
    public Dictionary<string, bool> EnabledHouseRules { get; } = [];

    /// <summary>Game-logic scratch space for phase state, results, etc.</summary>
    public Dictionary<string, string> Metadata { get; } = [];

    /// <summary>Ordered record of every notable event shown to the player.</summary>
    public List<string> GameLog { get; } = [];

    public Player CurrentPlayer => Players[CurrentPlayerIndex];

    public Zone GetZone(string id) => Zones[id];

    public Zone? FindZone(string id) => Zones.GetValueOrDefault(id);

    // Returns the zone owned by a specific player, e.g. "hand:player0"
    public Zone? GetPlayerZone(string zoneId, string playerId)
        => Zones.GetValueOrDefault($"{zoneId}:{playerId}");

    public int GetScore(string playerId) => Scores.GetValueOrDefault(playerId, 0);

    /// <summary>Returns the team this player belongs to, or null for individual games.</summary>
    public Team? GetPlayerTeam(string playerId)
        => Teams.FirstOrDefault(t => t.PlayerIds.Contains(playerId));

    /// <summary>
    /// Returns the score for a team ID (e.g. "team0").
    /// In team games scores are stored under team IDs, not player IDs.
    /// </summary>
    public int GetTeamScore(string teamId) => Scores.GetValueOrDefault(teamId, 0);

    /// <summary>
    /// Set by <see cref="StandardDealEngine"/> (or custom deal logic via
    /// <see cref="StandardDealEngine.RecordResult"/>) after each deal.
    /// Consumed by the animation layer; not persisted to save files.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DealResult? LastDealResult { get; set; }

    /// <summary>
    /// Which phase has had its handler set itself up — see IPhaseHandler.OnPhaseEnter.
    /// Kept on the state rather than in the logic so a fresh deal onto a fresh state is
    /// never mistaken for a phase already entered, and not saved: a restored game enters
    /// its phase again, which every handler's setup is written to survive.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EnteredPhase { get; set; }

    /// <summary>
    /// Things a player has just said, waiting to be shown beside their seat — "spades",
    /// when they name trump.
    ///
    /// Drained by whatever is drawing the table, and never saved: a resumed game should
    /// not re-announce a bid made an hour ago. The log keeps the record; this is only
    /// the speaking.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<(string PlayerId, string Text)> Announcements { get; } = [];

    /// <summary>
    /// The names of the people at a shared table, by seat, set before the deal. They
    /// override the definition's seat names because the table writes names into its
    /// lines from the first deal on, and renaming afterwards would leave "West's turn"
    /// in the log for a seat Ana is sitting in. Null for a single-player table.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string?>? SeatNames { get; set; }

    /// <summary>
    /// Whose eyes the table is drawn for. Null means seat 0 — the person at a
    /// single-player screen. A view built for one seat of a shared game sets it, and
    /// everything that means "you" (the bottom of the table, which hand is face up,
    /// whose row on the score card is highlighted) follows it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ViewerId { get; set; }

    /// <summary>The seat the table is drawn for: <see cref="ViewerId"/>, else seat 0.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Viewer => ViewerId ?? (Players.Count > 0 ? Players[0].Id : "");

    /// <summary>
    /// Other seats' wordings of lines the engine wrote. Text is stored the way seat 0
    /// reads it — "Your turn" — so a single-player game reads as it always has; this
    /// holds how everyone else reads the same line ("Ana's turn", or "Your turn" for
    /// Ana), keyed by the stored text. <see cref="GameText.Render"/> reads it.
    /// Never saved: it is rebuilt as lines are written.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, TextVariant> TextVariants { get; } = [];

    /// <summary>
    /// Who each line the engine wrote is about, keyed by the stored text — so a bubble
    /// goes beside the right seat. See <see cref="GameText.SubjectOf"/>. Never saved.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> TextSubjects { get; } = [];

    /// <summary>
    /// AI agents registered for this game session, keyed by player ID.
    /// Not persisted to save files.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, IPlayerAgent> PlayerAgents { get; } = new();

    /// <summary>
    /// Randomness for shuffles, dealer selection and AI tie-breaking.
    /// Defaults to a non-deterministic shared source; assign a
    /// <see cref="SeededRandomSource"/> to make a game reproducible.
    /// The source itself is not serialized — <see cref="Seed"/> is what gets saved.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IRandomSource Rng { get; set; } = SharedRandomSource.Instance;

    /// <summary>
    /// Seed behind <see cref="Rng"/> when it is a <see cref="SeededRandomSource"/>, else 0.
    /// Host-side and save-file state only — never send this to a client, it leaks
    /// every future shuffle.
    /// </summary>
    public ulong Seed { get; set; }

    public void AddScore(string playerId, int points)
    {
        Scores[playerId] = GetScore(playerId) + points;
    }

    public void AdvancePlayer()
    {
        CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
    }

    /// <summary>
    /// Advances to the next player in the specified direction.
    /// "clockwise" in card-game convention means play passes to the left on screen
    /// (south→west→north→east = index−1).
    /// "counter_clockwise" means play passes to the right (index+1).
    /// </summary>
    public void AdvancePlayer(string direction)
    {
        int n = Players.Count;
        if (string.Equals(direction, "clockwise", StringComparison.OrdinalIgnoreCase))
            CurrentPlayerIndex = (CurrentPlayerIndex - 1 + n) % n;
        else
            CurrentPlayerIndex = (CurrentPlayerIndex + 1) % n;
    }
}

/// <summary>
/// One round's scoring: what each player or team scored that round, by id. The totals
/// live in <see cref="GameState.Scores"/>; this is the row behind them.
/// </summary>
public sealed record ScoreRound(int Round, Dictionary<string, int> Scores);

/// <summary>
/// A line as every seat reads it: <see cref="Neutral"/> for anyone it does not
/// address, and <see cref="ByViewer"/> for the seats it speaks to as "you".
/// </summary>
public sealed class TextVariant
{
    public required string Neutral { get; init; }
    public Dictionary<string, string> ByViewer { get; } = [];
}
