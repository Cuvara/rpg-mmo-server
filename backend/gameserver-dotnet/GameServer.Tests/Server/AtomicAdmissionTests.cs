using System.Net.Sockets;
using GameServer.Observability;
using GameServer.Persistence;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// Capacity admission over the socket (workspace audit, "make capacity admission atomic").
/// The old check read <c>ConnectionManager.Count</c>, awaited the player-store load, then
/// added the connection — so every join in flight during the load saw the same free slot.
/// A store that takes a while to answer is exactly what makes the window wide enough to
/// hit deterministically.
/// </summary>
public class AtomicAdmissionTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    /// <summary>Answers loads after a delay, so concurrent joins overlap inside the await.</summary>
    private sealed class SlowPlayerStore(TimeSpan delay) : IPlayerStore
    {
        public int Loads;

        public async Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
        {
            Interlocked.Increment(ref Loads);
            await Task.Delay(delay, ct);
            return null;
        }

        public Task SavePlayerAsync(PlayerState state, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentJoins_AgainstCapacityC_AdmitExactlyC()
    {
        const int capacity = 3;
        const int joiners = 12;
        using var metrics = NewMetrics();
        var store = new SlowPlayerStore(TimeSpan.FromMilliseconds(400));
        await using var h = await HardeningHarness.StartAsync(metrics, capacity: capacity, playerStore: store);

        // Connect everyone first so the joins themselves land as close together as the
        // loopback allows, then fire them all at once.
        var clients = new List<TcpClient>();
        try
        {
            for (int i = 0; i < joiners; i++) clients.Add(await h.ConnectAsync());

            var replies = await Task.WhenAll(clients.Select((c, i) =>
                HardeningHarness.SendJoinAsync(c, $"user-{i}", TimeSpan.FromSeconds(20))));

            int ok = replies.Count(r => r.Ok);
            int full = replies.Count(r => !r.Ok && r.Error == "Server is full");

            // Expected: exactly C admitted, everyone else refused as full — no other error.
            Assert.Equal(capacity, ok);
            Assert.Equal(joiners - capacity, full);
            Assert.Equal(capacity, metrics.PlayersOnline);
            Assert.Equal(capacity, h.Server.EntityCount);

            // Every refused join released its reservation; every admitted one committed.
            Assert.Equal(0, h.Server.PendingReservations);

            // The refusal happens BEFORE the load, so the store never saw the losers —
            // the slot is held across the await, not checked after it.
            Assert.Equal(capacity, store.Loads);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    /// <summary>
    /// The fast-rejoin case at capacity 1: the user's first socket is still open (a
    /// half-dead connection the heartbeat has not noticed) when the same user joins again.
    /// That is a replacement, and it must not be refused as a second player.
    /// </summary>
    [Fact]
    public async Task RejoinOverLiveConnection_AtCapacity_ReplacesInsteadOfAdding()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, capacity: 1, hold: TimeSpan.FromSeconds(10));

        var stale = await h.JoinAsync("user-a");
        try
        {
            // Full for anyone else.
            using var other = await h.ConnectAsync();
            var refused = await HardeningHarness.SendJoinAsync(other, "user-b");
            Assert.False(refused.Ok);
            Assert.Equal("Server is full", refused.Error);

            // Not full for the same user.
            using var fresh = await h.ConnectAsync();
            var replaced = await HardeningHarness.SendJoinAsync(fresh, "user-a");
            Assert.True(replaced.Ok, replaced.Error);

            await h.WaitForAsync(() => metrics.PlayersOnline == 1 && h.Server.PendingHolds == 0,
                what: "one player online, no hold");
            Assert.Equal(1, h.Server.EntityCount);
            Assert.Equal(0, h.Server.PendingReservations);

            // The fresh connection is the live one: the server still answers on it.
            var stream = fresh.GetStream();
            var ping = WireProtocol.NewEnvelope(MsgType.Ping, new PingMessage { Timestamp = 7 }, WireEncoding.Json);
            await stream.WriteAsync(WireProtocol.Encode(ping));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.NotNull(await WireProtocol.DecodeAsync(stream, cts.Token));
        }
        finally
        {
            stale.Dispose();
        }
    }

    /// <summary>
    /// Reconnect inside the hold window at capacity 1: the held player is not an occupant
    /// (the hold keeps the entity, not the slot), so the rejoin is admitted and reattaches
    /// the held entity — one entity, one player, one slot.
    /// </summary>
    [Fact]
    public async Task RejoinDuringHold_AtCapacity_ConsumesOneSlotAndReattaches()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, capacity: 1, hold: TimeSpan.FromSeconds(10));

        var first = await h.JoinAsync("user-a");
        first.Dispose();
        await h.WaitForAsync(() => metrics.PlayersOnline == 0 && h.Server.PendingHolds == 1,
            what: "player held");
        Assert.Equal(1, h.Server.EntityCount);

        using var again = await h.JoinAsync("user-a");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1 && h.Server.PendingHolds == 0,
            what: "player back, hold cancelled");
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(0, h.Server.PendingReservations);

        // And the slot is genuinely the only one: a second user is still refused.
        using var other = await h.ConnectAsync();
        var refused = await HardeningHarness.SendJoinAsync(other, "user-b");
        Assert.False(refused.Ok);
    }

    /// <summary>
    /// A join that fails after reserving (here: the client vanishes mid-load) must give its
    /// slot back, or the server slowly fills with ghosts nobody can see.
    /// </summary>
    [Fact]
    public async Task AbortedJoin_ReleasesItsReservation()
    {
        using var metrics = NewMetrics();
        var store = new SlowPlayerStore(TimeSpan.FromMilliseconds(500));
        await using var h = await HardeningHarness.StartAsync(metrics, capacity: 1, playerStore: store);

        var vanishing = await h.ConnectAsync();
        var join = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest
            {
                Token = TestHelpers.CreateTestJwt("user-gone", HardeningHarness.ServerId, HardeningHarness.JwtSecret)
            },
            WireEncoding.Json);
        await vanishing.GetStream().WriteAsync(WireProtocol.Encode(join));
        await h.WaitForAsync(() => h.Server.PendingReservations == 1, what: "reservation taken");
        vanishing.Dispose();   // gone while the load is still in flight

        // The join completes against a dead socket, tears down, and releases the slot.
        await h.WaitForAsync(() => h.Server.PendingReservations == 0 && metrics.PlayersOnline == 0,
            what: "reservation released");

        using var next = await h.JoinAsync("user-next");
        Assert.Equal(1, metrics.PlayersOnline);
    }
}
