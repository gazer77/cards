using Cards.Engine.Shared;
using Cards.Hosting;
using Microsoft.AspNetCore.SignalR;

namespace Cards.Server;

/// <summary>The table server's way of reaching people: SignalR groups and connections.</summary>
public sealed class SignalRTableClients(IHubContext<TableHub> hub) : ITableClients
{
    public Task JoinRoomAsync(string connectionId, string roomCode)
        => hub.Groups.AddToGroupAsync(connectionId, roomCode);

    public Task LeaveRoomAsync(string connectionId, string roomCode)
        => hub.Groups.RemoveFromGroupAsync(connectionId, roomCode);

    public Task SendRoomAsync(string roomCode, RoomInfo room)
        => hub.Clients.Group(roomCode).SendAsync(TableHubContract.RoomChanged, room);

    public Task SendViewAsync(string connectionId, TableView view)
        => hub.Clients.Client(connectionId).SendAsync(TableHubContract.ViewChanged, view);

    public Task SendDismissedAsync(string connectionId, string reason)
        => hub.Clients.Client(connectionId).SendAsync(TableHubContract.Dismissed, reason);
}

/// <summary>
/// Turns a table's refusal into the hub's own, so the caller gets the words — "That
/// table is full" — rather than SignalR's generic "an error occurred on the server".
/// </summary>
public sealed class RefusalFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try { return await next(context); }
        catch (TableRefusal refusal) { throw new HubException(refusal.Message); }
    }
}

/// <summary>Runs the rooms' clock — dropped-player votes, idle rooms — for as long as the server is up.</summary>
public sealed class RoomHousekeeping(RoomService rooms) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => rooms.RunHousekeepingAsync(stoppingToken);
}
