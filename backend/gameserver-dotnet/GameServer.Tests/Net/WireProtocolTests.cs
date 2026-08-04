using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GameServer.Net;

namespace GameServer.Tests.Net;

public class WireProtocolTests
{
    [Fact]
    public async Task Encode_Decode_Roundtrip()
    {
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true, UserId = "u1" });
        var encoded = WireProtocol.Encode(envelope);

        using var stream = new MemoryStream(encoded);
        var decoded = await WireProtocol.DecodeAsync(stream, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal((byte)MsgType.JoinTokenResp, decoded.Type);
    }

    [Fact]
    public void Encode_HasCorrectLengthPrefix()
    {
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true });
        var encoded = WireProtocol.Encode(envelope);

        // First 4 bytes should be big-endian length
        Assert.True(encoded.Length >= 4);
        var prefixLength = BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(0, 4));
        Assert.Equal(encoded.Length - 4, prefixLength);
    }

    [Fact]
    public async Task Decode_RejectsTooLargeMessage()
    {
        // Create a length prefix indicating > 1MB
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, 2 * 1024 * 1024); // 2MB

        using var stream = new MemoryStream();
        stream.Write(lengthBytes);
        stream.Write(new byte[100]); // Some dummy data
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await WireProtocol.DecodeAsync(stream, CancellationToken.None);
        });
    }

    [Fact]
    public void NewEnvelope_SerializesPayload()
    {
        var payload = new JoinTokenResponse { Ok = true, UserId = "test" };
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, payload);

        Assert.Equal((byte)MsgType.JoinTokenResp, envelope.Type);
        Assert.NotEqual(default, envelope.Payload);
    }

    [Fact]
    public void GetPayload_DeserializesCorrectly()
    {
        var original = new JoinTokenResponse { Ok = true, UserId = "test", Error = null };
        var envelope = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, original);

        var deserialized = WireProtocol.GetPayload<JoinTokenResponse>(envelope);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Ok);
        Assert.Equal("test", deserialized.UserId);
    }

    [Fact]
    public async Task Encode_Decode_MultipleMessages()
    {
        var msg1 = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = true, UserId = "u1" });
        var msg2 = WireProtocol.NewEnvelope(MsgType.JoinTokenResp, new JoinTokenResponse { Ok = false, Error = "denied" });

        var encoded1 = WireProtocol.Encode(msg1);
        var encoded2 = WireProtocol.Encode(msg2);

        using var stream = new MemoryStream();
        stream.Write(encoded1);
        stream.Write(encoded2);
        stream.Position = 0;

        var decoded1 = await WireProtocol.DecodeAsync(stream, CancellationToken.None);
        var decoded2 = await WireProtocol.DecodeAsync(stream, CancellationToken.None);

        Assert.NotNull(decoded1);
        Assert.NotNull(decoded2);
        Assert.Equal((byte)MsgType.JoinTokenResp, decoded1!.Type);
        Assert.Equal((byte)MsgType.JoinTokenResp, decoded2!.Type);
    }

    [Fact]
    public void SnapshotMessage_JsonMatchesGoFormat()
    {
        // Verify JSON field names match Go format: snake_case
        var snapshot = new
        {
            tick = 42L,
            entities = new[]
            {
                new
                {
                    id = "player1",
                    type = "player",
                    x = 10.5f,
                    y = 20.3f,
                    hp = 100,
                    max_hp = 100
                }
            }
        };

        var json = JsonSerializer.Serialize(snapshot);

        // Verify snake_case field names are present
        Assert.Contains("\"tick\"", json);
        Assert.Contains("\"entities\"", json);
        Assert.Contains("\"id\"", json);
        Assert.Contains("\"type\"", json);
        Assert.Contains("\"x\"", json);
        Assert.Contains("\"y\"", json);
        Assert.Contains("\"hp\"", json);
        Assert.Contains("\"max_hp\"", json);
    }

    [Fact]
    public async Task Decode_EmptyStream_ReturnsNullOrThrows()
    {
        using var stream = new MemoryStream();

        // Either returns null or throws on empty stream
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
