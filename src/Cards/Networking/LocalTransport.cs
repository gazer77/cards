using Cards.Engine.Multiplayer;

namespace Cards.Networking;

/// <summary>
/// In-process transport for same-device multiplayer (e.g. pass-and-play or local testing).
/// Two LocalTransport instances are wired together via a shared LocalBus.
/// </summary>
public sealed class LocalTransport : IMultiplayerTransport
{
    private readonly string _endpointId;
    private LocalBus? _bus;
    private bool _listening;

    public LocalTransport(string endpointId)
    {
        _endpointId = endpointId;
    }

    public string EndpointId => _endpointId;

    public event Action<string, byte[]>? MessageReceived;
    public event Action<string>?        PeerDisconnected;
    public event Action<string>?        PeerConnected;

    // ── IMultiplayerTransport ─────────────────────────────────────────────────

    public Task<string> ListenAsync(CancellationToken ct = default)
    {
        _listening = true;
        return Task.FromResult(_endpointId);
    }

    public Task ConnectAsync(string address, CancellationToken ct = default)
    {
        // address == the server's endpointId in local transport
        _bus = LocalBus.GetOrCreate(address);
        _bus.Register(this);
        _bus.Server.PeerConnected?.Invoke(_endpointId);
        return Task.CompletedTask;
    }

    public Task SendAsync(string toEndpointId, byte[] payload, CancellationToken ct = default)
    {
        var target = LocalBus.GetOrCreate(_listening ? _endpointId : toEndpointId)
                              .Find(toEndpointId);
        target?.Deliver(_endpointId, payload);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(byte[] payload, CancellationToken ct = default)
    {
        _bus?.Broadcast(_endpointId, payload);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _bus?.Unregister(this);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _ = DisconnectAsync();
        return ValueTask.CompletedTask;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    internal void Deliver(string fromId, byte[] payload)
        => MessageReceived?.Invoke(fromId, payload);

    internal void NotifyDisconnect(string fromId)
        => PeerDisconnected?.Invoke(fromId);
}

// ── Shared in-process bus ─────────────────────────────────────────────────────

public sealed class LocalBus
{
    private static readonly Dictionary<string, LocalBus> _buses = [];

    private readonly string _serverId;
    private readonly Dictionary<string, LocalTransport> _peers = [];

    public LocalTransport Server => _peers[_serverId];

    private LocalBus(string serverId) => _serverId = serverId;

    public static LocalBus GetOrCreate(string serverId)
    {
        lock (_buses)
        {
            if (!_buses.TryGetValue(serverId, out var bus))
                _buses[serverId] = bus = new LocalBus(serverId);
            return bus;
        }
    }

    public void Register(LocalTransport transport)
    {
        lock (_peers) _peers[transport.EndpointId] = transport;
    }

    public void Unregister(LocalTransport transport)
    {
        lock (_peers)
        {
            _peers.Remove(transport.EndpointId);
            foreach (var p in _peers.Values)
                p.NotifyDisconnect(transport.EndpointId);
        }
    }

    public LocalTransport? Find(string endpointId)
    {
        lock (_peers) return _peers.GetValueOrDefault(endpointId);
    }

    public void Broadcast(string fromId, byte[] payload)
    {
        List<LocalTransport> targets;
        lock (_peers) targets = _peers.Values.Where(p => p.EndpointId != fromId).ToList();
        foreach (var t in targets) t.Deliver(fromId, payload);
    }
}
