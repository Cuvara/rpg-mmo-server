using GameServer.Net;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// Workspace audit F04: authenticated input and transfer work was unbounded. Every decoded
/// input was appended to the world's pending queue and every <c>MsgTransferMap</c> started
/// its own task, so one connection could grow the queue without limit and run two
/// save-and-teardowns against one entity. The unit tests pin the ingest rules on
/// <see cref="EcsWorld"/>; the live tests prove them through the socket, with a second,
/// well-behaved player whose inputs must still be acknowledged while the first floods.
/// </summary>
public class InputIngestionTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    private static EcsWorld NewWorld(params string[] players)
    {
        var world = new EcsWorld(1);
        foreach (var p in players) world.AddEntity(TestHelpers.CreatePlayer(p));
        return world;
    }

    private static InputData Move(ulong tick) => new(tick, 1f, 0f, null);
    private static InputData Attack(ulong tick) => new(tick, 0f, 0f, "target");

    // ── Unit: ingest rules ──────────────────────────────────────────────────

    /// <summary>A movement flood occupies one queue slot, and the slot holds the newest input.</summary>
    [Fact]
    public void MovementOnly_CoalescesInPlace_NewestWins()
    {
        var world = NewWorld("a");
        var ingress = new InputIngress();

        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Move(1), ingress));
        for (ulong t = 2; t <= 1000; t++)
        {
            Assert.Equal(InputIngestResult.Coalesced, world.PushInput("a", Move(t), ingress));
        }

        Assert.Equal(1, world.PendingInputCount);
        var drained = world.DrainInputs();
        Assert.Single(drained);
        Assert.Equal(1000UL, drained[0].Input.Tick);
    }

    /// <summary>
    /// Attacks are edge-triggered: never replaced, never replacing, and they end the run of
    /// replaceable movement behind them so the queue keeps the client's order.
    /// </summary>
    [Fact]
    public void Attacks_StayDistinct_AndPreserveOrder()
    {
        var world = NewWorld("a");
        var ingress = new InputIngress();

        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Move(1), ingress));
        Assert.Equal(InputIngestResult.Coalesced, world.PushInput("a", Move(2), ingress));
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(3), ingress));
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Move(4), ingress));   // after an attack: new slot
        Assert.Equal(InputIngestResult.Coalesced, world.PushInput("a", Move(5), ingress));
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(6), ingress));
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(7), ingress));

        var drained = world.DrainInputs();
        Assert.Equal(new ulong[] { 2, 3, 5, 6, 7 }, drained.Select(d => d.Input.Tick).ToArray());
        Assert.Equal(new[] { false, true, false, true, true },
            drained.Select(d => !string.IsNullOrEmpty(d.Input.AttackTargetId)).ToArray());
    }

    /// <summary>Two connections never coalesce into each other.</summary>
    [Fact]
    public void Coalescing_IsPerConnection()
    {
        var world = NewWorld("a", "b");
        var ia = new InputIngress();
        var ib = new InputIngress();

        world.PushInput("a", Move(1), ia);
        world.PushInput("b", Move(1), ib);
        Assert.Equal(InputIngestResult.Coalesced, world.PushInput("a", Move(2), ia));
        Assert.Equal(InputIngestResult.Coalesced, world.PushInput("b", Move(2), ib));

        var drained = world.DrainInputs();
        Assert.Equal(2, drained.Count);
        Assert.Contains(drained, d => d.UserId == "a" && d.Input.Tick == 2);
        Assert.Contains(drained, d => d.UserId == "b" && d.Input.Tick == 2);
    }

    /// <summary>Edge-triggered inputs beyond the per-connection budget are dropped, and the budget resets on drain.</summary>
    [Fact]
    public void ConnectionBudget_BoundsAttacks_AndResetsOnDrain()
    {
        var world = NewWorld("a");
        world.ConfigureInputBounds(maxInputsPerConnection: 4, maxPendingInputs: 0);
        var ingress = new InputIngress();

        ulong t = 1;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(t++), ingress));
        }
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(InputIngestResult.DroppedConnectionBudget, world.PushInput("a", Attack(t++), ingress));
        }
        Assert.Equal(4, world.PendingInputCount);

        // Budget exhausted with no replaceable movement slot open (the newest entry is an
        // attack): a movement needs a new slot and is budgeted like everything else.
        Assert.Equal(InputIngestResult.DroppedConnectionBudget, world.PushInput("a", Move(t++), ingress));

        world.DrainInputs();
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(t++), ingress));
        Assert.Equal(1, world.PendingInputCount);
    }

    /// <summary>The world-wide cap applies across connections; coalescing still works when it is hit.</summary>
    [Fact]
    public void WorldWideBound_DropsBeyondTheCap_ButStillCoalesces()
    {
        var world = NewWorld("a", "b");
        world.ConfigureInputBounds(maxInputsPerConnection: 100, maxPendingInputs: 3);
        var ia = new InputIngress();
        var ib = new InputIngress();

        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(1), ia));
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("a", Attack(2), ia));
        Assert.Equal(InputIngestResult.Enqueued, world.PushInput("b", Move(1), ib));
        Assert.Equal(InputIngestResult.DroppedQueueFull, world.PushInput("a", Attack(3), ia));
        Assert.Equal(InputIngestResult.DroppedQueueFull, world.PushInput("b", Attack(2), ib));

        // b's movement slot is already in the queue, so newer movement replaces it for free.
        Assert.Equal(InputIngestResult.Coalesced, world.PushInput("b", Move(3), ib));
        Assert.Equal(3, world.PendingInputCount);

        // The unbounded (no-ingress) path honours the world-wide cap too.
        Assert.Equal(InputIngestResult.DroppedQueueFull, world.PushInput("a", Attack(9), null));
    }

    // ── Live: through the socket ────────────────────────────────────────────

    /// <summary>
    /// One connection floods; the queue stays inside its bound the whole time, and a second
    /// connection's input is still acknowledged in the snapshot stream.
    /// </summary>
    [Fact]
    public async Task InputFlood_FromOneConnection_StaysBounded_AndOthersStillAcked()
    {
        const int perConnection = 8;
        const int worldWide = 32;
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, capacity: 4, maxInputsPerConnection: perConnection, maxPendingInputs: worldWide);

        using var flooder = await h.JoinAsync("user-flood");
        using var honest = await h.JoinAsync("user-honest");

        // Sample the queue depth while the flood runs. Expected: never above the
        // world-wide bound (32); without ingest bounds a burst of thousands per 50ms tick
        // would read in the hundreds.
        int maxObserved = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                maxObserved = Math.Max(maxObserved, h.Server.PendingInputCount);
                await Task.Yield();
            }
        });

        var floodStream = flooder.GetStream();
        const int packets = 6000;
        for (int i = 1; i <= packets; i++)
        {
            // Alternate movement (coalesces) and attacks (budgeted) so both bounds are hit.
            if ((i & 1) == 0)
                await HardeningHarness.SendInputAsync(floodStream, (ulong)i, 1f, 0f);
            else
                await HardeningHarness.SendInputAsync(floodStream, (ulong)i, 0f, 0f, "nobody");
        }
        await floodStream.FlushAsync();

        // The honest player sends one input and reads snapshots until it is acked.
        const ulong honestTick = 5;
        var honestStream = honest.GetStream();
        await HardeningHarness.SendInputAsync(honestStream, honestTick, 0f, 1f);
        await honestStream.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        ulong lastAck = 0;
        while (lastAck < honestTick)
        {
            var env = await WireProtocol.DecodeAsync(honestStream, cts.Token);
            Assert.NotNull(env);
            if ((MsgType)env!.Type != MsgType.Snapshot) continue;
            lastAck = WireProtocol.GetPayload<SnapshotMessage>(env).AckTick;
        }
        Assert.Equal(honestTick, lastAck);

        sampling.Cancel();
        await sampler;

        Assert.True(maxObserved <= worldWide,
            $"pending input queue reached {maxObserved}, bound is {worldWide}");
        Assert.True(metrics.InputsCoalesced > 0, "movement flood was not coalesced");
        Assert.True(metrics.InputsDropped > 0, "attack flood was not budgeted");
        Assert.Equal(metrics.InputsDroppedConnectionBudget + metrics.InputsDroppedQueueFull, metrics.InputsDropped);
    }

    /// <summary>Blocks every save until released, so a transfer stays in flight on demand.</summary>
    private sealed class BlockingPlayerStore : IPlayerStore
    {
        public readonly TaskCompletionSource SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
            => Task.FromResult<PlayerState?>(null);

        public async Task SavePlayerAsync(PlayerState state, CancellationToken ct)
        {
            SaveEntered.TrySetResult();
            await Release.Task;
        }
    }

    /// <summary>A second <c>MsgTransferMap</c> while the first is still saving is refused, counted, and the first completes.</summary>
    [Fact]
    public async Task SecondConcurrentTransfer_IsRejected()
    {
        using var metrics = NewMetrics();
        var store = new BlockingPlayerStore();
        await using var h = await HardeningHarness.StartAsync(metrics, playerStore: store);

        using var client = await h.JoinAsync("user-mover");
        var stream = client.GetStream();

        var req = WireProtocol.Encode(WireProtocol.NewEnvelope(
            MsgType.TransferMap, new TransferMapRequest { MapId = "map_elsewhere" }, WireEncoding.Json));
        await stream.WriteAsync(req);
        await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));   // first transfer is now in flight
        await stream.WriteAsync(req);

        // The refusal arrives first: it never waits on the save.
        var refused = WireProtocol.GetPayload<TransferMapResponse>(
            await HardeningHarness.ReadUntilAsync(stream, MsgType.TransferMapResp));
        Assert.False(refused.Ok);
        Assert.Equal("transfer already in progress", refused.Error);
        Assert.Equal(1, metrics.TransfersRejected);

        // Release the save: the first transfer completes normally.
        store.Release.TrySetResult();
        var accepted = WireProtocol.GetPayload<TransferMapResponse>(
            await HardeningHarness.ReadUntilAsync(stream, MsgType.TransferMapResp));
        Assert.True(accepted.Ok, accepted.Error);

        await h.WaitForAsync(() => h.Server.EntityCount == 0 && metrics.PlayersOnline == 0,
            what: "transferred player gone");
        Assert.Equal(1, metrics.TransfersRejected);
    }
}
