using Shared.GameLogic.World;

namespace GameServer.Tests.Shared;

/// <summary>
/// Skillshot projectiles (ADR-29): spawn from an aim point, straight flight, world hits,
/// range expiry, and segment-vs-capsule target hits.
/// </summary>
public class ProjectileLogicTests
{
    private static readonly float Dt = MovementSystem.DeltaTimeForTickRate(GameConstants.DefaultTickRate);
    private static readonly MapBounds Wide = MapBounds.FromSize(100f, 100f);
    private const float R = CharacterMotor.DefaultCapsuleRadius;
    private const float H = CharacterMotor.DefaultCapsuleHeight;

    // ── Spawn ────────────────────────────────────────────────────────────

    [Fact]
    public void Spawn_DerivesVelocityFromOriginToAim()
    {
        Assert.True(ProjectileLogic.Spawn(new Vec3(0f, 0f, 1f), new Vec3(0f, 3f, 5f), 15f, 0.2f, 20f, out var s));
        Assert.Equal(new Vec3(0f, 0f, 1f), s.Position);
        Assert.Equal(0f, s.Velocity.X);
        Assert.Equal(9f, s.Velocity.Y, precision: 5);
        Assert.Equal(12f, s.Velocity.Z, precision: 5);
        Assert.Equal(15f, s.Velocity.Magnitude, precision: 4);
        Assert.Equal(20f, s.RemainingRange);
        Assert.Equal(0.2f, s.Radius);
    }

    [Theory]
    [InlineData(1f, 2f, 1f, 20f, 0.2f, 10f)]            // zero-length aim
    [InlineData(float.NaN, 0f, 1f, 20f, 0.2f, 10f)]     // NaN aim
    [InlineData(5f, 0f, 1f, 0f, 0.2f, 10f)]             // zero speed
    [InlineData(5f, 0f, 1f, 20f, -0.1f, 10f)]           // negative radius
    [InlineData(5f, 0f, 1f, 20f, 0.2f, 0f)]             // zero range
    [InlineData(5f, 0f, 1f, float.PositiveInfinity, 0.2f, 10f)]
    public void Spawn_Refuses(float ax, float ay, float az, float speed, float radius, float range)
    {
        Assert.False(ProjectileLogic.Spawn(new Vec3(1f, 2f, 1f), new Vec3(ax, ay, az), speed, radius, range, out var s));
        Assert.Equal(default(ProjectileState).Position, s.Position);
    }

    // ── Step ─────────────────────────────────────────────────────────────

    [Fact]
    public void Step_TravelsAndSpendsRange()
    {
        var s = new ProjectileState(new Vec3(0f, 0f, 1f), new Vec3(20f, 0f, 0f), 0.25f, 30f);
        Assert.True(ProjectileLogic.Step(s, Dt, MapGeometry.Flat(Wide), out var next, out bool hitWorld));
        Assert.False(hitWorld);
        Assert.Equal(20f * Dt, next.Position.X, precision: 5);
        Assert.Equal(30f - 20f * Dt, next.RemainingRange, precision: 4);
    }

    [Fact]
    public void Step_StopsExactlyAtRange()
    {
        var geo = MapGeometry.Flat(Wide);
        var s = new ProjectileState(new Vec3(0f, 0f, 1f), new Vec3(20f, 0f, 0f), 0.25f, 10f);
        int ticks = 0;
        bool alive = true;
        while (alive && ticks < 100)
        {
            alive = ProjectileLogic.Step(s, Dt, geo, out s, out bool hit);
            Assert.False(hit);
            ticks++;
        }

        Assert.Equal(8, ticks); // 10 / (20/15) = 7.5 -> the 8th tick is shortened
        Assert.Equal(10f, s.Position.X, precision: 4);
        Assert.Equal(0f, s.RemainingRange);
    }

    [Fact]
    public void Step_WallStopsProjectile()
    {
        var geo = new MapGeometry(Wide, null, new[] { new StaticBox(2f, -10f, 0f, 2.5f, 10f, 3f) }, null, null);
        var s = new ProjectileState(new Vec3(0f, 0f, 1f), new Vec3(20f, 0f, 0f), 0.25f, 30f);
        Assert.True(ProjectileLogic.Step(s, Dt, geo, out s, out bool hit)); // 1.33: clear
        Assert.False(hit);
        Assert.False(ProjectileLogic.Step(s, Dt, geo, out s, out hit));
        Assert.True(hit);
        Assert.Equal(1.75f, s.Position.X, precision: 4);
    }

    [Fact]
    public void Step_GroundStopsDescendingProjectile()
    {
        var geo = MapGeometry.Flat(Wide);
        var s = new ProjectileState(new Vec3(0f, 0f, 2f), new Vec3(15f, 0f, -6f), 0.2f, 30f);
        bool hit = false;
        for (int i = 0; i < 20 && !hit; i++) ProjectileLogic.Step(s, Dt, geo, out s, out hit);
        Assert.True(hit);
        Assert.Equal(0.2f, s.Position.Z, precision: 4);
    }

    [Fact]
    public void Step_LeavingBoundsEndsIt()
    {
        var geo = MapGeometry.Flat(MapBounds.FromSize(10f, 10f));
        var s = new ProjectileState(new Vec3(4.5f, 0f, 1f), new Vec3(20f, 0f, 0f), 0.25f, 30f);
        Assert.False(ProjectileLogic.Step(s, Dt, geo, out _, out bool hit));
        Assert.False(hit);
    }

    [Fact]
    public void Step_DoesNotAllocate()
    {
        var geo = new MapGeometry(Wide, null, new[] { new StaticBox(40f, -1f, 0f, 41f, 1f, 3f) }, null, null);
        var s = new ProjectileState(new Vec3(0f, 0f, 1f), new Vec3(20f, 0.1f, 0f), 0.25f, 30f);
        ProjectileLogic.Step(s, Dt, geo, out _, out _);
        ProjectileLogic.SegmentCapsuleHit(Vec3.Zero, new Vec3(5f, 0f, 1f), 0.2f, new Vec3(3f, 0f, 0f), R, H, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            ProjectileLogic.Step(s, Dt, geo, out var next, out _);
            ProjectileLogic.SegmentCapsuleHit(s.Position, next.Position, s.Radius, new Vec3(3f, 0f, 0f), R, H, out _);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    // ── Segment vs capsule ───────────────────────────────────────────────

    [Fact]
    public void Capsule_DirectHit_ReportsFirstContactNotClosestApproach()
    {
        Assert.True(ProjectileLogic.SegmentCapsuleHit(
            new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 0.25f, new Vec3(5f, 0f, 0f), R, H, out float t));
        // Contact when the centres are 0.65 apart: x = 4.35.
        Assert.Equal(0.435f, t, precision: 3);
    }

    [Theory]
    [InlineData(0f, 0f, 2.5f, 10f, 0f, 2.5f)]  // over the head
    [InlineData(0f, 1f, 1f, 10f, 1f, 1f)]      // beside
    [InlineData(0f, 0f, 1f, 3f, 0f, 1f)]       // stops short
    [InlineData(0f, 0f, -1f, 10f, 0f, -1f)]    // under the feet
    public void Capsule_Misses(float ax, float ay, float az, float bx, float by, float bz)
    {
        Assert.False(ProjectileLogic.SegmentCapsuleHit(
            new Vec3(ax, ay, az), new Vec3(bx, by, bz), 0.25f, new Vec3(5f, 0f, 0f), R, H, out float t));
        Assert.Equal(1f, t);
    }

    [Fact]
    public void Capsule_HeadEndcap_IsRounded()
    {
        // Straight down onto the head: contact when the sphere reaches top + radius.
        Assert.True(ProjectileLogic.SegmentCapsuleHit(
            new Vec3(5f, 0f, 4f), new Vec3(5f, 0f, 0f), 0.1f, new Vec3(5f, 0f, 0f), R, H, out float t));
        Assert.Equal((4f - 1.9f) / 4f, t, precision: 3);

        // Near the top corner the rounded cap is missed where a cylinder would be hit.
        Assert.False(ProjectileLogic.SegmentCapsuleHit(
            new Vec3(0f, 0.38f, 1.75f), new Vec3(10f, 0.38f, 1.75f), 0.01f, new Vec3(5f, 0f, 0f), R, H, out _));
    }

    [Fact]
    public void Capsule_StartInside_IsHitAtZero_AndZeroLengthSegmentWorks()
    {
        Assert.True(ProjectileLogic.SegmentCapsuleHit(
            new Vec3(5f, 0f, 1f), new Vec3(9f, 0f, 1f), 0.1f, new Vec3(5f, 0f, 0f), R, H, out float t));
        Assert.Equal(0f, t);
        Assert.True(ProjectileLogic.SegmentCapsuleHit(
            new Vec3(5.5f, 0f, 1f), new Vec3(5.5f, 0f, 1f), 0.2f, new Vec3(5f, 0f, 0f), R, H, out t));
        Assert.Equal(0f, t);
    }

    [Fact]
    public void Capsule_OrderingByT_PicksTheNearerTarget()
    {
        var a = new Vec3(0f, 0f, 1f);
        var b = new Vec3(20f, 0f, 1f);
        ProjectileLogic.SegmentCapsuleHit(a, b, 0.2f, new Vec3(12f, 0.3f, 0f), R, H, out float far);
        ProjectileLogic.SegmentCapsuleHit(a, b, 0.2f, new Vec3(6f, -0.3f, 0f), R, H, out float near);
        Assert.True(near < far);
    }
}
