using System.Security.Cryptography;
using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Models;

namespace Cards.Server;

/// <summary>
/// One table: its seats, and once play starts the single copy of the game. Everything
/// that reads or changes it holds <see cref="Lock"/>, since moves arrive from every
/// seat's connection at once and the computer players' turns run beside them.
/// </summary>
public sealed class Room
{
    public required string Code { get; init; }
    public required GameDefinition Definition { get; init; }
    public string? Configuration { get; init; }
    public required int PlayerCount { get; init; }
    /// <summary>
    /// The house rules in play. Before the deal, the host's proposal; at the deal, what
    /// the table agreed (<see cref="AgreedRules"/>).
    /// </summary>
    public required IReadOnlyList<string> Rules { get; set; }

    /// <summary>The house rules this game, shape and seat count offer — the ballot.</summary>
    public IReadOnlyList<HouseRule> Offered { get; init; } = [];

    /// <summary>Each seated person's ballot: the rules they say yes to.</summary>
    public Dictionary<string, HashSet<string>> Ballots { get; } = [];

    /// <summary>The host's rulings, where the game allows them: rule id → in or out.</summary>
    public Dictionary<string, bool> Forced { get; } = [];

    /// <summary>What the table has agreed: every offered rule that carries.</summary>
    public List<string> AgreedRules()
    {
        var terms  = Definition.HouseRuleVote;
        int voters = Ballots.Count;
        return Offered
            .Where(r => HouseRuleTally.Carries(terms, Ballots.Values.Count(b => b.Contains(r.Id)), voters,
                                               Forced.TryGetValue(r.Id, out var f) ? f : null))
            .Select(r => r.Id)
            .ToList();
    }
    public required List<RoomSeat> Seats { get; init; }

    /// <summary>How long a dropped player may hold the table before the others are asked; zero for never.</summary>
    public TimeSpan DropTimeout { get; init; }

    /// <summary>The question open at this table, if any.</summary>
    public RoomVote? Vote { get; set; }

    public string HostSeatId => Seats[0].Id;

    public GameState? State { get; set; }
    public IGameLogic? Logic { get; set; }
    public long Version { get; set; }

    /// <summary>Numbers every view sent, so a client can drop one that arrives after a newer one.</summary>
    public long ViewSequence { get; set; }

    /// <summary>The last status line written to the log, so a steady one is not repeated.</summary>
    public string LastStatus { get; set; } = "";

    /// <summary>The table is playing its own turns; people wait.</summary>
    public bool Busy { get; set; }
    public bool AutoRunning { get; set; }

    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public SemaphoreSlim Lock { get; } = new(1, 1);

    /// <summary>
    /// Keyed per room and never sent anywhere, so an alias says nothing about the card
    /// behind it to anyone at this table or any other.
    /// </summary>
    private readonly int _aliasKey = RandomNumberGenerator.GetInt32(int.MaxValue);

    /// <summary>The name a hidden card goes by: negative, so it can never be a real uid.</summary>
    public int Alias(int uid) => -1 - (int)((uint)HashCode.Combine(_aliasKey, uid) % 1_000_000_000u);

    /// <summary>The real card behind an alias, or null.</summary>
    public Card? Unalias(int alias)
        => State?.Zones.Values.SelectMany(z => z.Cards).FirstOrDefault(c => Alias(c.Uid) == alias);

    public RoomSeat? SeatByToken(string token) => Seats.FirstOrDefault(s => s.Token == token);

    public RoomInfo Info() => new()
    {
        Code          = Code,
        GameId        = Definition.Id,
        GameName      = Definition.Name,
        Configuration = Configuration,
        PlayerCount   = PlayerCount,
        EnabledRules  = State is null ? AgreedRules() : [.. Rules],
        HouseRuleTerms = HouseRuleTally.Describe(Definition.HouseRuleVote),
        HostOverride   = Definition.HouseRuleVote.HostOverride,
        HouseRules = Offered.Select(r => new LobbyRule
        {
            Id          = r.Id,
            Name        = r.Name,
            Description = r.Description,
            YesSeats    = Ballots.Where(b => b.Value.Contains(r.Id)).Select(b => b.Key).ToList(),
            Voters      = Ballots.Count,
            Forced      = Forced.TryGetValue(r.Id, out var f) ? f : null,
            Carries     = State is null
                ? HouseRuleTally.Carries(Definition.HouseRuleVote, Ballots.Values.Count(b => b.Contains(r.Id)), Ballots.Count, Forced.TryGetValue(r.Id, out var g) ? g : null)
                : Rules.Contains(r.Id),
        }).ToList(),
        Started       = State is not null,
        HostSeatId    = HostSeatId,
        DropTimeoutSeconds = (int)DropTimeout.TotalSeconds,
        Seats = Seats.Select(s => new LobbySeat
        {
            Id          = s.Id,
            Name        = s.Name,
            IsComputer  = s.IsComputer,
            IsConnected = s.ConnectionId is not null,
        }).ToList(),
    };

    public List<SeatView> SeatViews()
    {
        var views = Seats.Select(s => new SeatView
        {
            Id          = s.Id,
            Name        = State?.Players.FirstOrDefault(p => p.Id == s.Id)?.Name ?? s.Name ?? s.Id,
            IsComputer  = s.IsComputer,
            IsConnected = s.ConnectionId is not null,
            StandIn     = s.StandIn,
        }).ToList();

        // Role seats — the dealer — belong to the game, not to anyone in the room.
        if (State is not null)
            foreach (var role in State.Players.Where(p => p.Role is not null))
                views.Add(new SeatView { Id = role.Id, Name = role.Name, IsComputer = true, IsConnected = true, Role = role.Role });

        return views;
    }

    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
}

public sealed class RoomSeat
{
    public required string Id { get; init; }

    /// <summary>Who sits here, or null for an open seat.</summary>
    public string? Name { get; set; }

    /// <summary>Proves the seat is someone's; null while it is open.</summary>
    public string? Token { get; set; }

    /// <summary>The live connection, when the person here is connected.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>Played by the computer — an open seat once play starts, or a seat someone left.</summary>
    public bool IsComputer { get; set; }

    /// <summary>
    /// The computer is only standing in: the table voted to play on while this person
    /// was away, and the seat is theirs again the moment they are back.
    /// </summary>
    public bool StandIn { get; set; }

    /// <summary>When the person here lost their connection, while they are away.</summary>
    public DateTime? DisconnectedAt { get; set; }

    public bool IsTaken => Token is not null;
}

/// <summary>An open question: let the computer play for someone away?</summary>
public sealed class RoomVote
{
    public required string SeatId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Who may answer: everyone at the table, and connected, when it was asked.</summary>
    public required HashSet<string> Voters { get; init; }
    public Dictionary<string, bool> Answers { get; } = [];

    /// <summary>More than half of those asked.</summary>
    public int Needed => Voters.Count / 2 + 1;
    public int Yes => Answers.Values.Count(a => a);
    public int No  => Answers.Values.Count(a => !a);

    public bool Carried  => Yes >= Needed;

    /// <summary>Carrying is out of reach: too many have said no.</summary>
    public bool Defeated => Voters.Count - No < Needed;
}
