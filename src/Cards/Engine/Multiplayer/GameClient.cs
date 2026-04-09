using System.Text.Json;
using Cards.Engine;
using Cards.Services;

namespace Cards.Engine.Multiplayer;

/// <summary>
/// Client side of a multiplayer session.  Sends actions to the server and applies
/// ActionApplied messages from the server to keep local state in sync.
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IMultiplayerTransport _transport;
    private readonly IGameLogic            _logic;

    // ── State ─────────────────────────────────────────────────────────────────

    private GameState?   _state;
    private long         _lastSequence;
    private bool         _connected;
    private long         _heartbeatSequence;
    private CancellationTokenSource? _cts;

    // ── Config ────────────────────────────────────────────────────────────────

    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);

    // ── Identification ────────────────────────────────────────────────────────

    public string  LocalPlayerId   { get; private set; } = string.Empty;
    public string  LocalPlayerName { get; private set; } = string.Empty;
    public string? HostEndpointId  { get; private set; }
    public bool    IsConnected     => _connected;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Server confirmed an action; local state has been updated.</summary>
    public event Action<ActionAppliedMsg>? ActionApplied;

    /// <summary>Server sent a full state-sync (e.g. on reconnect or late join).</summary>
    public event Action<GameState>? StateSynced;

    /// <summary>A player joined or left.</summary>
    public event Action<string, bool>? PlayerConnectionChanged;   // (playerId, connected)

    /// <summary>Host role was transferred to a different player.</summary>
    public event Action<string>? HostTransferred;   // newHostEndpointId

    /// <summary>Connection to the server was lost.</summary>
    public event Action<string>? Disconnected;   // reason

    // ── Constructor ───────────────────────────────────────────────────────────

    public GameClient(IMultiplayerTransport transport, IGameLogic logic)
    {
        _transport = transport;
        _logic     = logic;

        _transport.MessageReceived  += OnMessageReceived;
        _transport.PeerDisconnected += OnServerDisconnected;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Connect to a server and announce this player.</summary>
    public async Task<bool> JoinAsync(
        string address,
        string playerId,
        string playerName,
        CancellationToken ct = default)
    {
        LocalPlayerId   = playerId;
        LocalPlayerName = playerName;

        await _transport.ConnectAsync(address, ct);

        var joinMsg = NetworkEnvelope.Encode(MessageType.Join, _transport.EndpointId,
            new JoinMsg { PlayerId = playerId, PlayerName = playerName });
        await _transport.SendAsync(address, joinMsg, ct);   // address == server endpoint id for TCP

        // Wait for JoinAck (simple poll; real impl would use TaskCompletionSource)
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !_connected)
            await Task.Delay(50, ct);

        if (_connected)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = HeartbeatLoopAsync(_cts.Token);
        }

        return _connected;
    }

    /// <summary>
    /// Send an action to the server.  The action will only be applied locally
    /// after the server broadcasts ActionApplied.
    /// </summary>
    public async Task SendActionAsync(GameAction action, CancellationToken ct = default)
    {
        if (!_connected) return;

        var msg = NetworkEnvelope.Encode(MessageType.Action, _transport.EndpointId,
            new ActionMsg
            {
                ActionType = action.Type,
                PlayerId   = action.PlayerId,
                ZoneId     = action.ZoneId,
                CardId     = action.CardId,
                Label      = action.Label,
                Sequence   = _lastSequence + 1,
            });
        await _transport.SendAsync(HostEndpointId!, msg, ct);
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        _connected = false;
        await _transport.DisconnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _transport.MessageReceived  -= OnMessageReceived;
        _transport.PeerDisconnected -= OnServerDisconnected;
        await _transport.DisposeAsync();
    }

    // ── Transport callbacks ───────────────────────────────────────────────────

    private void OnServerDisconnected(string endpointId)
    {
        _connected = false;
        Disconnected?.Invoke("server disconnected");
    }

    private void OnMessageReceived(string fromEndpointId, byte[] payload)
    {
        try
        {
            var env = NetworkEnvelope.Decode(payload);
            HandleMessage(fromEndpointId, env);
        }
        catch { /* ignore malformed */ }
    }

    private void HandleMessage(string fromEndpointId, NetworkEnvelope env)
    {
        switch (env.Type)
        {
            case MessageType.JoinAck:
                HandleJoinAck(env.DecodePayload<JoinAckMsg>());
                break;

            case MessageType.ActionApplied:
                HandleActionApplied(env.DecodePayload<ActionAppliedMsg>());
                break;

            case MessageType.StateSync:
                HandleStateSync(env.DecodePayload<StateSyncMsg>());
                break;

            case MessageType.HostTransfer:
                HandleHostTransfer(env.DecodePayload<HostTransferMsg>());
                break;

            case MessageType.PlayerDisconnected:
                HandlePlayerDisconnected(env.DecodePayload<PlayerDisconnectedMsg>());
                break;

            case MessageType.HeartbeatAck:
                // Heartbeat acknowledged — no action needed
                break;
        }
    }

    // ── Message handlers ──────────────────────────────────────────────────────

    private void HandleJoinAck(JoinAckMsg msg)
    {
        if (!msg.Accepted) return;
        _connected    = true;
        HostEndpointId = msg.HostId;
        PlayerConnectionChanged?.Invoke(msg.PlayerId, true);
    }

    private void HandleActionApplied(ActionAppliedMsg msg)
    {
        if (_state is null) return;

        // Skip actions we've already seen (in case of duplicate delivery)
        if (msg.Sequence <= _lastSequence) return;
        _lastSequence = msg.Sequence;

        var action = new GameAction(
            msg.ActionType,
            PlayerId: msg.PlayerId,
            ZoneId:   msg.ZoneId,
            CardId:   msg.CardId,
            Label:    msg.Label);

        _logic.Apply(_state, action);
        ActionApplied?.Invoke(msg);
    }

    private void HandleStateSync(StateSyncMsg msg)
    {
        if (_state is null) return;

        try
        {
            var opts  = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var saved = JsonSerializer.Deserialize<SavedGameState>(msg.SerializedState, opts);
            if (saved is null) return;

            // Rebuild runtime state from the snapshot
            _state.Zones.Clear();
            foreach (var sz in saved.Zones)
            {
                var zone = new Zone(sz.Id, sz.Type, sz.OwnerId, sz.Visibility);
                foreach (var sc in sz.Cards)
                    zone.Add(new Card((Suit)sc.Suit, (Rank)sc.Rank, sc.IsFaceUp) { IsWild = sc.IsWild });
                _state.Zones[sz.Id] = zone;
            }

            _state.CurrentPhaseId     = saved.PhaseId;
            _state.CurrentPlayerIndex = saved.PlayerIndex;
            _state.RoundNumber        = saved.RoundNumber;
            _state.DealerId           = saved.DealerId;

            _state.Scores.Clear();
            foreach (var (k, v) in saved.Scores)   _state.Scores[k]   = v;

            _state.Metadata.Clear();
            foreach (var (k, v) in saved.Metadata) _state.Metadata[k] = v;

            StateSynced?.Invoke(_state);
        }
        catch { /* ignore malformed state sync */ }
    }

    private void HandleHostTransfer(HostTransferMsg msg)
    {
        HostEndpointId = msg.NewHostId;
        HostTransferred?.Invoke(msg.NewHostId);
    }

    private void HandlePlayerDisconnected(PlayerDisconnectedMsg msg)
    {
        PlayerConnectionChanged?.Invoke(msg.PlayerId, false);
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _connected)
        {
            await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false);

            if (HostEndpointId is null) continue;

            _heartbeatSequence++;
            var hb = NetworkEnvelope.Encode(MessageType.Heartbeat, _transport.EndpointId,
                new HeartbeatMsg { Sequence = _heartbeatSequence });

            try { await _transport.SendAsync(HostEndpointId, hb, ct); }
            catch { /* ignore send errors — server will time us out if it cares */ }
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    /// <summary>Attach the local GameState so ActionApplied can update it.</summary>
    public void AttachState(GameState state) => _state = state;
}
