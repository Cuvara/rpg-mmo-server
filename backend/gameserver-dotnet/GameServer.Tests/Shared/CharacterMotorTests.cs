using Shared.GameLogic.World;

namespace GameServer.Tests.Shared;

/// <summary>
/// Kinematic capsule motor (ADR-28 decision 2): walking, jumping, landing, step-up,
/// walls, slopes, bounds.
/// </summary>
public class CharacterMotorTests
{
    private const float Speed = 5f;
    private static readonly float Dt = MovementSystem.DeltaTimeForTickRate(GameConstants.DefaultTickRate);
    private static readonly MotorParams P = MotorParams.Default;
    private static readonly MapBounds Wide = MapBounds.FromSize(100f, 100f);

    private static MapGeometry Flat => MapGeometry.Flat(Wide);

    private static MapGeometry WithBoxes(params StaticBox[] boxes) => new(Wide, null, boxes, null, null);

    private static MotorState Run(MapGeometry geo, MotorState s, float mx, float my, int ticks, int jumpTicks = 0)
    {
        for (int i = 0; i < ticks; i++)
        {
            CharacterMotor.Step(s, mx, my, i < jumpTicks, Speed, Dt, geo, P, out s);
        }

        return s;
    }

    // ── Flat ground: identical to the protocol 2 path ────────────────────

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, -0.25f)]
    [InlineData(-0.6f, 0.8f)]
    public void FlatWalk_MatchesPlanarMovementBitForBit(float mx, float my)
    {
        var entity = new EntityState { Id = "e", Type = "player", Position = new Vec2(1.25f, -3.5f), Speed = Speed, Hp = 1, MaxHp = 1 };
        MoveResult planar = MovementSystem.TryMove(entity, mx, my, Dt, Wide, out Vec2 expected);

        MoveResult motor = CharacterMotor.Step(MotorState.StandingAt(new Vec3(1.25f, -3.5f, 0f)),
            mx, my, false, Speed, Dt, Flat, P, out MotorState next);

        Assert.Equal(planar, motor);
        Assert.Equal(expected.X, next.Position.X);
        Assert.Equal(expected.Y, next.Position.Y);
        Assert.Equal(0f, next.Position.Z);
        Assert.True(next.Grounded);
    }

    [Fact]
    public void RejectedInput_DoesNotMove_ButGravityStillRuns()
    {
        var air = new MotorState(new Vec3(0f, 0f, 2f), 0f, false);
        Assert.Equal(MoveResult.Rejected,
            CharacterMotor.Step(air, float.NaN, 0f, false, Speed, Dt, Flat, P, out MotorState next));
        Assert.Equal(0f, next.Position.X);
        Assert.True(next.Position.Z < 2f);
        Assert.True(next.VelocityZ < 0f);
    }

    [Fact]
    public void ZeroSpeed_IsBlocked_ZeroDt_LeavesStateUntouched()
    {
        var s = MotorState.StandingAt(new Vec3(1f, 1f, 0f));
        Assert.Equal(MoveResult.Blocked, CharacterMotor.Step(s, 1f, 0f, false, 0f, Dt, Flat, P, out MotorState a));
        Assert.Equal(s.Position, a.Position);
        Assert.Equal(MoveResult.Blocked, CharacterMotor.Step(s, 1f, 0f, true, Speed, 0f, Flat, P, out MotorState b));
        Assert.Equal(s.Position, b.Position);
        Assert.True(b.Grounded);
    }

    // ── Jumping ──────────────────────────────────────────────────────────

    [Fact]
    public void Jump_RisesToApex_ThenLandsAndSnapsToGround()
    {
        var s = MotorState.StandingAt(Vec3.Zero);
        float apex = 0f;
        bool landed = false;
        int ticks = 0;
        for (; ticks < 40 && !landed; ticks++)
        {
            CharacterMotor.Step(s, 0f, 0f, ticks == 0, Speed, Dt, Flat, P, out s);
            if (s.Position.Z > apex) apex = s.Position.Z;
            landed = s.Grounded;
        }

        float theoretical = P.JumpSpeed * P.JumpSpeed / (2f * P.Gravity); // 1.6
        Assert.InRange(apex, theoretical * 0.8f, theoretical);
        Assert.True(landed);
        Assert.Equal(0f, s.Position.Z);
        Assert.Equal(0f, s.VelocityZ);
        Assert.InRange(ticks, 11, 14); // 0.8 s of airtime at 15 Hz
    }

    [Fact]
    public void Jump_IgnoredWhenAirborne()
    {
        var air = new MotorState(new Vec3(0f, 0f, 1f), -1f, false);
        CharacterMotor.Step(air, 0f, 0f, true, Speed, Dt, Flat, P, out MotorState next);
        Assert.True(next.VelocityZ < -1f);
    }

    [Fact]
    public void HeldJump_DoesNotFly()
    {
        // Bunny hops at most; never above one jump's apex.
        var probe = MotorState.StandingAt(Vec3.Zero);
        float apex = 0f;
        for (int i = 0; i < 60; i++)
        {
            CharacterMotor.Step(probe, 0f, 0f, true, Speed, Dt, Flat, P, out probe);
            if (probe.Position.Z > apex) apex = probe.Position.Z;
        }

        Assert.True(apex <= 1.6f);
    }

    [Fact]
    public void Ceiling_StopsTheRise()
    {
        var geo = WithBoxes(new StaticBox(-2f, -2f, 2.5f, 2f, 2f, 3f));
        var s = MotorState.StandingAt(Vec3.Zero);
        float maxHead = 0f;
        for (int i = 0; i < 15; i++)
        {
            CharacterMotor.Step(s, 0f, 0f, i == 0, Speed, Dt, geo, P, out s);
            maxHead = MathF.Max(maxHead, s.Position.Z + P.CapsuleHeight);
        }

        Assert.True(maxHead <= 2.5f + 1e-5f);
        Assert.True(s.Grounded);
    }

    // ── Boxes ────────────────────────────────────────────────────────────

    [Fact]
    public void StepUp_OntoLowBox()
    {
        var geo = WithBoxes(new StaticBox(2f, -2f, 0f, 4f, 2f, 0.3f));
        MotorState s = Run(geo, MotorState.StandingAt(new Vec3(1f, 0f, 0f)), 1f, 0f, 6);
        Assert.True(s.Position.X > 2f);
        Assert.Equal(0.3f, s.Position.Z);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void TallBox_BlocksWalking_ButCanBeJumpedOnto()
    {
        var crate = new StaticBox(2f, -2f, 0f, 4f, 2f, 1f);
        var geo = WithBoxes(crate);

        MotorState walked = Run(geo, MotorState.StandingAt(new Vec3(1f, 0f, 0f)), 1f, 0f, 10);
        Assert.True(walked.Position.X + P.CapsuleRadius <= crate.MinX);
        Assert.Equal(0f, walked.Position.Z);

        MotorState jumped = Run(geo, MotorState.StandingAt(new Vec3(1f, 0f, 0f)), 1f, 0f, 10, jumpTicks: 1);
        Assert.Equal(1f, jumped.Position.Z);
        Assert.True(jumped.Grounded);
    }

    [Fact]
    public void Wall_Blocks_AndDiagonalSlidesAlongIt()
    {
        var wall = new StaticBox(2f, -10f, 0f, 2.5f, 10f, 3f);
        var geo = WithBoxes(wall);

        MotorState blocked = Run(geo, MotorState.StandingAt(Vec3.Zero), 1f, 0f, 20);
        Assert.True(blocked.Position.X + P.CapsuleRadius <= wall.MinX);
        Assert.True(blocked.Position.X > 1f);

        MotorState slid = Run(geo, MotorState.StandingAt(new Vec3(1.5f, 0f, 0f)), 1f, 1f, 5);
        Assert.Equal(1.5f, slid.Position.X);
        Assert.True(slid.Position.Y > 1f);
    }

    [Fact]
    public void WalkingOffALedge_FallsAndLands()
    {
        var geo = WithBoxes(new StaticBox(2f, -2f, 0f, 4f, 2f, 1f));
        var s = MotorState.StandingAt(new Vec3(3.8f, 0f, 1f));
        s = Run(geo, s, 1f, 0f, 3);
        Assert.False(s.Grounded);
        Assert.True(s.Position.Z < 1f);
        s = Run(geo, s, 1f, 0f, 10);
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Z);
    }

    [Fact]
    public void SpawnedInsideABox_CanWalkOut()
    {
        var geo = WithBoxes(new StaticBox(-1f, -1f, 0f, 1f, 1f, 3f));
        MotorState s = Run(geo, MotorState.StandingAt(Vec3.Zero), 1f, 0f, 10);
        Assert.True(s.Position.X > 1f);
    }

    // ── Terrain ──────────────────────────────────────────────────────────

    private static MapGeometry Ramp(float rise)
    {
        var h = new float[6 * 5];
        for (int r = 0; r < 5; r++) for (int c = 0; c < 6; c++) h[r * 6 + c] = c * rise;
        return new MapGeometry(Wide, new HeightField(0f, -2f, 1f, 6, 5, h), null, null, null);
    }

    [Fact]
    public void GentleSlope_IsClimbedAndFollowed()
    {
        var geo = Ramp(0.5f);
        MotorState s = Run(geo, MotorState.StandingAt(new Vec3(0.5f, 0f, 0.25f)), 1f, 0f, 6);
        Assert.Equal(2.5f, s.Position.X, precision: 4);
        Assert.Equal(geo.GroundHeight(s.Position.X, s.Position.Y), s.Position.Z);
        Assert.True(s.Grounded);

        MotorState down = Run(geo, MotorState.StandingAt(new Vec3(4f, 0f, 2f)), -1f, 0f, 6);
        Assert.True(down.Grounded);
        Assert.Equal(geo.GroundHeight(down.Position.X, down.Position.Y), down.Position.Z);
    }

    [Fact]
    public void SteepSlope_Blocks()
    {
        MotorState s = Run(Ramp(3f), MotorState.StandingAt(new Vec3(0.5f, 0f, 1.5f)), 1f, 0f, 5);
        Assert.Equal(0.5f, s.Position.X);
        Assert.Equal(1.5f, s.Position.Z);
    }

    [Fact]
    public void DiagonalUpWalkableSlope_IsNotBlocked()
    {
        MotorState s = Run(Ramp(0.6f), MotorState.StandingAt(new Vec3(0.5f, -1f, 0.3f)), 1f, 1f, 4);
        Assert.True(s.Position.X > 1.4f);
        Assert.True(s.Position.Y > -0.1f);
    }

    // ── Bounds ───────────────────────────────────────────────────────────

    [Fact]
    public void ClampsToBounds()
    {
        var geo = MapGeometry.Flat(MapBounds.FromSize(10f, 10f));
        CharacterMotor.Step(MotorState.StandingAt(new Vec3(4.9f, 4.9f, 0f)), 1f, 1f, false, 50f, Dt, geo, P, out MotorState s);
        Assert.Equal(5f, s.Position.X);
        Assert.Equal(5f, s.Position.Y);
    }

    [Fact]
    public void Step_IsDeterministic()
    {
        var geo = Ramp(0.7f);
        MotorState a = Run(geo, MotorState.StandingAt(new Vec3(0.3f, -1.7f, 0.21f)), 0.83f, 0.41f, 25, jumpTicks: 3);
        MotorState b = Run(geo, MotorState.StandingAt(new Vec3(0.3f, -1.7f, 0.21f)), 0.83f, 0.41f, 25, jumpTicks: 3);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(BitConverter.SingleToInt32Bits(a.VelocityZ), BitConverter.SingleToInt32Bits(b.VelocityZ));
    }

    [Fact]
    public void Step_DoesNotAllocate()
    {
        var geo = WithBoxes(new StaticBox(2f, -2f, 0f, 4f, 2f, 0.3f), new StaticBox(6f, -2f, 0f, 7f, 2f, 3f));
        var s = MotorState.StandingAt(Vec3.Zero);
        CharacterMotor.Step(s, 1f, 0.2f, true, Speed, Dt, geo, P, out s); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            CharacterMotor.Step(s, 1f, 0.2f, (i & 7) == 0, Speed, Dt, geo, P, out s);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
