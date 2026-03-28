namespace Cards.Engine.Multiplayer;

/// <summary>
/// Abstraction over the physical delivery layer (TCP/LAN, in-process, relay, etc.).
/// A transport moves raw byte messages between one sender and N receivers.
/// </summary>
public interface IMultiplayerTransport : IAsyncDisposable
{
    /// <summary>Unique id assigned to this endpoint by the transport layer.</summary>
    string EndpointId { get; }

    /// <summary>Raised on the calling thread when a message arrives.</summary>
    event Action<string, byte[]> MessageReceived;   // (fromEndpointId, payload)

    /// <summary>Raised when a remote endpoint disconnects or times out.</summary>
    event Action<string> PeerDisconnected;           // (endpointId)

    /// <summary>Raised when a new peer connects (server only, ignored on client).</summary>
    event Action<string> PeerConnected;              // (endpointId)

    /// <summary>Send a message to a specific endpoint.</summary>
    Task SendAsync(string toEndpointId, byte[] payload, CancellationToken ct = default);

    /// <summary>Broadcast a message to all connected peers.</summary>
    Task BroadcastAsync(byte[] payload, CancellationToken ct = default);

    /// <summary>
    /// Start listening for incoming connections (server role).
    /// Returns the address string used to reach this endpoint (e.g. "192.168.1.5:7777").
    /// </summary>
    Task<string> ListenAsync(CancellationToken ct = default);

    /// <summary>Connect to a server endpoint (client role).</summary>
    Task ConnectAsync(string address, CancellationToken ct = default);

    /// <summary>Gracefully close all connections.</summary>
    Task DisconnectAsync();
}
