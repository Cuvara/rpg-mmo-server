using GameServer.Input;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// The server now moves players with <see cref="CharacterMotor"/> (ADR-28), while protocol 2
/// clients still predict with <see cref="MovementSystem"/>. On flat ground with no jump the
/// two must agree to the BIT on x/y, or every protocol 2 client is corrected on every step.
/// These tests are the proof: the shared functions compared directly over many random
/// inputs, and the server's own input path compared against a reference integration.
/// </summary>
public class MotorPlanarParityTests
{
    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    private static float RandomMove(Random rng)
    {
        // Mix of in-deadzone, partial, unit, over-unit (clamped) and rejected magnitudes,
        // and exact axis values, so every ResolveDirection branch is exercised.
        return rng.Next(10) switch
        {
            0 => 0f,
            1 => (float)(rng.NextDouble() * 0.02 - 0.01),
            2 => rng.Next(2) == 0 ? 1f : -1f,
            3 => (float)(rng.NextDouble() * 4 - 2),
            4 => (float)(rng.NextDouble() * 400 - 200),
            _ => (float)(rng.NextDouble() * 2 - 1),
        };
    }

    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    [InlineData(20)]
    [InlineData(15)]
    public void OnFlatGround_TheMotorMatchesMovementSystem_BitForBit(int hz)
    {
        var rng = new Random(20261003 + hz);
        var bounds = MapBounds.FromSize(200, 120);
        MapGeometry flat = MapGeometry.Flat(bounds);
        MotorParams motor = MotorParams.Default;
        float dt = MovementSystem.DeltaTimeForTickRate(hz);

        for (int i = 0; i < 50_000; i++)
        {
            // Positions inside the bounds, including right at the edges where clamping bites.
            float x = rng.Next(8) == 0
                ? (rng.Next(2) == 0 ? bounds.MinX : bounds.MaxX) - (float)(rng.NextDouble() * 0.05)
                : (float)(rng.NextDouble() * bounds.Width + bounds.MinX);
            float y = rng.Next(8) == 0
                ? (rng.Next(2) == 0 ? bounds.MinY : bounds.MaxY) + (float)(rng.NextDouble() * 0.05)
                : (float)(rng.NextDouble() * bounds.Height + bounds.MinY);
            x = bounds.Clamp(new Vec2(x, y)).X;
            y = bounds.Clamp(new Vec2(x, y)).Y;
            float mx = RandomMove(rng);
            float my = RandomMove(rng);
            float speed = rng.Next(6) == 0 ? 5f : (float)(rng.NextDouble() * 12 + 0.1);

            var probe = new EntityState { Position = new Vec2(x, y), Speed = speed };
            MoveResult planar = MovementSystem.TryMove(in probe, mx, my, dt, in bounds, out Vec2 expected);

            var state = MotorState.StandingAt(new Vec3(x, y, 0f));
            MoveResult result = CharacterMotor.Step(in state, mx, my, false, speed, dt, flat, in motor, out MotorState next);

            Assert.Equal(planar, result);
            if (Bits(expected.X) != Bits(next.Position.X) || Bits(expected.Y) != Bits(next.Position.Y))
            {
                Assert.Fail($"iteration {i}: planar ({expected.X:R}, {expected.Y:R}) vs motor " +
                            $"({next.Position.X:R}, {next.Position.Y:R}) from ({x:R}, {y:R}) input ({mx:R}, {my:R}) " +
                            $"speed {speed:R} dt {dt:R}");
            }

            Assert.Equal(0f, next.Position.Z);
            Assert.True(next.Grounded);
        }
    }

    /// <summary>
    /// The same property through the server's own path — packet inputs, coalescing, held
    /// coasting between packets — against a reference that integrates the identical input
    /// stream with <see cref="MovementSystem.TryMove"/>, as a protocol 2 client predicts.
    /// </summary>
    [Fact]
    public void TheServersInputPath_MatchesAPlanarReference_OverARandomSession()
    {
        var rng = new Random(77);
        var bounds = MapBounds.FromSize(100, 100);
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", 0f, 0f, speed: 5f));
        var handler = new InputHandler(world, NullLogger.Instance, null, 60, bounds);
        float dt = handler.DeltaTime;

        Vec2 reference = Vec2.Zero;
        float heldX = 0f, heldY = 0f;
        ulong heldFrom = 0;
        ulong inputTick = 0;

        for (ulong tick = 1; tick <= 3000; tick++)
        {
            bool sends = rng.Next(4) != 0;
            float mx = 0f, my = 0f;
            if (sends)
            {
                mx = RandomMove(rng);
                my = RandomMove(rng);
                if (Math.Abs(mx) > 50 || Math.Abs(my) > 50) { mx = 0.5f; my = -0.25f; } // keep it valid
            }

            world.UpdateComponents(w =>
            {
                handler.BeginTick(w, tick);
                if (sends) handler.ProcessInput(w, "p1", new InputData(++inputTick, mx, my, null), tick);
                handler.ApplyHeldMovement(w, tick);
                handler.StepGameplay(w, tick);
            });

            // Reference: the protocol 2 rules — a packet steps once with its own direction
            // and becomes the held direction; a deadzone packet clears the hold; a tick with
            // no packet coasts on the hold for at most MaxBankedTicks.
            var probe = new EntityState { Position = reference, Speed = 5f };
            bool stepped = false;
            if (sends)
            {
                MoveResult r = MovementSystem.TryMove(in probe, mx, my, dt, in bounds, out Vec2 next);
                if (r is MoveResult.Accepted or MoveResult.Clamped)
                {
                    reference = next;
                    heldX = mx; heldY = my; heldFrom = tick;
                    stepped = true;
                }
                else if (r == MoveResult.None)
                {
                    heldFrom = 0;
                }
            }

            // A tick whose packet did not move the player (none sent, or a rejected vector)
            // coasts on the held direction.
            if (!stepped && heldFrom != 0 && tick - heldFrom <= (ulong)handler.MaxBankedTicks)
            {
                probe = new EntityState { Position = reference, Speed = 5f };
                MoveResult r = MovementSystem.TryMove(in probe, heldX, heldY, dt, in bounds, out Vec2 next);
                if (r is MoveResult.Accepted or MoveResult.Clamped) reference = next;
            }

            EntityState actual = world.GetEntity("p1")!.Value;
            Assert.True(
                Bits(reference.X) == Bits(actual.Position.X) && Bits(reference.Y) == Bits(actual.Position.Y),
                $"tick {tick}: reference ({reference.X:R}, {reference.Y:R}) vs server ({actual.Position.X:R}, {actual.Position.Y:R})");
        }
    }

    [Fact]
    public void AJump_LeavesTheGround_AndGravityBringsThePlayerBack_WithoutFurtherInput()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        f.Input("p1", SimFixture.Move(1, 0f, 0f), new InputExtras(jump: true, 0f, 0, 0f, 0));
        f.Step();

        Assert.True(f.Pos("p1").Z > 0f);
        EntityView v = f.Views().Single(e => e.Id == "p1");
        Assert.True(v.VelZ > 0f);

        // No more input: the airborne pass keeps integrating until the player lands.
        float apex = 0f;
        for (int i = 0; i < 120; i++)
        {
            f.Step();
            apex = Math.Max(apex, f.Pos("p1").Z);
        }

        Assert.Equal(0f, f.Pos("p1").Z);
        Assert.InRange(apex, 1.4f, 1.7f); // JumpSpeed^2 / (2 g) = 1.6 with the defaults
        f.World.UpdateComponents(w => Assert.True(w.LocomotionOf(w.Resolve("p1")).Grounded));
    }

    [Fact]
    public void Players_WalkOnTheHeightfield_AndEntitiesSpawnOnTheGround()
    {
        // A 3x3 heightfield over [-10, 10]: a ramp rising 0.5 per unit along +x... sampled at
        // x = -10, 0, 10 → heights -5, 0, 5 (slope 0.5, walkable under the 45-degree limit).
        var hf = new HeightField(-10f, -10f, 10f, 3, 3, new[] { -5f, 0f, 5f, -5f, 0f, 5f, -5f, 0f, 5f });
        var geo = new MapGeometry(MapBounds.FromSize(20, 20), hf, null, null, null);
        using var f = new SimFixture(geo);
        f.AddPlayer("p1", 4f, 0f);
        f.AddMob("m1", -4f, 0f);

        Assert.Equal(2f, f.Pos("p1").Z, 4);
        Assert.Equal(-2f, f.Pos("m1").Z, 4);

        for (ulong t = 1; t <= 30; t++)
        {
            f.Input("p1", SimFixture.Move(t, 1f, 0f));
            f.Step();
        }

        Vec3 p = f.Pos("p1");
        Assert.True(p.X > 4f);
        Assert.Equal(p.X * 0.5f, p.Z, 3);
    }
}
