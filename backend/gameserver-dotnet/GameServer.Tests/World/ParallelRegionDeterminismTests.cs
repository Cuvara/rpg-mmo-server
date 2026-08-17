using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.World;

/// <summary>
/// The determinism harness for <see cref="EcsWorld.UpdateComponentsParallel"/>.
///
/// <para><b>Why this suite exists.</b> ADR-12 records two preconditions for parallel
/// simulation and says neither may be left to be discovered by the change that first
/// spawns a worker. Both are now fixed — the structural queue is per worker slot, and
/// the deferral decision is a property of the world rather than of the calling thread —
/// but a fix to a concurrency hazard that is never run concurrently is a claim, not a
/// result. Every test here starts real threads.</para>
///
/// <para><b>What determinism means here.</b> Structural ops are replayed by calling
/// <c>Arch.Create</c>, so replay order sets creation order, which sets chunk layout,
/// which sets iteration order, which sets the order floats accumulate downstream. The
/// observable that carries all of that is the order entities come back out of a scan —
/// so that ordered sequence, not just set membership, is what these assert on. A shared
/// structural queue would pass a set comparison and fail this one.</para>
/// </summary>
public class ParallelRegionDeterminismTests
{
    private const int Slots = 4;

    /// <summary>
    /// Ordered digest of the world: ids in iteration order, with positions. Order is the
    /// point — comparing an unordered set would not detect the failure this suite exists
    /// to catch.
    /// </summary>
    private static string Digest(EcsWorld world)
    {
        List<EntityState> all = world.GetEntitiesInRange(new Vec2(0, 0), float.MaxValue);
        var sb = new StringBuilder();
        foreach (EntityState e in all)
        {
            sb.Append(e.Id).Append(':')
              .Append(BitConverter.SingleToInt32Bits(e.Position.X)).Append(',')
              .Append(BitConverter.SingleToInt32Bits(e.Position.Y)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Each worker spawns a few entities, with a stagger that makes the completion order
    /// the opposite of the slot order.
    /// </summary>
    private static void SpawnPerWorker(WorldWriter writer, int slot, int perWorker)
    {
        // Worker 0 is the slowest, so it finishes last. If replay followed completion
        // order its entities would land last; slot order says they land first.
        Thread.Sleep((Slots - slot) * 12);

        for (int i = 0; i < perWorker; i++)
        {
            writer.Spawn(
                TestHelpers.CreatePlayer($"w{slot}e{i}", x: slot * 100 + i, y: slot),
                EntityTags.None);
        }
    }

    [Fact]
    public void StructuralOpsReplayInSlotOrder_NotInTheOrderWorkersFinished()
    {
        using var world = new EcsWorld(Slots);

        world.UpdateComponentsParallel(Slots, (w, slot) => SpawnPerWorker(w, slot, perWorker: 3));

        List<EntityState> all = world.GetEntitiesInRange(new Vec2(0, 0), float.MaxValue);
        var ids = new List<string>();
        foreach (EntityState e in all) ids.Add(e.Id);

        // The sleep in SpawnPerWorker makes worker 3 finish first and worker 0 last, so
        // this sequence is only produced by a drain that walks slots in index order.
        Assert.Equal(
            new[]
            {
                "w0e0", "w0e1", "w0e2",
                "w1e0", "w1e1", "w1e2",
                "w2e0", "w2e1", "w2e2",
                "w3e0", "w3e1", "w3e2",
            },
            ids);
    }

    [Fact]
    public void TheSameParallelRegionProducesAByteIdenticalWorldOnEveryRun()
    {
        string? expected = null;

        // Repeated because a race that only sometimes reorders would pass a single run.
        for (int attempt = 0; attempt < 25; attempt++)
        {
            using var world = new EcsWorld(Slots);
            world.UpdateComponentsParallel(Slots, (w, slot) => SpawnPerWorker(w, slot, perWorker: 4));

            string digest = Digest(world);
            expected ??= digest;
            Assert.Equal(expected, digest);
        }
    }

    [Fact]
    public void WorkerCountDoesNotChangeTheResultingWorld()
    {
        // The same total work, split over a different number of workers. Sequencing the
        // spawns by (slot, index) rather than by arrival is what makes these agree.
        string OneWorker()
        {
            using var world = new EcsWorld(Slots);
            world.UpdateComponentsParallel(1, (w, _) =>
            {
                for (int slot = 0; slot < Slots; slot++) SpawnPerWorker(w, slot, perWorker: 3);
            });
            return Digest(world);
        }

        string ManyWorkers()
        {
            using var world = new EcsWorld(Slots);
            world.UpdateComponentsParallel(Slots, (w, slot) => SpawnPerWorker(w, slot, perWorker: 3));
            return Digest(world);
        }

        Assert.Equal(OneWorker(), ManyWorkers());
    }

    [Fact]
    public void ASpawnInsideAParallelRegionIsNotVisibleUntilTheRegionEnds()
    {
        using var world = new EcsWorld(Slots);
        bool visibleInside = true;

        world.UpdateComponentsParallel(1, (w, _) =>
        {
            w.Spawn(TestHelpers.CreatePlayer("late"), EntityTags.None);

            // Resolve, not EntityCount: the world's public readers take the read lock,
            // which cannot be acquired while this scope holds the write lock. Resolving
            // through the writer is the in-scope way to ask whether an entity exists.
            //
            // This worker is not iterating, so before the world-level flag existed the
            // thread-static depth would have read 0 here and the spawn would have been
            // applied immediately -- mutating archetypes that another worker could be
            // iterating. Deferral has to hold for every worker in the region, whether or
            // not that particular worker happens to be inside a query.
            visibleInside = w.Resolve("late").IsValid;
        });

        Assert.False(visibleInside);
        Assert.Equal(1, world.EntityCount);
        Assert.NotNull(world.GetEntity("late"));
    }

    [Fact]
    public void DespawnsQueuedByDifferentWorkersAllApply()
    {
        using var world = new EcsWorld(Slots);
        for (int slot = 0; slot < Slots; slot++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"victim{slot}"));
        }

        world.UpdateComponentsParallel(Slots, (w, slot) =>
        {
            EntityHandle h = w.Resolve($"victim{slot}");
            if (h.IsValid) w.Despawn(in h);
        });

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AWorkerCountBeyondTheAllocatedSlotsIsRejected()
    {
        using var world = new EcsWorld(2);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => world.UpdateComponentsParallel(3, (_, _) => { }));

        Assert.Contains("2 structural slot", ex.Message);
    }

    [Fact]
    public void ASingleWorkerRegionStartsNoThread()
    {
        using var world = new EcsWorld(Slots);
        int callingThread = Environment.CurrentManagedThreadId;
        int bodyThread = -1;

        world.UpdateComponentsParallel(1, (_, _) => bodyThread = Environment.CurrentManagedThreadId);

        Assert.Equal(callingThread, bodyThread);
    }

    [Fact]
    public void AFailingWorkerSurfacesAndLeavesTheWorldUsable()
    {
        using var world = new EcsWorld(Slots);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            world.UpdateComponentsParallel(Slots, (w, slot) =>
            {
                if (slot == 2) throw new InvalidOperationException("worker 2 failed");
                w.Spawn(TestHelpers.CreatePlayer($"s{slot}"), EntityTags.None);
            }));

        Assert.Equal("worker 2 failed", ex.Message);

        // The region still closed: the flag is cleared and the lock released, so the
        // world takes further work. The surviving workers' spawns were still queued and
        // drained -- a failed worker must not discard the others' output.
        Assert.Equal(3, world.EntityCount);
        world.AddEntity(TestHelpers.CreatePlayer("after"));
        Assert.NotNull(world.GetEntity("after"));
    }

    [Fact]
    public void TwoFailingWorkersAreReportedTogether()
    {
        using var world = new EcsWorld(Slots);

        var ex = Assert.Throws<AggregateException>(() =>
            world.UpdateComponentsParallel(Slots, (_, slot) =>
            {
                if (slot is 1 or 3) throw new InvalidOperationException($"worker {slot} failed");
            }));

        Assert.Equal(2, ex.InnerExceptions.Count);
    }

    [Fact]
    public void TheSerialPathIsUnchangedByTheSlotMachinery()
    {
        // The default world has exactly one slot, and outside a parallel region nothing
        // is deferred unless an iteration is in progress -- the behaviour that was there
        // before per-slot queues existed.
        using var world = new EcsWorld();
        bool visibleInside = false;

        world.UpdateComponents(w =>
        {
            w.Spawn(TestHelpers.CreatePlayer("immediate"), EntityTags.None);

            // Not iterating, not in a region: applied at once, so it resolves already.
            // This is the case the parallel region deliberately changes, which is why it
            // is pinned here -- so a future widening of the deferral rule shows up as a
            // failure rather than as a quiet behaviour change on the serial path.
            visibleInside = w.Resolve("immediate").IsValid;
        });

        Assert.True(visibleInside);
        Assert.Equal(1, world.EntityCount);
    }
}
