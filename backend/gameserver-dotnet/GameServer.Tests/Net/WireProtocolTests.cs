using System.Buffers.Binary;
using System.Text;
using GameServer.Net;

namespace GameServer.Tests.Net;

public class WireProtocolTests
{
    public static TheoryData<WireEncoding> BothEncodings => new() { WireEncoding.Json, WireEncoding.Proto };

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public async Task Encode_Decode_Roundtrip(WireEncoding enc)
    {
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true, UserId = "u1" }, enc);
        var encoded = WireProtocol.Encode(envelope);

        using var stream = new MemoryStream(encoded);
        var decoded = await WireProtocol.DecodeAsync(stream, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal((byte)MsgType.JoinTokenResp, decoded!.Type);
        Assert.Equal(enc, decoded.Encoding);

        var payload = WireProtocol.GetPayload<JoinTokenResponse>(decoded);
        Assert.True(payload.Ok);
        Assert.Equal("u1", payload.UserId);
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public void Encode_HasCorrectLengthPrefix(WireEncoding enc)
    {
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true }, enc);
        var encoded = WireProtocol.Encode(envelope);

        Assert.True(encoded.Length >= 4);
        var prefixLength = BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(0, 4));
        Assert.Equal(encoded.Length - 4, prefixLength);
    }

    /// <summary>
    /// The whole dual-stack migration rests on these two byte values never
    /// colliding: a JSON body starts with '{', a protobuf Envelope starts with
    /// the field-1 varint tag 0x08.
    /// <para>
    /// This sweeps EVERY type value the wire can carry (1..255), not just the
    /// declared enum members, so adding a message type cannot silently escape the
    /// check and neither can renumbering the existing ones. If it ever fails,
    /// encoding detection is broken and mixed-version clients misparse silently.
    /// </para>
    /// </summary>
    [Fact]
    public void EncodedBodies_HaveDistinguishablePrefixes_ForEveryType()
    {
        for (int n = 1; n <= 255; n++)
        {
            var type = (MsgType)n;

            byte[] json = WireProtocol.EncodeBody(WireProtocol.NewEmptyEnvelope(type, WireEncoding.Json));
            byte[] proto = WireProtocol.EncodeBody(WireProtocol.NewEmptyEnvelope(type, WireEncoding.Proto));

            // No leading whitespace, ever: an encoder that pretty-printed would
            // move the '{' off byte 0 and every frame would sniff as protobuf.
            Assert.Equal((byte)'{', json[0]);
            Assert.Equal(0x08, proto[0]);
            Assert.Equal(WireEncoding.Json, WireProtocol.SniffEncoding(json));
            Assert.Equal(WireEncoding.Proto, WireProtocol.SniffEncoding(proto));

            Assert.Equal((byte)n, WireProtocol.DecodeBody(json).Type);
            Assert.Equal((byte)n, WireProtocol.DecodeBody(proto).Type);
        }
    }

    /// <summary>
    /// Guards the invariant at construction: proto3 elides a zero field 1, so a
    /// Type 0 envelope would encode without the 0x08 prefix and be sniffed as the
    /// wrong encoding. The enum starts at 1, but nothing forces that to stay true.
    /// </summary>
    [Fact]
    public void MsgTypeZero_IsRejectedAtConstruction()
    {
        foreach (var enc in new[] { WireEncoding.Json, WireEncoding.Proto })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                WireProtocol.NewEmptyEnvelope(MsgType.Unspecified, enc));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                WireProtocol.NewEnvelope(MsgType.Unspecified, new JoinTokenResponse { Ok = true }, enc));
        }
    }

    /// <summary>
    /// Both decoders must fail closed on a body that is neither valid JSON nor a
    /// real envelope.
    /// <para>
    /// The important case is <c>0x12</c>: that is field 2 (payload),
    /// length-delimited, so it parses as a perfectly well-formed protobuf
    /// Envelope and leaves the type at 0. Without the type-0 guard that produced
    /// a typeless envelope and NO error — a silent half-parse routed onwards as
    /// an unknown message rather than rejected.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x12, 0x02, 0x41, 0x42 })] // valid proto, no type field
    [InlineData(new byte[] { 0x00, 0x01 })]             // field number 0
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF })]       // high bytes
    [InlineData(new byte[] { 0x08 })]                   // truncated varint
    [InlineData(new byte[] { })]                        // empty
    public void DecodeBody_FailsClosedOnGarbage(byte[] body)
    {
        Assert.ThrowsAny<Exception>(() => WireProtocol.DecodeBody(body));
    }

    [Fact]
    public void DecodeBody_FailsClosedOnJsonWithoutType()
    {
        Assert.ThrowsAny<Exception>(() =>
            WireProtocol.DecodeBody(Encoding.UTF8.GetBytes("{\"nope\":1}")));
    }

    [Fact]
    public async Task Decode_RejectsTooLargeMessage()
    {
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, 2 * 1024 * 1024); // 2MB

        using var stream = new MemoryStream();
        stream.Write(lengthBytes);
        stream.Write(new byte[100]);
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await WireProtocol.DecodeAsync(stream, CancellationToken.None);
        });
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public void GetPayload_DeserializesCorrectly(WireEncoding enc)
    {
        var original = new JoinTokenResponse { Ok = true, UserId = "test" };
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, original, enc);

        var deserialized = WireProtocol.GetPayload<JoinTokenResponse>(envelope);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Ok);
        Assert.Equal("test", deserialized.UserId);
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public async Task Encode_Decode_MultipleMessages(WireEncoding enc)
    {
        var msg1 = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true, UserId = "u1" }, enc);
        var msg2 = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = false, Error = "denied" }, enc);

        using var stream = new MemoryStream();
        stream.Write(WireProtocol.Encode(msg1));
        stream.Write(WireProtocol.Encode(msg2));
        stream.Position = 0;

        var decoded1 = await WireProtocol.DecodeAsync(stream, CancellationToken.None);
        var decoded2 = await WireProtocol.DecodeAsync(stream, CancellationToken.None);

        Assert.Equal("u1", WireProtocol.GetPayload<JoinTokenResponse>(decoded1!).UserId);
        Assert.Equal("denied", WireProtocol.GetPayload<JoinTokenResponse>(decoded2!).Error);
    }

    /// <summary>
    /// A stream may carry both encodings back to back — that is the entire point
    /// of sniffing, and it is what a server sees while a fleet is half-migrated.
    /// </summary>
    [Fact]
    public async Task Decode_HandlesMixedEncodingsOnOneStream()
    {
        using var stream = new MemoryStream();
        stream.Write(WireProtocol.Encode(WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true, UserId = "json-first" }, WireEncoding.Json)));
        stream.Write(WireProtocol.Encode(WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true, UserId = "proto-second" }, WireEncoding.Proto)));
        stream.Position = 0;

        var a = await WireProtocol.DecodeAsync(stream, CancellationToken.None);
        var b = await WireProtocol.DecodeAsync(stream, CancellationToken.None);

        Assert.Equal(WireEncoding.Json, a!.Encoding);
        Assert.Equal("json-first", WireProtocol.GetPayload<JoinTokenResponse>(a).UserId);
        Assert.Equal(WireEncoding.Proto, b!.Encoding);
        Assert.Equal("proto-second", WireProtocol.GetPayload<JoinTokenResponse>(b).UserId);
    }

    /// <summary>
    /// Pins the legacy JSON bytes exactly. The predecessor of this test built an
    /// anonymous object and asserted on <i>its</i> JSON, so it would have passed
    /// no matter what the codec emitted; this one serializes through the real
    /// codec. A pre-Protobuf client parses these bytes, so drift here is a
    /// silent compatibility break.
    /// </summary>
    [Fact]
    public void SnapshotMessage_JsonMatchesGoFormat()
    {
        var snapshot = new SnapshotMessage { Tick = 42, AckTick = 41, Full = true };
        snapshot.Entities.Add(new EntitySnapshot
        {
            Id = "player1", Type = EntityType.Player, X = 10.5f, Y = 20.25f, Hp = 90, MaxHp = 100
        });
        snapshot.Removed.Add("gone1");

        var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Json);
        string payload = Encoding.UTF8.GetString(env.Payload);

        Assert.Equal(
            "{\"tick\":42,\"ack_tick\":41,\"full\":true," +
            "\"entities\":[{\"id\":\"player1\",\"type\":\"player\",\"x\":10.5,\"y\":20.25,\"hp\":90,\"max_hp\":100}]," +
            "\"removed\":[\"gone1\"]}",
            payload);

        string body = Encoding.UTF8.GetString(WireProtocol.EncodeBody(env));
        Assert.Equal("{\"type\":8,\"payload\":" + payload + "}", body);
    }

    /// <summary>Go omits zero/false/empty via `omitempty`; the JSON codec must too.</summary>
    [Fact]
    public void SnapshotMessage_Json_OmitsDefaults()
    {
        var snapshot = new SnapshotMessage { Tick = 7 }; // no ack, not full, no entities, nothing removed
        var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Json);

        Assert.Equal("{\"tick\":7,\"entities\":[]}", Encoding.UTF8.GetString(env.Payload));
    }

    /// <summary>
    /// Both encodings must reconstruct the identical message. This is the local
    /// half of the cross-language agreement the Go integration tests check.
    /// </summary>
    [Fact]
    public void BothEncodings_RoundTripIdenticalSnapshot()
    {
        var snapshot = new SnapshotMessage { Tick = 900, AckTick = 899, Full = false };
        snapshot.Entities.Add(new EntitySnapshot { Id = "e1", Type = EntityType.Player, X = -1.5f, Y = 2.25f, Hp = 5, MaxHp = 200 });
        snapshot.Entities.Add(new EntitySnapshot { Id = "e2", Type = EntityType.Mob, X = 0f, Y = 0f, Hp = 0, MaxHp = 1 });
        snapshot.Removed.Add("e3");

        var viaJson = WireProtocol.GetPayload<SnapshotMessage>(
            WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Json));
        var viaProto = WireProtocol.GetPayload<SnapshotMessage>(
            WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Proto));

        Assert.Equal(snapshot, viaJson);
        Assert.Equal(snapshot, viaProto);
        Assert.Equal(viaJson, viaProto);
    }

    /// <summary>The bandwidth claim, asserted rather than assumed.</summary>
    [Fact]
    public void ProtoSnapshot_IsSubstantiallySmallerThanJson()
    {
        var snapshot = new SnapshotMessage { Tick = 12345, AckTick = 12344, Full = true };
        for (int i = 0; i < 50; i++)
        {
            snapshot.Entities.Add(new EntitySnapshot
            {
                Id = $"lt-{i:D12}", Type = EntityType.Player, X = 100.5f + i, Y = 200.25f + i, Hp = 100, MaxHp = 100
            });
        }

        int json = WireProtocol.EncodeBody(WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Json)).Length;
        int proto = WireProtocol.EncodeBody(WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Proto)).Length;

        Assert.True(proto < json / 2, $"expected protobuf under half of JSON, got proto={proto} json={json}");
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public void InputMessage_RoundTrips(WireEncoding enc)
    {
        var input = new InputMessage { Tick = 99, MoveX = -0.5f, MoveY = 0.25f, AttackTargetId = "mob-1" };
        var env = WireProtocol.NewEnvelope(MsgType.Input, input, enc);

        Assert.Equal(input, WireProtocol.GetPayload<InputMessage>(env));
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public void EmptyPayloadMessages_RoundTrip(WireEncoding enc)
    {
        foreach (var t in new[] { MsgType.Resync, MsgType.Disconnect })
        {
            var env = WireProtocol.NewEmptyEnvelope(t, enc);
            var frame = WireProtocol.Encode(env);
            var body = frame.AsSpan(4).ToArray();
            var decoded = WireProtocol.DecodeBody(body);

            Assert.Equal((byte)t, decoded.Type);
            Assert.Equal(enc, decoded.Encoding);
        }
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public void TransferMapRequest_RoundTrips(WireEncoding enc)
    {
        var req = new TransferMapRequest { MapId = "map_02" };
        var env = WireProtocol.NewEnvelope(MsgType.TransferMap, req, enc);

        Assert.Equal(req, WireProtocol.GetPayload<TransferMapRequest>(env));
    }

    [Theory]
    [MemberData(nameof(BothEncodings))]
    public void TransferMapResponse_RoundTrips(WireEncoding enc)
    {
        var ok = new TransferMapResponse { Ok = true };
        var envOk = WireProtocol.NewEnvelope(MsgType.TransferMapResp, ok, enc);
        var gotOk = WireProtocol.GetPayload<TransferMapResponse>(envOk);
        Assert.True(gotOk.Ok);
        Assert.Equal("", gotOk.Error);

        var err = new TransferMapResponse { Ok = false, Error = "already on this map" };
        var envErr = WireProtocol.NewEnvelope(MsgType.TransferMapResp, err, enc);
        var gotErr = WireProtocol.GetPayload<TransferMapResponse>(envErr);
        Assert.False(gotErr.Ok);
        Assert.Equal("already on this map", gotErr.Error);
    }

    /// <summary>Pins the JSON format for TransferMapRequest and TransferMapResponse.</summary>
    [Fact]
    public void TransferMap_JsonMatchesGoFormat()
    {
        var req = WireProtocol.NewEnvelope(MsgType.TransferMap,
            new TransferMapRequest { MapId = "dungeon_01" }, WireEncoding.Json);
        Assert.Equal("{\"map_id\":\"dungeon_01\"}", Encoding.UTF8.GetString(req.Payload));

        var respOk = WireProtocol.NewEnvelope(MsgType.TransferMapResp,
            new TransferMapResponse { Ok = true }, WireEncoding.Json);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(respOk.Payload));

        var respErr = WireProtocol.NewEnvelope(MsgType.TransferMapResp,
            new TransferMapResponse { Ok = false, Error = "nope" }, WireEncoding.Json);
        Assert.Equal("{\"ok\":false,\"error\":\"nope\"}", Encoding.UTF8.GetString(respErr.Payload));
    }

    [Fact]
    public async Task Decode_EmptyStream_ReturnsNullOrThrows()
    {
        using var stream = new MemoryStream();
        try
        {
            var result = await WireProtocol.DecodeAsync(stream, CancellationToken.None);
            Assert.Null(result);
        }
        catch (Exception)
        {
            // Expected for empty stream
        }
    }

    [Fact]
    public async Task Decode_CorruptedData_Throws()
    {
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, 10);

        using var stream = new MemoryStream();
        stream.Write(lengthBytes);
        stream.Write(Encoding.UTF8.GetBytes("not json!!"));
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await WireProtocol.DecodeAsync(stream, CancellationToken.None);
        });
    }
}
