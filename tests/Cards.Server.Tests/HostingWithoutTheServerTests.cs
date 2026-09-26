using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cards.Server.Tests;

/// <summary>
/// The rooms run without the table server around them — no ASP.NET, no SignalR — which
/// is what a phone hosting a game will do. Everything here goes through
/// <see cref="ITableClients"/>, played by a recorder standing in for the phone's own
/// connections.
/// </summary>
public sealed class HostingWithoutTheServerTests
{
    private sealed class Recorder : ITableClients
    {
        public readonly Dictionary<string, List<TableView>> Views = [];
        public readonly List<RoomInfo> Rooms = [];
        public readonly HashSet<(string Connection, string Room)> Groups = [];

        public Task JoinRoomAsync(string connectionId, string roomCode)  { lock (this) Groups.Add((connectionId, roomCode)); return Task.CompletedTask; }
        public Task LeaveRoomAsync(string connectionId, string roomCode) { lock (this) Groups.Remove((connectionId, roomCode)); return Task.CompletedTask; }
        public Task SendRoomAsync(string roomCode, RoomInfo room)        { lock (this) Rooms.Add(room); return Task.CompletedTask; }

        public Task SendViewAsync(string connectionId, TableView view)
        {
            lock (this)
            {
                if (!Views.TryGetValue(connectionId, out var list)) Views[connectionId] = list = [];
                list.Add(view);
            }
            return Task.CompletedTask;
        }

        public TableView? Latest(string connection)
        {
            lock (this) return Views.TryGetValue(connection, out var l) ? l.MaxBy(v => v.Version) : null;
        }
    }

    private static RoomService Host(Recorder clients)
        => new(clients, new GameLoader(new EmbeddedGameAssetSource()), NullLogger<RoomService>.Instance) { TurnPace = 0 };

    [Fact]
    public async Task A_host_without_a_web_server_seats_deals_and_shows_each_seat_its_own_table()
    {
        var clients = new Recorder();
        var rooms   = Host(clients);

        // "phone" is the host's own player; "friend" connected over the phone's transport.
        var ana = await rooms.CreateAsync("phone", "hearts", 4, [], null, "Ana");
        var bo  = await rooms.JoinAsync("friend", ana.Code, "Bo");
        await rooms.StartAsync(ana.Code, ana.Token);

        Assert.Contains(("phone", ana.Code), clients.Groups);
        Assert.Contains(("friend", ana.Code), clients.Groups);

        var anaView = clients.Latest("phone")!;
        var boView  = clients.Latest("friend")!;
        Assert.Equal("player0", anaView.ViewerId);
        Assert.Equal("player1", boView.ViewerId);
        Assert.All(boView.State.Zones.Single(z => z.Id == "hand:player0").Cards, c => Assert.True(c.IsHidden));
    }

    [Fact]
    public async Task A_refusal_comes_back_in_words_the_host_can_show()
    {
        var rooms = Host(new Recorder());
        var ana = await rooms.CreateAsync("phone", "blackjack", 1, [], null, "Ana");

        var refusal = await Assert.ThrowsAsync<TableRefusal>(() => rooms.JoinAsync("friend", ana.Code, "Bo"));
        Assert.Equal("That table is full.", refusal.Message);
    }
}
