using Cards.Engine;
using Cards.Engine.Shared;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Cards.Server.Tests;

/// <summary>
/// The server, in memory, with people connecting to it the way a browser or phone does:
/// they open a table, sit down, play, drop out and come back — and each sees only what
/// their seat could see.
/// </summary>
public sealed class SharedTableServerTests : IClassFixture<SharedTableServerTests.Server>
{
    public sealed class Server : WebApplicationFactory<Program>
    {
        public Server()
        {
            // Computer players move at once; the tests are about who sees what, not pace.
            Services.GetRequiredService<RoomService>().TurnPace = 0;
        }
    }

    private readonly Server _server;
    public SharedTableServerTests(Server server) => _server = server;

    /// <summary>One person at the table, as their client sees it.</summary>
    private sealed class Person : IAsyncDisposable
    {
        public required HubConnection Hub { get; init; }
        public SeatTicket? Ticket { get; set; }
        private readonly object _gate = new();
        private TableView? _view;
        private RoomInfo? _room;

        public TableView? View { get { lock (_gate) return _view; } }
        public RoomInfo? Room  { get { lock (_gate) return _room; } }

        public void Watch()
        {
            Hub.On<TableView>(TableHubContract.ViewChanged, v =>
            {
                lock (_gate) if (_view is null || v.Version > _view.Version) _view = v;
            });
            Hub.On<RoomInfo>(TableHubContract.RoomChanged, r => { lock (_gate) _room = r; });
        }

        public async Task<TableView> WaitForView(Func<TableView, bool> ready, string what)
        {
            for (int i = 0; i < 500; i++)
            {
                if (View is { } v && ready(v)) return v;
                await Task.Delay(20);
            }
            throw new TimeoutException($"Never saw {what}.");
        }

        public Task Act(GameAction action)
            => Hub.InvokeAsync(TableHubContract.Act, Ticket!.Code, Ticket.Token, action);

        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }

    private async Task<Person> Connect()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Server.BaseAddress, TableHubContract.Path.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => _server.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        var person = new Person { Hub = hub };
        person.Watch();
        await hub.StartAsync();
        return person;
    }

    private async Task<(Person Ana, Person Bo)> TwoAt(string gameId, int seats)
    {
        var ana = await Connect();
        ana.Ticket = await ana.Hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom, gameId, seats, new List<string>(), (string?)null, "Ana");

        var bo = await Connect();
        bo.Ticket = await bo.Hub.InvokeAsync<SeatTicket>(TableHubContract.JoinRoom, ana.Ticket.Code, "Bo");

        await ana.Hub.InvokeAsync(TableHubContract.StartGame, ana.Ticket.Code, ana.Ticket.Token);
        await ana.WaitForView(_ => true, "Ana's first view");
        await bo.WaitForView(_ => true, "Bo's first view");
        return (ana, bo);
    }

    // ── Seating ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task People_take_seats_in_the_order_they_arrive()
    {
        var ana = await Connect();
        ana.Ticket = await ana.Hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom, "hearts", 4, new List<string>(), (string?)null, "Ana");
        var bo = await Connect();
        bo.Ticket = await bo.Hub.InvokeAsync<SeatTicket>(TableHubContract.JoinRoom, ana.Ticket.Code.ToLowerInvariant(), "Bo");

        Assert.Equal(5, ana.Ticket.Code.Length);
        Assert.Equal("player0", ana.Ticket.SeatId);
        Assert.Equal("player1", bo.Ticket.SeatId);

        for (int i = 0; i < 100 && ana.Room?.Seats.Count(s => s.Name is not null) != 2; i++) await Task.Delay(20);
        Assert.Equal(["Ana", "Bo", null, null], ana.Room!.Seats.Select(s => s.Name));

        await ana.DisposeAsync(); await bo.DisposeAsync();
    }

    [Fact]
    public async Task A_game_that_cannot_be_shared_is_refused_at_the_door()
    {
        var ana = await Connect();
        var ex = await Assert.ThrowsAsync<HubException>(() =>
            ana.Hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom, "go-fish", 2, new List<string>(), (string?)null, "Ana"));
        Assert.Contains("computer", ex.Message);
        await ana.DisposeAsync();
    }

    [Fact]
    public async Task Nobody_sits_down_after_the_deal()
    {
        var (ana, bo) = await TwoAt("hearts", 4);
        var late = await Connect();

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            late.Hub.InvokeAsync<SeatTicket>(TableHubContract.JoinRoom, ana.Ticket!.Code, "Cy"));
        Assert.Contains("started", ex.Message);

        await ana.DisposeAsync(); await bo.DisposeAsync(); await late.DisposeAsync();
    }

    // ── What each seat sees ───────────────────────────────────────────────────

    [Fact]
    public async Task Each_person_sees_their_own_hand_and_only_backs_of_the_others()
    {
        var (ana, bo) = await TwoAt("hearts", 4);

        var anaView = ana.View!;
        var boView  = bo.View!;

        Assert.Equal("player0", anaView.ViewerId);
        Assert.Equal("player1", boView.ViewerId);

        Hand(anaView, "player0").ForEach(c => Assert.False(c.IsHidden));
        Hand(anaView, "player1").ForEach(c => Assert.True(c.IsHidden));
        Hand(boView,  "player1").ForEach(c => Assert.False(c.IsHidden));
        Hand(boView,  "player0").ForEach(c => Assert.True(c.IsHidden));

        // And the names they chose are the names at the table.
        Assert.Contains(anaView.Seats, s => s.Id == "player1" && s.Name == "Bo");

        await ana.DisposeAsync(); await bo.DisposeAsync();

        static List<SavedCard> Hand(TableView v, string seat) => v.State.Zones.Single(z => z.Id == $"hand:{seat}").Cards;
    }

    // ── Who may act ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_move_out_of_turn_is_refused()
    {
        var (ana, bo) = await TwoAt("blackjack", 2);

        // Wait until one of them is being asked to hit or stand.
        Person? waiting = null;
        for (int i = 0; i < 500 && waiting is null; i++)
        {
            if (Asked(ana.View) && !Asked(bo.View)) waiting = bo;
            else if (Asked(bo.View) && !Asked(ana.View)) waiting = ana;
            else await Task.Delay(20);
        }
        if (waiting is null) return;   // both dealt naturals: nobody is asked this round

        var ex = await Assert.ThrowsAsync<HubException>(() => waiting.Act(new GameAction("stand")));
        Assert.Contains("not your turn", ex.Message);

        await ana.DisposeAsync(); await bo.DisposeAsync();

        static bool Asked(TableView? v) => v is { IsBusy: false } && v.Actions.Any(a => a.Type == "stand");
    }

    // ── A whole game ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_people_and_the_house_play_blackjack_to_the_end()
    {
        var (ana, bo) = await TwoAt("blackjack", 2);
        var people = new[] { ana, bo };

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline && !(ana.View?.IsGameOver ?? false))
        {
            bool moved = false;
            foreach (var p in people)
            {
                var v = p.View;
                if (v is null || v.IsBusy) continue;

                // Stand on anything — the point is that the game gets played, by both.
                var move = v.Actions.FirstOrDefault(a => a.Type == "stand")
                        ?? v.Actions.FirstOrDefault(a => !SeatGate.IsTableGesture(a))
                        ?? v.Actions.FirstOrDefault();
                if (move is null) continue;

                try { await p.Act(move); moved = true; }
                catch (HubException) { /* the view was a step behind; look again */ }
                break;
            }
            if (!moved) await Task.Delay(20);
        }

        Assert.True(ana.View!.IsGameOver, "The game never finished.");
        var final = await bo.WaitForView(v => v.IsGameOver, "the end of the game from Bo's seat");
        Assert.Contains(final.State.GameLog, l => l.StartsWith("Bo:") || l.StartsWith("You:"));

        await ana.DisposeAsync(); await bo.DisposeAsync();
    }

    // ── Dropping out ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dropped_player_comes_back_to_their_own_seat()
    {
        var (ana, bo) = await TwoAt("hearts", 4);
        var ticket = bo.Ticket!;
        var handBefore = bo.View!.State.Zones.Single(z => z.Id == "hand:player1").Cards.Select(c => c.Uid).ToList();

        await bo.DisposeAsync();   // the phone went into a pocket

        var back = await Connect();
        back.Ticket = await back.Hub.InvokeAsync<SeatTicket>(TableHubContract.Rejoin, ticket.Code, ticket.Token);
        var view = await back.WaitForView(_ => true, "Bo's view after coming back");

        Assert.Equal("player1", back.Ticket.SeatId);
        Assert.Equal("player1", view.ViewerId);
        Assert.Equal(handBefore, view.State.Zones.Single(z => z.Id == "hand:player1").Cards.Select(c => c.Uid).ToList());

        // A made-up token gets nothing.
        var stranger = await Connect();
        await Assert.ThrowsAsync<HubException>(() =>
            stranger.Hub.InvokeAsync<SeatTicket>(TableHubContract.Rejoin, ticket.Code, "not-a-real-token"));

        await ana.DisposeAsync(); await back.DisposeAsync(); await stranger.DisposeAsync();
    }
}
