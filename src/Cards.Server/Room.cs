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
    public required IReadOnlyList<string> Rules { get; init; }
    public required List<RoomSeat> Seats { get; init; }

    public string HostSeatId => Seats[0].Id;

    public GameState? State { get; set; }
    public IGameLogic? Logic { get; set; }
    public long Version { get; set; }

    /// <summary>Numbers every view sent, so a client can drop one that arrives after a newer one.</summary>
    public long ViewSequence { get; set; }

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
        EnabledRules  = [.. Rules],
        Started       = State is not null,
        HostSeatId    = HostSeatId,
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

    public bool IsTaken => Token is not null;
}
