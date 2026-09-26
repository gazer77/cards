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

    private async Task<(Person Ana, Person Bo)> TwoAt(string gameId, int seats, int dropTimeout = 60)
    {
        var ana = await Connect();
        ana.Ticket = await ana.Hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom, gameId, seats, new List<string>(), (string?)null, "Ana", dropTimeout);

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
        ana.Ticket = await ana.Hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom, "hearts", 4, new List<string>(), (string?)null, "Ana", 60);
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
    public async Task A_table_the_game_cannot_seat_is_refused_at_the_door()
    {
        var ana = await Connect();
        var ex = await Assert.ThrowsAsync<HubException>(() =>
            ana.Hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom, "go-fish", 9, new List<string>(), (string?)null, "Ana", 60));
        Assert.Contains("2 to 6", ex.Message);
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

    // ── Go Fish, shared ───────────────────────────────────────────────────────

    [Fact]
    public async Task Two_people_and_the_computer_play_go_fish_to_the_end()
    {
        var (ana, bo) = await TwoAt("go-fish", 3);
        var people = new[] { ana, bo };

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline && !(ana.View?.IsGameOver ?? false))
        {
            bool moved = false;
            foreach (var p in people)
            {
                var v = p.View;
                if (v is null || v.IsBusy) continue;

                // A person's turn: tap a card, then ask whoever is offered first.
                var move = v.Actions.FirstOrDefault(a => a.Type == "ask")
                        ?? (v.SelectableCardIds.Count > 0 ? new GameAction("select_card", CardId: v.SelectableCardIds[0]) : null)
                        ?? v.Actions.FirstOrDefault();
                if (move is null) continue;

                try { await p.Act(move); moved = true; }
                catch (HubException) { }
                break;
            }
            if (!moved) await Task.Delay(20);
        }

        Assert.True(ana.View!.IsGameOver, "Go Fish never finished.");

        // Both people asked someone at some point, in their own words.
        Assert.Contains(ana.View.State.GameLog.Concat([ana.View.Status]), l => l.Contains("Bo asked"));
        var boEnd = await bo.WaitForView(v => v.IsGameOver, "the end from Bo's seat");
        Assert.True(boEnd.State.Zones.Where(z => z.Id.StartsWith("books:")).Sum(z => z.Cards.Count) == 52);

        await ana.DisposeAsync(); await bo.DisposeAsync();
    }

    // ── Someone away ──────────────────────────────────────────────────────────

    /// <summary>Waits until one of the two is being asked to hit or stand; returns (asked, other).</summary>
    private static async Task<(Person Asked, Person Other)?> OneAsked(Person ana, Person bo)
    {
        for (int i = 0; i < 500; i++)
        {
            if (Asked(ana.View) && !Asked(bo.View)) return (ana, bo);
            if (Asked(bo.View) && !Asked(ana.View)) return (bo, ana);
            await Task.Delay(20);
        }
        return null;

        static bool Asked(TableView? v) => v is { IsBusy: false } && v.Actions.Any(a => a.Type == "stand");
    }

    [Fact]
    public async Task The_table_votes_to_let_the_computer_play_for_someone_away_and_gives_it_back()
    {
        var (ana, bo) = await TwoAt("blackjack", 2, dropTimeout: 1);
        if (await OneAsked(ana, bo) is not var (asked, other)) return;   // both naturals

        var ticket = asked.Ticket!;
        string seat = ticket.SeatId;
        await asked.DisposeAsync();   // gone, on their own turn

        var question = await other.WaitForView(v => v.Vote is not null, "the vote");
        Assert.Equal(seat, question.Vote!.SeatId);
        Assert.Null(question.Vote.Mine);

        await other.Hub.InvokeAsync(TableHubContract.Vote, other.Ticket!.Code, other.Ticket.Token, true);

        // The computer plays their hand, and everyone can see it is only standing in.
        var covered = await other.WaitForView(v => v.Vote is null && v.Seats.Any(s => s.Id == seat && s.StandIn),
                                              "the stand-in");
        Assert.Contains(covered.Seats, s => s.Id == seat && s.IsComputer);

        // Back: the seat is theirs again.
        var back = await Connect();
        back.Ticket = await back.Hub.InvokeAsync<SeatTicket>(TableHubContract.Rejoin, ticket.Code, ticket.Token);
        var mine = await back.WaitForView(v => v.Seats.Any(s => s.Id == seat && !s.IsComputer && !s.StandIn),
                                          "the seat handed back");
        Assert.Equal(seat, mine.ViewerId);

        await other.DisposeAsync(); await back.DisposeAsync();
    }

    [Fact]
    public async Task A_vote_to_wait_keeps_the_seat_for_its_owner()
    {
        var (ana, bo) = await TwoAt("blackjack", 2, dropTimeout: 1);
        if (await OneAsked(ana, bo) is not var (asked, other)) return;

        string seat = asked.Ticket!.SeatId;
        await asked.DisposeAsync();

        await other.WaitForView(v => v.Vote is not null, "the vote");
        await other.Hub.InvokeAsync(TableHubContract.Vote, other.Ticket!.Code, other.Ticket.Token, false);

        var after = await other.WaitForView(v => v.Vote is null, "the vote closing");
        Assert.Contains(after.Seats, s => s.Id == seat && !s.IsComputer && !s.IsConnected);

        await other.DisposeAsync();
    }

    [Fact]
    public async Task A_table_set_never_to_ask_waits()
    {
        var (ana, bo) = await TwoAt("blackjack", 2, dropTimeout: 0);
        if (await OneAsked(ana, bo) is not var (asked, other)) return;

        await asked.DisposeAsync();
        await Task.Delay(2500);

        Assert.Null(other.View!.Vote);
        await other.DisposeAsync();
    }
}
