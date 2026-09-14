using GameServer.Input;
using GameServer.Net;
using GameServer.Server;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The byte-identity proof for issue #237: the trimmed gather path — the 7-field
/// <see cref="EntityView"/> compose plus the int-keyed delta state — produces exactly
/// the bytes the full <see cref="EntityState"/> gather plus the legacy (string-hashing)
/// entry point produced, frame for frame.
///
/// <para><b>How it proves it.</b> One deterministic scenario runs through the real
/// <see cref="TickLoop"/>. Every tick, every viewer is encoded twice from the same
/// world state: once through the old pipeline (EntityState scan →
/// <c>Encode(ReadOnlySpan&lt;EntityState&gt;)</c>) and once through the new one
/// (EntityView scan → <c>Encode(ReadOnlySpan&lt;EntityView&gt;)</c>), each arm with
/// its own <see cref="SnapshotDeltaState"/> so the delta/keyframe bookkeeping evolves
/// independently. Every frame is compared byte for byte, so a divergence names the
/// tick, the viewer and the frame sizes rather than failing at the end with a digest.</para>
///
/// <para>The scenario exercises keyframes, deltas and despawns — asserted at the end,
/// because a stream of empty snapshots would pass any refactor — and runs long enough
/// to cross several keyframe intervals. Complements
/// <see cref="SnapshotByteIdentityTests"/>, whose pinned pre-change digests prove the
/// legacy arm itself has not moved.</para>
/// </summary>
public class TrimmedGatherByteIdentityTests
{
    private const int TickRate = 15;
    private const int Ticks = 120;   // > 3 keyframe intervals at the default of 30
    private const int Players = 8;
    private const int Mobs = 6;

    private static void RunScenario(WireEncoding encoding)
    {
        using var world = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(world, NullLogger.Instance, null, TickRate, MapBounds.Default);

        // simulationPhase: null — the enemy spawner draws from Random.Shared.
        var loop = new TickLoop(
            world, handler, connections, TickRate, GameConstants.DefaultAoiRadius,
            NullLogger.Instance, metrics: null,
            keyframeInterval: GameConstants.DefaultKeyframeInterval, simulationPhase: null);

        var ids = new string[Players];
        var oldStates = new SnapshotDeltaState[Players];
        var newStates = new SnapshotDeltaState[Players];

        for (int i = 0; i < Players; i++)
        {
            ids[i] = $"p{i}";
            world.AddEntity(TestHelpers.CreatePlayer(ids[i],
                x: (i - Players / 2f) * (GameConstants.DefaultAoiRadius / 2f),
                y: 0f, speed: 4f));
            oldStates[i] = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor(ids[i]));
            newStates[i] = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor(ids[i]));
        }

        for (int i = 0; i < Mobs; i++)
        {
            world.AddEntity(TestHelpers.CreateMob($"m{i}", x: i * 3f, y: 2f));
        }

        var stateBuffer = new EntityState[Players + Mobs];
        var viewBuffer = new EntityView[Players + Mobs];
        bool intern = encoding == WireEncoding.Proto;

        bool sawFull = false, sawDelta = false, sawRemoved = false;

        for (int t = 1; t <= Ticks; t++)
        {
            for (int i = 0; i < Players; i++)
            {
                float dir = ((t / (8 + i)) % 2 == 0) ? 1f : -1f;
                world.PushInput(ids[i], new InputData((ulong)t, dir, dir * 0.5f, null));
            }

            // A mid-run despawn and a same-id respawn: the respawned mob must keep its
            // stable key, or the int-keyed arm would emit a spurious despawn/re-introduce
            // where the string-keyed arm sees the same entity throughout.
            if (t == 45) world.RemoveEntity("m0");
            if (t == 60) world.AddEntity(TestHelpers.CreateMob("m0", x: 1f, y: 1f));

            loop.TickOnce();

            for (int i = 0; i < Players; i++)
            {
                world.TryGetSnapshotAnchor(ids[i], out Vec2 anchor, out ulong ackTick);

                int oldCount = world.GetEntitiesInRange(anchor, GameConstants.DefaultAoiRadius,
                    stateBuffer.AsSpan());
                int newCount = world.GetEntitiesInRange(anchor, GameConstants.DefaultAoiRadius,
                    viewBuffer.AsSpan());
                Assert.True(oldCount == newCount,
                    $"t{t} {ids[i]}: match counts diverged (old {oldCount}, new {newCount})");
                Assert.True(oldCount <= stateBuffer.Length, "scenario buffer undersized");

                SnapshotMessage oldMsg = oldStates[i].Encode(
                    loop.CurrentTick, ackTick, stateBuffer.AsSpan(0, oldCount),
                    GameConstants.DefaultKeyframeInterval, intern);
                byte[] oldFrame = WireProtocol.Encode(
                    WireProtocol.NewEnvelope(MsgType.Snapshot, oldMsg, encoding));

                sawFull |= oldMsg.Full;
                sawDelta |= !oldMsg.Full;
                sawRemoved |= oldMsg.Removed.Count > 0;

                SnapshotMessage newMsg = newStates[i].Encode(
                    loop.CurrentTick, ackTick, viewBuffer.AsSpan(0, newCount),
                    GameConstants.DefaultKeyframeInterval, intern);
                byte[] newFrame = WireProtocol.Encode(
                    WireProtocol.NewEnvelope(MsgType.Snapshot, newMsg, encoding));

                Assert.True(oldFrame.AsSpan().SequenceEqual(newFrame),
                    $"t{t} {ids[i]}: wire frames diverged " +
                    $"(old {oldFrame.Length}B full={oldMsg.Full} n={oldMsg.Entities.Count} rm={oldMsg.Removed.Count}, " +
                    $"new {newFrame.Length}B full={newMsg.Full} n={newMsg.Entities.Count} rm={newMsg.Removed.Count})");
            }
        }

        // The comparison must have covered every frame kind or it proves nothing.
        Assert.True(sawFull, "scenario produced no keyframe");
        Assert.True(sawDelta, "scenario produced no delta");
        Assert.True(sawRemoved, "scenario produced no despawn — AOI transitions missing");
    }

    /// <summary>Protobuf with interning: the order-sensitive handle allocation is the
    /// riskiest thing downstream of the gather rewrite.</summary>
    [Fact]
    public void ProtobufFrames_AreByteIdentical_TrimmedVersusFullGather() =>
        RunScenario(WireEncoding.Proto);

    /// <summary>Legacy JSON: never interns, does not elide zeroes, fails differently.</summary>
    [Fact]
    public void JsonFrames_AreByteIdentical_TrimmedVersusFullGather() =>
        RunScenario(WireEncoding.Json);
}
