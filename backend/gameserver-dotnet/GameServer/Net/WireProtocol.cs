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
    public static Envelope DecodeBody(byte[] body) => DecodeBody(body.AsSpan());

    /// <inheritdoc cref="DecodeBody(byte[])"/>
    /// <remarks>
    /// The span form is what the read loop calls, so the frame bytes can live in a
    /// reused buffer. <b>The returned envelope never aliases <paramref name="body"/>:</b>
    /// both decoders copy the payload region into a fresh array before returning
    /// (the Protobuf path via <c>ToByteArray</c>, the JSON path via <c>ToArray</c>).
    /// That copy is required, not an oversight — the envelope escapes the read-loop
    /// iteration (the transfer handler retains it in a fire-and-forget task), so its
    /// payload cannot point into a buffer the next frame will overwrite.
    /// <c>FrameLifetimeTests</c> pins this.
    /// <para>Parsing is span-based here for the same measured reason as
    /// <see cref="GetPayload{T}"/>: <c>ParseFrom(byte[])</c> allocates a
    /// <c>CodedInputStream</c> — 272 vs 104 B per outer envelope, measured.</para>
    /// </remarks>
    public static Envelope DecodeBody(ReadOnlySpan<byte> body)
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
    /// <remarks>
    /// Allocates fresh header and body arrays per frame. Callers with a read loop
    /// should hold a <see cref="FrameReadBuffer"/> and use the overload below; this
    /// form remains for one-shot callers and tests.
    /// </remarks>
    public static async Task<Envelope?> DecodeAsync(Stream stream, CancellationToken ct)
    {
        var scratch = new FrameReadBuffer();
        return await DecodeAsync(stream, scratch, ct);
    }

    /// <summary>
    /// Read one length-prefixed envelope from a stream into <paramref name="scratch"/>'s
    /// reused buffers. Returns null on EOF.
    /// </summary>
    /// <remarks>
    /// <para><b>Why reuse is safe:</b> the frame bytes are consumed entirely inside
    /// <see cref="DecodeBody(ReadOnlySpan{byte})"/>, whose contract is that the returned
    /// envelope never aliases the input — the payload is copied out before return,
    /// because envelopes escape the read-loop iteration (transfer handling). The scratch
    /// is therefore dead the moment this method returns, and the next frame may
    /// overwrite it. <c>FrameLifetimeTests</c> drives frames through one scratch,
    /// clobbers it after every decode, and asserts every payload survived.</para>
    /// <para><b>Why a pooled ValueTask:</b> the Task&lt;Envelope?&gt; overload above
    /// allocates its Task per frame (72 B measured on a synchronously-completing
    /// stream) and a state-machine box per suspension on a real socket. The pooling
    /// builder recycles the state machine, which on the network threads is the
    /// per-packet steady state. One caller per scratch at a time — the same
    /// single-reader discipline the scratch itself already requires.</para>
    /// <para><b>Not <see cref="System.Buffers.ArrayPool{T}"/>:</b> a shared pool would
    /// need a return on every exit path and turns an early return into cross-connection
    /// buffer corruption; a connection-owned grow-only buffer has no return to forget
    /// and caps at <see cref="MaxMessageSize"/> like the frames themselves.</para>
    /// </remarks>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(
        typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Envelope?> DecodeAsync(
        Stream stream, FrameReadBuffer scratch, CancellationToken ct)
    {
        int read = await ReadExactAsync(stream, scratch.Header, 4, ct);
        if (read == 0) return null; // clean EOF
        if (read < 4) throw new IOException("Incomplete length header");

        int length = BinaryPrimitives.ReadInt32BigEndian(scratch.Header);
        if (length <= 0 || length > MaxMessageSize)
            throw new IOException($"Invalid message length: {length}");

        scratch.EnsureBody(length);
        read = await ReadExactAsync(stream, scratch.Body, length, ct);
        if (read < length) throw new IOException("Incomplete message body");

        return DecodeBody(scratch.Body.AsSpan(0, length));
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

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, PingMessage payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, PongMessage payload, WireEncoding encoding) =>
        new()
        {
            Type = RequireMsgType(type),
            Payload = encoding == WireEncoding.Proto ? payload.ToByteArray() : JsonWriter.Write(payload),
            Encoding = encoding
        };

    /// <inheritdoc cref="NewEnvelope(MsgType, JoinTokenResponse, WireEncoding)"/>
    public static Envelope NewEnvelope(MsgType type, KickMessage payload, WireEncoding encoding) =>
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
    /// <remarks>
    /// The Protobuf branches parse from a <see cref="ReadOnlySpan{T}"/> over the payload,
    /// not from the <c>byte[]</c> overload: <c>ParseFrom(byte[])</c> routes through a
    /// <c>CodedInputStream</c> object while the span overload parses on the stack —
    /// measured at 216 vs 48 B for an <see cref="InputMessage"/> (Release,
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> over 20 000 parses). This runs once
    /// per received packet on the network threads, so the 168 B difference is steady
    /// ingest churn, not a one-off.
    /// </remarks>
    public static T GetPayload<T>(Envelope envelope) where T : class
    {
        bool proto = envelope.Encoding == WireEncoding.Proto;
        ReadOnlySpan<byte> span = envelope.Payload;
        object? result = typeof(T) switch
        {
            var t when t == typeof(JoinTokenRequest) => proto
                ? JoinTokenRequest.Parser.ParseFrom(span)
                : JsonReader.ReadJoinTokenRequest(envelope.Payload),
            var t when t == typeof(JoinTokenResponse) => proto
                ? JoinTokenResponse.Parser.ParseFrom(span)
                : JsonReader.ReadJoinTokenResponse(envelope.Payload),
            var t when t == typeof(InputMessage) => proto
                ? InputMessage.Parser.ParseFrom(span)
                : JsonReader.ReadInputMessage(envelope.Payload),
            var t when t == typeof(SnapshotMessage) => proto
                ? SnapshotMessage.Parser.ParseFrom(span)
                : JsonReader.ReadSnapshotMessage(envelope.Payload),
            var t when t == typeof(TransferMapRequest) => proto
                ? TransferMapRequest.Parser.ParseFrom(span)
                : JsonReader.ReadTransferMapRequest(envelope.Payload),
            var t when t == typeof(TransferMapResponse) => proto
                ? TransferMapResponse.Parser.ParseFrom(span)
                : JsonReader.ReadTransferMapResponse(envelope.Payload),
            var t when t == typeof(PingMessage) => proto
                ? PingMessage.Parser.ParseFrom(span)
                : JsonReader.ReadPingMessage(envelope.Payload),
            var t when t == typeof(PongMessage) => proto
                ? PongMessage.Parser.ParseFrom(span)
                : JsonReader.ReadPongMessage(envelope.Payload),
            var t when t == typeof(KickMessage) => proto
                ? KickMessage.Parser.ParseFrom(span)
                : JsonReader.ReadKickMessage(envelope.Payload),
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

    private static Envelope DecodeJsonEnvelope(ReadOnlySpan<byte> body)
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
                    // ToArray, never a slice: the envelope may outlive the frame
                    // buffer (see DecodeBody's span overload).
                    payload = body.Slice((int)start, (int)(end - start)).ToArray();
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
    private static Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct) =>
        ReadExactAsync(stream, buffer, buffer.Length, ct).AsTask();

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes into the front of
    /// <paramref name="buffer"/>. Returns bytes actually read (0 = EOF). The count
    /// parameter exists for the reused-scratch path, whose buffer is usually larger
    /// than the frame it is reading.
    /// </summary>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(
        typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<int> ReadExactAsync(
        Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0) return offset; // EOF
            offset += n;
        }
        return offset;
    }
}

/// <summary>
/// Reusable per-reader scratch for <see cref="WireProtocol.DecodeAsync(Stream, FrameReadBuffer, CancellationToken)"/>:
/// the 4-byte length header and a grow-only body buffer.
/// </summary>
/// <remarks>
/// <para><b>Ownership:</b> one reader at a time. A connection's read loop is the
/// intended owner — reads on one connection are strictly sequential — and the
/// handshake's one-shot reads use the same instance before the loop starts.</para>
/// <para><b>Lifetime contract:</b> the buffers are valid only until the next
/// DecodeAsync call on the same scratch. That is safe because
/// <see cref="WireProtocol.DecodeBody(ReadOnlySpan{byte})"/> never lets a decoded
/// envelope alias the frame bytes; see its remarks and <c>FrameLifetimeTests</c>.</para>
/// </remarks>
public sealed class FrameReadBuffer
{
    /// <summary>The 4-byte big-endian length prefix. Internal for the lifetime tests.</summary>
    internal byte[] Header { get; } = new byte[4];

    /// <summary>Grow-only frame body buffer. Internal for the lifetime tests.</summary>
    internal byte[] Body { get; private set; } = new byte[512];

    /// <summary>Grow <see cref="Body"/> to hold <paramref name="length"/> bytes, doubling
    /// so a stream whose frames creep upward does not reallocate on every frame.</summary>
    internal void EnsureBody(int length)
    {
        if (Body.Length >= length) return;
        int capacity = Body.Length;
        while (capacity < length) capacity *= 2;
        Body = new byte[capacity];
    }
}
