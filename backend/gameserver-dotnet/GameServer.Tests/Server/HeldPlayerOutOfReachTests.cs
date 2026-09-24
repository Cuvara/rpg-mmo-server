using GameServer.Observability;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// The lifecycle of <c>PlayerTag.Linkdead</c> through a real server: set when a dropped
/// connection turns into a reconnect hold, cleared when the player reattaches.
///
/// <para>The enemy side -- that a held player is neither attacked nor killed and moved to the
/// spawn point -- is proved in <c>EnemyAttackTests</c>. This file proves the flag is actually
/// raised and lowered by the join/disconnect path, which is the half a unit test on the
/// enemy systems cannot see: a filter on a flag nobody sets passes every enemy test and
/// protects no one.</para>
/// </summary>
public class HeldPlayerOutOfReachTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    [Fact]
    public async Task ADroppedPlayer_IsLinkdeadForTheHold_AndNotOnceTheyAreBack()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, capacity: 4, hold: TimeSpan.FromSeconds(10));

        var first = await h.JoinAsync("user-a");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1, what: "player online");
        Assert.False(h.Server.IsLinkdead("user-a"), "a connected player must be in reach");

        first.Dispose();
        await h.WaitForAsync(() => metrics.PlayersOnline == 0 && h.Server.PendingHolds == 1,
            what: "player held");
        Assert.True(h.Server.IsLinkdead("user-a"),
            "the hold started but the entity is still a target -- enemies can kill a player for losing their connection");

        using var again = await h.JoinAsync("user-a");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1 && h.Server.PendingHolds == 0,
            what: "player back, hold cancelled");
        Assert.False(h.Server.IsLinkdead("user-a"),
            "the player reattached but is still out of reach -- they would be invulnerable for the rest of the session");
    }
}
