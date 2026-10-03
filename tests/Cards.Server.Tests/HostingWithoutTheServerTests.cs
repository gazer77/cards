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
        public readonly List<(string Connection, string Reason)> Dismissed = [];
        public Task SendDismissedAsync(string connectionId, string reason) { lock (this) Dismissed.Add((connectionId, reason)); return Task.CompletedTask; }

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

    private sealed class MemoryStore : IRoomStore
    {
        public readonly Dictionary<string, string> Rooms = [];
        public Task SaveAsync(string code, string json) { lock (Rooms) Rooms[code] = json; return Task.CompletedTask; }
        public Task DeleteAsync(string code) { lock (Rooms) Rooms.Remove(code); return Task.CompletedTask; }
        public Task<IReadOnlyList<(string Code, string Json)>> LoadAllAsync()
        {
            lock (Rooms) return Task.FromResult<IReadOnlyList<(string, string)>>(Rooms.Select(r => (r.Key, r.Value)).ToList());
        }
    }

    private static RoomService Host(Recorder clients, IRoomStore store)
        => new(clients, new GameLoader(new EmbeddedGameAssetSource()), NullLogger<RoomService>.Instance, store) { TurnPace = 0 };

    /// <summary>A move for whichever of these people the table is waiting on; false when it waits on none.</summary>
    private static async Task<bool> SomeoneMoves(RoomService rooms, Recorder clients, params (string Connection, SeatTicket Ticket)[] people)
    {
        foreach (var (connection, ticket) in people)
        {
            if (clients.Latest(connection) is not { IsBusy: false } view) continue;
            var move = view.Actions.FirstOrDefault()
                    ?? view.SelectableCardIds.Select(id => view.DefaultCardActions.GetValueOrDefault(id)
                                                           ?? new GameAction("select_card", CardId: id)).FirstOrDefault();
            if (move is null) continue;
            try { await rooms.ActAsync(ticket.Code, ticket.Token, move); return true; }
            catch (TableRefusal) { }
        }
        return false;
    }

    /// <summary>
    /// A restart — every deploy is one — keeps the table: the same code, seats and tokens,
    /// the same cards in the same hands, each seat still reading the table in its own
    /// words, and the game carries on from there.
    /// </summary>
    [Fact]
    public async Task A_table_outlives_its_host_restarting()
    {
        var store   = new MemoryStore();
        var before  = new Recorder();
        var rooms   = Host(before, store);

        var ana = await rooms.CreateAsync("ana-1", "go-fish", 3, [], null, "Ana");
        var bo  = await rooms.JoinAsync("bo-1", ana.Code, "Bo");
        await rooms.StartAsync(ana.Code, ana.Token);

        int moves = 0;
        for (int i = 0; i < 6; i++)
            if (await SomeoneMoves(rooms, before, ("ana-1", ana), ("bo-1", bo))) moves++;
            else break;
        Assert.True(moves > 0, "Nobody moved before the restart; the test proves nothing.");
        await Task.Delay(50);   // the computer's seat finishes its turn

        var anaBefore = before.Latest("ana-1")!;
        var boBefore  = before.Latest("bo-1")!;
        await rooms.SaveChangedAsync();
        Assert.Single(store.Rooms);

        // The host stops, and a new one starts on the same store.
        var after = new Recorder();
        var again = Host(after, store);
        Assert.Equal(1, await again.RestoreAsync());

        await again.RejoinAsync("ana-2", ana.Code, ana.Token);
        await again.RejoinAsync("bo-2", bo.Code, bo.Token);
        var anaAfter = after.Latest("ana-2")!;
        var boAfter  = after.Latest("bo-2")!;

        static List<string> Hand(TableView v, string seat)
            => v.State.Zones.Single(z => z.Id == $"hand:{seat}").Cards.Select(c => $"{c.Rank}/{c.Suit}/{c.Uid}").ToList();

        Assert.Equal(Hand(anaBefore, "player0"), Hand(anaAfter, "player0"));
        Assert.Equal(Hand(boBefore, "player1"), Hand(boAfter, "player1"));
        Assert.Equal(anaBefore.State.PlayerIndex, anaAfter.State.PlayerIndex);
        Assert.Equal(anaBefore.Seats.Select(s => s.Name), anaAfter.Seats.Select(s => s.Name));

        // Each still reads the table in their own words: a line "about you" for Ana
        // is not "about you" for Bo.
        Assert.Equal(anaBefore.Status, anaAfter.Status);
        Assert.Equal(boBefore.Status, boAfter.Status);

        // A view from before is never mistaken for a newer one.
        Assert.True(anaAfter.Version > anaBefore.Version);

        // And play goes on.
        await Task.Delay(50);
        Assert.True(await SomeoneMoves(again, after, ("ana-2", ana), ("bo-2", bo)));
    }

    [Fact]
    public async Task The_host_chooses_how_well_the_computer_plays_and_a_restart_keeps_it()
    {
        var store = new MemoryStore();
        var rooms = Host(new Recorder(), store);

        var ana = await rooms.CreateAsync("ana", "hearts", 4, [], null, "Ana", difficulty: Difficulty.Hard);
        await rooms.StartAsync(ana.Code, ana.Token);

        var room = rooms.Find(ana.Code)!;
        Assert.All(room.State!.Players.Skip(1), p => Assert.True(Assert.IsType<SmartDefaultAiAgent>(room.State.PlayerAgents[p.Id]).Hard));

        await rooms.SaveChangedAsync();
        var again = Host(new Recorder(), store);
        await again.RestoreAsync();
        var restored = again.Find(ana.Code)!;
        Assert.Equal(Difficulty.Hard, restored.Difficulty);
        Assert.True(Assert.IsType<SmartDefaultAiAgent>(restored.State!.PlayerAgents["player1"]).Hard);
    }

    /// <summary>
    /// Taken off a table, a person is told so — and only they are; the others carry on,
    /// the computer playing the seat once the game has started.
    /// </summary>
    [Fact]
    public async Task Whoever_is_taken_off_a_table_is_told_and_the_game_goes_on()
    {
        var clients = new Recorder();
        var rooms   = Host(clients);
        var ana = await rooms.CreateAsync("ana", "hearts", 4, [], null, "Ana");
        await rooms.JoinAsync("bo", ana.Code, "Bo");
        await rooms.StartAsync(ana.Code, ana.Token);

        var (removed, _) = await rooms.RemoveSeatAsync(ana.Code, "player1");

        Assert.True(removed);
        Assert.Equal([("bo", "A manager took you off this table.")], clients.Dismissed);
        Assert.True(rooms.Find(ana.Code)!.Seats[1].IsComputer);

        // Closing the table tells whoever is left.
        await rooms.CloseByManagerAsync(ana.Code);
        Assert.Contains(("ana", "A manager closed this table."), clients.Dismissed);
        Assert.Null(rooms.Find(ana.Code));
    }

    [Fact]
    public async Task A_closed_table_leaves_nothing_behind()
    {
        var store = new MemoryStore();
        var rooms = Host(new Recorder(), store);

        var ana = await rooms.CreateAsync("ana", "hearts", 4, [], null, "Ana");
        await rooms.SaveChangedAsync();
        Assert.Single(store.Rooms);

        await rooms.LeaveAsync(ana.Code, ana.Token);
        Assert.Empty(store.Rooms);
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
