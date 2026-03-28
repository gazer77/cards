using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cards.Engine.Multiplayer;

// ── Message type identifiers ──────────────────────────────────────────────────

public enum MessageType
{
    Join,
    JoinAck,
    Action,
    ActionApplied,
    Heartbeat,
    HeartbeatAck,
    StateSync,
    HostTransfer,
    PlayerDisconnected,
    Error,
}

// ── Base envelope ─────────────────────────────────────────────────────────────

public sealed class NetworkEnvelope
{
    public MessageType Type      { get; set; }
    public string      SenderId  { get; set; } = string.Empty;
    public long        Timestamp { get; set; }   // UTC ticks
    public string      Payload   { get; set; } = string.Empty;  // JSON-encoded body

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<T>(MessageType type, string senderId, T body)
    {
        var env = new NetworkEnvelope
        {
            Type      = type,
            SenderId  = senderId,
            Timestamp = DateTime.UtcNow.Ticks,
            Payload   = JsonSerializer.Serialize(body, _opts),
        };
        return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env, _opts));
    }

    public static NetworkEnvelope Decode(byte[] raw)
    {
        var json = System.Text.Encoding.UTF8.GetString(raw);
        return JsonSerializer.Deserialize<NetworkEnvelope>(json, _opts)
               ?? throw new InvalidDataException("Null envelope");
    }

    public T DecodePayload<T>()
        => JsonSerializer.Deserialize<T>(Payload, _opts)
           ?? throw new InvalidDataException($"Null payload for {Type}");
}

// ── Message body types ────────────────────────────────────────────────────────

public sealed class JoinMsg
{
    public string PlayerId   { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
}

public sealed class JoinAckMsg
{
    public string              PlayerId    { get; set; } = string.Empty;
    public bool                Accepted    { get; set; }
    public string?             RejectReason { get; set; }
    public List<PlayerInfo>    Players     { get; set; } = [];
    public string              HostId      { get; set; } = string.Empty;
}

public sealed class PlayerInfo
{
    public string PlayerId   { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public bool   IsHost     { get; set; }
}

public sealed class ActionMsg
{
    public string ActionType { get; set; } = string.Empty;
    public string? PlayerId  { get; set; }
    public string? ZoneId    { get; set; }
    public string? CardId    { get; set; }
    public string? Label     { get; set; }
    public long    Sequence  { get; set; }
}

public sealed class ActionAppliedMsg
{
    public long    Sequence  { get; set; }
    public string  ActionType { get; set; } = string.Empty;
    public string? PlayerId  { get; set; }
    public string? ZoneId    { get; set; }
    public string? CardId    { get; set; }
    public string? Label     { get; set; }
}

public sealed class HeartbeatMsg
{
    public long Sequence { get; set; }
}

public sealed class HeartbeatAckMsg
{
    public long Sequence { get; set; }
}

public sealed class StateSyncMsg
{
    public string SerializedState { get; set; } = string.Empty;  // JSON SavedGameState
    public int    PlayerCount     { get; set; }
    public List<string> EnabledRules { get; set; } = [];
}

public sealed class HostTransferMsg
{
    public string NewHostId { get; set; } = string.Empty;
}

public sealed class PlayerDisconnectedMsg
{
    public string PlayerId { get; set; } = string.Empty;
    public string Reason   { get; set; } = string.Empty;
}

public sealed class ErrorMsg
{
    public string Code    { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
