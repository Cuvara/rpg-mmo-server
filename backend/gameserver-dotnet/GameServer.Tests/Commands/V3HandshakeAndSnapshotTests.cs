using GameServer.Net;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Systems;
using Envelope = GameServer.Net.Envelope;

namespace GameServer.Tests.Commands;

/// <summary>
/// Protocol 2 and 3 side by side on one server: both are admitted and recorded per connection,
/// the version 3 peer gets z / stats / statuses and its character id, the version 2 peer gets
/// the version 2 shape only. Over real sockets.
/// </summary>
public class V3HandshakeAndSnapshotTests
{
    [Theory]
    // peer, min, expected
    [InlineData(2u, 0u, WireProtocol.VersionVerdict.Accepted)]
    [InlineData(3u, 0u, WireProtocol.VersionVerdict.Accepted)]
    [InlineData(2u, 1u, WireProtocol.VersionVerdict.Accepted)]
    [InlineData(2u, 3u, WireProtocol.VersionVerdict.Refused)]   // the floor raised: v2 retired
    [InlineData(3u, 3u, WireProtocol.VersionVerdict.Accepted)]
    [InlineData(1u, 0u, WireProtocol.VersionVerdict.Refused)]   // below the supported window
    [InlineData(4u, 0u, WireProtocol.VersionVerdict.Refused)]   // ahead of this build
    [InlineData(0u, 0u, WireProtocol.VersionVerdict.AcceptedUnversioned)]
    [InlineData(0u, 2u, WireProtocol.VersionVerdict.Refused)]
    public void SupportedWindow_DecisionTable(uint peer, uint min, WireProtocol.VersionVerdict expected) =>
        Assert.Equal(expected, WireProtocol.CheckProtocolVersion(peer, min));

    [Theory]
    [InlineData(2u, WireEncoding.Proto)]
    [InlineData(3u, WireEncoding.Proto)]
    [InlineData(2u, WireEncoding.Json)]
    [InlineData(3u, WireEncoding.Json)]
    public async Task VersionsTwoAndThree_AreAdmitted_AndRecordedPerConnection(uint version, WireEncoding encoding)
    {
        await using var h = await V3ServerHarness.StartAsync();
        string user = $"u-v{version}-{encoding}";
        using var c = await h.JoinAsync(user, version, encoding);

        Assert.Equal(WireProtocol.ProtocolVersion, c.Join.ProtocolVersion); // the server echoes ITS version
        Connection conn = h.Server.ConnectionOf(user)!;
        Assert.Equal(version, conn.PeerProtocolVersion);
        Assert.Equal(version, conn.DeltaState.PeerProtocolVersion);
        Assert.True(conn.DeltaState.FieldDelta, "a version 2+ peer merges partial entities");
        Assert.Equal(0, h.Metrics.HandshakesRejectedProtocolVersion);
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(4u, 0u)]
    [InlineData(2u, 3u)]
    public async Task OutsideTheWindow_IsRefusedWithTheNamedReason(uint version, uint min)
    {
        await using var h = await V3ServerHarness.StartAsync(minProtocolVersion: min);
        using var c = await h.JoinAsync("u-out", version, WireEncoding.Proto, requireOk: false);
        Assert.False(c.Join.Ok);
        Assert.Equal(WireProtocol.ReasonProtocolVersionMismatch, c.Join.Error);
        Assert.Equal(WireProtocol.ProtocolVersion, c.Join.ProtocolVersion);
    }

    [Theory]
    [InlineData(WireEncoding.Proto)]
    [InlineData(WireEncoding.Json)]
    public async Task CharacterId_IsEchoed_FromTheTokensCid(WireEncoding encoding)
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var withCid = await h.JoinAsync("u-cid", 3, encoding, characterId: "char-42");
        Assert.Equal("char-42", withCid.Join.CharacterId);

        using var without = await h.JoinAsync("u-nocid", 3, encoding);
        Assert.Equal("", without.Join.CharacterId);
    }

    /// <summary>
    /// A version 3 and a version 2 player stand together; the version 3 one jumps. The version 3
    /// client sees its height move (bit 0x0200) and stat blocks on its keyframe; the version 2
    /// client, watching the same jump, receives no v3 field and no v3 mask bit at all.
    /// </summary>
    [Fact]
    public async Task AJump_ReachesTheV3Peer_AsZ_AndTheV2PeerSeesTheV2ShapeOnly()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var v3 = await h.JoinAsync("u-jumper", 3, WireEncoding.Proto);
        using var v2 = await h.JoinAsync("u-watcher", 2, WireEncoding.Proto);

        var v3Snaps = new List<SnapshotMessage>();
        var v2Snaps = new List<SnapshotMessage>();
        Task reader3 = ReadSnapshotsAsync(v3, v3Snaps, TimeSpan.FromSeconds(3));
        Task reader2 = ReadSnapshotsAsync(v2, v2Snaps, TimeSpan.FromSeconds(3));

        for (ulong t = 1; t <= 6; t++)
        {
            await v3.SendAsync(WireProtocol.NewEnvelope(MsgType.Input,
                new InputMessage { Tick = t, Jump = t == 2 }, WireEncoding.Proto));
            await Task.Delay(50);
        }
        await Task.WhenAll(reader3, reader2);

        Assert.Contains(v3Snaps, s => s.Full && s.Entities.Any(e => e.Stats.Count > 0));
        Assert.Contains(v3Snaps, s => s.Entities.Any(e => e.Z > 0f));
        Assert.Contains(v3Snaps, s => s.Entities.Any(e => (e.ChangedFields & SnapshotFieldBits.Z) != 0));

        Assert.NotEmpty(v2Snaps);
        foreach (SnapshotMessage s in v2Snaps)
        {
            foreach (EntitySnapshot e in s.Entities)
            {
                Assert.Equal(0f, e.Z);
                Assert.True(e.VelX == 0f && e.VelY == 0f && e.VelZ == 0f);
                Assert.Empty(e.Stats);
                Assert.Empty(e.Statuses);
                Assert.Equal(0u, e.ChangedFields & ~SnapshotFieldBits.AllVersion2);
            }
        }
        // The v2 watcher did see the jumper (its x/y), so the absence above is the shape, not the AOI.
        Assert.Contains(v2Snaps, s => s.Entities.Any(e => e.Id == "u-jumper"));
    }

    private static async Task ReadSnapshotsAsync(V3ServerHarness.Client c, List<SnapshotMessage> into, TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        try
        {
            while (true)
            {
                Envelope? env = await WireProtocol.DecodeAsync(c.Stream, cts.Token);
                if (env == null) return;
                // Copy: the parsed message is ours, but keep the list independent of later frames.
                if (env.Type == (uint)MsgType.Snapshot) into.Add(WireProtocol.GetPayload<SnapshotMessage>(env));
            }
        }
        catch (OperationCanceledException) { /* window over */ }
    }
}
