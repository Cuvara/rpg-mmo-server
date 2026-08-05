namespace GameServer.Tests.Shared;

/// <summary>
/// Movement model: direction * speed * dt, normalized, bounds-clamped, deterministic.
/// </summary>
public class MovementSystemTests
{
    private const float Eps = 1e-4f;

    private static MapBounds Big => MapBounds.FromSize(10_000f, 10_000f);

    // ─────────────────────────────── ResolveDirection ───────────────────────────────

    [Theory]
    // raw input                    expected result            expected direction
    [InlineData(1f, 0f, MoveResult.Accepted, 1f, 0f)]
    [InlineData(-1f, 0f, MoveResult.Accepted, -1f, 0f)]
    [InlineData(0f, 1f, MoveResult.Accepted, 0f, 1f)]
    [InlineData(0f, -1f, MoveResult.Accepted, 0f, -1f)]
    [InlineData(0.5f, 0f, MoveResult.Accepted, 0.5f, 0f)]        // analog half-stick preserved
    [InlineData(0f, 0f, MoveResult.None, 0f, 0f)]
    [InlineData(1f, 1f, MoveResult.Clamped, 0.70710678f, 0.70710678f)]   // raw diagonal
    [InlineData(-1f, 1f, MoveResult.Clamped, -0.70710678f, 0.70710678f)]
    [InlineData(1.2f, 0f, MoveResult.Clamped, 1f, 0f)]           // slight overshoot -> unit
    [InlineData(5f, 0f, MoveResult.Rejected, 0f, 0f)]            // grossly invalid
    [InlineData(100f, 100f, MoveResult.Rejected, 0f, 0f)]
    [InlineData(float.NaN, 0f, MoveResult.Rejected, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, MoveResult.Rejected, 0f, 0f)]
    public void ResolveDirection_Matrix(float mx, float my, MoveResult expected, float ex, float ey)
    {
        var result = MovementSystem.ResolveDirection(mx, my, out var dir);

        Assert.Equal(expected, result);
        Assert.Equal(ex, dir.X, precision: 4);
        Assert.Equal(ey, dir.Y, precision: 4);
    }

    [Fact]
    public void ResolveDirection_NeverReturnsMagnitudeAboveOne()
    {
        float[] samples = { -1.4f, -1f, -0.3f, 0f, 0.3f, 1f, 1.4f };
        foreach (var x in samples)
        {
            foreach (var y in samples)
            {
                var result = MovementSystem.ResolveDirection(x, y, out var dir);
                if (result is MoveResult.Accepted or MoveResult.Clamped)
                    Assert.True(dir.Magnitude <= 1f + Eps, $"({x},{y}) -> |{dir}| = {dir.Magnitude}");
            }
        }
    }

    // ─────────────────────────────── Speed / dt ───────────────────────────────

    [Fact]
    public void Diagonal_TravelsSameDistanceAsCardinal()
    {
        var entity = TestHelpers.CreatePlayer("p", speed: 6f);
        float dt = MovementSystem.DeltaTimeForTickRate(15);

        MovementSystem.TryMove(in entity, 1f, 0f, dt, Big, out var cardinal);
        MovementSystem.TryMove(in entity, 1f, 1f, dt, Big, out var diagonal);

        float cardinalDist = Vec2.Distance(entity.Position, cardinal);
        float diagonalDist = Vec2.Distance(entity.Position, diagonal);

        Assert.Equal(cardinalDist, diagonalDist, precision: 4);
        Assert.Equal(6f * dt, cardinalDist, precision: 4);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(60)]
    public void SameSpeed_RegardlessOfTickRate(int tickRate)
    {
        // Simulate exactly one second of holding "right" at the given tick rate.
        var entity = TestHelpers.CreatePlayer("p", speed: 7f);
        float dt = MovementSystem.DeltaTimeForTickRate(tickRate);

        for (int i = 0; i < tickRate; i++)
        {
            MovementSystem.TryMove(in entity, 1f, 0f, dt, Big, out var next);
            entity.Position = next;
        }

        // 7 units/second * 1 second = 7 units, independent of tick rate.
        Assert.Equal(7f, entity.Position.X, precision: 3);
        Assert.Equal(0f, entity.Position.Y, precision: 3);
    }

    [Fact]
    public void DeltaTimeForTickRate_MatchesReciprocal()
    {
        Assert.Equal(1f / 15f, MovementSystem.DeltaTimeForTickRate(15), precision: 6);
        Assert.Equal(0f, MovementSystem.DeltaTimeForTickRate(0));
        Assert.Equal(0f, MovementSystem.DeltaTimeForTickRate(-10));
    }

    [Fact]
    public void SpeedScales_DisplacementLinearly()
    {
        float dt = 0.1f;
        var slow = TestHelpers.CreatePlayer("slow", speed: 2f);
        var fast = TestHelpers.CreatePlayer("fast", speed: 8f);

        MovementSystem.TryMove(in slow, 1f, 0f, dt, Big, out var slowPos);
        MovementSystem.TryMove(in fast, 1f, 0f, dt, Big, out var fastPos);

        Assert.Equal(0.2f, slowPos.X, precision: 4);
        Assert.Equal(0.8f, fastPos.X, precision: 4);
    }

    [Fact]
    public void SingleInput_IsNotATeleport()
    {
        // Regression guard for the old model: one input of (1,0) used to move a full
        // world unit. It must now move speed*dt = 5/15 units.
        var entity = TestHelpers.CreatePlayer("p", speed: 5f);
        MovementSystem.TryMove(in entity, 1f, 0f, 1f / 15f, Big, out var pos);

        Assert.Equal(5f / 15f, pos.X, precision: 4);
        Assert.True(pos.X < 1f);
    }

    // ─────────────────────────────── TryMove gating ───────────────────────────────

    [Fact]
    public void TryMove_ZeroInput_DoesNotMove()
    {
        var entity = TestHelpers.CreatePlayer("p", x: 3f, y: 4f, speed: 5f);
        var result = MovementSystem.TryMove(in entity, 0f, 0f, 0.1f, Big, out var pos);

        Assert.Equal(MoveResult.None, result);
        Assert.Equal(entity.Position, pos);
    }

    [Fact]
    public void TryMove_RejectedInput_DoesNotMove()
    {
        var entity = TestHelpers.CreatePlayer("p", x: 3f, y: 4f, speed: 5f);
        var result = MovementSystem.TryMove(in entity, 99f, 0f, 0.1f, Big, out var pos);

        Assert.Equal(MoveResult.Rejected, result);
        Assert.Equal(entity.Position, pos);
    }

    [Fact]
    public void TryMove_DeadEntity_IsBlocked()
    {
        var entity = TestHelpers.CreatePlayer("p", x: 1f, y: 1f, speed: 5f);
        entity.Dead = true;

        var result = MovementSystem.TryMove(in entity, 1f, 0f, 0.1f, Big, out var pos);

        Assert.Equal(MoveResult.Blocked, result);
        Assert.Equal(entity.Position, pos);
    }

    [Theory]
    [InlineData(0f, 0.1f)]      // zero speed
    [InlineData(-3f, 0.1f)]     // negative speed
    [InlineData(5f, 0f)]        // zero dt
    [InlineData(5f, -0.1f)]     // negative dt
    public void TryMove_NonPositiveSpeedOrDt_IsBlocked(float speed, float dt)
    {
        var entity = TestHelpers.CreatePlayer("p", x: 2f, y: 2f, speed: speed);
        var result = MovementSystem.TryMove(in entity, 1f, 0f, dt, Big, out var pos);

        Assert.Equal(MoveResult.Blocked, result);
        Assert.Equal(entity.Position, pos);
    }

    [Fact]
    public void TryMove_HugeDt_IsClampedToMaxDeltaTime()
    {
        var entity = TestHelpers.CreatePlayer("p", speed: 10f);
        MovementSystem.TryMove(in entity, 1f, 0f, 60f, Big, out var pos);

        Assert.Equal(10f * GameConstants.MaxDeltaTime, pos.X, precision: 4);
    }

    // ─────────────────────────────── Bounds ───────────────────────────────

    [Theory]
    // start position,   direction,   expected clamped coordinate on the moving axis
    [InlineData(99f, 0f, 1f, 0f, 100f, 0f)]      // east edge
    [InlineData(-99f, 0f, -1f, 0f, -100f, 0f)]   // west edge
    [InlineData(0f, 99f, 0f, 1f, 0f, 100f)]      // north edge
    [InlineData(0f, -99f, 0f, -1f, 0f, -100f)]   // south edge
    public void Integrate_ClampsAtEachEdge(float sx, float sy, float dx, float dy, float ex, float ey)
    {
        var bounds = MapBounds.FromSize(200f, 200f);   // -100..100 on both axes
        var entity = TestHelpers.CreatePlayer("p", x: sx, y: sy, speed: 50f);

        MovementSystem.TryMove(in entity, dx, dy, 0.5f, bounds, out var pos);

        Assert.Equal(ex, pos.X, precision: 4);
        Assert.Equal(ey, pos.Y, precision: 4);
        Assert.True(bounds.Contains(pos));
    }

    [Theory]
    [InlineData(1f, 1f, 100f, 100f)]       // NE corner
    [InlineData(-1f, 1f, -100f, 100f)]     // NW corner
    [InlineData(-1f, -1f, -100f, -100f)]   // SW corner
    [InlineData(1f, -1f, 100f, -100f)]     // SE corner
    public void Integrate_ClampsAtCorners(float dx, float dy, float ex, float ey)
    {
        var bounds = MapBounds.FromSize(200f, 200f);
        var entity = TestHelpers.CreatePlayer("p", x: 0f, y: 0f, speed: 1000f);

        MovementSystem.TryMove(in entity, dx, dy, 0.5f, bounds, out var pos);

        Assert.Equal(ex, pos.X, precision: 4);
        Assert.Equal(ey, pos.Y, precision: 4);
    }

    [Fact]
    public void Integrate_SlidesAlongEdge_KeepsTangentialMovement()
    {
        // Pinned to the east wall, moving north-east: X stays clamped, Y still advances.
        var bounds = MapBounds.FromSize(200f, 200f);
        var entity = TestHelpers.CreatePlayer("p", x: 100f, y: 0f, speed: 10f);

        MovementSystem.TryMove(in entity, 1f, 1f, 0.5f, bounds, out var pos);

        Assert.Equal(100f, pos.X, precision: 4);
        Assert.Equal(10f * 0.5f * 0.70710678f, pos.Y, precision: 3);
    }

    [Fact]
    public void ManyTicks_NeverEscapeBounds()
    {
        var bounds = MapBounds.FromSize(50f, 50f);
        var entity = TestHelpers.CreatePlayer("p", speed: 20f);
        float dt = MovementSystem.DeltaTimeForTickRate(15);

        for (int i = 0; i < 500; i++)
        {
            // Deterministic zig-zag (no randomness allowed in shared logic).
            float dx = (i % 3) - 1;
            float dy = ((i / 3) % 3) - 1;
            MovementSystem.TryMove(in entity, dx, dy, dt, bounds, out var next);
            entity.Position = next;
            Assert.True(bounds.Contains(entity.Position), $"escaped at i={i}: {entity.Position}");
        }
    }

    // ─────────────────────────────── MapBounds ───────────────────────────────

    [Fact]
    public void MapBounds_Default_IsCenteredThousandSquare()
    {
        var b = MapBounds.Default;
        Assert.Equal(-500f, b.MinX);
        Assert.Equal(-500f, b.MinY);
        Assert.Equal(500f, b.MaxX);
        Assert.Equal(500f, b.MaxY);
        Assert.Equal(GameConstants.DefaultMapWidth, b.Width);
        Assert.Equal(GameConstants.DefaultMapHeight, b.Height);
        Assert.True(b.Contains(Vec2.Zero));
    }

    [Fact]
    public void MapBounds_NormalizesSwappedEdges()
    {
        var b = new MapBounds(10f, 20f, -10f, -20f);
        Assert.Equal(-10f, b.MinX);
        Assert.Equal(10f, b.MaxX);
        Assert.Equal(-20f, b.MinY);
        Assert.Equal(20f, b.MaxY);
    }

    // ─────────────────────────────── Displacement audit ───────────────────────────────

    [Fact]
    public void IsDisplacementLegal_AcceptsModelOutput_RejectsTeleport()
    {
        var entity = TestHelpers.CreatePlayer("p", speed: 5f);
        float dt = MovementSystem.DeltaTimeForTickRate(15);

        MovementSystem.TryMove(in entity, 1f, 1f, dt, Big, out var legal);

        Assert.True(MovementSystem.IsDisplacementLegal(entity.Position, legal, entity.Speed, dt));
        Assert.False(MovementSystem.IsDisplacementLegal(
            entity.Position, new Vec2(50f, 0f), entity.Speed, dt));
    }

    [Fact]
    public void MaxDisplacementPerTick_IsSpeedTimesDtWithTolerance()
    {
        float expected = 5f * 0.1f * GameConstants.DisplacementTolerance;
        Assert.Equal(expected, MovementSystem.MaxDisplacementPerTick(5f, 0.1f), precision: 5);
    }

    // ─────────────────────────────── Determinism ───────────────────────────────

    [Fact]
    public void Integrate_IsDeterministic_AcrossRepeatedRuns()
    {
        static Vec2 Run()
        {
            var e = TestHelpers.CreatePlayer("p", speed: 3.3f);
            float dt = MovementSystem.DeltaTimeForTickRate(15);
            for (int i = 0; i < 200; i++)
            {
                MovementSystem.TryMove(in e, 0.37f, 0.91f, dt, MapBounds.Default, out var next);
                e.Position = next;
            }
            return e.Position;
        }

        Assert.Equal(Run(), Run());
    }
}
