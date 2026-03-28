using Cards.Engine;
using Cards.Engine.Multiplayer;
using Cards.Networking;

namespace Cards.Services;

/// <summary>
/// High-level coordinator for a multiplayer session.
/// Handles host/join lifecycle, exposes the current server or client, and
/// provides a unified event surface consumed by the UI layer.
/// </summary>
public sealed class MultiplayerService : IAsyncDisposable
{
    // ── Active session ────────────────────────────────────────────────────────

    public GameServer? Server { get; private set; }
    public GameClient? Client { get; private set; }

    public bool IsHost     => Server is not null;
    public bool IsActive   => Server is not null || Client is not null;
    public string? RoomCode { get; private set; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>An action was validated by the server and applied to game state.</summary>
    public event Action<ActionAppliedMsg>? ActionApplied;

    /// <summary>The server sent a fresh state snapshot (late-join / reconnect).</summary>
    public event Action<GameState>? StateSynced;

    /// <summary>A player joined or disconnected.</summary>
    public event Action<string, bool>? PlayerConnectionChanged;

    /// <summary>Host role was transferred.</summary>
    public event Action<string>? HostTransferred;

    /// <summary>Lost connection to the server (client only).</summary>
    public event Action<string>? Disconnected;

    // ── Host ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new hosted game.  Returns the room code other players use to join.
    /// </summary>
    public async Task<string> HostAsync(
        string hostPlayerId,
        IGameLogic logic,
        GameSaveService saves,
        int playerCount,
        IReadOnlyList<string> enabledRules,
        GameState? existingState = null,
        bool useTcp = true,
        CancellationToken ct = default)
    {
        await DisposeAsync();   // clean up any previous session

        IMultiplayerTransport transport = useTcp
            ? new TcpTransport()
            : new LocalTransport(hostPlayerId);

        var server = new GameServer(transport, logic, saves);

        if (existingState is not null)
            server.AttachState(existingState);

        server.ActionApplied          += msg  => ActionApplied?.Invoke(msg);
        server.PlayerConnectionChanged += (id, c) => PlayerConnectionChanged?.Invoke(id, c);
        server.HostTransferred         += id  => HostTransferred?.Invoke(id);

        var address = await server.StartAsync(hostPlayerId, playerCount, enabledRules, ct);
        RoomCode = Engine.Multiplayer.RoomCode.Encode(address);
        Server   = server;
        return RoomCode;
    }

    // ── Client ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Join a hosted game using a room code.  Returns true if accepted.
    /// </summary>
    public async Task<bool> JoinAsync(
        string roomCode,
        string playerId,
        string playerName,
        IGameLogic logic,
        GameState state,
        bool useTcp = true,
        CancellationToken ct = default)
    {
        await DisposeAsync();

        var address = Engine.Multiplayer.RoomCode.Decode(roomCode);
        if (address is null) return false;

        IMultiplayerTransport transport = useTcp
            ? new TcpTransport()
            : new LocalTransport(playerId);

        var client = new GameClient(transport, logic);
        client.AttachState(state);

        client.ActionApplied          += msg  => ActionApplied?.Invoke(msg);
        client.StateSynced             += gs   => StateSynced?.Invoke(gs);
        client.PlayerConnectionChanged += (id, c) => PlayerConnectionChanged?.Invoke(id, c);
        client.HostTransferred         += id  => HostTransferred?.Invoke(id);
        client.Disconnected            += reason => Disconnected?.Invoke(reason);

        bool accepted = await client.JoinAsync(address, playerId, playerName, ct);
        if (accepted)
        {
            RoomCode = roomCode;
            Client   = client;
        }
        else
        {
            await client.DisposeAsync();
        }

        return accepted;
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Send a game action.  When hosting, applies directly to the server.
    /// When a client, sends to the server and waits for ActionApplied.
    /// </summary>
    public async Task SendActionAsync(GameAction action, CancellationToken ct = default)
    {
        if (Client is not null)
            await Client.SendActionAsync(action, ct);
        // Server-side actions (host acting as a player) are handled by
        // injecting them directly into the server's HandleActionAsync flow;
        // for now the host also goes through the client path when acting as a player.
    }

    /// <summary>Transfer the host role to another player (host only).</summary>
    public Task TransferHostAsync(string newHostEndpointId)
        => Server?.TransferHostAsync(newHostEndpointId) ?? Task.CompletedTask;

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
            Server = null;
        }
        if (Client is not null)
        {
            await Client.DisposeAsync();
            Client = null;
        }
        RoomCode = null;
    }
}
