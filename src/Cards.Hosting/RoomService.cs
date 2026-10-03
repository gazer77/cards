using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Services;
using Microsoft.Extensions.Logging;

namespace Cards.Hosting;

/// <summary>
/// Every room a host holds, and everything that happens in one: seating, starting,
/// moves, the computer players' turns, and telling each seat what it now sees. The
/// same whether the host is the table server or a phone: how the telling travels is
/// <see cref="ITableClients"/>, and refusals come back as <see cref="TableRefusal"/>.
///
/// The rules run here and only here. A client sends what its player wants to do; this
/// checks the seat may do it (<see cref="SeatGate"/>), applies it, and sends every seat
/// its own <see cref="TableView"/>. No client ever holds another's cards, and no two
/// copies of a game exist to disagree.
/// </summary>
public sealed class RoomService(
    ITableClients clients, GameLoader loader, ILogger<RoomService> log, IRoomStore? store = null,
    AccountStore? accounts = null)
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A room nobody has touched in this long is closed.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(6);

    /// <summary>Stretches the engine's pauses between automatic turns — 1.0 is its own timing.</summary>
    public double TurnPace { get; set; } = 1.0;

    public Room? Find(string code) => _rooms.GetValueOrDefault(code.Trim());

    private const string BannedWords = "You can't play at tables on this server.";

    /// <summary>The account sitting down, if its device said which — refused at the door when it is barred.</summary>
    private StoredAccount? Admit(string? accountCode)
    {
        var account = accounts?.Find(accountCode);
        if (account is { Banned: true }) throw new TableRefusal(BannedWords);
        return account;
    }

    // ── A manager's view ──────────────────────────────────────────────────────

    /// <summary>Every open table, as a manager sees it: the game, who sits where, and how lately anyone played.</summary>
    public IReadOnlyList<RoomSummary> Summaries()
        => _rooms.Values.OrderByDescending(r => r.LastActivity).Select(r => new RoomSummary
        {
            Code         = r.Code,
            GameName     = r.Definition.Name,
            Started      = r.State is not null,
            LastActivity = r.LastActivity,
            Seats        = r.Seats.Select(s => new SeatSummary
            {
                Id = s.Id, Name = s.Name, IsComputer = s.IsComputer, IsConnected = s.ConnectionId is not null,
                AccountId = s.AccountId,
            }).ToList(),
        }).ToList();

    /// <summary>Closes a table at a manager's word, telling everyone at it why. False when there is no such table.</summary>
    public async Task<bool> CloseByManagerAsync(string code)
    {
        if (Find(code) is not { } room) return false;

        List<string> connected;
        await room.Lock.WaitAsync();
        try { connected = room.Seats.Where(s => s.ConnectionId is not null).Select(s => s.ConnectionId!).ToList(); }
        finally { room.Lock.Release(); }

        foreach (var connection in connected)
        {
            await clients.SendDismissedAsync(connection, "A manager closed this table.");
            await clients.LeaveRoomAsync(connection, room.Code);
        }
        await CloseAsync(room.Code);
        log.LogInformation("Room {Code} closed by a manager", room.Code);
        return true;
    }

    /// <summary>
    /// Takes someone off a table at a manager's word: before the deal their seat is freed,
    /// once play has started the computer takes it for good. They are told, and their
    /// token stops working. Returns the account that sat there, if known — so a ban can
    /// follow — or null when there is no such seat.
    /// </summary>
    public async Task<(bool Removed, string? AccountId)> RemoveSeatAsync(string code, string seatId)
    {
        if (Find(code) is not { } room) return (false, null);

        string? connection, accountId;
        await room.Lock.WaitAsync();
        try
        {
            var seat = room.Seats.FirstOrDefault(s => s.Id == seatId && s.IsTaken && !s.IsComputer);
            if (seat is null) return (false, null);

            connection = seat.ConnectionId;
            accountId  = seat.AccountId;
            if (room.Vote?.SeatId == seat.Id) room.Vote = null;

            if (room.State is null)
            {
                seat.Name = null; seat.Token = null; seat.ConnectionId = null; seat.AccountId = null;
                room.Ballots.Remove(seat.Id);
            }
            else
            {
                seat.Token = null; seat.ConnectionId = null; seat.AccountId = null;
                seat.IsComputer = true; seat.StandIn = false;
                room.State.PlayerAgents[seat.Id] = ComputerPlayers.For(room.State, seat.Id);
            }
            room.Dirty = true;
        }
        finally { room.Lock.Release(); }

        if (connection is not null)
        {
            await clients.SendDismissedAsync(connection, "A manager took you off this table.");
            await clients.LeaveRoomAsync(connection, room.Code);
        }

        if (room.Seats.All(s => !s.IsTaken))
            await CloseAsync(room.Code);
        else
        {
            await SendRoomAsync(room);
            await SendViewsAsync(room);
            _ = RunTableAsync(room);
        }
        return (true, accountId);
    }

    // ── Seating ───────────────────────────────────────────────────────────────

    public async Task<SeatTicket> CreateAsync(
        string connectionId, string gameId, int playerCount, IReadOnlyList<string> rules,
        string? configuration, string name, int dropTimeoutSeconds = 60, string? difficulty = null,
        string? accountCode = null)
    {
        var account = Admit(accountCode);
        var definition = await loader.LoadAsync(gameId)
            ?? throw new TableRefusal($"There is no game called \"{gameId}\".");

        int min = definition.Players?.Min ?? 1, max = definition.Players?.Max ?? 8;
        if (playerCount < min || playerCount > max)
            throw new TableRefusal($"{definition.Name} is for {min} to {max} players.");

        // Deal a table now and throw it away, so a game that cannot be shared, or a
        // shape that does not fit the count, is refused before anyone sits down.
        var trial = new GameState { GameId = definition.Id, Definition = definition, ConfigurationName = configuration };
        var trialLogic = LogicRegistry.Create(definition);
        try { trialLogic.Initialize(trial, playerCount, rules); }
        catch (Exception ex) { throw new TableRefusal($"{definition.Name} cannot be dealt that way: {ex.Message}"); }
        if (!trialLogic.SharedTableReady)
            throw new TableRefusal($"{definition.Name} can only be played against the computer for now.");

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
            Difficulty    = Difficulty.Of(difficulty),
        };
        _rooms[room.Code] = room;

        var ticket = Sit(room, room.Seats[0], connectionId, name);
        room.Seats[0].AccountId = account?.Id;

        // The host's choices on the setup screen are their ballot; everyone else starts
        // from the game's own defaults.
        room.Ballots[room.HostSeatId] = rules.Where(r => room.Offered.Any(o => o.Id == r)).ToHashSet();
        await clients.JoinRoomAsync(connectionId, room.Code);
        await SendRoomAsync(room);
        log.LogInformation("Room {Code} opened for {Game} at {Count} seats", room.Code, definition.Id, playerCount);
        return ticket;
    }

    public async Task<SeatTicket> JoinAsync(string connectionId, string code, string name, string? accountCode = null)
    {
        var account = Admit(accountCode);
        var room = Find(code) ?? throw new TableRefusal("No table has that code.");

        SeatTicket ticket;
        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new TableRefusal("That game has already started.");
            var seat = room.Seats.FirstOrDefault(s => !s.IsTaken) ?? throw new TableRefusal("That table is full.");
            ticket = Sit(room, seat, connectionId, name);
            seat.AccountId = account?.Id;
        }
        finally { room.Lock.Release(); }

        await clients.JoinRoomAsync(connectionId, room.Code);
        await SendRoomAsync(room);
        return ticket;
    }

    /// <summary>
    /// Back to a seat after a dropped connection — a phone put in a pocket, a page
    /// reloaded. The token is the seat; the connection is only how it is reached today.
    /// </summary>
    public async Task<SeatTicket> RejoinAsync(string connectionId, string code, string token)
    {
        var room = Find(code) ?? throw new TableRefusal("That table has closed.");

        RoomSeat seat;
        await room.Lock.WaitAsync();
        try
        {
            seat = room.SeatByToken(token) ?? throw new TableRefusal("That seat is no longer yours.");
            if (seat.AccountId is { } id && accounts?.ById(id) is { Banned: true })
                throw new TableRefusal(BannedWords);
            seat.ConnectionId   = connectionId;
            seat.DisconnectedAt = null;
            room.LastActivity   = DateTime.UtcNow;
            room.Dirty          = true;

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

        await clients.JoinRoomAsync(connectionId, room.Code);
        await SendRoomAsync(room);
        await SendViewsAsync(room);
        _ = RunTableAsync(room);   // a table stopped mid-way through its own turns, by a restart, carries on
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
                await clients.LeaveRoomAsync(seat.ConnectionId, room.Code);

            if (room.State is null)
            {
                seat.Name = null; seat.Token = null; seat.ConnectionId = null;
                room.Ballots.Remove(seat.Id);
            }
            else
            {
                if (room.Vote?.SeatId == seat.Id) room.Vote = null;
                seat.Token = null; seat.ConnectionId = null; seat.IsComputer = true; seat.StandIn = false;
                room.State.PlayerAgents[seat.Id] = ComputerPlayers.For(room.State, seat.Id);
            }
            room.Dirty = true;

            // Nobody left: close the table.
            if (room.Seats.All(s => !s.IsTaken))
            {
                await CloseAsync(room.Code);
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
        var room = Find(code) ?? throw new TableRefusal("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new TableRefusal("The game has already started.");
            if (room.SeatByToken(token)?.Id != room.HostSeatId) throw new TableRefusal("Only the host can start the game.");

            foreach (var seat in room.Seats.Where(s => !s.IsTaken)) seat.IsComputer = true;

            // What the table agreed is what is dealt.
            room.Rules = room.AgreedRules();

            var state = new GameState
            {
                GameId            = room.Definition.Id,
                Definition        = room.Definition,
                ConfigurationName = room.Configuration,
                Difficulty        = room.Difficulty,
                Rng               = new SeededRandomSource((ulong)RandomNumberGenerator.GetInt32(int.MaxValue) << 16
                                                           ^ (ulong)RandomNumberGenerator.GetInt32(int.MaxValue)),
                // People are named from the first line the table writes.
                SeatNames = room.Seats.Select(s => s.IsComputer ? null : s.Name).ToList(),
                // And the computer's seats too, never with a name someone is sitting under.
                BotNames  = BotNames.Pick(room.PlayerCount, room.Seats.Where(s => !s.IsComputer).Select(s => s.Name), new Random()),
            };
            var logic = LogicRegistry.Create(room.Definition);
            logic.Initialize(state, room.PlayerCount, room.Rules);

            // The engine seats a computer everywhere but seat 0; here people sit where
            // they chose, and the computer everywhere nobody did.
            foreach (var seat in room.Seats)
            {
                if (seat.IsComputer)
                    state.PlayerAgents.TryAdd(seat.Id, ComputerPlayers.For(state, seat.Id));
                else
                    state.PlayerAgents.Remove(seat.Id);
            }

            room.State = state;
            room.Logic = logic;
            room.LastActivity = DateTime.UtcNow;
            room.Dirty = true;
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
        await SendViewsAsync(room);
        _ = RunTableAsync(room);
    }

    /// <summary>A move from a seat: checked, applied, and shown to everyone.</summary>
    public async Task ActAsync(string code, string token, GameAction action)
    {
        var room = Find(code) ?? throw new TableRefusal("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not { } state || room.Logic is not { } logic)
                throw new TableRefusal("The game has not started.");
            var seat = room.SeatByToken(token) ?? throw new TableRefusal("That seat is no longer yours.");
            if (room.Busy) throw new TableRefusal("Wait for the table.");

            // A hidden card is named by its alias; the rules know it by what it is. The alias
            // may come as the uid or only in the name ("hidden-123") — a client that knows
            // the card by nothing else sends just that, and Golf is all such cards.
            int? alias = action.CardUid is < 0 ? action.CardUid
                       : action.CardId is { } named && named.StartsWith("hidden") && int.TryParse(named[6..], out var n) ? n
                       : null;
            if (alias is { } aliased && room.Unalias(aliased) is { } real)
                action = action with { CardId = real.Id, CardUid = real.Uid };
            action = action with { PlayerId = seat.Id };

            if (!SeatGate.Allows(state, logic, seat.Id, action, out var reason))
                throw new TableRefusal(reason ?? "That move is not available.");

            logic.Apply(state, action);
            RecordStatus(room, state, logic);
            room.Version++;
            room.LastActivity = DateTime.UtcNow;
            room.Dirty = true;
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
        var room = Find(code) ?? throw new TableRefusal("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            var seat = room.SeatByToken(token) ?? throw new TableRefusal("That seat is no longer yours.");
            if (room.Vote is not { } vote || !vote.Voters.Contains(seat.Id))
                throw new TableRefusal("There is nothing to vote on.");

            vote.Answers[seat.Id] = letComputerPlay;
            var away = room.Seats.First(s => s.Id == vote.SeatId);

            if (vote.Carried && room.State is { } state)
            {
                away.IsComputer = true;
                away.StandIn    = true;
                state.PlayerAgents[away.Id] = ComputerPlayers.For(state, away.Id);
                room.Vote = null;
                room.Version++;
                room.Dirty = true;
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
                        room.Dirty = true;
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
        => clients.SendRoomAsync(room.Code, room.Info());

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
            await clients.SendViewAsync(connection, view);
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
        room.Dirty = true;

        // A new ballot starts from the game's own defaults.
        room.Ballots[seat.Id] = room.Offered.Where(r => r.Default).Select(r => r.Id).ToHashSet();
        return new SeatTicket { Code = room.Code, SeatId = seat.Id, Token = seat.Token };
    }

    // ── Keeping rooms across a restart ────────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions StoreJson = new() { WriteIndented = false };

    /// <summary>
    /// Views sent before a restart outnumber the ones the store last saw by however many
    /// went out in the second after it. A client drops a view older than one it has, so
    /// a restored room starts its count well past anything it could have sent.
    /// </summary>
    private const long ViewSequenceMargin = 100_000;

    /// <summary>Writes every room that has changed since it was last written.</summary>
    public async Task SaveChangedAsync()
    {
        if (store is null) return;

        foreach (var room in _rooms.Values.Where(r => r.Dirty))
        {
            string json;
            await room.Lock.WaitAsync();
            try
            {
                json = System.Text.Json.JsonSerializer.Serialize(Snapshot(room), StoreJson);
                room.Dirty = false;
            }
            finally { room.Lock.Release(); }

            try { await store.SaveAsync(room.Code, json); }
            catch (Exception ex)
            {
                room.Dirty = true;   // try again next time round
                log.LogError(ex, "Room {Code} could not be saved", room.Code);
            }
        }
    }

    private async Task CloseAsync(string code)
    {
        _rooms.TryRemove(code, out _);
        if (store is null) return;
        try { await store.DeleteAsync(code); }
        catch (Exception ex) { log.LogError(ex, "Room {Code} closed but its save could not be removed", code); }
    }

    /// <summary>
    /// Opens again every room the store holds, as it was: the same code, the same seats
    /// and tokens, the same game. Nobody is connected — each person comes back by the
    /// token their device kept, exactly as after a dropped connection. Run it before the
    /// host takes connections, or a device that asks first is told its table has closed
    /// and forgets it. Returns how many rooms came back.
    /// </summary>
    public async Task<int> RestoreAsync()
    {
        if (store is null) return 0;

        int restored = 0;
        foreach (var (code, json) in await store.LoadAllAsync())
        {
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<SavedRoom>(json, StoreJson)
                    ?? throw new InvalidDataException("empty");

                if (DateTime.UtcNow - saved.LastActivity > IdleLimit)
                {
                    await store.DeleteAsync(code);
                    continue;
                }

                var room = await RebuildAsync(saved);
                if (room is null)
                {
                    log.LogWarning("Room {Code} is for {Game}, which this host no longer has", code, saved.GameId);
                    continue;
                }

                room.Dirty = false;
                _rooms[room.Code] = room;
                restored++;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Room {Code} could not be restored", code);
            }
        }

        // A table that was playing its own turns when the host stopped carries on.
        foreach (var room in _rooms.Values.Where(r => r.State is not null))
            _ = RunTableAsync(room);

        if (restored > 0) log.LogInformation("Restored {Count} room(s)", restored);
        return restored;
    }

    private static SavedRoom Snapshot(Room room)
    {
        var saved = new SavedRoom
        {
            Code               = room.Code,
            GameId             = room.Definition.Id,
            Configuration      = room.Configuration,
            PlayerCount        = room.PlayerCount,
            Rules              = [.. room.Rules],
            Ballots            = room.Ballots.ToDictionary(b => b.Key, b => b.Value.ToList()),
            Forced             = new(room.Forced),
            DropTimeoutSeconds = (int)room.DropTimeout.TotalSeconds,
            Difficulty         = room.Difficulty,
            Seats              = room.Seats.Select(s => new SavedSeat
            {
                Id = s.Id, Name = s.Name, Token = s.Token, IsComputer = s.IsComputer, StandIn = s.StandIn, AccountId = s.AccountId,
            }).ToList(),
            LastActivity = room.LastActivity,
            Version      = room.Version,
            ViewSequence = room.ViewSequence,
            LastStatus   = room.LastStatus,
        };

        if (room.State is { } state)
        {
            saved.Game        = GameStateSerializer.Snapshot(state, room.PlayerCount, room.Rules);
            saved.PlayerNames = state.Players.Select(p => p.Name).ToList();

            // Only the lines still on show: the log, and what the metadata holds (the
            // status among it). Everything else was said and has gone.
            var shown = state.GameLog.Concat(state.Metadata.Values).ToHashSet();
            foreach (var line in shown)
            {
                if (state.TextVariants.TryGetValue(line, out var v))
                    saved.Wordings[line] = new SavedWording { Neutral = v.Neutral, ByViewer = new(v.ByViewer) };
                if (state.TextSubjects.TryGetValue(line, out var about))
                    saved.Subjects[line] = about;
            }
        }

        return saved;
    }

    private async Task<Room?> RebuildAsync(SavedRoom saved)
    {
        var definition = await loader.LoadAsync(saved.GameId);
        if (definition is null) return null;

        var room = new Room
        {
            Code          = saved.Code,
            Definition    = definition,
            Configuration = saved.Configuration,
            PlayerCount   = saved.PlayerCount,
            Rules         = saved.Rules,
            Offered       = GameConfiguration.Resolve(definition, saved.PlayerCount, saved.Configuration).HouseRules,
            Seats         = saved.Seats.Select(s => new RoomSeat
            {
                Id = s.Id, Name = s.Name, Token = s.Token, IsComputer = s.IsComputer, StandIn = s.StandIn, AccountId = s.AccountId,
                // Away since the host came back: the drop timeout runs from now.
                DisconnectedAt = s.Token is not null && !s.IsComputer ? DateTime.UtcNow : null,
            }).ToList(),
            DropTimeout   = TimeSpan.FromSeconds(saved.DropTimeoutSeconds),
            Difficulty    = Difficulty.Of(saved.Difficulty),
            LastActivity  = saved.LastActivity,
            Version       = saved.Version,
            ViewSequence  = saved.ViewSequence + ViewSequenceMargin,
            LastStatus    = saved.LastStatus,
        };
        foreach (var (seat, ballot) in saved.Ballots) room.Ballots[seat] = [.. ballot];
        foreach (var (rule, forced) in saved.Forced) room.Forced[rule] = forced;

        if (saved.Game is { } game)
        {
            var state = new GameState
            {
                GameId            = definition.Id,
                Definition        = definition,
                ConfigurationName = saved.Configuration,
                Rng               = new SeededRandomSource((ulong)RandomNumberGenerator.GetInt32(int.MaxValue) << 16
                                                           ^ (ulong)RandomNumberGenerator.GetInt32(int.MaxValue)),
                // Everyone keeps the name they had, the computer's seats included.
                SeatNames = saved.PlayerNames.Take(saved.PlayerCount).Select(n => (string?)n).ToList(),
            };
            var logic = LogicRegistry.Create(definition);
            GameStateSerializer.Restore(state, logic, game, saved.PlayerCount, saved.Rules);

            foreach (var (line, wording) in saved.Wordings)
            {
                var variant = new TextVariant { Neutral = wording.Neutral };
                foreach (var (viewer, text) in wording.ByViewer) variant.ByViewer[viewer] = text;
                state.TextVariants[line] = variant;
            }
            foreach (var (line, about) in saved.Subjects) state.TextSubjects[line] = about;

            foreach (var seat in room.Seats)
            {
                if (seat.IsComputer)
                    state.PlayerAgents[seat.Id] = ComputerPlayers.For(state, seat.Id);
                else
                    state.PlayerAgents.Remove(seat.Id);
            }

            room.State = state;
            room.Logic = logic;
        }

        return room;
    }

    // ── House rules ───────────────────────────────────────────────────────────

    /// <summary>One person's yes or no on one house rule, before the deal.</summary>
    public async Task SetBallotAsync(string code, string token, string ruleId, bool yes)
    {
        var room = Find(code) ?? throw new TableRefusal("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new TableRefusal("The rules were settled at the deal.");
            var seat = room.SeatByToken(token) ?? throw new TableRefusal("That seat is no longer yours.");
            if (room.Offered.All(r => r.Id != ruleId)) throw new TableRefusal("That is not a rule of this game.");

            var ballot = room.Ballots.TryGetValue(seat.Id, out var b) ? b : room.Ballots[seat.Id] = [];
            if (yes) ballot.Add(ruleId); else ballot.Remove(ruleId);
            room.LastActivity = DateTime.UtcNow;
            room.Dirty = true;
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
        var room = Find(code) ?? throw new TableRefusal("That table has closed.");

        await room.Lock.WaitAsync();
        try
        {
            if (room.State is not null) throw new TableRefusal("The rules were settled at the deal.");
            if (room.SeatByToken(token)?.Id != room.HostSeatId) throw new TableRefusal("Only the host can overrule the vote.");
            if (!room.Definition.HouseRuleVote.HostOverride) throw new TableRefusal($"In {room.Definition.Name} the vote decides; the host cannot overrule it.");
            if (room.Offered.All(r => r.Id != ruleId)) throw new TableRefusal("That is not a rule of this game.");

            if (forced is { } f) room.Forced[ruleId] = f;
            else                 room.Forced.Remove(ruleId);
            room.Dirty = true;
        }
        finally { room.Lock.Release(); }

        await SendRoomAsync(room);
    }

    /// <summary>
    /// The host's clock: once a second, asks about anyone who has dropped out past the
    /// timeout; every ten minutes, closes rooms nobody has touched in <see cref="IdleLimit"/>.
    /// Run it for as long as the host is up — the server does so as a background service.
    /// </summary>
    public async Task RunHousekeepingAsync(CancellationToken stoppingToken)
    {
        var lastSweep = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException)
            {
                // Shutting down: whatever changed in the last second goes to the store
                // too, so a restart picks up exactly where the table was.
                await SaveChangedAsync();
                return;
            }

            try { await CheckDropsAsync(DateTime.UtcNow); }
            catch (Exception ex) { log.LogError(ex, "Checking for dropped players failed"); }

            await SaveChangedAsync();

            if (DateTime.UtcNow - lastSweep < TimeSpan.FromMinutes(10)) continue;
            lastSweep = DateTime.UtcNow;

            foreach (var (code, room) in _rooms)
                if (DateTime.UtcNow - room.LastActivity > IdleLimit)
                    await CloseAsync(code);
        }
    }
}
