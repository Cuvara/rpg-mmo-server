using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using RpgMmo.Wire.V1;

namespace GameServer.Net;

/// <summary>
/// Which serialization a frame body uses. Both encodings share the same framing
/// ([4-byte big-endian length][body]) and the same <see cref="MsgType"/> space,
/// so they are interchangeable per connection and the transport never sees the
/// difference.
/// </summary>
public enum WireEncoding : byte
{
    /// <summary>Legacy <c>{"type":N,"payload":{...}}</c>. Default.</summary>
    Json = 0,

    /// <summary>Protobuf, generated from <c>shared/proto/wire.proto</c>.</summary>
    Proto = 1
}

/// <summary>
/// Wire envelope: 4-byte big-endian length prefix + body.
/// </summary>
/// <remarks>
/// <see cref="Payload"/> holds the already-serialized inner message in whichever
/// encoding <see cref="Encoding"/> names. It is raw bytes rather than a
/// <c>JsonElement</c> so that the JSON path no longer has to re-parse its own
/// freshly written output just to nest it, and so the Protobuf path can carry
/// binary that is not valid JSON at all.
/// </remarks>
public sealed class Envelope
{
    public byte Type { get; set; }

    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public WireEncoding Encoding { get; set; }
}

/// <summary>
/// Length-prefixed codec for the realtime wire protocol, speaking both the
/// Protobuf and the legacy JSON encoding.
/// </summary>
/// <remarks>
/// <para>
/// Message types come from <c>shared/proto/wire.proto</c> via the generated
/// <see cref="RpgMmo.Wire.V1"/> types — there is deliberately no second,
/// hand-maintained set of C# message classes, because two definitions of one
/// wire format drift.
/// </para>
/// <para>
/// <b>Encoding detection.</b> A JSON body always starts with '{' (0x7B); a
/// Protobuf <c>Envelope</c> always starts with 0x08, the tag byte for field 1
/// (<c>type</c>, varint), which proto3 always emits because the type is >= 1 for
/// every real message. Those cannot collide, so the first body byte identifies
/// the encoding with no negotiation and no handshake. A server therefore answers
/// each client in the encoding that client used, and the gateway, the game server
/// and the Unity client can be upgraded in any order.
/// </para>
/// <para>
/// The JSON codec below is hand-written against <see cref="Utf8JsonWriter"/> and
/// <see cref="Utf8JsonReader"/> rather than going through a serializer. That
/// keeps it NativeAOT-safe with no reflection and no source-generator context,
/// and it lets the generated Protobuf types be the only message classes: the
/// alternative (Protobuf's own JsonFormatter) emits camelCase and drives
/// descriptor reflection, so it would match neither the wire format nor AOT.
/// </para>
/// </remarks>
public static class WireProtocol
{
    /// <summary>Maximum message size (1 MB).</summary>
    public const int MaxMessageSize = 1 << 20;

    /// <summary>First byte of a JSON body.</summary>
    private const byte JsonPrefix = (byte)'{';

    /// <summary>
    /// Reject a zero message type at construction.
    /// </summary>
    /// <remarks>
    /// proto3 elides a zero field 1, so a Type 0 envelope would encode WITHOUT
    /// the 0x08 prefix and be sniffed as the wrong encoding by the peer. The
    /// declared values start at 1, but nothing forces that to stay true, so the
    /// constraint that the whole scheme rests on is enforced rather than assumed.
    /// </remarks>
    private static byte RequireMsgType(MsgType type)
    {
        if (type == MsgType.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(type), "message type 0 is not a valid wire type");
        return (byte)type;
    }

    /// <summary>Classify a frame body by its first byte.</summary>
    public static WireEncoding SniffEncoding(ReadOnlySpan<byte> body) =>
        body.Length > 0 && body[0] == JsonPrefix ? WireEncoding.Json : WireEncoding.Proto;

    // ─────────────────────────── framing ───────────────────────────

    /// <summary>Encode an envelope to a length-prefixed frame.</summary>
    public static byte[] Encode(Envelope envelope)
    {
        byte[] body = EncodeBody(envelope);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Encode an envelope body without the length prefix.</summary>
    public static byte[] EncodeBody(Envelope envelope)
    {
        if (envelope.Encoding == WireEncoding.Proto)
        {
            var pb = new RpgMmo.Wire.V1.Envelope { Type = envelope.Type };
            if (envelope.Payload.Length > 0)
                pb.Payload = ByteString.CopyFrom(envelope.Payload);
            return pb.ToByteArray();
        }

        // {"type":N,"payload":<raw>}
        // Assembled directly: the payload is already valid JSON, so running it
        // back through a parser only to re-emit it (what this used to do via
        // JsonDocument.Parse) is pure waste on the per-tick snapshot path.
        ReadOnlySpan<byte> head = "{\"type\":"u8;
        ReadOnlySpan<byte> mid = ",\"payload\":"u8;
        Span<byte> typeDigits = stackalloc byte[3];
        int typeLen = WriteByteDecimal(envelope.Type, typeDigits);

        byte[] payload = envelope.Payload.Length > 0 ? envelope.Payload : "null"u8.ToArray();
        byte[] body = new byte[head.Length + typeLen + mid.Length + payload.Length + 1];

        int o = 0;
        head.CopyTo(body.AsSpan(o)); o += head.Length;
        typeDigits[..typeLen].CopyTo(body.AsSpan(o)); o += typeLen;
        mid.CopyTo(body.AsSpan(o)); o += mid.Length;
        payload.CopyTo(body.AsSpan(o)); o += payload.Length;
        body[o] = (byte)'}';
        return body;
    }

    /// <summary>Parse one frame body into an envelope, detecting the encoding.</summary>
    /// <remarks>
    /// Fails closed. Sniffing narrows a body to one of two decoders, but it
    /// cannot tell a real Protobuf envelope from arbitrary bytes that merely
    /// happen to be valid Protobuf: a body beginning 0x12 parses as a well-formed
    /// Envelope carrying only field 2, leaving the type at 0. Rejecting type 0 is
    /// what turns that from a silent half-parse into an error.
    /// </remarks>
    public static Envelope DecodeBody(byte[] body)
    {
        if (body.Length == 0)
            throw new IOException("Empty envelope body");

        if (SniffEncoding(body) == WireEncoding.Proto)
        {
            var pb = RpgMmo.Wire.V1.Envelope.Parser.ParseFrom(body);
            if (pb.Type == 0)
                throw new IOException("Invalid message type 0");
            if (pb.Type > byte.MaxValue)
                throw new IOException($"Message type out of range: {pb.Type}");
            return new Envelope
            {
                Type = (byte)pb.Type,
                Payload = pb.Payload.IsEmpty ? Array.Empty<byte>() : pb.Payload.ToByteArray(),
                Encoding = WireEncoding.Proto
            };
        }

        var env = DecodeJsonEnvelope(body);
        if (env.Type == 0)
            throw new IOException("Invalid message type 0");
        return env;
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

        return DecodeBody(body);
    }

    // ─────────────────────── envelope construction ───────────────────────

    /// <summary>
    /// Build an envelope carrying <paramref name="payload"/>, serialized in
    /// <paramref name="encoding"/>.
    /// </summary>
    /// <remarks>
    /// Every server reply should be built with the encoding of the message it
    /// answers (see <c>Connection.Encoding</c>), never with a hard-coded one —
    /// that is what keeps a Protobuf server able to serve a JSON client.
    /// </remarks>
    public static Envelope NewEnvelope(MsgType type, JoinTokenResponse payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, SnapshotMessage payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, JoinTokenRequest payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, InputMessage payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, DisconnectMessage payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <summary>Build an envelope with no payload (Disconnect, Resync).</summary>
    public static Envelope NewEmptyEnvelope(MsgType type, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? Array.Empty<byte>() : "{}"u8.ToArray(),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, TransferMapRequest payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, TransferMapResponse payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    // ─────────────────────────── payload access ───────────────────────────

    /// <summary>Deserialize the payload as <typeparamref name="T"/>, honouring the envelope's encoding.</summary>
    public static T GetPayload<T>(Envelope envelope) where T : class
    {
        bool proto = envelope.Encoding == WireEncoding.Proto;
        object? result = typeof(T) switch
        {
            var t when t == typeof(JoinTokenRequest) => proto
                ? JoinTokenRequest.Parser.ParseFrom(envelope.Payload)
                : JsonReader.ReadJoinTokenRequest(envelope.Payload),
            var t when t == typeof(JoinTokenResponse) => proto
                ? JoinTokenResponse.Parser.ParseFrom(envelope.Payload)
                : JsonReader.ReadJoinTokenResponse(envelope.Payload),
            var t when t == typeof(InputMessage) => proto
                ? InputMessage.Parser.ParseFrom(envelope.Payload)
                : JsonReader.ReadInputMessage(envelope.Payload),
            var t when t == typeof(SnapshotMessage) => proto
                ? SnapshotMessage.Parser.ParseFrom(envelope.Payload)
                : JsonReader.ReadSnapshotMessage(envelope.Payload),
            var t when t == typeof(TransferMapRequest) => proto
                ? TransferMapRequest.Parser.ParseFrom(envelope.Payload)
                : JsonReader.ReadTransferMapRequest(envelope.Payload),
            var t when t == typeof(TransferMapResponse) => proto
                ? TransferMapResponse.Parser.ParseFrom(envelope.Payload)
                : JsonReader.ReadTransferMapResponse(envelope.Payload),
            _ => throw new NotSupportedException($"Unsupported payload type: {typeof(T).Name}")
        };
        return (T)(result ?? throw new InvalidOperationException($"Failed to deserialize payload as {typeof(T).Name}"));
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static int WriteByteDecimal(byte value, Span<byte> dst)
    {
        if (value >= 100) { dst[0] = (byte)('0' + value / 100); dst[1] = (byte)('0' + value / 10 % 10); dst[2] = (byte)('0' + value % 10); return 3; }
        if (value >= 10) { dst[0] = (byte)('0' + value / 10); dst[1] = (byte)('0' + value % 10); return 2; }
        dst[0] = (byte)('0' + value);
        return 1;
    }

    private static Envelope DecodeJsonEnvelope(byte[] body)
    {
        var reader = new Utf8JsonReader(body);
        byte type = 0;
        byte[] payload = Array.Empty<byte>();

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new IOException("Malformed JSON envelope");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new IOException("Malformed JSON envelope");

            bool isType = reader.ValueTextEquals("type"u8);
            bool isPayload = reader.ValueTextEquals("payload"u8);

            if (!reader.Read()) throw new IOException("Truncated JSON envelope");

            if (isType)
            {
                type = reader.GetByte();
            }
            else if (isPayload)
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    payload = Array.Empty<byte>();
                }
                else
                {
                    long start = reader.TokenStartIndex;
                    reader.Skip();
                    long end = reader.BytesConsumed;
                    payload = body.AsSpan((int)start, (int)(end - start)).ToArray();
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return new Envelope { Type = type, Payload = payload, Encoding = WireEncoding.Json };
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
