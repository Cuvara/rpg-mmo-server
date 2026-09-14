using GameServer.Net;
using GameServer.Snapshot;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit;
using SimAction = Shared.GameLogic.Components.EntityAction;
using WireAction = RpgMmo.Wire.V1.EntityAction;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The two entity-state fields added for facing and action (wire.proto fields 10 and 11).
/// </summary>
/// <remarks>
/// <para>
/// Two properties matter more than the round-trip, and both are covered here:
/// </para>
/// <para>
/// <b>1. No real angle encodes to the reserved zero.</b> The whole reason facing is a
/// biased integer rather than a float is that 0.0 radians is a perfectly ordinary facing
/// (due east) and proto3 elides a zero — so a float would make "east" and "not sent"
/// identical bytes. If the bias is ever lost, that collision returns silently.
/// </para>
/// <para>
/// <b>2. The delta encoder resends an entity whose facing or action changed.</b>
/// <c>SnapshotDeltaState.SentView</c> is the ONLY thing deciding whether a delta carries
/// an entity. An entity that turns on the spot, or starts attacking without moving,
/// changes nothing else — so a field missing from that comparison produces a client
/// showing a stale facing until the next keyframe, up to 30 ticks later, with no error
/// on either side.
/// </para>
/// </remarks>
public class FacingAndActionTests
{
    // ---- The encoding ------------------------------------------------------

    /// <summary>
    /// The property the whole design rests on: nothing representable collides with the
    /// reserved "not sent" value.
    /// </summary>
    [Fact]
    public void NoRealAngleEncodesToTheReservedZero()
    {
        // Swept far more finely than the encoding's own 65536-step resolution.
        for (int i = 0; i <= 200_000; i++)
        {
            float angle = (float)(i / 200_000.0 * System.Math.PI * 2);
            Assert.True(FacingCodec.FromRadians(angle) != FacingCodec.NotSent,
                $"angle {angle} encoded to the reserved zero — 'east' and 'not sent' have collided");
        }
    }

    /// <summary>Due east is 0.0 radians — the exact value a float would elide.</summary>
    [Fact]
    public void DueEastIsOneNotZero()
    {
        Assert.Equal(1u, FacingCodec.FromRadians(0f));

        Assert.True(FacingCodec.TryToRadians(1u, out float radians));
        Assert.Equal(0f, radians);
    }

    [Fact]
    public void FacingRoundTripsWithinOneStep()
    {
        double tolerance = 2 * System.Math.PI / FacingCodec.BradSteps * 1.5;

        for (int i = 0; i < 3600; i++)
        {
            double want = i / 3600.0 * System.Math.PI * 2;

            uint brad = FacingCodec.FromRadians((float)want);
            Assert.True(FacingCodec.TryToRadians(brad, out float got));

            double diff = System.Math.Abs(got - want);
            if (diff > System.Math.PI) diff = 2 * System.Math.PI - diff; // wrap-around
            Assert.True(diff <= tolerance, $"angle {want} round-tripped to {got} (diff {diff})");
        }
    }

    /// <summary>A zero-length direction has no angle, so it reports "not sent".</summary>
    [Fact]
    public void ZeroDirectionIsNotSent()
    {
        Assert.Equal(FacingCodec.NotSent, FacingCodec.FromDirection(0f, 0f));
        Assert.Equal(FacingCodec.NotSent, FacingCodec.FromRadians(float.NaN));
        Assert.Equal(FacingCodec.NotSent, FacingCodec.FromRadians(float.PositiveInfinity));
    }

    /// <summary>Cardinal directions land where a renderer expects them.</summary>
    [Theory]
    [InlineData(1f, 0f, 0.0)]                     // east
    [InlineData(0f, 1f, System.Math.PI / 2)]      // north
    [InlineData(-1f, 0f, System.Math.PI)]         // west
    public void CardinalDirectionsEncodeToTheExpectedAngle(float x, float y, double expected)
    {
        uint brad = FacingCodec.FromDirection(x, y);
        Assert.True(FacingCodec.TryToRadians(brad, out float got));
        Assert.True(System.Math.Abs(got - expected) < 0.001,
            $"direction ({x},{y}) decoded to {got}, want {expected}");
    }

    /// <summary>
    /// The receiver rule. Zero must report "no value" rather than handing back a
    /// plausible-looking 0 radians, and an out-of-range value must be refused rather
    /// than aliased onto a wrong direction — a wrong facing is far harder to notice
    /// than an absent one.
    /// </summary>
    [Fact]
    public void ZeroAndOutOfRangeAreRefused()
    {
        Assert.False(FacingCodec.TryToRadians(FacingCodec.NotSent, out _));
        Assert.False(FacingCodec.TryToRadians(FacingCodec.BradSteps + 1, out _));
        Assert.True(FacingCodec.TryToRadians(FacingCodec.BradSteps, out _),
            "the top of the legal range must still decode");
    }

    /// <summary>
    /// Idle is 1, not 0, so "standing still" and "no action reported" stay distinct.
    /// These values are on the wire and are frozen.
    /// </summary>
    [Fact]
    public void ActionZeroIsReservedAndIdleIsOne()
    {
        Assert.Equal(0, (int)SimAction.Unspecified);
        Assert.Equal(1, (int)SimAction.Idle);
        Assert.Equal(2, (int)SimAction.Moving);
        Assert.Equal(3, (int)SimAction.Attacking);
        Assert.Equal(4, (int)SimAction.Dead);

        // The simulation enum and the generated wire enum must agree numerically, or
        // the cast in SnapshotDeltaState.ToMsg silently relabels every action.
        Assert.Equal((int)SimAction.Unspecified, (int)WireAction.Unspecified);
        Assert.Equal((int)SimAction.Idle, (int)WireAction.Idle);
        Assert.Equal((int)SimAction.Moving, (int)WireAction.Moving);
        Assert.Equal((int)SimAction.Attacking, (int)WireAction.Attacking);
        Assert.Equal((int)SimAction.Dead, (int)WireAction.Dead);
    }

    // ---- The delta encoder -------------------------------------------------

    private static EntityView View(
        int key, float x, uint facingBrad, SimAction action, float speed = 4f) =>
        new(key, $"e{key}", "player", new Vec2(x, 0f), 100, 100, speed, facingBrad, action);

    private static SnapshotMessage Encode(
        SnapshotDeltaState state, ulong tick, params EntityView[] views) =>
        state.Encode(tick, ackTick: 0, views.AsSpan(), keyframeInterval: 1000, intern: true);

    /// <summary>
    /// An entity that turns on the spot changes nothing else — so if facing is missing
    /// from the change comparison it is dropped from the delta and the client keeps
    /// rendering the old direction until the next keyframe.
    /// </summary>
    [Fact]
    public void TurningOnTheSpotResendsTheEntity()
    {
        var state = new SnapshotDeltaState(0);
        uint east = FacingCodec.FromDirection(1f, 0f);
        uint north = FacingCodec.FromDirection(0f, 1f);

        // Keyframe establishes the baseline.
        SnapshotMessage first = Encode(state, 1, View(1, 0f, east, SimAction.Idle));
        Assert.True(first.Full);

        // Nothing changed: the entity must be omitted, or deltas are pointless.
        SnapshotMessage unchanged = Encode(state, 2, View(1, 0f, east, SimAction.Idle));
        Assert.False(unchanged.Full);
        Assert.Empty(unchanged.Entities);

        // Only the facing changed — same position, same hp, same action.
        SnapshotMessage turned = Encode(state, 3, View(1, 0f, north, SimAction.Idle));
        Assert.False(turned.Full);
        Assert.Single(turned.Entities);
        Assert.Equal(north, turned.Entities[0].FacingBrad);
    }

    /// <summary>
    /// The same for action: an entity that starts attacking without moving changes
    /// nothing else.
    /// </summary>
    [Fact]
    public void StartingToAttackWithoutMovingResendsTheEntity()
    {
        var state = new SnapshotDeltaState(0);
        uint east = FacingCodec.FromDirection(1f, 0f);

        Assert.True(Encode(state, 1, View(1, 0f, east, SimAction.Idle)).Full);
        Assert.Empty(Encode(state, 2, View(1, 0f, east, SimAction.Idle)).Entities);

        SnapshotMessage attacking = Encode(state, 3, View(1, 0f, east, SimAction.Attacking));
        Assert.Single(attacking.Entities);
        Assert.Equal(WireAction.Attacking, attacking.Entities[0].Action);
    }

    /// <summary>
    /// Both fields ride EVERY mention, including handle-only ones. Sending them only
    /// alongside the id would leave them correct once per keyframe interval and stale
    /// in between — the same rule as <c>speed</c>.
    /// </summary>
    [Fact]
    public void BothFieldsRideHandleOnlyMentions()
    {
        var state = new SnapshotDeltaState(0);
        uint east = FacingCodec.FromDirection(1f, 0f);
        uint north = FacingCodec.FromDirection(0f, 1f);

        SnapshotMessage keyframe = Encode(state, 1, View(1, 0f, east, SimAction.Idle));
        Assert.Equal("e1", keyframe.Entities[0].Id);
        uint handle = keyframe.Entities[0].Handle;
        Assert.NotEqual(0u, handle);

        // A delta mentioning the same entity by handle only.
        SnapshotMessage delta = Encode(state, 2, View(1, 5f, north, SimAction.Moving));
        Assert.Single(delta.Entities);
        EntitySnapshot e = delta.Entities[0];

        Assert.Equal("", e.Id);              // interned: handle only
        Assert.Equal(handle, e.Handle);
        Assert.Equal(north, e.FacingBrad);   // and yet fully stated
        Assert.Equal(WireAction.Moving, e.Action);
    }

    /// <summary>
    /// An entity the server has no facing for must put the reserved zero on the wire,
    /// not a fabricated east. Protobuf then elides it, which is the point.
    /// </summary>
    [Fact]
    public void UnknownFacingStaysZeroRatherThanBecomingEast()
    {
        var state = new SnapshotDeltaState(0);

        SnapshotMessage msg = Encode(
            state, 1, View(1, 0f, FacingCodec.NotSent, SimAction.Unspecified));

        Assert.Equal(0u, msg.Entities[0].FacingBrad);
        Assert.Equal(WireAction.Unspecified, msg.Entities[0].Action);
        Assert.False(FacingCodec.TryToRadians(msg.Entities[0].FacingBrad, out _));
    }
}
