using System.Text.Json;
using Cards.Engine;
using Cards.Services;

namespace Cards.Engine.Multiplayer;

/// <summary>
/// Authoritative game server.  Transport-agnostic — supply any IMultiplayerTransport.
/// Lifecycle: Create → StartAsync → (game runs) → StopAsync.
/// </summary>
public sealed class GameServer : IAsyncDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IMultiplayerTransport _transport;
    private readonly IGameLogic            _logic;
    private readonly GameSaveService       _saves;

    // ── Game state ────────────────────────────────────────────────────────────

    private GameState?  _state;
    private int         _playerCount;
    private IReadOnlyList<string> _enabledRules = [];
    private long        _actionSequence;

    // ── Player registry ───────────────────────────────────────────────────────

    private readonly Dictionary<string, ServerPlayer> _players = [];   // endpointId → info
    private string   _hostEndpointId = string.Empty;

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private readonly TimeSpan _heartbeatInterval  = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _heartbeatTimeout   = TimeSpan.FromSeconds(15);

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised on the server when a game action has been validated and applied.</summary>
    public event Action<ActionAppliedMsg>? ActionApplied;

    /// <summary>Raised when a player joins or leaves.</summary>
    public event Action<string, bool>? PlayerConnectionChanged;   // (playerId, connected)

    /// <summary>Raised when the host is transferred to a new endpoint.</summary>
    public event Action<string>? HostTransferred;   // new hostEndpointId

    // ── Constructor ───────────────────────────────────────────────────────────

    public GameServer(IMultiplayerTransport transport, IGameLogic logic, GameSaveService saves)
    {
        _transport = transport;
        _saves     = saves;
        _logic     = logic;

        _transport.MessageReceived  += OnMessageReceived;
        _transport.PeerConnected    += OnPeerConnected;
        _transport.PeerDisconnected += OnPeerDisconnected;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public IReadOnlyDictionary<string, ServerPlayer> Players => _players;
    public string HostEndpointId => _hostEndpointId;

    /// <summary>
    /// Start the server.  Returns the address string clients should connect to.
    /// hostEndpointId identifies which connected peer is the designated host
    /// (pass the local endpoint id when the host is also running on this machine).
    /// </summary>
    public async Task<string> StartAsync(
        string hostEndpointId,
        int playerCount,
        IReadOnlyList<string> enabledRules,
        CancellationToken ct = default)
    {
        _hostEndpointId = hostEndpointId;
        _playerCount    = playerCount;
        _enabledRules   = enabledRules;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var address = await _transport.ListenAsync(_cts.Token);

        _ = HeartbeatLoopAsync(_cts.Token);

        return address;
    }

    /// <summary>
    /// Attach a pre-built GameState (used when the host already has a game running
    /// and is converting it to a multiplayer session, or restoring a saved game).
    /// </summary>
    public void AttachState(GameState state)
    {
        _state = state;
    }

    /// <summary>
    /// Manually transfer host role to another connected player.
    /// </summary>
    public async Task TransferHostAsync(string newHostEndpointId)
    {
        if (!_players.ContainsKey(newHostEndpointId))
            throw new InvalidOperationException($"Unknown endpoint: {newHostEndpointId}");

        _hostEndpointId = newHostEndpointId;
        foreach (var ep in _players.Keys)
            _players[ep].IsHost = ep == newHostEndpointId;

        var msg = NetworkEnvelope.Encode(MessageType.HostTransfer, _transport.EndpointId,
            new HostTransferMsg { NewHostId = newHostEndpointId });
        await _transport.BroadcastAsync(msg);

        HostTransferred?.Invoke(newHostEndpointId);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await _transport.DisconnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _transport.MessageReceived  -= OnMessageReceived;
        _transport.PeerConnected    -= OnPeerConnected;
        _transport.PeerDisconnected -= OnPeerDisconnected;
        await _transport.DisposeAsync();
    }

    // ── Transport callbacks ───────────────────────────────────────────────────

    private void OnPeerConnected(string endpointId)
    {
        _players.TryAdd(endpointId, new ServerPlayer { EndpointId = endpointId });
    }

    private void OnPeerDisconnected(string endpointId)
    {
        if (!_players.TryGetValue(endpointId, out var player)) return;

        _players.Remove(endpointId);

        var playerId = player.PlayerId ?? endpointId;
        PlayerConnectionChanged?.Invoke(playerId, false);

        _ = BroadcastPlayerDisconnectedAsync(playerId, "connection lost");
    }

    private void OnMessageReceived(string fromEndpointId, byte[] payload)
    {
        try
        {
            var envelope = NetworkEnvelope.Decode(payload);
            _ = HandleMessageAsync(fromEndpointId, envelope);
        }
        catch { /* ignore malformed messages */ }
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private async Task HandleMessageAsync(string fromEndpointId, NetworkEnvelope env)
    {
        switch (env.Type)
        {
            case MessageType.Join:
                await HandleJoinAsync(fromEndpointId, env.DecodePayload<JoinMsg>());
                break;

            case MessageType.Action:
                await HandleActionAsync(fromEndpointId, env.DecodePayload<ActionMsg>());
                break;

            case MessageType.Heartbeat:
                await HandleHeartbeatAsync(fromEndpointId, env.DecodePayload<HeartbeatMsg>());
                break;
        }
    }

    private async Task HandleJoinAsync(string fromEndpointId, JoinMsg msg)
    {
        // Reject if game is full
        if (_players.Values.Count(p => p.PlayerId is not null) >= _playerCount)
        {
            var reject = NetworkEnvelope.Encode(MessageType.JoinAck, _transport.EndpointId,
                new JoinAckMsg { Accepted = false, RejectReason = "Game is full" });
            await _transport.SendAsync(fromEndpointId, reject);
            return;
        }

        // Register player
        var sp = _players.GetValueOrDefault(fromEndpointId) ?? new ServerPlayer { EndpointId = fromEndpointId };
        sp.PlayerId   = msg.PlayerId;
        sp.PlayerName = msg.PlayerName;
        sp.IsHost     = fromEndpointId == _hostEndpointId;
        sp.LastSeen   = DateTime.UtcNow;
        _players[fromEndpointId] = sp;

        var playerList = _players.Values
            .Where(p => p.PlayerId is not null)
            .Select(p => new PlayerInfo
            {
                PlayerId   = p.PlayerId!,
                PlayerName = p.PlayerName ?? p.PlayerId!,
                IsHost     = p.IsHost,
            }).ToList();

        var ack = NetworkEnvelope.Encode(MessageType.JoinAck, _transport.EndpointId,
            new JoinAckMsg
            {
                PlayerId = msg.PlayerId,
                Accepted = true,
                Players  = playerList,
                HostId   = _hostEndpointId,
            });
        await _transport.SendAsync(fromEndpointId, ack);

        PlayerConnectionChanged?.Invoke(msg.PlayerId, true);

        // If a game is already running, send state sync
        if (_state is not null)
            await SendStateSyncAsync(fromEndpointId);
    }

    private async Task HandleActionAsync(string fromEndpointId, ActionMsg msg)
    {
        if (_state is null) return;

        var action = new GameAction(
            msg.ActionType,
            PlayerId: msg.PlayerId,
            ZoneId:   msg.ZoneId,
            CardId:   msg.CardId,
            Label:    msg.Label);

        // Validate: only the current player may act (except auto-advance actions from server)
        var validActions = _logic.GetValidActions(_state);
        if (!validActions.Any(a => a.Type == action.Type))
        {
            var err = NetworkEnvelope.Encode(MessageType.Error, _transport.EndpointId,
                new ErrorMsg { Code = "INVALID_ACTION", Message = $"Action '{action.Type}' is not valid right now" });
            await _transport.SendAsync(fromEndpointId, err);
            return;
        }

        _logic.Apply(_state, action);
        _actionSequence++;

        var applied = new ActionAppliedMsg
        {
            Sequence   = _actionSequence,
            ActionType = action.Type,
            PlayerId   = action.PlayerId,
            ZoneId     = action.ZoneId,
            CardId     = action.CardId,
            Label      = action.Label,
        };

        var broadcast = NetworkEnvelope.Encode(MessageType.ActionApplied, _transport.EndpointId, applied);
        await _transport.BroadcastAsync(broadcast);

        ActionApplied?.Invoke(applied);
    }

    private async Task HandleHeartbeatAsync(string fromEndpointId, HeartbeatMsg msg)
    {
        if (_players.TryGetValue(fromEndpointId, out var p))
            p.LastSeen = DateTime.UtcNow;

        var ack = NetworkEnvelope.Encode(MessageType.HeartbeatAck, _transport.EndpointId,
            new HeartbeatAckMsg { Sequence = msg.Sequence });
        await _transport.SendAsync(fromEndpointId, ack);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SendStateSyncAsync(string toEndpointId)
    {
        if (_state is null) return;

        var saved = GameSaveService.Snapshot(_state, _playerCount, _enabledRules);
        var json  = JsonSerializer.Serialize(saved, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var msg = NetworkEnvelope.Encode(MessageType.StateSync, _transport.EndpointId,
            new StateSyncMsg
            {
                SerializedState = json,
                PlayerCount     = _playerCount,
                EnabledRules    = [.. _enabledRules],
            });
        await _transport.SendAsync(toEndpointId, msg);
    }

    private async Task BroadcastPlayerDisconnectedAsync(string playerId, string reason)
    {
        var msg = NetworkEnvelope.Encode(MessageType.PlayerDisconnected, _transport.EndpointId,
            new PlayerDisconnectedMsg { PlayerId = playerId, Reason = reason });
        await _transport.BroadcastAsync(msg);
    }

    // ── Heartbeat loop ────────────────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false);

            var now   = DateTime.UtcNow;
            var stale = _players.Values
                .Where(p => p.LastSeen.HasValue && now - p.LastSeen.Value > _heartbeatTimeout)
                .ToList();

            foreach (var p in stale)
            {
                _players.Remove(p.EndpointId);
                var pid = p.PlayerId ?? p.EndpointId;
                PlayerConnectionChanged?.Invoke(pid, false);
                _ = BroadcastPlayerDisconnectedAsync(pid, "heartbeat timeout");
            }
        }
    }
}

// ── Server-side player record ─────────────────────────────────────────────────

public sealed class ServerPlayer
{
    public string  EndpointId { get; set; } = string.Empty;
    public string? PlayerId   { get; set; }
    public string? PlayerName { get; set; }
    public bool    IsHost     { get; set; }
    public DateTime? LastSeen { get; set; }
}
