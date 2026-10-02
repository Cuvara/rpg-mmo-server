using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// ADR-29 through the real input path and gameplay step: projectile entities with owner and
/// spawn_seq, first-step lag compensation through the hitbox history, the 200 ms clamp, and
/// world geometry stopping a shot.
/// </summary>
/// <remarks>
/// Geometry of the shot used throughout: the caster stands at the origin and fires Bolt
/// (600 u/s, radius 0.3, range 12) at (0, 10). At 60 Hz the FIRST step covers y 0 → 10 and
/// the second y 10 → 12, so a target standing at y = 10 is hit on the first step if its x
/// is within 0.7 (sphere + capsule radius) of 0 at the instant tested, and missed otherwise.
/// The target slides along x one unit per tick, AFTER the critical scope (where enemies
/// move in the real loop).
/// </remarks>
public class ProjectileAndRewindTests
{
    private static Action<WorldWriter, ulong> SlideMob(SimFixture f, Func<ulong, float> xAt) =>
        (w, tick) => f.SetPos(w, "m1", xAt(tick), 10f);

    private static SimFixture ShootingRange(Func<ulong, float> xAt, out Action<WorldWriter, ulong> phase)
    {
        var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", xAt(0), 10f, hp: 100, defense: 5);
        phase = SlideMob(f, xAt);
        return f;
    }

    [Fact]
    public void AMovingTarget_IsHitOnlyWithRewind()
    {
        // x_t = t - 5: in front of the shot (x = 0) at tick 5, two units right of it by tick 7.
        static float X(ulong t) => t - 5f;

        // With rewind: fire at tick 8 claiming to have rendered tick 5.
        using (var f = ShootingRange(X, out var phase))
        {
            f.StepUntil(7, phase);
            f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 10f),
                new InputExtras(false, 0f, renderTick: 5, renderAlpha: 0f, spawnSeq: 7));
            f.Step(phase);

            // 10 attack + 20 power - 5 defense = 25, then the DoT is applied.
            Assert.Equal(75, f.State("m1").Hp);
            Assert.Single(f.EventsOf(GameEventType.ProjectileHit));
            Assert.Contains(f.EventsOf(GameEventType.StatusApplied), e => e.EffectId == SimFixture.Burning);
            Assert.DoesNotContain(f.Views(), v => v.Type == "projectile");
        }

        // Same shot, no render tick: tested against where the target is NOW (x = 2) — miss.
        using (var f = ShootingRange(X, out var phase))
        {
            f.StepUntil(7, phase);
            f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 10f),
                new InputExtras(false, 0f, renderTick: 0, renderAlpha: 0f, spawnSeq: 7));
            f.Step(phase);
            f.StepUntil(12, phase);

            Assert.Equal(100, f.State("m1").Hp);
            Assert.Empty(f.EventsOf(GameEventType.ProjectileHit));
        }
    }

    [Fact]
    public void TheRewindIsClampedTo200Milliseconds()
    {
        // At 60 Hz the cap is 12 ticks. The target stands in front of the shot ONLY at tick 28
        // (= 40 - 12) and at tick 5. A claim of render_tick 5 at tick 40 is clamped to 28 and
        // hits; were it honoured literally it would also hit, so a second claim (tick 30,
        // inside the window, target away) must miss — proving the instant tested is the
        // clamped one and not "no rewind".
        static float X(ulong t) => t is 5 or 28 ? 0f : 5f;

        Assert.Equal(28ul, HitboxHistory.ClampRewindTick(40, 5, 60, HitboxHistory.MaxRewindMs));

        using (var f = ShootingRange(X, out var phase))
        {
            f.StepUntil(39, phase);
            f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 10f),
                new InputExtras(false, 0f, renderTick: 5, renderAlpha: 0f, spawnSeq: 1));
            f.Step(phase);
            Assert.Equal(75, f.State("m1").Hp);
        }

        using (var f = ShootingRange(X, out var phase))
        {
            f.StepUntil(39, phase);
            f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 10f),
                new InputExtras(false, 0f, renderTick: 30, renderAlpha: 0f, spawnSeq: 1));
            f.Step(phase);
            f.StepUntil(45, phase);
            Assert.Equal(100, f.State("m1").Hp);
        }
    }

    [Fact]
    public void AProjectileIsAnEntity_OwnedByTheCaster_CarryingTheSpawnSeq_AndStepsEveryTick()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);

        // Fire along +x into empty space: 600 u/s for 12 units = two steps.
        f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 10f, aimY: 0f),
            new InputExtras(false, 0f, 0, 0f, spawnSeq: 42));
        f.Step();

        EntityView proj = Assert.Single(f.Views(), v => v.Type == "projectile");
        Assert.Equal("p1", proj.OwnerId);
        Assert.NotEqual(0, proj.OwnerKey);
        Assert.Equal(42u, proj.SpawnSeq);
        // First step already taken in the spawning tick: 10 units along +x, at launch height.
        Assert.Equal(10f, proj.Position.X, 3);
        Assert.Equal(1.0f, proj.Z, 3);
        Assert.Equal(600f, proj.VelX, 3);
        Assert.Equal(0f, proj.VelY, 3);

        // Range runs out on the next step and the entity is despawned.
        f.Step();
        Assert.DoesNotContain(f.Views(), v => v.Type == "projectile");
    }

    [Fact]
    public void ProjectileIdsAreRecycledOnlyAfterTheQuarantine()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);

        f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 10f, aimY: 0f));
        f.Step();
        string first = Assert.Single(f.Views(), v => v.Type == "projectile").Id;
        f.StepUntil(5);

        f.Input("p1", SimFixture.Cast(6, SimFixture.Bolt, aimX: 10f, aimY: 0f));
        f.Step();
        string second = Assert.Single(f.Views(), v => v.Type == "projectile").Id;
        Assert.NotEqual(first, second);

        // Past the quarantine (10 s at 60 Hz) the first id is handed out again.
        f.StepUntil(10 * 60 + 20);
        f.Input("p1", SimFixture.Cast(700, SimFixture.Bolt, aimX: 10f, aimY: 0f));
        f.Step();
        Assert.Equal(first, Assert.Single(f.Views(), v => v.Type == "projectile").Id);
    }

    [Fact]
    public void AWallStopsTheProjectileBeforeATargetBehindIt()
    {
        // A wall across y = 4..5 between the caster and a target at y = 10.
        var geo = new global::Shared.GameLogic.World.MapGeometry(
            MapBounds.FromSize(200, 200), null,
            new[] { new global::Shared.GameLogic.World.StaticBox(-5f, 4f, 0f, 5f, 5f, 3f) },
            null, null);
        using var f = new SimFixture(geo);
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", 0f, 10f);

        f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 10f));
        f.Step();
        f.StepUntil(5);

        Assert.Equal(100, f.State("m1").Hp);
        Assert.DoesNotContain(f.Views(), v => v.Type == "projectile");
    }

    [Fact]
    public void TheNearestTargetAlongThePathIsHit_NotTheFirstInIterationOrder()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        // Created far-first, so a hit test that took the first roster entry would pick m-far.
        f.AddMob("m-far", 0f, 9f);
        f.AddMob("m-near", 0f, 5f);

        f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 10f));
        f.Step();

        Assert.Equal(75, f.State("m-near").Hp);
        Assert.Equal(100, f.State("m-far").Hp);
    }

    [Fact]
    public void AGroundCast_HitsWhereTheTargetWasRendered_AndRoots()
    {
        // The mob stands at x = 0 at tick 5 and walks away to x = 6 by tick 7.
        static float X(ulong t) => t <= 5 ? 0f : (t - 5f) * 3f;

        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", X(0), 10f);
        Action<WorldWriter, ulong> phase = (w, t) => f.SetPos(w, "m1", X(t), 10f);

        f.StepUntil(7, phase);
        f.Input("p1", SimFixture.Cast(1, SimFixture.Snare, aimX: 0f, aimY: 10f),
            new InputExtras(false, 0f, renderTick: 5, renderAlpha: 0f, spawnSeq: 0));
        f.Step(phase);

        // 10 + 5 - 5.
        Assert.Equal(90, f.State("m1").Hp);
        Assert.True(f.Record("m1").Statuses!.Contains(SimFixture.Rooted));
    }

    [Fact]
    public void ALinkdeadPlayer_IsNeitherHitByAnAreaNorTargetable()
    {
        using var f = new SimFixture();
        f.AddMob("caster", 0f, 0f);
        f.AddPlayer("held", 2f, 0f);
        f.AddPlayer("live", -2f, 0f);
        f.AddPlayer("attacker", 3f, 0f);
        f.World.UpdateComponents(w => w.PlayerTagOf(w.Resolve("held")).Linkdead = true);

        // A mob casting an area on both players: only the connected one is hit.
        f.Input("caster", SimFixture.Cast(1, SimFixture.Snare, aimX: 0f, aimY: 0f));
        f.Step();
        Assert.Equal(100, f.State("held").Hp);
        Assert.True(f.State("live").Hp < 100);

        // A basic attack by id on the held player is refused.
        f.Input("attacker", new InputData(1, 0f, 0f, "held"));
        f.Step();
        Assert.Equal(100, f.State("held").Hp);
        Assert.Equal(GameServer.Input.InputHandler.NotTargetableRejection, f.Handler.Attacks.LastRejection);
    }
}
