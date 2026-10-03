using Shared.GameLogic.World;

namespace GameServer.Tests.Golden;

/// <summary>
/// ADR-28/ADR-29 conformance gate: replays <c>motor3d.json</c>, <c>projectile.json</c> and
/// <c>hitbox_rewind.json</c> through <c>Shared.GameLogic</c> and compares every float
/// bit-for-bit, exactly as <see cref="GoldenVectorTests"/> does for the planar vectors.
/// A diff in these fixtures means the client's predicted 3D movement or projectile path
/// changed. Regenerate with <c>GOLDEN_REGEN=1 dotnet test --filter Regenerate</c>.
/// </summary>
public class World3DGoldenVectorTests
{
    private const string MotorFile = "motor3d.json";
    private const string ProjectileFile = "projectile.json";
    private const string HitboxFile = "hitbox_rewind.json";

    public static TheoryData<string> MotorNames() => Names(GoldenVectors.Load<MotorCase>(MotorFile), c => c.name);
    public static TheoryData<string> ProjectileNames() => Names(GoldenVectors.Load<ProjectileCase>(ProjectileFile), c => c.name);
    public static TheoryData<string> HitboxNames() => Names(GoldenVectors.Load<HitboxCase>(HitboxFile), c => c.name);

    private static TheoryData<string> Names<T>(T[] cases, Func<T, string> name)
    {
        var data = new TheoryData<string>();
        foreach (T c in cases) data.Add(name(c));
        return data;
    }

    [Theory]
    [MemberData(nameof(MotorNames))]
    public void Motor(string name)
    {
        MotorCase c = Array.Find(GoldenVectors.Load<MotorCase>(MotorFile), v => v.name == name)!;

        MoveResult result = World3DGolden.RunMotor(c, out MotorState s);

        Assert.Equal(c.expectedResult, result.ToString());
        GoldenVectors.AssertBitEqual(c.expectedX, s.Position.X, name + ".x");
        GoldenVectors.AssertBitEqual(c.expectedY, s.Position.Y, name + ".y");
        GoldenVectors.AssertBitEqual(c.expectedZ, s.Position.Z, name + ".z");
        GoldenVectors.AssertBitEqual(c.expectedVelZ, s.VelocityZ, name + ".velZ");
        Assert.Equal(c.expectedGrounded, s.Grounded);
    }

    [Theory]
    [MemberData(nameof(ProjectileNames))]
    public void Projectile(string name)
    {
        ProjectileCase c = Array.Find(GoldenVectors.Load<ProjectileCase>(ProjectileFile), v => v.name == name)!;
        var a = new Vec3(GoldenVectors.Float(c.ax), GoldenVectors.Float(c.ay), GoldenVectors.Float(c.az));
        var b = new Vec3(GoldenVectors.Float(c.bx), GoldenVectors.Float(c.by), GoldenVectors.Float(c.bz));

        switch (c.kind)
        {
            case "spawn":
            {
                bool ok = ProjectileLogic.Spawn(a, b,
                    GoldenVectors.Float(c.speed), GoldenVectors.Float(c.radius), GoldenVectors.Float(c.range),
                    out ProjectileState s);
                Assert.Equal(c.expectedOk, ok);
                GoldenVectors.AssertBitEqual(c.expectedX, s.Position.X, name + ".x");
                GoldenVectors.AssertBitEqual(c.expectedY, s.Position.Y, name + ".y");
                GoldenVectors.AssertBitEqual(c.expectedZ, s.Position.Z, name + ".z");
                GoldenVectors.AssertBitEqual(c.expectedVx, s.Velocity.X, name + ".vx");
                GoldenVectors.AssertBitEqual(c.expectedVy, s.Velocity.Y, name + ".vy");
                GoldenVectors.AssertBitEqual(c.expectedVz, s.Velocity.Z, name + ".vz");
                GoldenVectors.AssertBitEqual(c.expectedRemaining, s.RemainingRange, name + ".remaining");
                break;
            }
            case "step":
            {
                int executed = World3DGolden.RunProjectile(c, out ProjectileState s, out bool alive, out bool hitWorld);
                Assert.Equal(c.expectedTicks, executed);
                Assert.Equal(c.expectedOk, alive);
                Assert.Equal(c.expectedHitWorld, hitWorld);
                GoldenVectors.AssertBitEqual(c.expectedX, s.Position.X, name + ".x");
                GoldenVectors.AssertBitEqual(c.expectedY, s.Position.Y, name + ".y");
                GoldenVectors.AssertBitEqual(c.expectedZ, s.Position.Z, name + ".z");
                GoldenVectors.AssertBitEqual(c.expectedRemaining, s.RemainingRange, name + ".remaining");
                break;
            }
            case "sweep":
            {
                bool hit = World3DGolden.Geometry(c).SweepSphere(a, b, GoldenVectors.Float(c.radius), out Vec3 hp, out float t);
                Assert.Equal(c.expectedOk, hit);
                GoldenVectors.AssertBitEqual(c.expectedX, hp.X, name + ".x");
                GoldenVectors.AssertBitEqual(c.expectedY, hp.Y, name + ".y");
                GoldenVectors.AssertBitEqual(c.expectedZ, hp.Z, name + ".z");
                GoldenVectors.AssertBitEqual(c.expectedT, t, name + ".t");
                break;
            }
            case "segment_capsule":
            {
                var capBase = new Vec3(GoldenVectors.Float(c.cx), GoldenVectors.Float(c.cy), GoldenVectors.Float(c.cz));
                bool hit = ProjectileLogic.SegmentCapsuleHit(a, b, GoldenVectors.Float(c.radius),
                    capBase, GoldenVectors.Float(c.capsuleRadius), GoldenVectors.Float(c.capsuleHeight), out float t);
                Assert.Equal(c.expectedOk, hit);
                GoldenVectors.AssertBitEqual(c.expectedT, t, name + ".t");
                break;
            }
            default:
                throw new InvalidDataException("unknown projectile case kind: " + c.kind);
        }
    }

    [Theory]
    [MemberData(nameof(HitboxNames))]
    public void HitboxRewind(string name)
    {
        HitboxCase c = Array.Find(GoldenVectors.Load<HitboxCase>(HitboxFile), v => v.name == name)!;

        switch (c.kind)
        {
            case "clamp":
            {
                HitboxHistory.ClampRewind((ulong)c.currentTick, (ulong)c.renderTick,
                    GoldenVectors.Float(c.renderAlpha), c.tickRate, c.maxRewindMs, out ulong tick, out float alpha);
                Assert.Equal(c.expectedTick, (long)tick);
                Assert.Equal((ulong)c.expectedTick,
                    HitboxHistory.ClampRewindTick((ulong)c.currentTick, (ulong)c.renderTick, c.tickRate, c.maxRewindMs));
                GoldenVectors.AssertBitEqual(c.expectedAlpha, alpha, name + ".alpha");
                break;
            }
            case "interp":
            {
                bool found = World3DGoldenVectorGenerator.RunInterp(c, out Vec3 pos);
                Assert.Equal(c.expectedFound, found);
                GoldenVectors.AssertBitEqual(c.expectedX, pos.X, name + ".x");
                GoldenVectors.AssertBitEqual(c.expectedY, pos.Y, name + ".y");
                GoldenVectors.AssertBitEqual(c.expectedZ, pos.Z, name + ".z");
                break;
            }
            default:
                throw new InvalidDataException("unknown hitbox case kind: " + c.kind);
        }
    }
}
