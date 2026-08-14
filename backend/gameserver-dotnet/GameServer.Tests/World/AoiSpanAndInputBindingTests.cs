using GameServer.Input;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.World;

/// <summary>
/// Two things stage 1 changed about the per-tick data path, and the contracts they have
/// to keep.
///
/// <para><b>The AOI scan fills a caller-owned buffer.</b> It used to allocate a
/// <c>List&lt;EntityState&gt;</c> per connected client per tick. The replacement must
/// implement the <i>same</i> overflow contract `Shared.GameLogic`'s
/// <c>AoiLogic.GetNearbyEntities</c> already defines — count, do not saturate — because
/// two AOI functions in one server with two different overflow contracts is worse than
/// either contract on its own.</para>
///
/// <para><b>Input is bound to its entity at ingest.</b> The user id is resolved on the
/// network thread, so the tick loop never hashes a string. The handle can go stale
/// between ingest and drain, and these tests pin that the stale case resolves the way
/// the old string lookup did rather than silently dropping input.</para>
/// </summary>
public class AoiSpanAndInputBindingTests
{
    private const float Radius = GameConstants.DefaultAoiRadius;

    private static EcsWorld WorldWith(params EntityState[] entities)
    {
        var world = new EcsWorld();
        foreach (var e in entities) world.AddEntity(e);
        return world;
    }

    // ── AOI: overflow contract ───────────────────────────────────────────────

    [Fact]
    public void SpanScan_ExactlyFullBuffer_IsNotReportedAsOverflow()
    {
        using var world = WorldWith(
            TestHelpers.CreatePlayer("a", 0, 0),
            TestHelpers.CreatePlayer("b", 1, 1),
            TestHelpers.CreatePlayer("c", 2, 2));

        var buffer = new EntityState[3];
        int count = world.GetEntitiesInRange(new Vec2(0, 0), Radius, buffer);

        Assert.Equal(3, count);
        Assert.False(count > buffer.Length, "exactly full must not read as overflow");
    }

    [Fact]
    public void SpanScan_BufferTooSmall_ReturnsNeededCount_AndFillsWhatFits()
    {
        using var world = WorldWith(
            TestHelpers.CreatePlayer("a", 0, 0),
            TestHelpers.CreatePlayer("b", 1, 1),
            TestHelpers.CreatePlayer("c", 2, 2),
            TestHelpers.CreatePlayer("d", 3, 3));

        var small = new EntityState[2];
        int needed = world.GetEntitiesInRange(new Vec2(0, 0), Radius, small);

        // Count, do not saturate: the return value is the size the buffer needed to be.
        Assert.Equal(4, needed);
        Assert.True(needed > small.Length);
        Assert.All(small, e => Assert.False(string.IsNullOrEmpty(e.Id), "prefix must be filled"));
    }

    [Fact]
    public void SpanScan_EmptyDestination_StillCounts()
    {
        using var world = WorldWith(
            TestHelpers.CreatePlayer("a", 0, 0),
            TestHelpers.CreatePlayer("b", 1, 1));

        Assert.Equal(2, world.GetEntitiesInRange(new Vec2(0, 0), Radius, Span<EntityState>.Empty));
    }

    [Fact]
    public void SpanScan_ResizeAndRetry_SucceedsInOneRetry()
    {
        using var world = WorldWith(
            TestHelpers.CreatePlayer("a", 0, 0),
            TestHelpers.CreatePlayer("b", 1, 1),
            TestHelpers.CreatePlayer("c", 2, 2));

        int needed = world.GetEntitiesInRange(new Vec2(0, 0), Radius, new EntityState[1]);
        var right = new EntityState[needed];
        int count = world.GetEntitiesInRange(new Vec2(0, 0), Radius, right);

        Assert.Equal(needed, count);
        Assert.Equal(3, count);
    }

    /// <summary>
    /// The overflow contract here must be the one `Shared.GameLogic` already publishes.
    /// Cross-checked directly against it rather than restated, so the two cannot drift.
    /// </summary>
    [Fact]
    public void SpanScan_OverflowContract_MatchesAoiLogic()
    {
        var entities = new[]
        {
            TestHelpers.CreatePlayer("a", 0, 0),
            TestHelpers.CreatePlayer("b", 1, 1),
            TestHelpers.CreatePlayer("c", 2, 2),
            TestHelpers.CreateMob("m", Radius * 4, 0), // out of range on both sides
        };
        using var world = WorldWith(entities);

        foreach (int size in new[] { 0, 1, 2, 3, 4 })
        {
            int fromWorld = world.GetEntitiesInRange(new Vec2(0, 0), Radius, new EntityState[size]);
            int fromShared = AoiLogic.GetNearbyEntities(
                entities, new Vec2(0, 0), Radius, new EntityState[size]);

            Assert.Equal(fromShared, fromWorld);
        }
    }

    /// <summary>
    /// The span form and the list form must return the same entities in the same order —
    /// the snapshot encoder's delta bookkeeping is order-sensitive, so a reordering here
    /// would be a wire change.
    /// </summary>
    [Fact]
    public void SpanScan_MatchesListScan_ElementForElement()
    {
        using var world = new EcsWorld();
        for (int i = 0; i < 12; i++) world.AddEntity(TestHelpers.CreatePlayer($"p{i}", i, 0));
        for (int i = 0; i < 5; i++) world.AddEntity(TestHelpers.CreateMob($"m{i}", -i, 1));
        world.AddEntity(TestHelpers.CreateMob("far", Radius * 10, Radius * 10));

        var center = new Vec2(0, 0);
        List<EntityState> expected = world.GetEntitiesInRange(center, Radius);

        var buffer = new EntityState[expected.Count];
        int count = world.GetEntitiesInRange(center, Radius, buffer);

        Assert.Equal(expected.Count, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(expected[i].Id, buffer[i].Id);
            Assert.Equal(expected[i].Position.X, buffer[i].Position.X);
            Assert.Equal(expected[i].Position.Y, buffer[i].Position.Y);
            Assert.Equal(expected[i].Hp, buffer[i].Hp);
        }
    }

    [Fact]
    public void SpanScan_EmptyWorld_ReturnsZero()
    {
        using var world = new EcsWorld();
        Assert.Equal(0, world.GetEntitiesInRange(new Vec2(0, 0), Radius, new EntityState[4]));
    }

    // ── Input binding at ingest ──────────────────────────────────────────────

    [Fact]
    public void PushInput_BindsTheEntityHandleAtIngest()
    {
        using var world = WorldWith(TestHelpers.CreatePlayer("p1"));
        world.PushInput("p1", new InputData(1, 1f, 0f, null));

        var drained = world.DrainInputs();

        Assert.Single(drained);
        Assert.True(drained[0].Handle.IsValid, "the id must be resolved on the network thread");
        Assert.Equal("p1", drained[0].UserId);
    }

    [Fact]
    public void PushInput_UnknownEntity_QueuesAnInvalidHandleButKeepsTheId()
    {
        using var world = new EcsWorld();
        world.PushInput("ghost", new InputData(1, 1f, 0f, null));

        var drained = world.DrainInputs();

        Assert.Single(drained);
        Assert.False(drained[0].Handle.IsValid);
        Assert.Equal("ghost", drained[0].UserId);
    }

    /// <summary>
    /// The reconnect-inside-the-hold-window case: input is queued, the entity is then
    /// destroyed and a new one created for the same user id. The queued handle is stale,
    /// and `RebindStale` must resolve it to the *new* entity — which is what the old
    /// string lookup did, because it resolved at process time.
    /// </summary>
    [Fact]
    public void RebindStale_ReboundsInputQueuedBeforeARespawn()
    {
        using var world = WorldWith(TestHelpers.CreatePlayer("p1", x: 0, y: 0, speed: 5f));
        world.PushInput("p1", new InputData(1, 1f, 0f, null));

        world.RemoveEntity("p1");
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 100, y: 100, speed: 5f));

        var drained = world.DrainInputs();
        Assert.False(world.IsAlive(drained[0].Handle), "handle should be stale");

        world.RebindStale(drained);

        Assert.True(drained[0].Handle.IsValid);
        Assert.True(world.IsAlive(drained[0].Handle));

        // And the rebound handle addresses the new entity, not a ghost of the old one.
        var handler = new InputHandler(world, NullLogger.Instance, null, 15, MapBounds.Default);
        var rebound = drained[0];
        world.UpdateComponents(w => handler.ProcessInput(w, in rebound, 1));

        Assert.True(world.GetEntity("p1")!.Value.Position.X > 100f);
    }

    [Fact]
    public void RebindStale_LeavesUnresolvableInputsInvalid()
    {
        using var world = WorldWith(TestHelpers.CreatePlayer("p1"));
        world.PushInput("p1", new InputData(1, 1f, 0f, null));
        world.RemoveEntity("p1");

        var drained = world.DrainInputs();
        world.RebindStale(drained);

        Assert.False(drained[0].Handle.IsValid);
    }

    [Fact]
    public void DrainInputs_IntoCallerList_ClearsItFirstAndEmptiesTheQueue()
    {
        using var world = WorldWith(TestHelpers.CreatePlayer("p1"));
        var reused = new List<PendingInput> { default, default };

        world.PushInput("p1", new InputData(1, 1f, 0f, null));
        world.DrainInputs(reused);
        Assert.Single(reused);

        world.DrainInputs(reused);
        Assert.Empty(reused);
    }

    /// <summary>
    /// Movement coalescing is now keyed by handle, not by user id. Two entities must
    /// still be grouped apart, and repeated inputs for one entity must still collapse to
    /// a single integration step — the anti-spam property, verified end to end through
    /// the handle path.
    /// </summary>
    [Fact]
    public void HandleKeyedCoalescing_SeparatesEntitiesAndCollapsesSpam()
    {
        using var spam = WorldWith(TestHelpers.CreatePlayer("p1", speed: 5f));
        using var fair = WorldWith(TestHelpers.CreatePlayer("p1", speed: 5f));

        for (int i = 0; i < 10; i++) spam.PushInput("p1", new InputData((ulong)(i + 1), 1f, 0f, null));
        fair.PushInput("p1", new InputData(1, 1f, 0f, null));

        TickOnce(spam);
        TickOnce(fair);

        Assert.Equal(fair.GetEntity("p1")!.Value.Position.X, spam.GetEntity("p1")!.Value.Position.X, precision: 5);
        Assert.Equal(10UL, spam.GetEntity("p1")!.Value.LastInputTick);
    }

    [Fact]
    public void HandleKeyedCoalescing_TwoEntitiesEachMoveOnce()
    {
        using var world = WorldWith(
            TestHelpers.CreatePlayer("a", 0, 0, speed: 5f),
            TestHelpers.CreatePlayer("b", 0, 0, speed: 5f));

        world.PushInput("a", new InputData(1, 1f, 0f, null));
        world.PushInput("b", new InputData(1, 1f, 0f, null));

        TickOnce(world);

        float ax = world.GetEntity("a")!.Value.Position.X;
        float bx = world.GetEntity("b")!.Value.Position.X;
        Assert.True(ax > 0f);
        Assert.Equal(ax, bx, precision: 5);
    }

    /// <summary>
    /// Minimal stand-in for the input phase of <c>TickLoop.TickOnce</c>: rebind, coalesce
    /// by handle, process. Duplicated here rather than driving a whole TickLoop because
    /// a TickLoop needs a ConnectionManager and real sockets.
    /// </summary>
    private static void TickOnce(EcsWorld world, ulong tick = 1)
    {
        var handler = new InputHandler(world, NullLogger.Instance, null, 15, MapBounds.Default);
        var inputs = new List<PendingInput>();
        world.DrainInputs(inputs);
        world.RebindStale(inputs);

        var newest = new Dictionary<EntityHandle, int>();
        for (int i = 0; i < inputs.Count; i++)
        {
            if (!inputs[i].Handle.IsValid) continue;
            if (!newest.TryGetValue(inputs[i].Handle, out int best) ||
                inputs[i].Input.Tick >= inputs[best].Input.Tick)
            {
                newest[inputs[i].Handle] = i;
            }
        }

        world.UpdateComponents(w =>
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                if (!inputs[i].Handle.IsValid) continue;
                var pi = inputs[i];
                handler.ProcessInput(w, in pi, tick, newest[pi.Handle] == i);
            }
        });
    }
}
