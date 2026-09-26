using Cards.Engine;
using Cards.Engine.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Cards.Server;

/// <summary>
/// The door every client comes in by — browser and phone alike. It only passes things
/// through to <see cref="RoomService"/>; a refusal comes back to the caller as the
/// exception from its own call, in words it can show.
/// </summary>
public sealed class TableHub(RoomService rooms) : Hub
{
    public Task<SeatTicket> CreateRoom(string gameId, int playerCount, List<string> rules, string? configuration, string name, int dropTimeoutSeconds)
        => rooms.CreateAsync(Context.ConnectionId, gameId, playerCount, rules ?? [], configuration, name, dropTimeoutSeconds);

    public Task<SeatTicket> JoinRoom(string code, string name)
        => rooms.JoinAsync(Context.ConnectionId, code, name);

    public Task<SeatTicket> Rejoin(string code, string token)
        => rooms.RejoinAsync(Context.ConnectionId, code, token);

    public Task StartGame(string code, string token)
        => rooms.StartAsync(code, token);

    public Task Act(string code, string token, GameAction action)
        => rooms.ActAsync(code, token, action);

    public Task LeaveRoom(string code, string token)
        => rooms.LeaveAsync(code, token);

    public Task Vote(string code, string token, bool letComputerPlay)
        => rooms.VoteAsync(code, token, letComputerPlay);

    public Task SetBallot(string code, string token, string ruleId, bool yes)
        => rooms.SetBallotAsync(code, token, ruleId, yes);

    public Task ForceRule(string code, string token, string ruleId, bool? forced)
        => rooms.ForceRuleAsync(code, token, ruleId, forced);

    public override Task OnDisconnectedAsync(Exception? exception)
        => rooms.DisconnectedAsync(Context.ConnectionId);
}
