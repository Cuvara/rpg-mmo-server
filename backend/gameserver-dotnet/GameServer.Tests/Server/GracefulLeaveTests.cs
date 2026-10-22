using GameServer.Net;
using GameServer.Observability;
using GameServer.Persistence;
using RpgMmo.Wire.V1;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// A GRACEFUL leave (the client sends <c>disconnect</c>) saves and releases the player's entity
/// at once. A DROP (dead link, idle or ping timeout, a killed process) keeps the reconnect hold
/// (<c>ServerOptions.HoldTtl</c>, 30 s on a map, 60 s in a dungeon).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> The Netcode client sends <c>disconnect</c> on a user-chosen leave
/// precisely so that "the server saves and releases the entity now rather than at the end of
/// its 30 s hold" (<c>NetworkClient.BeginOperation</c>). It never reconnects after one. The
/// server used to answer that frame with a plain close, which is the drop path. The player
/// then stayed in the world for the whole hold, visible to everyone and indistinguishable
/// from one standing still, after they had chosen to leave (test lab S08G, T35).</para>
///
/// <para>The hold is still the right answer for a drop, which is the case it exists for
/// (<c>HeldPlayerOutOfReachTests</c>). It is pinned again here beside the graceful case, so a
/// fix that releases every disconnect immediately fails as well.</para>
/// </remarks>
public class GracefulLeaveTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Well under <see cref="Hold"/>: a removal that only happens when the hold expires
    /// cannot pass this.
    /// </summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(3);

    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    private static async Task SendDisconnectAsync(Stream stream, string reason)
    {
        var env = WireProtocol.NewEnvelope(MsgType.Disconnect, new DisconnectMessage { Reason = reason }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(env));
    }

    [Fact]
    public async Task AGracefulLeave_SavesAndReleasesTheEntityAtOnce_WithoutAHold()
    {
        using var metrics = NewMetrics();
        var store = new MemoryPlayerStore();
        await using var h = await HardeningHarness.StartAsync(metrics, capacity: 4, hold: Hold, playerStore: store);

        using var client = await h.JoinAsync("user-leaver");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1 && h.Server.EntityCount == 1, what: "player in the world");

        await SendDisconnectAsync(client.GetStream(), "quit");

        // Removed, not held: no pending hold and no entity, long before the hold could expire.
        await h.WaitForAsync(() => metrics.PlayersOnline == 0 && h.Server.PendingHolds == 0 && h.Server.EntityCount == 0,
            timeout: Prompt, what: "entity released after a graceful leave (not held for the reconnect window)");

        // Saved before it left the world: the save is what the client's disconnect asks for.
        Assert.NotNull(await store.LoadPlayerAsync("user-leaver", CancellationToken.None));
        Assert.Equal(0, h.Server.Connections);
    }

    [Fact]
    public async Task ADrop_StillGetsTheReconnectHold()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics, capacity: 4, hold: Hold);

        var client = await h.JoinAsync("user-dropper");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1, what: "player online");

        // A close with no disconnect frame: the drop path.
        client.Dispose();
        await h.WaitForAsync(() => metrics.PlayersOnline == 0 && h.Server.PendingHolds == 1, what: "player held");

        Assert.Equal(1, h.Server.EntityCount);
        Assert.True(h.Server.IsLinkdead("user-dropper"), "a held player must be out of reach");
    }

    /// <summary>
    /// Leaving and coming straight back starts a fresh session. Nothing from the leave may
    /// remove the new entity afterwards: there is no pending hold left to expire.
    /// </summary>
    [Fact]
    public async Task AGracefulLeave_ThenARejoin_IsAFreshSessionThatStays()
    {
        using var metrics = NewMetrics();
        var store = new MemoryPlayerStore();
        await using var h = await HardeningHarness.StartAsync(metrics, capacity: 4, hold: TimeSpan.FromSeconds(1), playerStore: store);

        using (var first = await h.JoinAsync("user-back"))
        {
            await h.WaitForAsync(() => metrics.PlayersOnline == 1, what: "first session online");
            await SendDisconnectAsync(first.GetStream(), "quit");
            await h.WaitForAsync(() => h.Server.EntityCount == 0 && h.Server.PendingHolds == 0,
                timeout: Prompt, what: "first session released");
        }

        using var again = await h.JoinAsync("user-back");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1 && h.Server.EntityCount == 1, what: "second session in the world");

        // Longer than the configured hold: a stale removal from the leave would land in here.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(0, h.Server.PendingHolds);
        Assert.False(h.Server.IsLinkdead("user-back"), "the rejoined player must be in reach");
    }
}
