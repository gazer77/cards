using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cards.Engine;
using Cards.Engine.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Cards.Server;

/// <summary>
/// Every room on this server, and everything that happens in one: seating, starting,
/// moves, the computer players' turns, and telling each seat what it now sees.
///
/// The rules run here and only here. A client sends what its player wants to do; this
/// checks the seat may do it (<see cref="SeatGate"/>), applies it, and sends every seat
/// its own <see cref="TableView"/>. No client ever holds another's cards, and no two
/// copies of a game exist to disagree.
/// </summary>
public sealed class RoomService(IHubContext<TableHub> hub, GameLoader loader, ILogger<RoomService> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A room nobody has touched in this long is closed.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(6);

    /// <summary>Stretches the engine's pauses between automatic turns — 1.0 is its own timing.</summary>
    public double TurnPace { get; set; } = 1.0;

    public Room? Find(string code) => _rooms.GetValueOrDefault(code.Trim());

    // ── Seating ───────────────────────────────────────────────────────────────

    public async Task<SeatTicket> CreateAsync(
        string connectionId, string gameId, int playerCount, IReadOnlyList<string> rules,
        string? configuration, string name, int dropTimeoutSeconds = 60)
    {
        var definition = await loader.LoadAsync(gameId)
            ?? throw new HubException($"There is no game called \"{gameId}\".");

        int min = definition.Players?.Min ?? 1, max = definition.Players?.Max ?? 8;
        if (playerCount < min || playerCount > max)
            throw new HubException($"{definition.Name} is for {min} to {max} players.");

        // Deal a table now and throw it away, so a game that cannot be shared, or a
        // shape that does not fit the count, is refused before anyone sits down.
        var trial = new GameState { GameId = definition.Id, Definition = definition, ConfigurationName = configuration };
        var trialLogic = LogicRegistry.Create(definition);
        try { trialLogic.Initialize(trial, playerCount, rules); }
        catch (Exception ex) { throw new HubException($"{definition.Name} cannot be dealt that way: {ex.Message}"); }
        if (!trialLogic.SharedTableReady)
            throw new HubException($"{definition.Name} can only be played against the computer for now.");

        var room = new Room
        {
            Code          = NewCode(),
            Definition    = definition,
            Configuration = configuration,
            PlayerCount   = playerCount,
            Rules         = [.. rules],
            Offered       = GameConfiguration.Resolve(definition, playerCount, configuration).HouseRules,
            Seats         = Enumerable.Range(0, playerCount).Select(i => new RoomSeat { Id = $"player{i}" }).ToList(),
            DropTimeout   = TimeSpan.FromSeconds(Math.Clamp(dropTimeoutSeconds, 0, 3600)),
        };
        _rooms[room.Code] = room;

        var ticket = Sit(room, room.Seats[0], connectionId, name);

        // The host's choices on the setup screen are their ballot; everyone else starts
        // from the game's own defaults.
        room.Ballots[room.HostSeatId] = rules.Where(r => room.Offered.Any(o => o.Id == r)).ToHashSet();
        await hub.Groups.AddToGroupAsync(connectionId, room.Code);
        await SendRoomAsync(room);
        log.LogInformation("Room {Code} opened for {Game} at {Count} seats", room.Code, definition.Id, playerCount);
        return ticket;
    }

    public async Task<SeatTicket> JoinAsync(string connectionId, string code, string name)
    {
        var room = Find(code) ?? throw new HubException("No table has that code.");

        SeatTicket ticket;
        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new HubException("That game has already started.");
            var seat = room.Seats.FirstOrDefault(s => !s.IsTaken) ?? throw new HubException("That table is full.");
            ticket = Sit(room, seat, connectionId, name);
        }
        finally { room.Lock.Release(); }

        await hub.Groups.AddToGroupAsync(connectionId, room.Code);
        await SendRoomAsync(room);
        return ticket;
    }

    /// <summary>
    /// Back to a seat after a dropped connection — a phone put in a pocket, a page
    /// reloaded. The token is the seat; the connection is only how it is reached today.
    /// </summary>
    public async Task<SeatTicket> RejoinAsync(string connectionId, string code, string token)
    {
        var room = Find(code) ?? throw new HubException("That table has closed.");

        RoomSeat seat;
        await room.Lock.WaitAsync();
        try
        {
            seat = room.SeatByToken(token) ?? throw new HubException("That seat is no longer yours.");
            seat.ConnectionId   = connectionId;
            seat.DisconnectedAt = null;
            room.LastActivity   = DateTime.UtcNow;

            // Back: a question about them is moot, and a stand-in hands the seat over.
            if (room.Vote?.SeatId == seat.Id) room.Vote = null;
            if (seat.StandIn && room.State is { } state)
            {
                seat.StandIn    = false;
                seat.IsComputer = false;
                state.PlayerAgents.Remove(seat.Id);
            }
        }
        finally { room.Lock.Release(); }

        await hub.Groups.AddToGroupAsync(connectionId, room.Code);
        await SendRoomAsync(room);
        await SendViewsAsync(room);
        return new SeatTicket { Code = room.Code, SeatId = seat.Id, Token = token };
    }

    /// <summary>
    /// Leaving before the deal frees the seat. Leaving during play hands it to the
    /// computer, so the others can finish the game.
    /// </summary>
    public async Task LeaveAsync(string code, string token)
    {
        var room = Find(code);
        if (room is null) return;

        await room.Lock.WaitAsync();
        try
        {
            var seat = room.SeatByToken(token);
            if (seat is null) return;

            if (seat.ConnectionId is not null)
                await hub.Groups.RemoveFromGroupAsync(seat.ConnectionId, room.Code);

            if (room.State is null)
            {
                seat.Name = null; seat.Token = null; seat.ConnectionId = null;
                room.Ballots.Remove(seat.Id);
            }
            else
            {
                if (room.Vote?.SeatId == seat.Id) room.Vote = null;
                seat.Token = null; seat.ConnectionId = null; seat.IsComputer = true; seat.StandIn = false;
                room.State.PlayerAgents[seat.Id] = new SmartDefaultAiAgent(seat.Id, room.State.Rng);
            }

            // Nobody left: close the table.
            if (room.Seats.All(s => !s.IsTaken))
            {
                _rooms.TryRemove(room.Code, out _);
                return;
            }
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
        await SendViewsAsync(room);
        _ = RunTableAsync(room);
    }

    public async Task DisconnectedAsync(string connectionId)
    {
        foreach (var room in _rooms.Values)
        {
            bool changed = false;
            await room.Lock.WaitAsync();
            try
            {
                foreach (var seat in room.Seats.Where(s => s.ConnectionId == connectionId))
                {
                    seat.ConnectionId   = null;
                    seat.DisconnectedAt = DateTime.UtcNow;
                    changed = true;
                }
            }
            finally { room.Lock.Release(); }

            if (changed)
            {
                await SendRoomAsync(room);
                await SendViewsAsync(room);
            }
        }
    }

    // ── Play ──────────────────────────────────────────────────────────────────

    /// <summary>Deals. Only the host may, and every open seat becomes a computer player.</summary>
    public async Task StartAsync(string code, string token)
    {
        var room = Find(code) ?? throw new HubException("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new HubException("The game has already started.");
            if (room.SeatByToken(token)?.Id != room.HostSeatId) throw new HubException("Only the host can start the game.");

            foreach (var seat in room.Seats.Where(s => !s.IsTaken)) seat.IsComputer = true;

            // What the table agreed is what is dealt.
            room.Rules = room.AgreedRules();

            var state = new GameState
            {
                GameId            = room.Definition.Id,
                Definition        = room.Definition,
                ConfigurationName = room.Configuration,
                Rng               = new SeededRandomSource((ulong)RandomNumberGenerator.GetInt32(int.MaxValue) << 16
                                                           ^ (ulong)RandomNumberGenerator.GetInt32(int.MaxValue)),
                // People are named from the first line the table writes.
                SeatNames = room.Seats.Select(s => s.IsComputer ? null : s.Name).ToList(),
            };
            var logic = LogicRegistry.Create(room.Definition);
            logic.Initialize(state, room.PlayerCount, room.Rules);

            // The engine seats a computer everywhere but seat 0; here people sit where
            // they chose, and the computer everywhere nobody did.
            foreach (var seat in room.Seats)
            {
                if (seat.IsComputer)
                    state.PlayerAgents.TryAdd(seat.Id, new SmartDefaultAiAgent(seat.Id, state.Rng));
                else
                    state.PlayerAgents.Remove(seat.Id);
            }

            room.State = state;
            room.Logic = logic;
            room.LastActivity = DateTime.UtcNow;
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
        await SendViewsAsync(room);
        _ = RunTableAsync(room);
    }

    /// <summary>A move from a seat: checked, applied, and shown to everyone.</summary>
    public async Task ActAsync(string code, string token, GameAction action)
    {
        var room = Find(code) ?? throw new HubException("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not { } state || room.Logic is not { } logic)
                throw new HubException("The game has not started.");
            var seat = room.SeatByToken(token) ?? throw new HubException("That seat is no longer yours.");
            if (room.Busy) throw new HubException("Wait for the table.");

            // A hidden card is named by its alias; the rules know it by what it is.
            if (action.CardUid is < 0 and var aliased && room.Unalias(aliased) is { } real)
                action = action with { CardId = real.Id, CardUid = real.Uid };
            action = action with { PlayerId = seat.Id };

            if (!SeatGate.Allows(state, logic, seat.Id, action, out var reason))
                throw new HubException(reason ?? "That move is not available.");

            logic.Apply(state, action);
            RecordStatus(room, state, logic);
            room.Version++;
            room.LastActivity = DateTime.UtcNow;
        }
        finally { room.Lock.Release(); }

        await SendViewsAsync(room);
        _ = RunTableAsync(room);
    }

    // ── Someone away ──────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a vote on anyone who has held the table up past the room's timeout: the
    /// game is waiting on their move, and they have been disconnected that long.
    /// Everyone still at the table is asked whether the computer should play for them.
    /// </summary>
    public async Task CheckDropsAsync(DateTime now)
    {
        foreach (var room in _rooms.Values)
        {
            if (room.DropTimeout <= TimeSpan.Zero) continue;

            bool opened = false;
            await room.Lock.WaitAsync();
            try
            {
                if (room.Vote is not null || room.Busy) continue;
                if (room.State is not { } state || room.Logic is not { } logic || logic.IsGameOver(state)) continue;

                var waitingOn = room.Seats.FirstOrDefault(s => s.Id == state.CurrentPlayer.Id);
                if (waitingOn is not { IsComputer: false, DisconnectedAt: { } since }) continue;
                if (now - since < room.DropTimeout) continue;

                var voters = room.Seats
                    .Where(s => !s.IsComputer && s.ConnectionId is not null && s.Id != waitingOn.Id)
                    .Select(s => s.Id)
                    .ToHashSet();
                if (voters.Count == 0) continue;   // nobody left to ask, nobody waiting

                room.Vote = new RoomVote { SeatId = waitingOn.Id, Voters = voters, StartedAt = now };
                opened = true;
            }
            finally { room.Lock.Release(); }

            if (opened) await SendViewsAsync(room);
        }
    }

    /// <summary>
    /// An answer to the open question. A majority for hands the seat to the computer
    /// until its owner is back; enough against closes the question, and it is asked
    /// again only after another full timeout.
    /// </summary>
    public async Task VoteAsync(string code, string token, bool letComputerPlay)
    {
        var room = Find(code) ?? throw new HubException("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            var seat = room.SeatByToken(token) ?? throw new HubException("That seat is no longer yours.");
            if (room.Vote is not { } vote || !vote.Voters.Contains(seat.Id))
                throw new HubException("There is nothing to vote on.");

            vote.Answers[seat.Id] = letComputerPlay;
            var away = room.Seats.First(s => s.Id == vote.SeatId);

            if (vote.Carried && room.State is { } state)
            {
                away.IsComputer = true;
                away.StandIn    = true;
                state.PlayerAgents[away.Id] = new SmartDefaultAiAgent(away.Id, state.Rng);
                room.Vote = null;
                room.Version++;
            }
            else if (vote.Defeated)
            {
                room.Vote = null;
                away.DisconnectedAt = DateTime.UtcNow;   // a fresh wait before asking again
            }
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
        await SendViewsAsync(room);
        _ = RunTableAsync(room);
    }

    /// <summary>
    /// Plays every step that needs no person — computer players, a dealer drawing,
    /// a trick being gathered — at the pace the engine asks for, showing each one.
    /// The same loop the single-player table runs, moved to where the game lives.
    /// </summary>
    private async Task RunTableAsync(Room room)
    {
        await room.Lock.WaitAsync();
        try
        {
            if (room.AutoRunning) return;
            room.AutoRunning = true;
        }
        finally { room.Lock.Release(); }

        try
        {
            while (true)
            {
                TimeSpan delay;
                await room.Lock.WaitAsync();
                try
                {
                    if (room.State is not { } state || room.Logic is not { } logic) break;
                    if (logic.IsGameOver(state) || logic.GetAutoAdvanceDelay(state) is not { } d) break;
                    delay = d;
                    if (!room.Busy) { room.Busy = true; }
                }
                finally { room.Lock.Release(); }

                await SendViewsAsync(room);
                if (delay > TimeSpan.Zero) await Task.Delay(delay * TurnPace);

                bool stop = false;
                await room.Lock.WaitAsync();
                try
                {
                    var state = room.State!;
                    var logic = room.Logic!;
                    var actions = logic.GetValidActions(state);
                    var cards   = logic.GetSelectableCardIds(state);

                    // Nothing to do, or a lone "ready" left for the people to press
                    // once they have looked at what was revealed.
                    if ((actions.Count == 0 && cards.Count == 0)
                        || (actions.Count == 1 && cards.Count == 0 && actions[0].Type == "ready"))
                        stop = true;
                    else
                    {
                        logic.Apply(state, logic.GetAutoAction(state));
                        RecordStatus(room, state, logic);
                        room.Version++;
                    }
                }
                finally { room.Lock.Release(); }

                if (stop) break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Room {Code} stopped playing its own turns", room.Code);
        }
        finally
        {
            await room.Lock.WaitAsync();
            try { room.AutoRunning = false; room.Busy = false; }
            finally { room.Lock.Release(); }
            await SendViewsAsync(room);
        }
    }

    // ── Telling people ────────────────────────────────────────────────────────

    /// <summary>
    /// Adds the status line to the log when it changes, as a single-player table does:
    /// the log is the history of what the table said, and the engine writes only the
    /// moves themselves. Lines are stored in seat 0's words like everything else and
    /// read back in each seat's own.
    /// </summary>
    private static void RecordStatus(Room room, GameState state, IGameLogic logic)
    {
        string status = logic.GetStatusText(state);
        if (string.IsNullOrEmpty(status) || status == room.LastStatus) return;
        room.LastStatus = status;
        state.GameLog.Add(status);
    }

    private Task SendRoomAsync(Room room)
        => hub.Clients.Group(room.Code).SendAsync(TableHubContract.RoomChanged, room.Info());

    /// <summary>Each connected person gets their own view — never anyone else's.</summary>
    private async Task SendViewsAsync(Room room)
    {
        var sends = new List<(string Connection, TableView View)>();

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not { } state || room.Logic is not { } logic) return;

            var said  = state.Announcements.ToList();
            var seats = room.SeatViews();
            foreach (var seat in room.Seats.Where(s => s.ConnectionId is not null && !s.IsComputer))
            {
                var view = TableProjection.For(
                    state, logic, seat.Id, room.PlayerCount, room.Rules, seats,
                    room.Alias, ++room.ViewSequence, room.Busy, said);

                if (room.Vote is { } vote && vote.Voters.Contains(seat.Id))
                    view.Vote = new SeatVoteView
                    {
                        SeatId      = vote.SeatId,
                        Name        = seats.FirstOrDefault(s => s.Id == vote.SeatId)?.Name ?? vote.SeatId,
                        AwaySeconds = (int)(DateTime.UtcNow - (room.Seats.First(s => s.Id == vote.SeatId).DisconnectedAt ?? DateTime.UtcNow)).TotalSeconds,
                        Yes         = vote.Yes,
                        No          = vote.No,
                        Needed      = vote.Needed,
                        Voters      = vote.Voters.Count,
                        Mine        = vote.Answers.TryGetValue(seat.Id, out var mine) ? mine : null,
                    };

                sends.Add((seat.ConnectionId!, view));
            }

            // Said once: a bubble is shown with the view that brought it.
            state.Announcements.Clear();
        }
        finally { room.Lock.Release(); }

        foreach (var (connection, view) in sends)
            await hub.Clients.Client(connection).SendAsync(TableHubContract.ViewChanged, view);
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    private static readonly char[] CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();

    /// <summary>Five letters, none of them I or O — easy to read out across a room.</summary>
    private string NewCode()
    {
        while (true)
        {
            string code = new(Enumerable.Range(0, 5).Select(_ => CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)]).ToArray());
            if (!_rooms.ContainsKey(code)) return code;
        }
    }

    private static SeatTicket Sit(Room room, RoomSeat seat, string connectionId, string name)
    {
        seat.Name         = string.IsNullOrWhiteSpace(name) ? $"Player {room.Seats.IndexOf(seat) + 1}" : name.Trim()[..Math.Min(name.Trim().Length, 24)];
        seat.Token        = Room.NewToken();
        seat.ConnectionId = connectionId;
        seat.IsComputer   = false;
        room.LastActivity = DateTime.UtcNow;

        // A new ballot starts from the game's own defaults.
        room.Ballots[seat.Id] = room.Offered.Where(r => r.Default).Select(r => r.Id).ToHashSet();
        return new SeatTicket { Code = room.Code, SeatId = seat.Id, Token = seat.Token };
    }

    // ── House rules ───────────────────────────────────────────────────────────

    /// <summary>One person's yes or no on one house rule, before the deal.</summary>
    public async Task SetBallotAsync(string code, string token, string ruleId, bool yes)
    {
        var room = Find(code) ?? throw new HubException("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new HubException("The rules were settled at the deal.");
            var seat = room.SeatByToken(token) ?? throw new HubException("That seat is no longer yours.");
            if (room.Offered.All(r => r.Id != ruleId)) throw new HubException("That is not a rule of this game.");

            var ballot = room.Ballots.TryGetValue(seat.Id, out var b) ? b : room.Ballots[seat.Id] = [];
            if (yes) ballot.Add(ruleId); else ballot.Remove(ruleId);
            room.LastActivity = DateTime.UtcNow;
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
    }

    /// <summary>
    /// The host settling a rule over the vote — in, out, or back to the vote (null).
    /// Only where the game's definition says the host may (<c>house_rule_vote.host_override</c>).
    /// </summary>
    public async Task ForceRuleAsync(string code, string token, string ruleId, bool? forced)
    {
        var room = Find(code) ?? throw new HubException("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new HubException("The rules were settled at the deal.");
            if (room.SeatByToken(token)?.Id != room.HostSeatId) throw new HubException("Only the host can overrule the vote.");
            if (!room.Definition.HouseRuleVote.HostOverride) throw new HubException($"In {room.Definition.Name} the vote decides; the host cannot overrule it.");
            if (room.Offered.All(r => r.Id != ruleId)) throw new HubException("That is not a rule of this game.");

            if (forced is { } f) room.Forced[ruleId] = f;
            else                 room.Forced.Remove(ruleId);
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
    }

    /// <summary>Closes rooms nobody has touched in <see cref="IdleLimit"/>.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastSweep = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { return; }

            try { await CheckDropsAsync(DateTime.UtcNow); }
            catch (Exception ex) { log.LogError(ex, "Checking for dropped players failed"); }

            if (DateTime.UtcNow - lastSweep < TimeSpan.FromMinutes(10)) continue;
            lastSweep = DateTime.UtcNow;

            foreach (var (code, room) in _rooms)
                if (DateTime.UtcNow - room.LastActivity > IdleLimit)
                    _rooms.TryRemove(code, out _);
        }
    }
}
