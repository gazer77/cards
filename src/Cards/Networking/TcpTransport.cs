using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Cards.Engine.Multiplayer;

namespace Cards.Networking;

/// <summary>
/// LAN TCP transport.  Each peer is identified by its remote IP:port string.
/// Messages are length-prefixed (4-byte big-endian int followed by payload bytes).
/// </summary>
public sealed class TcpTransport : IMultiplayerTransport
{
    private TcpListener?   _listener;
    private TcpClient?     _serverConnection;  // client role only
    private readonly ConcurrentDictionary<string, TcpPeer> _peers = new();
    private readonly string _endpointId;
    private bool _isServer;

    public TcpTransport()
    {
        // EndpointId is determined at listen/connect time; use a placeholder until then.
        _endpointId = Guid.NewGuid().ToString("N")[..8];
    }

    public string EndpointId => _endpointId;

    public event Action<string, byte[]>? MessageReceived;
    public event Action<string>?        PeerDisconnected;
    public event Action<string>?        PeerConnected;

    // ── IMultiplayerTransport ─────────────────────────────────────────────────

    public async Task<string> ListenAsync(CancellationToken ct = default)
    {
        _isServer = true;
        _listener = new TcpListener(IPAddress.Any, 0);  // OS picks a free port
        _listener.Start();

        var localEp = (IPEndPoint)_listener.LocalEndpoint;
        _ = AcceptLoopAsync(ct);
        return $"{GetLocalIp()}:{localEp.Port}";
    }

    public async Task ConnectAsync(string address, CancellationToken ct = default)
    {
        var parts = address.Split(':');
        string host = parts[0];
        int    port = int.Parse(parts[1]);

        _serverConnection = new TcpClient();
        await _serverConnection.ConnectAsync(host, port, ct);

        var peer = new TcpPeer(address, _serverConnection);
        _peers[address] = peer;
        _ = ReadLoopAsync(peer, ct);
    }

    public async Task SendAsync(string toEndpointId, byte[] payload, CancellationToken ct = default)
    {
        if (_peers.TryGetValue(toEndpointId, out var peer))
            await peer.SendAsync(payload, ct);
    }

    public async Task BroadcastAsync(byte[] payload, CancellationToken ct = default)
    {
        foreach (var peer in _peers.Values)
            await peer.SendAsync(payload, ct);
    }

    public Task DisconnectAsync()
    {
        _listener?.Stop();
        _serverConnection?.Close();
        foreach (var peer in _peers.Values) peer.Close();
        _peers.Clear();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    // ── Accept loop (server) ──────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }

            var ep = ((IPEndPoint)client.Client.RemoteEndPoint!).ToString();
            var peer = new TcpPeer(ep, client);
            _peers[ep] = peer;
            PeerConnected?.Invoke(ep);
            _ = ReadLoopAsync(peer, ct);
        }
    }

    // ── Read loop (per peer) ──────────────────────────────────────────────────

    private async Task ReadLoopAsync(TcpPeer peer, CancellationToken ct)
    {
        try
        {
            var stream = peer.Stream;
            var lenBuf = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(stream, lenBuf, 4, ct);
                int len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
                if (len is <= 0 or > 1_048_576) break; // sanity: 0 < len ≤ 1 MB

                var payload = new byte[len];
                await ReadExactAsync(stream, payload, len, ct);
                MessageReceived?.Invoke(peer.EndpointId, payload);
            }
        }
        catch { /* connection closed */ }
        finally
        {
            _peers.TryRemove(peer.EndpointId, out _);
            peer.Close();
            PeerDisconnected?.Invoke(peer.EndpointId);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    // ── Local IP helper ───────────────────────────────────────────────────────

    private static string GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }
}

// ── Per-peer helper ───────────────────────────────────────────────────────────

internal sealed class TcpPeer
{
    private readonly TcpClient _client;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string     EndpointId { get; }
    public NetworkStream Stream  => _client.GetStream();

    public TcpPeer(string endpointId, TcpClient client)
    {
        EndpointId = endpointId;
        _client    = client;
    }

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var lenBuf = BitConverter.GetBytes(
                System.Net.IPAddress.HostToNetworkOrder(payload.Length));
            await Stream.WriteAsync(lenBuf, ct);
            await Stream.WriteAsync(payload, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Close() => _client.Close();
}
