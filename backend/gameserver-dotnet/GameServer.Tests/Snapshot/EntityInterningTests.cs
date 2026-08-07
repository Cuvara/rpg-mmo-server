using Shared.GameLogic.Components;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// Server-side half of entity-id interning. The receiver's half — refusing an
/// unknown handle and recovering via a keyframe — is tested in Go's
/// <c>shared/messages/interning_test.go</c>, because that is where the client
/// merge lives.
/// </summary>
public class EntityInterningTests
{
    private static EntityState Ent(string id, float x = 0) => new()
    {
        Id = id, Type = "player", Position = new Vec2(x, 0), Hp = 100, MaxHp = 100
    };

    /// <summary>
    /// The id is written once to introduce the handle, and never again in that
    /// interval. That is the entire saving; if the id kept being sent the change
    /// would decode identically and cost the same, which is how an optimisation
    /// quietly stops working.
    /// </summary>
    [Fact]
    public void IdIsSentOnceToIntroduceTheHandle_ThenHandleOnly()
    {
        var s = new SnapshotDeltaState();
        var world = new List<EntityState> { Ent("lt-000000000042") };

        var keyframe = s.Encode(1, 0, world, keyframeInterval: 30, intern: true);
        var first = keyframe.Entities[0];
        Assert.Equal("lt-000000000042", first.Id);
        Assert.NotEqual(0u, first.Handle);

        // Move it so the delta actually carries the entity.
        world[0] = Ent("lt-000000000042", x: 5);
        var delta = s.Encode(2, 0, world, keyframeInterval: 30, intern: true);

        var again = Assert.Single(delta.Entities);
        Assert.Equal("", again.Id);
        Assert.Equal(first.Handle, again.Handle);
    }

    /// <summary>
    /// A keyframe restarts the handle space. Both sides reset there, which is
    /// what bounds how long any disagreement can survive.
    /// </summary>
    [Fact]
    public void KeyframeRestartsTheHandleSpaceAndReintroducesEveryId()
    {
        var s = new SnapshotDeltaState();
        var world = new List<EntityState> { Ent("a"), Ent("b") };

        var first = s.Encode(1, 0, world, keyframeInterval: 30, intern: true);
        Assert.All(first.Entities, e => Assert.NotEqual("", e.Id));

        s.RequestFull();
        var second = s.Encode(2, 0, world, keyframeInterval: 30, intern: true);

        Assert.True(second.Full);
        // Every id re-introduced: a keyframe must be self-sufficient, or a client
        // that resynced because it was lost would still be lost afterwards.
        Assert.All(second.Entities, e => Assert.NotEqual("", e.Id));
        Assert.Equal(first.Entities.Select(e => e.Handle).OrderBy(h => h),
                     second.Entities.Select(e => e.Handle).OrderBy(h => h));
    }

    /// <summary>
    /// A handle freed by a despawn must NOT come back within the same interval.
    /// Reuse would let a client that missed the despawn attribute an update to
    /// the wrong entity — wrong state rather than absent state, and far harder to
    /// detect than a handle that simply does not resolve.
    /// </summary>
    [Fact]
    public void HandlesAreNotReusedWithinAnInterval()
    {
        var s = new SnapshotDeltaState();
        var world = new List<EntityState> { Ent("gone") };

        var keyframe = s.Encode(1, 0, world, keyframeInterval: 30, intern: true);
        uint retired = keyframe.Entities[0].Handle;

        // "gone" leaves the AOI; "fresh" arrives.
        world[0] = Ent("fresh");
        var delta = s.Encode(2, 0, world, keyframeInterval: 30, intern: true);

        Assert.Contains("gone", delta.Removed);
        var fresh = Assert.Single(delta.Entities);
        Assert.Equal("fresh", fresh.Id);
        Assert.NotEqual(retired, fresh.Handle);
    }

    /// <summary>
    /// JSON has no handle field, so interning there would emit entities with an
    /// empty id and silently break every pre-interning client. The encoding is a
    /// property of the connection, so the caller decides.
    /// </summary>
    [Fact]
    public void JsonConnectionsAreNeverInterned()
    {
        var s = new SnapshotDeltaState();
        var world = new List<EntityState> { Ent("e1") };

        s.Encode(1, 0, world, keyframeInterval: 30, intern: false);
        world[0] = Ent("e1", x: 5);
        var delta = s.Encode(2, 0, world, keyframeInterval: 30, intern: false);

        var e = Assert.Single(delta.Entities);
        Assert.Equal("e1", e.Id);
        Assert.Equal(0u, e.Handle);
    }

    /// <summary>The saving on the wire, asserted rather than assumed.</summary>
    [Fact]
    public void RepeatMentionsAreSubstantiallySmallerWhenInterned()
    {
        int Encode(bool intern)
        {
            var s = new SnapshotDeltaState();
            var world = new List<EntityState>();
            for (int i = 0; i < 50; i++) world.Add(Ent($"lt-{i:D12}"));

            s.Encode(1, 0, world, keyframeInterval: 30, intern: intern); // introduces
            for (int i = 0; i < 50; i++) world[i] = Ent($"lt-{i:D12}", x: 5);
            var delta = s.Encode(2, 0, world, keyframeInterval: 30, intern: intern);

            return WireProtocol.EncodeBody(
                WireProtocol.NewEnvelope(MsgType.Snapshot, delta, WireEncoding.Proto)).Length;
        }

        int plain = Encode(false), interned = Encode(true);
        double saving = 1 - (double)interned / plain;
        Assert.True(saving >= 0.30,
            $"expected >= 30% off a repeat-mention delta, got {saving:P1} (plain={plain}B interned={interned}B)");
    }
}
