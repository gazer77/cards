using Cards.Engine.Shared;

namespace Cards.Hosting;

/// <summary>
/// How a host reaches the people at its tables. The rooms decide what to say and to
/// whom; this carries it. The table server implements it over SignalR; a phone hosting
/// a game implements it over whatever connects it to the other players.
///
/// A connection id is whatever the transport calls one connection. A room's code
/// names the group of everyone at that table.
/// </summary>
public interface ITableClients
{
    /// <summary>Adds a connection to a room's group, so it hears <see cref="SendRoomAsync"/>.</summary>
    Task JoinRoomAsync(string connectionId, string roomCode);

    Task LeaveRoomAsync(string connectionId, string roomCode);

    /// <summary>Tells everyone at a table who is sitting where, and how the rules vote stands.</summary>
    Task SendRoomAsync(string roomCode, RoomInfo room);

    /// <summary>Gives one person their own view of the table. Never broadcast: each seat's view is its own.</summary>
    Task SendViewAsync(string connectionId, TableView view);
}

/// <summary>
/// A request the table will not carry out, in words to show the person who asked —
/// "That table is full", "It is not your turn". A host passes the message back to the
/// caller; the table server turns it into the hub's own refusal.
/// </summary>
public sealed class TableRefusal(string message) : Exception(message);
