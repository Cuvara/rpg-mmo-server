using GameServer.Snapshot;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// Unit tests for per-connection delta encoding: what lands on the wire and what does not.
/// </summary>
public class SnapshotDeltaStateTests
{
    private const int Keyframe = 30;

    private static List<EntityState> World(params EntityState[] entities) => new(entities);

    private static EntityState Player(string id, float x = 0, float y = 0, int hp = 100)
        => TestHelpers.CreatePlayer(id, x, y, hp);

    // --- Keyframes ---

    [Fact]
    public void FirstSnapshot_IsFullKeyframe()
    {
        var state = new SnapshotDeltaState();
        var snap = state.Encode(1, 0, World(Player("p1"), Player("p2", 5, 5)), Keyframe);

        Assert.True(snap.Full);
        Assert.Equal(2, snap.Entities.Count);
        // RepeatedField is never null; "no removals" is Count == 0. The wire is
        // unchanged either way: the JSON codec still omits "removed" when empty.
        Assert.Empty(snap.Removed);
    }

    [Fact]
    public void SecondSnapshot_IsDelta()
    {
        var state = new SnapshotDeltaState();
        var world = World(Player("p1"));
        state.Encode(1, 0, world, Keyframe);

        var snap = state.Encode(2, 0, world, Keyframe);
        Assert.False(snap.Full);
    }

    [Fact]
    public void KeyframeInterval_ForcesPeriodicFullSnapshot()
    {
        var state = new SnapshotDeltaState();
        var world = World(Player("p1"));

        var fullTicks = new List<ulong>();
        for (ulong t = 1; t <= 65; t++)
        {
            var snap = state.Encode(t, 0, world, Keyframe);
            if (snap.Full) fullTicks.Add(t);
        }

        // Keyframe at join (tick 1), then every 30 snapshots thereafter.
        Assert.Equal(new ulong[] { 1, 32, 63 }, fullTicks);
    }

    [Fact]
    public void KeyframeIntervalZero_DisablesDeltaEncoding()
    {
        var state = new SnapshotDeltaState();
        var world = World(Player("p1"));

        for (ulong t = 1; t <= 10; t++)
        {
            var snap = state.Encode(t, 0, world, 0);
            Assert.True(snap.Full);
            Assert.Single(snap.Entities);
        }
    }

    [Fact]
    public void RequestFull_PromotesNextSnapshotToKeyframe()
    {
        var state = new SnapshotDeltaState();
        var world = World(Player("p1"));
        state.Encode(1, 0, world, Keyframe);
        Assert.False(state.Encode(2, 0, world, Keyframe).Full);

        state.RequestFull();
        var snap = state.Encode(3, 0, world, Keyframe);

        Assert.True(snap.Full);
        Assert.Single(snap.Entities);
        // The forced keyframe restarts the interval, it does not just borrow a slot.
        Assert.Equal(0, state.SinceKeyframe);
    }

    // --- Delta content ---

    [Fact]
    public void UnchangedEntity_IsExcludedFromDelta()
    {
        var state = new SnapshotDeltaState();
        var world = World(Player("p1", 1, 1), Player("p2", 2, 2));
        state.Encode(1, 0, world, Keyframe);

        var snap = state.Encode(2, 0, world, Keyframe);

        Assert.Empty(snap.Entities);
        // RepeatedField is never null; "no removals" is Count == 0. The wire is
        // unchanged either way: the JSON codec still omits "removed" when empty.
        Assert.Empty(snap.Removed);
    }

    [Fact]
    public void MovedEntity_IsIncludedInDelta()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1", 1, 1), Player("p2", 2, 2)), Keyframe);

        var snap = state.Encode(2, 0, World(Player("p1", 1, 1), Player("p2", 7, 2)), Keyframe);

        var e = Assert.Single(snap.Entities);
        Assert.Equal("p2", e.Id);
        Assert.Equal(7f, e.X);
    }

    [Fact]
    public void DamagedEntity_IsIncludedInDelta()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1"), Player("p2", 2, 2)), Keyframe);

        var snap = state.Encode(2, 0, World(Player("p1"), Player("p2", 2, 2, hp: 40)), Keyframe);

        var e = Assert.Single(snap.Entities);
        Assert.Equal("p2", e.Id);
        Assert.Equal(40, e.Hp);
    }

    [Fact]
    public void NewEntity_IsIncludedInDelta()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1")), Keyframe);

        var snap = state.Encode(2, 0, World(Player("p1"), Player("p3", 4, 4)), Keyframe);

        var e = Assert.Single(snap.Entities);
        Assert.Equal("p3", e.Id);
        // RepeatedField is never null; "no removals" is Count == 0. The wire is
        // unchanged either way: the JSON codec still omits "removed" when empty.
        Assert.Empty(snap.Removed);
    }

    [Fact]
    public void EntityLeavingAoi_IsReportedInRemoved()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1"), Player("p2", 2, 2)), Keyframe);

        var snap = state.Encode(2, 0, World(Player("p1")), Keyframe);

        Assert.Empty(snap.Entities);
        Assert.NotNull(snap.Removed);
        Assert.Equal("p2", Assert.Single(snap.Removed!));
    }

    [Fact]
    public void RemovedEntity_IsNotReportedTwice()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1"), Player("p2", 2, 2)), Keyframe);
        state.Encode(2, 0, World(Player("p1")), Keyframe);

        var snap = state.Encode(3, 0, World(Player("p1")), Keyframe);

        // RepeatedField is never null; "no removals" is Count == 0. The wire is
        // unchanged either way: the JSON codec still omits "removed" when empty.
        Assert.Empty(snap.Removed);
        Assert.Empty(snap.Entities);
    }

    [Fact]
    public void EntityReenteringAoi_IsResentInFull()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1"), Player("p2", 2, 2)), Keyframe);
        state.Encode(2, 0, World(Player("p1")), Keyframe);

        var snap = state.Encode(3, 0, World(Player("p1"), Player("p2", 2, 2)), Keyframe);

        var e = Assert.Single(snap.Entities);
        Assert.Equal("p2", e.Id);
    }

    [Fact]
    public void Keyframe_NeverCarriesRemovals()
    {
        var state = new SnapshotDeltaState();
        state.Encode(1, 0, World(Player("p1"), Player("p2", 2, 2)), Keyframe);

        // Force a keyframe on the tick where p2 disappears: the client discards
        // everything not listed, so an explicit removal list is unnecessary.
        state.RequestFull();
        var snap = state.Encode(2, 0, World(Player("p1")), Keyframe);

        Assert.True(snap.Full);
        // RepeatedField is never null; "no removals" is Count == 0. The wire is
        // unchanged either way: the JSON codec still omits "removed" when empty.
        Assert.Empty(snap.Removed);
        Assert.Single(snap.Entities);
    }

    // --- Ack ---

    [Fact]
    public void AckTick_IsCarriedOnKeyframesAndDeltas()
    {
        var state = new SnapshotDeltaState();
        var world = World(Player("p1"));

        Assert.Equal(11u, state.Encode(1, 11, world, Keyframe).AckTick);
        Assert.Equal(12u, state.Encode(2, 12, world, Keyframe).AckTick);
    }

    [Fact]
    public void AckTick_IsPerConnection()
    {
        var a = new SnapshotDeltaState();
        var b = new SnapshotDeltaState();
        var world = World(Player("p1"), Player("p2", 1, 1));

        Assert.Equal(5u, a.Encode(1, 5, world, Keyframe).AckTick);
        Assert.Equal(9u, b.Encode(1, 9, world, Keyframe).AckTick);
    }

    // --- Reconstruction ---

    [Fact]
    public void DeltaStream_ReconstructsExactlyTheServerState()
    {
        var state = new SnapshotDeltaState();
        var merger = new SnapshotMerger();

        // 3 movers, 5 statics; one static leaves AOI halfway through.
        for (ulong tick = 1; tick <= 100; tick++)
        {
            var world = new List<EntityState>();
            for (int i = 0; i < 3; i++)
                world.Add(Player($"mover{i}", x: tick * 0.1f + i, y: i, hp: 100 - (int)(tick / 10)));
            for (int i = 0; i < 5; i++)
                world.Add(Player($"static{i}", x: 10 + i, y: 10));
            if (tick > 50) world.RemoveAll(e => e.Id == "static3");

            var snap = state.Encode(tick, tick, world, Keyframe);
            merger.Apply(ToData(snap));

            // Reconstructed state must equal the server's AOI set on EVERY tick,
            // not just at the end.
            Assert.Equal(world.Count, merger.Count);
            foreach (var e in world)
            {
                Assert.True(merger.TryGet(e.Id, out var got), $"missing {e.Id} at tick {tick}");
                Assert.Equal(e.Position.X, got.X);
                Assert.Equal(e.Position.Y, got.Y);
                Assert.Equal(e.Hp, got.Hp);
                Assert.Equal(e.MaxHp, got.MaxHp);
                Assert.Equal(e.Type, got.Type);
            }
        }

        Assert.False(merger.TryGet("static3", out _));
        Assert.Equal(100u, merger.AckTick);
        Assert.Equal(100u, merger.Tick);
        Assert.True(merger.Deltas > merger.Keyframes);
    }

    /// <summary>Convert a server-side wire message into the client-side mirror type.</summary>
    internal static SnapshotData ToData(SnapshotMessage msg)
    {
        var entities = new EntitySnapshotData[msg.Entities.Count];
        for (int i = 0; i < msg.Entities.Count; i++)
        {
            var e = msg.Entities[i];
            entities[i] = new EntitySnapshotData(e.Id, EntityTypes.NameOf(e), e.X, e.Y, e.Hp, e.MaxHp);
        }
        return new SnapshotData(msg.Tick, msg.AckTick, msg.Full, entities, msg.Removed?.ToArray());
    }
}
