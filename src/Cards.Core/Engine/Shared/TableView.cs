namespace Cards.Engine.Shared;

/// <summary>
/// One seat's view of a shared table: everything that seat may see, in its own words,
/// and what it may do next. The server builds one per seat after every change; a client
/// draws from it and never runs the rules itself.
///
/// The cards are the game's own save shape, so a client rebuilds its table with the same
/// code that resumes a saved game. Cards the seat may not see arrive as backs with an
/// alias for a uid (see <see cref="TableProjection"/>), so the view carries nothing a
/// player could read out of the traffic that they could not read off the table.
/// </summary>
public sealed class TableView
{
    /// <summary>Counts up with every change, so a late message never replaces a newer view.</summary>
    public long Version { get; set; }

    public string GameId { get; set; } = "";
    public string ViewerId { get; set; } = "";
    public int PlayerCount { get; set; }
    public List<string> EnabledRules { get; set; } = [];

    public SavedGameState State { get; set; } = new();
    public List<SeatView> Seats { get; set; } = [];

    /// <summary>What was said at the table since the last view — bubbles beside seats.</summary>
    public List<AnnouncementView> Announcements { get; set; } = [];

    /// <summary>Actions this seat may take now. Empty when it is not this seat's move.</summary>
    public List<GameAction> Actions { get; set; } = [];
    public List<string> SelectableCardIds { get; set; } = [];

    /// <summary>For each selectable card, the zones it may be played to.</summary>
    public Dictionary<string, List<string>> DropZones { get; set; } = [];

    /// <summary>For each selectable card, what a double tap on it does.</summary>
    public Dictionary<string, GameAction> DefaultCardActions { get; set; } = [];

    public string Status { get; set; } = "";
    public bool IsGameOver { get; set; }

    /// <summary>The table is playing itself — computer players, a dealer drawing — and takes no input.</summary>
    public bool IsBusy { get; set; }
}

public sealed class SeatView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsComputer { get; set; }
    public bool IsConnected { get; set; }
    public string? Role { get; set; }
}

public sealed class AnnouncementView
{
    public string PlayerId { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>A room before and during play: who is sitting where, and what is being played.</summary>
public sealed class RoomInfo
{
    public string Code { get; set; } = "";
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string? Configuration { get; set; }
    public int PlayerCount { get; set; }
    public List<string> EnabledRules { get; set; } = [];
    public List<LobbySeat> Seats { get; set; } = [];
    public bool Started { get; set; }
    public string HostSeatId { get; set; } = "";
}

public sealed class LobbySeat
{
    public string Id { get; set; } = "";

    /// <summary>The person sitting here, or null for an open seat (a computer player once play starts).</summary>
    public string? Name { get; set; }
    public bool IsComputer { get; set; }
    public bool IsConnected { get; set; }
}

/// <summary>What joining a room gives a person: their seat, and the token that keeps it theirs.</summary>
public sealed class SeatTicket
{
    public string Code { get; set; } = "";
    public string SeatId { get; set; } = "";

    /// <summary>
    /// Proves the seat is yours on reconnect. A phone drops its connection whenever the
    /// app goes to the background, so a seat belongs to this, never to a connection.
    /// </summary>
    public string Token { get; set; } = "";
}

/// <summary>The hub's names, shared by the server and every client so they cannot drift.</summary>
public static class TableHubContract
{
    public const string Path = "/hub/table";

    // Client → server
    public const string CreateRoom = nameof(CreateRoom);
    public const string JoinRoom   = nameof(JoinRoom);
    public const string Rejoin     = nameof(Rejoin);
    public const string StartGame  = nameof(StartGame);
    public const string Act        = nameof(Act);
    public const string LeaveRoom  = nameof(LeaveRoom);

    // Server → client
    public const string RoomChanged = nameof(RoomChanged);
    public const string ViewChanged = nameof(ViewChanged);
}
