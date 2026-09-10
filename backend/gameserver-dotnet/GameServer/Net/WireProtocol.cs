using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using GameServer.Net.Sealed;
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

    /// <summary>
    /// Version of the wire schema this build implements. Mirrors
    /// <c>shared/messages.WireProtocolVersion</c> (Go) and
    /// <c>Runtime/Protocol/WireProtocolVersion.cs</c> (Unity client).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It names the SEMANTICS of <c>shared/proto/wire.proto</c> — what the fields
    /// mean — not its shape and not its encoding. Shape is self-describing
    /// (proto3 skips unknown fields) and encoding is sniffed from byte 0; neither
    /// catches two peers that parse every byte and then disagree about what a
    /// field means. That is the failure this number makes loud.
    /// </para>
    /// <para>
    /// <b>Bump it</b> for: reusing or renumbering a field, removing a field a
    /// receiver acts on, changing the meaning/units/reference frame of an
    /// existing field, changing the snapshot state machine (handle lifecycle,
    /// keyframe reset, the delta "changed" rule, the merge algorithm), or adding
    /// something a receiver MUST act on to stay correct. Do NOT bump for a purely
    /// additive optional field covered by a documented "zero means not sent"
    /// rule. Full contract: <c>wire.proto</c> under "Protocol version", and
    /// normatively <c>docs/API.md</c>.
    /// </para>
    /// <para>
    /// No language can be authoritative for the other two, so each pins the value
    /// and tests assert it on its own side.
    /// </para>
    /// </remarks>
    public const uint ProtocolVersion = 1;

    /// <summary>
    /// Wire value meaning "this peer does not advertise a version" — a peer built
    /// before the field existed.
    /// </summary>
    /// <remarks>
    /// proto3 elides a zero uint32, so an absent field and an explicit 0 are the
    /// same bytes. Real versions therefore start at 1 and 0 is permanently
    /// reserved for "unknown", exactly as <c>ENTITY_TYPE_UNSPECIFIED</c> reserves
    /// 0. A receiver must not read 0 as "version zero".
    /// </remarks>
    public const uint ProtocolVersionUnversioned = 0;

    /// <summary>
    /// The named reason a peer is refused for speaking a different wire protocol
    /// version. Travels in <c>JoinTokenResponse.Error</c>.
    /// </summary>
    /// <remarks>
    /// Follows the existing machine-readable reason convention
    /// (<c>duplicate_login</c>, <c>server_shutdown</c>). The point of the version
    /// handshake is that this string appears instead of a parse error, a silent
    /// close, or a successful connection that is confidently wrong.
    /// </remarks>
    public const string ReasonProtocolVersionMismatch = "protocol_version_mismatch";

    /// <summary>Outcome of checking a peer's advertised protocol version.</summary>
    public enum VersionVerdict
    {
        /// <summary>The peer advertised exactly this build's version.</summary>
        Accepted,

        /// <summary>
        /// The peer advertised nothing and the configured minimum still tolerates
        /// that. Admission on trust — callers MUST count it separately, because
        /// the counter reaching zero is the only evidence that raising the
        /// minimum will not lock out real players.
        /// </summary>
        AcceptedUnversioned,

        /// <summary>The peer's version is one this build cannot serve.</summary>
        Refused,
    }

    /// <summary>
    /// Decide whether a peer advertising <paramref name="peerVersion"/> may be
    /// admitted by a receiver whose configured floor is
    /// <paramref name="minVersion"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is EXACT MATCH against <see cref="ProtocolVersion"/>, with one
    /// configured exemption for the unversioned case. Exact match, rather than
    /// "peer >= min", is the honest rule for a single integer carrying no
    /// compatibility range: a peer one version AHEAD is refused just as firmly as
    /// one behind, because this build cannot know what a later version changed
    /// and admitting it would be the guess the mechanism exists to prevent.
    /// </para>
    /// <para>
    /// Mirrors <c>shared/messages.CheckProtocolVersion</c> in Go; the two are
    /// asserted to agree by the interop tests.
    /// </para>
    /// </remarks>
    public static VersionVerdict CheckProtocolVersion(uint peerVersion, uint minVersion)
    {
        if (peerVersion == ProtocolVersion) return VersionVerdict.Accepted;
        if (peerVersion == ProtocolVersionUnversioned && minVersion == ProtocolVersionUnversioned)
            return VersionVerdict.AcceptedUnversioned;
        return VersionVerdict.Refused;
    }

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

    /// <summary>Add the 4-byte big-endian length prefix to an already-built body.</summary>
    /// <remarks>
    /// The prefix stays in the clear even when the body is sealed: it is what finds the
    /// frame boundary, so a reader needs it before it can have a key. Its value leaks only
    /// the frame's length, which traffic analysis already sees.
    /// </remarks>
    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame.AsSpan(4));
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

    /// <summary>
    /// Read one frame, unsealing it first when a sealed session is in force.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <paramref name="inbound"/> is null this is exactly the cleartext path. When it
    /// is not, a frame that is NOT sealed is refused rather than parsed: after the
    /// handshake, cleartext where a sealed frame is required is either a confused peer or
    /// an attacker stripping the encryption, and there is no third reading. Accepting it
    /// would be a downgrade the protocol deliberately has no room for.
    /// </para>
    /// <para>
    /// <see cref="SealedSession.Open"/> is what enforces authenticate-before-replay-check,
    /// so nothing here may look at the sequence number.
    /// </para>
    /// </remarks>
    public static async ValueTask<Envelope?> DecodeAsync(
        Stream stream, FrameReadBuffer scratch, SealedSession? inbound, CancellationToken ct)
    {
        if (inbound is null) return await DecodeAsync(stream, scratch, ct);

        int read = await ReadExactAsync(stream, scratch.Header, 4, ct);
        if (read == 0) return null; // clean EOF
        if (read < 4) throw new IOException("Incomplete length header");

        int length = BinaryPrimitives.ReadInt32BigEndian(scratch.Header);
        if (length <= 0 || length > MaxMessageSize)
            throw new IOException($"Invalid message length: {length}");

        scratch.EnsureBody(length);
        read = await ReadExactAsync(stream, scratch.Body, length, ct);
        if (read < length) throw new IOException("Incomplete message body");

        SealedOpenResult result = inbound.Open(scratch.Body.AsSpan(0, length), out byte[] plaintext);
        if (result != SealedOpenResult.Ok)
        {
            // ONE message to the peer, but a distinguishable one to US. A rejected sealed
            // frame closes the connection, and without this it is indistinguishable in the
            // log from an ordinary disconnect — which is the "a check nobody reads is not
            // a check" failure, one layer down. The counts on the session say which rule
            // fired; this says that one did.
            throw new SealedFrameRejectedException();
        }

        return DecodeBody(plaintext);
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
    /// <summary>
    /// Build a sealed-handshake reply. <b>Protobuf only</b>, deliberately: the handshake
    /// fields are absent from the JSON message set so key material can never be rendered
    /// into a human-readable payload, which is also why a JSON client cannot be encrypted
    /// and must be refused rather than served in the clear.
    /// </summary>
    public static Envelope NewEnvelope(MsgType type, SealedServerHello payload, WireEncoding encoding)
    {
        if (encoding != WireEncoding.Proto)
            throw new InvalidOperationException(
                "the sealed handshake has no JSON encoding; a JSON client cannot be sealed");

        return new Envelope
        {
            Type = RequireMsgType(type),
            Payload = payload.ToByteArray(),
            Encoding = WireEncoding.Proto,
        };
    }

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
            // Protobuf only. A JSON peer reaching here is a peer that cannot be
            // sealed, and the refusal is the point rather than a gap.
            var t when t == typeof(SealedClientHello) => proto
                ? SealedClientHello.Parser.ParseFrom(span)
                : throw new InvalidOperationException(
                    "the sealed handshake has no JSON encoding; a JSON client cannot be sealed"),
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

/// <summary>
/// A sealed frame did not authenticate, replayed, or arrived as cleartext where a sealed
/// frame was required.
/// </summary>
/// <remarks>
/// An <see cref="IOException"/> so the existing read-loop teardown handles it unchanged,
/// but its own type so the log can say what happened. The peer learns nothing either way:
/// the connection simply closes, exactly as it would for any other frame-level failure.
/// </remarks>
public sealed class SealedFrameRejectedException : IOException
{
    /// <summary>Build the exception.</summary>
    public SealedFrameRejectedException()
        : base("sealed frame rejected (not authenticated, replayed, or not sealed)") { }
}
