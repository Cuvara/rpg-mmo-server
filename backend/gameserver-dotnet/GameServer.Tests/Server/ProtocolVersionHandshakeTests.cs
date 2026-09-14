using GameServer.Net;
using GameServer.Observability;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// The wire protocol version handshake on the game-server hop.
/// </summary>
/// <remarks>
/// <para>
/// The promise being tested is narrow and worth stating: a peer that disagrees with this
/// build about what the schema MEANS is refused with a named reason, rather than admitted
/// and then quietly misparsing snapshots. Before this field existed, client and server
/// agreed by convention alone — a version-skewed build connected, parsed every byte, and
/// was confidently wrong about the world.
/// </para>
/// <para>
/// Everything here goes through a real socket, because the failure being prevented is a
/// wire failure: a test of <see cref="WireProtocol.CheckProtocolVersion"/> alone would
/// prove the decision and nothing about whether the client ever sees it.
/// </para>
/// </remarks>
public class ProtocolVersionHandshakeTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    /// <summary>A client on this build's version joins normally and is told the version back.</summary>
    [Theory]
    [InlineData(WireEncoding.Json)]
    [InlineData(WireEncoding.Proto)]
    public async Task MatchingVersion_IsAdmitted(WireEncoding encoding)
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        var resp = await HardeningHarness.SendJoinAsync(
            client, "user-match", protocolVersion: WireProtocol.ProtocolVersion, encoding: encoding);

        Assert.True(resp.Ok, resp.Error);
        Assert.Equal(WireProtocol.ProtocolVersion, resp.ProtocolVersion);
        Assert.Equal(0, metrics.UnversionedHandshakes);
        Assert.Equal(0, metrics.HandshakesRejectedProtocolVersion);
    }

    /// <summary>
    /// The central case. A skewed client is refused with the exact machine-readable reason,
    /// over both encodings, and the refusal names the version it failed against.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Json)]
    [InlineData(WireEncoding.Proto)]
    public async Task MismatchedVersion_IsRefusedWithANamedReason(WireEncoding encoding)
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();

        // A VALID join token deliberately: the refusal must be about the schema, not the
        // credential. Reporting "Invalid or expired token" here would send an operator to
        // the wrong layer, which is exactly the wasted chase this field exists to prevent.
        var resp = await HardeningHarness.SendJoinAsync(
            client, "user-skew", protocolVersion: WireProtocol.ProtocolVersion + 1, encoding: encoding);

        Assert.False(resp.Ok);
        Assert.Equal(WireProtocol.ReasonProtocolVersionMismatch, resp.Error);
        Assert.Equal(WireProtocol.ProtocolVersion, resp.ProtocolVersion);

        // Counted as its own reason, not as "malformed": the frame parsed perfectly, and
        // conflating the two hides a rollout skew behind a client-is-broken signal.
        Assert.Equal(1, metrics.HandshakesRejectedProtocolVersion);
        Assert.Equal(0, metrics.HandshakesRejectedMalformed);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>A client BEHIND this build is refused just as firmly as one ahead.</summary>
    [Fact]
    public async Task OlderVersion_IsRefused()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        // 999_999 stands in for "some other version" in both directions; the rule is exact
        // match, so the sign of the difference is irrelevant by design.
        var resp = await HardeningHarness.SendJoinAsync(client, "user-old", protocolVersion: 999_999);

        Assert.False(resp.Ok);
        Assert.Equal(WireProtocol.ReasonProtocolVersionMismatch, resp.Error);
    }

    /// <summary>
    /// An old client sends no version at all. Under the shipping default it is admitted —
    /// and counted, because that counter is the only thing that will ever say the fleet is
    /// ready to have the default raised.
    /// </summary>
    [Fact]
    public async Task UnversionedClient_IsAdmittedAndCounted()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        var resp = await HardeningHarness.SendJoinAsync(
            client, "user-legacy", protocolVersion: WireProtocol.ProtocolVersionUnversioned);

        Assert.True(resp.Ok, resp.Error);
        Assert.Equal(1, metrics.UnversionedHandshakes);
        Assert.Equal(0, metrics.HandshakesRejectedProtocolVersion);

        // Still a real session: admission on trust is admission, not a half-open state.
        Assert.Equal(1, metrics.PlayersOnline);
    }

    /// <summary>
    /// Raising the floor to 1 is the migration step. The same unversioned client is then
    /// refused through the same named path as a mismatched one — not a second convention.
    /// </summary>
    [Fact]
    public async Task UnversionedClient_IsRefusedOnceAdvertisementIsRequired()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics, minProtocolVersion: 1);

        using var client = await h.ConnectAsync();
        var resp = await HardeningHarness.SendJoinAsync(
            client, "user-legacy", protocolVersion: WireProtocol.ProtocolVersionUnversioned);

        Assert.False(resp.Ok);
        Assert.Equal(WireProtocol.ReasonProtocolVersionMismatch, resp.Error);
        Assert.Equal(1, metrics.HandshakesRejectedProtocolVersion);
        Assert.Equal(0, metrics.UnversionedHandshakes);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>Raising the floor must not become a blanket refusal.</summary>
    [Fact]
    public async Task MatchingVersion_SurvivesTheRequiredFlip()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics, minProtocolVersion: 1);

        using var client = await h.ConnectAsync();
        var resp = await HardeningHarness.SendJoinAsync(
            client, "user-modern", protocolVersion: WireProtocol.ProtocolVersion);

        Assert.True(resp.Ok, resp.Error);
        Assert.Equal(1, metrics.PlayersOnline);
    }

    /// <summary>
    /// The version check precedes JWT verification, so a client that is both skewed AND
    /// unauthenticated is told about the version. The schema fault is the actionable one.
    /// </summary>
    [Fact]
    public async Task VersionRefusal_OutranksAnInvalidToken()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        var resp = await HardeningHarness.SendJoinBadTokenAsync(
            client, protocolVersion: WireProtocol.ProtocolVersion + 1);

        Assert.False(resp.Ok);
        Assert.Equal(WireProtocol.ReasonProtocolVersionMismatch, resp.Error);
    }

    /// <summary>
    /// The decision table itself, including the case a socket test cannot reach: version 0
    /// must never be readable as "version zero".
    /// </summary>
    [Theory]
    // peer,                                          min, expected
    [InlineData(WireProtocol.ProtocolVersion, 0u, WireProtocol.VersionVerdict.Accepted)]
    [InlineData(WireProtocol.ProtocolVersion, 1u, WireProtocol.VersionVerdict.Accepted)]
    [InlineData(0u, 0u, WireProtocol.VersionVerdict.AcceptedUnversioned)]
    [InlineData(0u, 1u, WireProtocol.VersionVerdict.Refused)]
    [InlineData(WireProtocol.ProtocolVersion + 1, 0u, WireProtocol.VersionVerdict.Refused)]
    [InlineData(9999u, 0u, WireProtocol.VersionVerdict.Refused)]
    public void CheckProtocolVersion_DecisionTable(
        uint peerVersion, uint minVersion, WireProtocol.VersionVerdict expected)
    {
        Assert.Equal(expected, WireProtocol.CheckProtocolVersion(peerVersion, minVersion));
    }

    /// <summary>
    /// Version numbering starts at 1 so that "absent" and "version zero" cannot collide —
    /// the trap documented at length on <c>EntitySnapshot.speed</c>.
    /// </summary>
    [Fact]
    public void VersionNumbering_StartsAtOne()
    {
        Assert.Equal(0u, WireProtocol.ProtocolVersionUnversioned);
        Assert.True(WireProtocol.ProtocolVersion >= 1,
            "a version of 0 would be indistinguishable from an elided proto3 field");
    }

    /// <summary>
    /// The reason is a bare machine-readable token, matching the convention set by
    /// <c>duplicate_login</c> and <c>server_shutdown</c>. Clients branch on this string.
    /// </summary>
    [Fact]
    public void MismatchReason_IsABareToken()
    {
        Assert.Equal("protocol_version_mismatch", WireProtocol.ReasonProtocolVersionMismatch);
        Assert.DoesNotContain(" ", WireProtocol.ReasonProtocolVersionMismatch);
    }
}
