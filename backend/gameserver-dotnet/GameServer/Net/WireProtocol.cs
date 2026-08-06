using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameServer.Net;

/// <summary>Message type constants matching the Go gateway wire protocol.</summary>
public enum MsgType : byte
{
    Auth = 1,
    AuthResp = 2,
    EnterWorld = 3,
    EnterWorldResp = 4,
    JoinToken = 5,
    JoinTokenResp = 6,
    Input = 7,
    Snapshot = 8,
    Disconnect = 9,
    /// <summary>Client -> gameserver: request a full keyframe on the next tick.</summary>
    Resync = 10
}

/// <summary>Wire envelope: 4-byte big-endian length prefix + JSON body.</summary>
public sealed class Envelope
{
    [JsonPropertyName("type")]
    public byte Type { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

// ── Inner message types (JSON field names match Go exactly) ──

public sealed class JoinTokenRequest
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public sealed class JoinTokenResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class InputMessage
{
    [JsonPropertyName("tick")]
    public ulong Tick { get; set; }

    [JsonPropertyName("move_x")]
    public float MoveX { get; set; }

    [JsonPropertyName("move_y")]
    public float MoveY { get; set; }

    [JsonPropertyName("attack_target_id")]
    public string? AttackTargetId { get; set; }
}

/// <summary>
/// World state update. Either a KEYFRAME (<see cref="Full"/> = true, <see cref="Entities"/>
/// is the complete AOI set) or a DELTA (only entities whose visible state changed since the
/// previous snapshot sent to this connection, plus <see cref="Removed"/> for entities that
/// left the AOI or the world).
/// New fields are omitted when default, so the JSON of a plain full snapshot is byte-identical
/// to the pre-delta protocol.
/// </summary>
public sealed class SnapshotMessage
{
    [JsonPropertyName("tick")]
    public ulong Tick { get; set; }

    /// <summary>
    /// Highest client input tick accepted for the receiving player. The client
    /// reconciles its prediction against this. 0 = nothing accepted yet.
    /// </summary>
    [JsonPropertyName("ack_tick")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong AckTick { get; set; }

    /// <summary>True when this snapshot is a keyframe (complete AOI state).</summary>
    [JsonPropertyName("full")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Full { get; set; }

    [JsonPropertyName("entities")]
    public List<EntitySnapshotMsg> Entities { get; set; } = new();

    /// <summary>Entity IDs that left the AOI/world. Null on keyframes.</summary>
    [JsonPropertyName("removed")]
    public List<string>? Removed { get; set; }
}

public sealed class EntitySnapshotMsg
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("hp")]
    public int Hp { get; set; }

    [JsonPropertyName("max_hp")]
    public int MaxHp { get; set; }
}

/// <summary>AOT-compatible JSON source generator context for all wire types.</summary>
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(JoinTokenRequest))]
[JsonSerializable(typeof(JoinTokenResponse))]
[JsonSerializable(typeof(InputMessage))]
[JsonSerializable(typeof(SnapshotMessage))]
[JsonSerializable(typeof(EntitySnapshotMsg))]
[JsonSerializable(typeof(List<EntitySnapshotMsg>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class WireJsonContext : JsonSerializerContext;

/// <summary>
/// Length-prefixed JSON codec matching the Go gateway wire format.
/// Format: [4-byte big-endian length][JSON envelope body]
/// </summary>
public static class WireProtocol
{
    /// <summary>Maximum message size (1 MB).</summary>
    public const int MaxMessageSize = 1 << 20;

    /// <summary>Encode an envelope to bytes with a 4-byte big-endian length prefix.</summary>
    public static byte[] Encode(Envelope envelope)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, WireJsonContext.Default.Envelope);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Read one length-prefixed envelope from a stream. Returns null on EOF.</summary>
    public static async Task<Envelope?> DecodeAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int read = await ReadExactAsync(stream, header, ct);
        if (read == 0) return null; // clean EOF
        if (read < 4) throw new IOException("Incomplete length header");

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxMessageSize)
            throw new IOException($"Invalid message length: {length}");

        byte[] body = new byte[length];
        read = await ReadExactAsync(stream, body, ct);
        if (read < length) throw new IOException("Incomplete message body");

        return JsonSerializer.Deserialize(body, WireJsonContext.Default.Envelope)
               ?? throw new IOException("Failed to deserialize envelope");
    }

    /// <summary>Create an envelope with a serialized payload (AOT-safe).</summary>
    public static Envelope NewEnvelope(MsgType type, JoinTokenResponse payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, WireJsonContext.Default.JoinTokenResponse);
        return MakeEnvelope(type, payloadBytes);
    }

    /// <summary>Create an envelope with a serialized payload (AOT-safe).</summary>
    public static Envelope NewEnvelope(MsgType type, SnapshotMessage payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, WireJsonContext.Default.SnapshotMessage);
        return MakeEnvelope(type, payloadBytes);
    }

    /// <summary>Deserialize the payload as a JoinTokenRequest (AOT-safe).</summary>
    public static T GetPayload<T>(Envelope envelope)
    {
        var json = envelope.Payload.GetRawText();
        object? result = typeof(T) switch
        {
            Type t when t == typeof(JoinTokenRequest) =>
                JsonSerializer.Deserialize(json, WireJsonContext.Default.JoinTokenRequest),
            Type t when t == typeof(JoinTokenResponse) =>
                JsonSerializer.Deserialize(json, WireJsonContext.Default.JoinTokenResponse),
            Type t when t == typeof(InputMessage) =>
                JsonSerializer.Deserialize(json, WireJsonContext.Default.InputMessage),
            Type t when t == typeof(SnapshotMessage) =>
                JsonSerializer.Deserialize(json, WireJsonContext.Default.SnapshotMessage),
            _ => throw new NotSupportedException($"Unsupported payload type: {typeof(T).Name}")
        };
        return (T)(result ?? throw new InvalidOperationException($"Failed to deserialize payload as {typeof(T).Name}"));
    }

    private static Envelope MakeEnvelope(MsgType type, byte[] payloadBytes)
    {
        return new Envelope
        {
            Type = (byte)type,
            Payload = JsonDocument.Parse(payloadBytes).RootElement.Clone()
        };
    }

    /// <summary>Read exactly buffer.Length bytes from stream. Returns bytes actually read (0 = EOF).</summary>
    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) return offset; // EOF
            offset += n;
        }
        return offset;
    }
}
