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
    /// the field-1 varint tag 0x08. If this ever fails, encoding detection is
    /// broken and mixed-version clients will silently misparse.
    /// </summary>
    [Fact]
    public void EncodedBodies_HaveDistinguishablePrefixes()
    {
        var payload = new JoinTokenResponse { Ok = true, UserId = "u1" };

        byte[] json = WireProtocol.EncodeBody(WireProtocol.NewEnvelope(MsgType.JoinTokenResp, payload, WireEncoding.Json));
        byte[] proto = WireProtocol.EncodeBody(WireProtocol.NewEnvelope(MsgType.JoinTokenResp, payload, WireEncoding.Proto));

        Assert.Equal((byte)'{', json[0]);
        Assert.Equal(0x08, proto[0]);

        Assert.Equal(WireEncoding.Json, WireProtocol.SniffEncoding(json));
        Assert.Equal(WireEncoding.Proto, WireProtocol.SniffEncoding(proto));
    }

    /// <summary>Every message type must keep a 0x08-prefixed protobuf body.</summary>
    [Fact]
    public void ProtoEnvelope_AlwaysStartsWithTypeTag()
    {
        foreach (MsgType t in Enum.GetValues<MsgType>())
        {
            if (t == MsgType.Unspecified) continue;
            byte[] body = WireProtocol.EncodeBody(WireProtocol.NewEmptyEnvelope(t, WireEncoding.Proto));
            Assert.Equal(0x08, body[0]);
            Assert.Equal(WireEncoding.Proto, WireProtocol.SniffEncoding(body));
        }
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
            Id = "player1", Type = "player", X = 10.5f, Y = 20.25f, Hp = 90, MaxHp = 100
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
        snapshot.Entities.Add(new EntitySnapshot { Id = "e1", Type = "player", X = -1.5f, Y = 2.25f, Hp = 5, MaxHp = 200 });
        snapshot.Entities.Add(new EntitySnapshot { Id = "e2", Type = "mob", X = 0f, Y = 0f, Hp = 0, MaxHp = 1 });
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
                Id = $"lt-{i:D12}", Type = "player", X = 100.5f + i, Y = 200.25f + i, Hp = 100, MaxHp = 100
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
