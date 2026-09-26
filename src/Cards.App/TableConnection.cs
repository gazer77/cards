using System.Text.Json;
using Cards.Engine;
using Cards.Engine.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cards.App;

/// <summary>
/// This screen's line to a shared table: opening one, sitting down, starting, sending
/// moves, and hearing back what the table now looks like from this seat.
///
/// One per app, living across pages, so the lobby and the table are one connection.
/// The seat is held by a token remembered on the device, not by the connection: a phone
/// drops its connection whenever it sleeps and a browser whenever the page reloads, and
/// either way the player comes back to the chair they left.
/// </summary>
public sealed class TableConnection(Uri hubUrl, ISettingsStore store) : IAsyncDisposable
{
    private HubConnection? _hub;
    private readonly SemaphoreSlim _connecting = new(1, 1);

    public SeatTicket? Ticket { get; private set; }
    public RoomInfo? Room { get; private set; }
    public TableView? View { get; private set; }

    public event Action? RoomChanged;
    public event Action<TableView>? ViewChanged;

    /// <summary>A move the table would not take, in words to show the player.</summary>
    public event Action<string>? Refused;

    /// <summary>The connection dropped or came back; the page may want to say so.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    // ── Seating ───────────────────────────────────────────────────────────────

    public async Task<SeatTicket> CreateAsync(
        string gameId, int players, IReadOnlyList<string> rules, string? configuration, string name,
        int dropTimeoutSeconds = 60)
    {
        var hub = await HubAsync();
        Reset();
        Ticket = await hub.InvokeAsync<SeatTicket>(TableHubContract.CreateRoom,
            gameId, players, rules.ToList(), configuration, name, dropTimeoutSeconds);
        Remember(Ticket);
        return Ticket;
    }

    public async Task<SeatTicket> JoinAsync(string code, string name)
    {
        var hub = await HubAsync();
        Reset();
        Ticket = await hub.InvokeAsync<SeatTicket>(TableHubContract.JoinRoom, code.Trim().ToUpperInvariant(), name);
        Remember(Ticket);
        return Ticket;
    }

    /// <summary>
    /// Back to a seat this device holds at <paramref name="code"/>, if it holds one.
    /// False when it does not — the player has to join — or the seat is gone.
    /// </summary>
    public async Task<bool> ResumeAsync(string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (Ticket?.Code == code && IsConnected) return true;

        var saved = Recall(code);
        if (saved is null) return false;

        var hub = await HubAsync();
        try
        {
            Reset();
            Ticket = await hub.InvokeAsync<SeatTicket>(TableHubContract.Rejoin, saved.Code, saved.Token);
            return true;
        }
        catch (HubException)
        {
            Forget(code);
            Ticket = null;
            return false;
        }
    }

    public async Task StartAsync()
    {
        if (Ticket is not { } t) return;
        var hub = await HubAsync();
        await hub.InvokeAsync(TableHubContract.StartGame, t.Code, t.Token);
    }

    public async Task LeaveAsync()
    {
        if (Ticket is not { } t) return;
        Forget(t.Code);
        try
        {
            if (_hub is not null) await _hub.InvokeAsync(TableHubContract.LeaveRoom, t.Code, t.Token);
        }
        catch (Exception) { /* leaving a table that has already gone is still leaving */ }
        Reset();
    }

    /// <summary>This person's yes or no on a house rule, before the deal.</summary>
    public async Task SetBallotAsync(string ruleId, bool yes)
    {
        if (Ticket is not { } t) return;
        var hub = await HubAsync();
        await hub.InvokeAsync(TableHubContract.SetBallot, t.Code, t.Token, ruleId, yes);
    }

    /// <summary>The host settling a rule over the vote: in, out, or null to let the vote decide.</summary>
    public async Task ForceRuleAsync(string ruleId, bool? forced)
    {
        if (Ticket is not { } t) return;
        var hub = await HubAsync();
        await hub.InvokeAsync(TableHubContract.ForceRule, t.Code, t.Token, ruleId, forced);
    }

    /// <summary>Answers the open question about someone away: let the computer play for them?</summary>
    public async Task VoteAsync(bool letComputerPlay)
    {
        if (Ticket is not { } t) return;
        try
        {
            var hub = await HubAsync();
            await hub.InvokeAsync(TableHubContract.Vote, t.Code, t.Token, letComputerPlay);
        }
        catch (HubException ex) { Refused?.Invoke(Clean(ex.Message)); }
        catch (Exception)       { Refused?.Invoke("Lost the connection to the table."); }
    }

    /// <summary>Sends a move. A refusal is reported through <see cref="Refused"/>, not thrown.</summary>
    public async Task ActAsync(GameAction action)
    {
        if (Ticket is not { } t) return;
        try
        {
            var hub = await HubAsync();
            await hub.InvokeAsync(TableHubContract.Act, t.Code, t.Token, action);
        }
        catch (HubException ex) { Refused?.Invoke(Clean(ex.Message)); }
        catch (Exception)       { Refused?.Invoke("Lost the connection to the table."); }
    }

    // ── The line itself ───────────────────────────────────────────────────────

    private async Task<HubConnection> HubAsync()
    {
        await _connecting.WaitAsync();
        try
        {
            if (_hub is null)
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl(hubUrl)
                    .WithAutomaticReconnect()
                    .Build();

                _hub.On<RoomInfo>(TableHubContract.RoomChanged, room =>
                {
                    // The room can arrive before the call that joined it returns.
                    if (Ticket is not null && room.Code != Ticket.Code) return;
                    Room = room;
                    RoomChanged?.Invoke();
                });
                _hub.On<TableView>(TableHubContract.ViewChanged, view =>
                {
                    if (View is not null && view.Version <= View.Version) return;
                    View = view;
                    ViewChanged?.Invoke(view);
                });

                // Back after a drop: sit down again with the token, and the server sends
                // this seat's view as if nothing happened.
                _hub.Reconnected += async _ =>
                {
                    ConnectionChanged?.Invoke(true);
                    if (Ticket is { } t)
                    {
                        try { await _hub.InvokeAsync<SeatTicket>(TableHubContract.Rejoin, t.Code, t.Token); }
                        catch (HubException) { /* the table closed while we were away */ }
                    }
                };
                _hub.Reconnecting += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
                _hub.Closed       += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };
            }

            if (_hub.State == HubConnectionState.Disconnected)
                await _hub.StartAsync();

            return _hub;
        }
        finally { _connecting.Release(); }
    }

    private void Reset()
    {
        Ticket = null;
        Room   = null;
        View   = null;
    }

    /// <summary>The server's words, without the framework's preamble around them.</summary>
    private static string Clean(string message)
    {
        const string marker = "HubException: ";
        int at = message.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? message[(at + marker.Length)..] : message;
    }

    // ── Remembered seats ──────────────────────────────────────────────────────

    private const string SeatsKey = "shared_seats";

    private Dictionary<string, SeatTicket> Remembered()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SeatTicket>>(store.Get(SeatsKey, "{}"))
                ?? [];
        }
        catch (JsonException) { return []; }
    }

    private void Remember(SeatTicket ticket)
    {
        var seats = Remembered();
        seats[ticket.Code] = ticket;
        store.Set(SeatsKey, JsonSerializer.Serialize(seats));
    }

    private SeatTicket? Recall(string code) => Remembered().GetValueOrDefault(code);

    private void Forget(string code)
    {
        var seats = Remembered();
        if (seats.Remove(code)) store.Set(SeatsKey, JsonSerializer.Serialize(seats));
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}

/// <summary>What went wrong reaching a shared table, in words for the player.</summary>
public static class TableErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        HubException hub => hub.Message.Contains("HubException: ")
            ? hub.Message[(hub.Message.IndexOf("HubException: ", StringComparison.Ordinal) + 14)..]
            : hub.Message,
        HttpRequestException => "Could not reach the table server. Is it running?",
        _ => "Could not reach the table server: " + ex.Message,
    };
}
