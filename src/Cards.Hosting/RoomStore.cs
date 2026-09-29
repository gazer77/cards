using Cards.Engine;

namespace Cards.Hosting;

/// <summary>
/// Where a host keeps its rooms between runs, so a restart — a deploy, a phone that
/// backgrounded the app — does not end every game. Each room is one document, keyed by
/// its code. The host writes a room when it has changed and reads them all back at
/// start; what the document says is <see cref="SavedRoom"/>.
/// </summary>
public interface IRoomStore
{
    Task SaveAsync(string code, string json);
    Task DeleteAsync(string code);
    Task<IReadOnlyList<(string Code, string Json)>> LoadAllAsync();
}

/// <summary>A room per file in one folder: <c>{code}.json</c>.</summary>
public sealed class FileRoomStore : IRoomStore
{
    private readonly string _directory;

    public FileRoomStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathOf(string code) => Path.Combine(_directory, code.ToUpperInvariant() + ".json");

    public async Task SaveAsync(string code, string json)
    {
        // Written beside and moved over, so a crash mid-write leaves the last good copy
        // rather than half of a new one.
        string path = PathOf(code), temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    public Task DeleteAsync(string code)
    {
        File.Delete(PathOf(code));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<(string Code, string Json)>> LoadAllAsync()
    {
        var rooms = new List<(string, string)>();
        foreach (var path in Directory.GetFiles(_directory, "*.json"))
            rooms.Add((Path.GetFileNameWithoutExtension(path), await File.ReadAllTextAsync(path)));
        return rooms;
    }
}

/// <summary>
/// A room as written to a <see cref="IRoomStore"/>: the table (seats and their tokens,
/// the house-rule vote) and, once dealt, the game. Only what outlives a connection —
/// who is connected, an open question about someone away, and the table's own busy
/// flags are not kept; everyone is away when a host starts, and comes back by token.
/// </summary>
public sealed class SavedRoom
{
    public int Format { get; set; } = 1;

    public string Code { get; set; } = "";
    public string GameId { get; set; } = "";
    public string? Configuration { get; set; }
    public int PlayerCount { get; set; }
    public List<string> Rules { get; set; } = [];
    public Dictionary<string, List<string>> Ballots { get; set; } = [];
    public Dictionary<string, bool> Forced { get; set; } = [];
    public int DropTimeoutSeconds { get; set; }
    public List<SavedSeat> Seats { get; set; } = [];

    public DateTime LastActivity { get; set; }
    public long Version { get; set; }
    public long ViewSequence { get; set; }
    public string LastStatus { get; set; } = "";

    /// <summary>The game, once dealt; null in the lobby.</summary>
    public SavedGameState? Game { get; set; }

    /// <summary>Everyone at the table as the game names them, dealer included.</summary>
    public List<string> PlayerNames { get; set; } = [];

    /// <summary>
    /// How each seat reads the lines still on show — the status and the log — and who
    /// each is about. A line is stored in seat 0's words; without these, every seat
    /// would read "Your turn" after a restart.
    /// </summary>
    public Dictionary<string, SavedWording> Wordings { get; set; } = [];
    public Dictionary<string, string> Subjects { get; set; } = [];
}

public sealed class SavedSeat
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Token { get; set; }
    public bool IsComputer { get; set; }
    public bool StandIn { get; set; }
}

public sealed class SavedWording
{
    public string Neutral { get; set; } = "";
    public Dictionary<string, string> ByViewer { get; set; } = [];
}
