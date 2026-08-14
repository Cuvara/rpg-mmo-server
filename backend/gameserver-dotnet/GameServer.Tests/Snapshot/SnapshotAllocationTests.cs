using GameServer.Net;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The snapshot path's per-tick allocation, and the guard that keeps it down.
///
/// <para><b>Why a paired A/B in one process.</b> Both arms run in the same binary over
/// the same inputs, so the comparison cannot be confounded by build, machine or day —
/// the failure mode of quoting a "before" measured on a tree that no longer exists.
/// Arm A rebuilds the pre-change shape by hand (a fresh <c>EntitySnapshot</c> per entity
/// per viewer, then <c>ToByteArray</c> twice and a framed copy); arm B is the shipped
/// path (pooled entities, reused serialization buffers).</para>
///
/// <para><b>Allocation only, deliberately.</b> No wall-clock assertion: this host's
/// spread on an unchanged binary is wide enough to swallow the effect, and the claim
/// being made is about garbage, not latency.</para>
///
/// <para><b>What makes this a guard rather than a benchmark.</b> The thresholds are
/// generous — they exist to fail if pooling is removed or quietly defeated (say by a
/// caller that starts holding messages across encodes, forcing a copy back), not to pin
/// an exact byte count that a runtime upgrade could move.</para>
/// </summary>
public class SnapshotAllocationTests(ITestOutputHelper output)
{
    private const int Keyframe = 30;
    private const int Ticks = 60;

    /// <summary>Entities visible to one viewer — the AOI slice, not the whole map.</summary>
    private const int VisiblePerViewer = 40;

    private static EntityState[] MakeWorld(int count, int tick)
    {
        var world = new EntityState[count];
        for (int i = 0; i < count; i++)
        {
            // Move every entity a little each tick, so deltas are non-empty and the
            // measurement is not of an idle stream that would flatter either arm.
            world[i] = TestHelpers.CreatePlayer(
                $"player-{i:D4}", x: i * 0.25f + tick * 0.1f, y: i * 0.5f - tick * 0.05f);
        }
        return world;
    }

    /// <summary>The pre-change shape: everything fresh, every tick.</summary>
    private static EntityState[][] PrebuiltWorlds()
    {
        // Built once, outside every measured region: the harness's own world-building
        // garbage would otherwise be counted against all three arms and flatter the two
        // that allocate least.
        var worlds = new EntityState[Ticks][];
        for (int t = 0; t < Ticks; t++) worlds[t] = MakeWorld(VisiblePerViewer, t);
        return worlds;
    }

    private static long MeasureLegacy(int viewers)
    {
        var states = new SnapshotDeltaState[viewers];
        for (int v = 0; v < viewers; v++) states[v] = new SnapshotDeltaState();
        var worlds = PrebuiltWorlds();

        // Warm up so first-call JIT and dictionary growth are not counted as steady state.
        RunLegacy(states, worlds, viewers, warmup: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunLegacy(states, worlds, viewers, warmup: false);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;
    }

    private static void RunLegacy(SnapshotDeltaState[] states, EntityState[][] worlds, int viewers, bool warmup)
    {
        int ticks = warmup ? 5 : Ticks;
        for (int t = 0; t < ticks; t++)
        {
            var world = worlds[t % worlds.Length];
            for (int v = 0; v < viewers; v++)
            {
                // Delta bookkeeping still comes from the real encoder — the arm under
                // study is what happens to the message afterwards, plus the per-entity
                // objects, which is what the legacy copy below reproduces.
                var snap = states[v].Encode(
                    (ulong)t, 0, world.AsSpan(), Keyframe, intern: true);

                var legacy = new SnapshotMessage
                {
                    Tick = snap.Tick, AckTick = snap.AckTick, Full = snap.Full
                };
                foreach (var e in snap.Entities)
                {
                    legacy.Entities.Add(new EntitySnapshot
                    {
                        Id = e.Id, Handle = e.Handle, X = e.X, Y = e.Y,
                        Hp = e.Hp, MaxHp = e.MaxHp, Type = e.Type, TypeName = e.TypeName
                    });
                }
                foreach (string r in snap.Removed) legacy.Removed.Add(r);

                byte[] frame = WireProtocol.Encode(
                    WireProtocol.NewEnvelope(MsgType.Snapshot, legacy, WireEncoding.Proto));
                if (frame.Length == 0) throw new InvalidOperationException("empty frame");
            }
        }
    }

    /// <summary>The shipped path: pooled entities, reused buffers.</summary>
    private static long MeasurePooled(int viewers)
    {
        var states = new SnapshotDeltaState[viewers];
        var writers = new SnapshotFrameWriter[viewers];
        for (int v = 0; v < viewers; v++)
        {
            states[v] = new SnapshotDeltaState();
            writers[v] = new SnapshotFrameWriter();
        }
        var worlds = PrebuiltWorlds();

        RunPooled(states, writers, worlds, viewers, warmup: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunPooled(states, writers, worlds, viewers, warmup: false);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;
    }

    private static void RunPooled(
        SnapshotDeltaState[] states, SnapshotFrameWriter[] writers, EntityState[][] worlds, int viewers, bool warmup)
    {
        int ticks = warmup ? 5 : Ticks;
        for (int t = 0; t < ticks; t++)
        {
            var world = worlds[t % worlds.Length];
            for (int v = 0; v < viewers; v++)
            {
                var snap = states[v].Encode(
                    (ulong)t, 0, world.AsSpan(), Keyframe, intern: true);
                var frame = writers[v].WriteFrame((byte)MsgType.Snapshot, snap);
                if (frame.Length == 0) throw new InvalidOperationException("empty frame");
            }
        }
    }

    /// <summary>
    /// Middle arm: pooled entities, but the old <c>ToByteArray</c> serialization. Reported
    /// so the two changes can be attributed separately rather than credited as one number.
    /// </summary>
    private static long MeasurePooledEntitiesOnly(int viewers)
    {
        var states = new SnapshotDeltaState[viewers];
        for (int v = 0; v < viewers; v++) states[v] = new SnapshotDeltaState();
        var worlds = PrebuiltWorlds();

        RunPooledEntitiesOnly(states, worlds, viewers, warmup: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunPooledEntitiesOnly(states, worlds, viewers, warmup: false);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;
    }

    private static void RunPooledEntitiesOnly(SnapshotDeltaState[] states, EntityState[][] worlds, int viewers, bool warmup)
    {
        int ticks = warmup ? 5 : Ticks;
        for (int t = 0; t < ticks; t++)
        {
            var world = worlds[t % worlds.Length];
            for (int v = 0; v < viewers; v++)
            {
                var snap = states[v].Encode((ulong)t, 0, world.AsSpan(), Keyframe, intern: true);
                byte[] frame = WireProtocol.Encode(
                    WireProtocol.NewEnvelope(MsgType.Snapshot, snap, WireEncoding.Proto));
                if (frame.Length == 0) throw new InvalidOperationException("empty frame");
            }
        }
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void PooledPath_AllocatesFarLessPerTick(int viewers)
    {
        long legacy = MeasureLegacy(viewers);
        long entitiesOnly = MeasurePooledEntitiesOnly(viewers);
        long pooled = MeasurePooled(viewers);

        output.WriteLine(
            $"{viewers} viewers x {VisiblePerViewer} visible: legacy {legacy:N0} B/tick, " +
            $"pooled-entities-only {entitiesOnly:N0} B/tick, " +
            $"pooled+reused-buffers {pooled:N0} B/tick, " +
            $"{(legacy == 0 ? 0 : 100.0 * pooled / legacy):F1}% of legacy");

        // Generous: the point is that per-entity and per-frame garbage is gone, not an
        // exact figure. Anything near parity means pooling was removed or defeated.
        Assert.True(pooled < legacy / 4,
            $"expected the pooled path to allocate far less; legacy {legacy}, pooled {pooled}");
    }

    /// <summary>
    /// The reused-buffer writer must produce exactly the bytes the allocating path did.
    /// <see cref="SnapshotByteIdentityTests"/> guards the message contents; this guards
    /// the framing and envelope wrapping, which that test does not reach because it
    /// frames through <see cref="WireProtocol.Encode"/> itself.
    /// </summary>
    [Fact]
    public void WriteFrame_IsByteIdenticalToTheAllocatingPath()
    {
        var state = new SnapshotDeltaState();
        var writer = new SnapshotFrameWriter();

        for (int t = 0; t < 40; t++)
        {
            var world = MakeWorld(12, t);
            var snap = state.Encode((ulong)t, (ulong)t, world.AsSpan(), Keyframe, intern: true);

            byte[] expected = WireProtocol.Encode(
                WireProtocol.NewEnvelope(MsgType.Snapshot, snap, WireEncoding.Proto));
            byte[] actual = writer.WriteFrame((byte)MsgType.Snapshot, snap).ToArray();

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// Reuse must not leak state between snapshots: an entity that stops being interned,
    /// or one whose id was written once, must not carry a stale field into a later frame.
    /// This is the failure a pool introduces and a fresh object cannot.
    /// </summary>
    [Fact]
    public void PooledEntities_CarryNoStaleFieldsBetweenTicks()
    {
        var state = new SnapshotDeltaState();

        // Keyframe: both id and handle are written for every entity.
        var world = MakeWorld(3, 0);
        var full = state.Encode(1, 0, world.AsSpan(), Keyframe, intern: true);
        Assert.True(full.Full);
        Assert.All(full.Entities, e => Assert.False(string.IsNullOrEmpty(e.Id)));

        // Delta with the same entities moved: handles only, no ids re-sent.
        var moved = MakeWorld(3, 1);
        var delta = state.Encode(2, 0, moved.AsSpan(), Keyframe, intern: true);
        Assert.False(delta.Full);
        Assert.NotEmpty(delta.Entities);
        Assert.All(delta.Entities, e =>
        {
            Assert.True(string.IsNullOrEmpty(e.Id), $"stale id '{e.Id}' survived pooling");
            Assert.NotEqual(0u, e.Handle);
        });
    }
}
