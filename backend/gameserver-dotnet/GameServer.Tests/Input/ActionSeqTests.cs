using GameServer.Input;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Input;

/// <summary>
/// The retrigger counter and the one-shot latch: the two halves of making an attack
/// visible to a client.
///
/// <para>They are tested together because either alone is useless. The counter without
/// the latch bumps for a state no snapshot ever samples; the latch without the counter
/// keeps the state alive but cannot distinguish a second attack from the first.</para>
/// </summary>
public class ActionSeqTests
{
    private const int CriticalHz = 60;
    private const int WorldEvery = 4;   // the 60/15 default: one world tick per 4 base ticks

    // ── ActionTransitions, in isolation ──────────────────────────────────────────

    [Fact]
    public void Enter_BumpsTheCounterOnATransition()
    {
        var loco = new Locomotion(5f);

        ActionTransitions.Enter(ref loco, EntityAction.Moving, baseTick: 1, holdTicks: 1);
        uint afterMoving = loco.ActionSeq;

        ActionTransitions.Enter(ref loco, EntityAction.Idle, baseTick: 2, holdTicks: 1);

        Assert.NotEqual(0u, afterMoving);
        Assert.NotEqual(afterMoving, loco.ActionSeq);
        Assert.Equal(EntityAction.Idle, loco.Action);
    }

    /// <summary>
    /// Re-asserting a continuous action must NOT bump. A walking entity re-enters Moving
    /// on every base tick; if that bumped, the entity would differ on the wire every
    /// single snapshot and the delta encoder's "omit unchanged entities" property would
    /// collapse for everything in motion — the opposite of what this field is for.
    /// </summary>
    [Fact]
    public void Enter_DoesNotBumpWhenAContinuousActionIsReasserted()
    {
        var loco = new Locomotion(5f);
        ActionTransitions.Enter(ref loco, EntityAction.Moving, baseTick: 1, holdTicks: 1);
        uint first = loco.ActionSeq;

        for (ulong t = 2; t <= 20; t++)
        {
            ActionTransitions.Enter(ref loco, EntityAction.Moving, t, holdTicks: 1);
        }

        Assert.Equal(first, loco.ActionSeq);
    }

    /// <summary>
    /// Two attacks in a row are the case the level-triggered field cannot express, so
    /// re-entering a one-shot MUST bump even though the action value is unchanged.
    /// </summary>
    [Fact]
    public void Enter_BumpsOnEveryOneShotEntry_EvenWithoutAChangeOfValue()
    {
        var loco = new Locomotion(5f);

        ActionTransitions.Enter(ref loco, EntityAction.Attacking, baseTick: 1, holdTicks: 1);
        uint first = loco.ActionSeq;
        ActionTransitions.Enter(ref loco, EntityAction.Attacking, baseTick: 40, holdTicks: 1);

        Assert.Equal(EntityAction.Attacking, loco.Action);
        Assert.NotEqual(first, loco.ActionSeq);
    }

    /// <summary>
    /// Zero is the wire's "not sent". A counter that wrapped onto it would read to every
    /// client as "this server does not implement action_seq" and would silently stop
    /// retriggering for the rest of the session.
    /// </summary>
    [Fact]
    public void Advance_SkipsZeroOnWrap()
    {
        // Pins the SHARED rule, not a server copy of it: ActionStateLogic lives in
        // Shared.GameLogic and is compiled into the Unity client, so this is the contract
        // both sides read. A server that wrapped onto 0 would tell every client "this
        // sender does not send a retrigger counter" and stop retriggering that entity's
        // animations for four billion actions.
        var action = EntityAction.Idle;
        uint seq = uint.MaxValue;

        Assert.True(ActionStateLogic.Advance(
            ref action, ref seq, EntityAction.Attacking));

        Assert.Equal(1u, seq);
    }

    [Fact]
    public void Latch_BlocksAContinuousActionUntilTheHoldExpires()
    {
        var loco = new Locomotion(5f);
        ActionTransitions.Enter(ref loco, EntityAction.Attacking, baseTick: 10, holdTicks: WorldEvery);

        // Inside the hold: Moving must not win.
        for (ulong t = 11; t < 10 + WorldEvery; t++)
        {
            ActionTransitions.Enter(ref loco, EntityAction.Moving, t, holdTicks: WorldEvery);
            Assert.Equal(EntityAction.Attacking, loco.Action);
        }

        // The tick the hold expires on, Moving takes over again.
        ActionTransitions.Enter(ref loco, EntityAction.Moving, 10 + WorldEvery, holdTicks: WorldEvery);
        Assert.Equal(EntityAction.Moving, loco.Action);
    }

    /// <summary>A corpse must not keep swinging: death outranks the latch.</summary>
    [Fact]
    public void Latch_DoesNotBlockDeath()
    {
        var loco = new Locomotion(5f);
        ActionTransitions.Enter(ref loco, EntityAction.Attacking, baseTick: 10, holdTicks: WorldEvery);

        ActionTransitions.Enter(ref loco, EntityAction.Dead, baseTick: 11, holdTicks: WorldEvery);

        Assert.Equal(EntityAction.Dead, loco.Action);
        Assert.Equal(0UL, loco.ActionHoldUntilTick);
    }

    /// <summary>
    /// holdTicks &lt;= 1 is the pre-latch behaviour, which is what the sixteen fixtures
    /// that construct an <see cref="InputHandler"/> directly still get by default.
    /// </summary>
    [Fact]
    public void Latch_IsDisabledWhenHoldTicksIsOne()
    {
        var loco = new Locomotion(5f);
        ActionTransitions.Enter(ref loco, EntityAction.Attacking, baseTick: 10, holdTicks: 1);

        ActionTransitions.Enter(ref loco, EntityAction.Moving, baseTick: 11, holdTicks: 1);

        Assert.Equal(EntityAction.Moving, loco.Action);
    }

    // ── End to end: the sampling gap ─────────────────────────────────────────────

    /// <summary>
    /// The defect this whole change exists to fix, and its control.
    ///
    /// <para>Actions are written on the critical group (60 Hz) and sampled by the snapshot
    /// gather on the world group (15 Hz), so only one base tick in four is ever observable.
    /// An attack lands on tick 1 and the player keeps walking on 2, 3 and 4; the snapshot
    /// is built on tick 4. Without the latch the gather sees Moving and the attack never
    /// reaches any client.</para>
    ///
    /// <para>The control arm runs the identical scenario with the latch off and asserts the
    /// attack IS lost — without it this test would pass just as happily against a build
    /// where the latch does nothing.</para>
    /// </summary>
    [Theory]
    [InlineData(WorldEvery, EntityAction.Attacking)]  // latched: the attack survives
    [InlineData(1, EntityAction.Moving)]              // control: pre-latch, the attack is lost
    public void AttackSurvivesTheSamplingGap_OnlyWhenLatched(
        int holdTicks, EntityAction expectedAtSampleTime)
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        world.AddEntity(TestHelpers.CreateMob("m1", x: 1, y: 0));

        var handler = new InputHandler(
            world, NullLogger.Instance, onDeath: null, tickRate: CriticalHz,
            bounds: MapBounds.Default, onRejected: null, onAttackAccepted: null,
            oneShotHoldTicks: holdTicks);

        // Base tick 1: attack. Base ticks 2..4: keep walking, which is what overwrites it.
        handler.ProcessInput("p1",
            new InputData(tick: 1, moveX: 0f, moveY: 0f, attackTargetId: "m1"), currentTick: 1);
        for (ulong t = 2; t <= WorldEvery; t++)
        {
            handler.ProcessInput("p1",
                new InputData(t, moveX: 1f, moveY: 0f, attackTargetId: null), currentTick: t);
        }

        EntityView p = ViewOf(world, "p1");

        Assert.Equal(expectedAtSampleTime, p.Action);
        Assert.NotEqual(0u, p.ActionSeq);   // the counter advances either way; only the value survives or not
    }

    /// <summary>
    /// The counter must reach the gather, not merely the component. This is the field that
    /// was dropped twice before on exactly this path — see
    /// <c>AoiIndexDifferentialTests</c>.
    /// </summary>
    [Fact]
    public void TheCounterReachesTheAoiGather()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        var handler = new InputHandler(
            world, NullLogger.Instance, onDeath: null, tickRate: CriticalHz,
            bounds: MapBounds.Default, onRejected: null, onAttackAccepted: null,
            oneShotHoldTicks: WorldEvery);

        Assert.Equal(0u, ViewOf(world, "p1").ActionSeq);   // never set yet

        handler.ProcessInput("p1",
            new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null), currentTick: 1);

        Assert.NotEqual(0u, ViewOf(world, "p1").ActionSeq);
    }

    // ── The delta encoder must not coalesce two attacks ──────────────────────────

    /// <summary>
    /// The other half of the fix. The latch gets <c>Attacking</c> to the gather; this is
    /// what stops the encoder from throwing the second one away.
    ///
    /// <para>Two attacks in a row are identical in every field except the counter. If
    /// <c>SentView.Equals</c> ignored it, the encoder would classify the second attack as
    /// "unchanged since last send" and omit it — the client would keep showing the first
    /// swing and no test of the transport, the latch or the gather would notice.</para>
    /// </summary>
    [Fact]
    public void DeltaEncoder_SendsASecondAttackThatDiffersOnlyInTheCounter()
    {
        var state = new GameServer.Snapshot.SnapshotDeltaState();
        var buffer = new EntityView[1];

        // Keyframe first, so the following encodes are deltas against a known baseline.
        buffer[0] = Attacking(seq: 1);
        state.Encode(tick: 1, ackTick: 1, buffer.AsSpan(), keyframeInterval: int.MaxValue);

        // Identical state, identical counter: genuinely unchanged, must be omitted.
        buffer[0] = Attacking(seq: 1);
        var unchanged = state.Encode(tick: 2, ackTick: 2, buffer.AsSpan(), keyframeInterval: int.MaxValue);
        Assert.Empty(unchanged.Entities);

        // A second attack: same action, same position, new counter. Must be sent.
        buffer[0] = Attacking(seq: 2);
        var secondAttack = state.Encode(tick: 3, ackTick: 3, buffer.AsSpan(), keyframeInterval: int.MaxValue);

        Assert.Single(secondAttack.Entities);
        Assert.Equal(2u, secondAttack.Entities[0].ActionSeq);
    }

    private static EntityView Attacking(uint seq) => new(
        key: 1, id: "p1", type: "player", position: new Vec2(0, 0), hp: 100, maxHp: 100,
        speed: 5f, facingBrad: 1, action: EntityAction.Attacking, actionSeq: seq);

    private static EntityView ViewOf(EcsWorld world, string id)
    {
        var buffer = new EntityView[16];
        int n = world.GetEntitiesInRange(new Vec2(0, 0), 100f, buffer.AsSpan());
        Assert.True(n <= buffer.Length, "view buffer undersized");
        for (int i = 0; i < n; i++)
        {
            if (buffer[i].Id == id) return buffer[i];
        }

        Assert.Fail($"entity {id} not in range");
        return default;
    }
}
