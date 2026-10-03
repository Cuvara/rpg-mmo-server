using Shared.GameLogic;
using Shared.GameLogic.World;

namespace GameServer.Tests.Golden;

/// <summary>
/// Builds <c>motor3d.json</c>, <c>projectile.json</c> and <c>hitbox_rewind.json</c> by running
/// the current implementation — the same observed-not-computed rule as
/// <see cref="GoldenVectorGenerator"/>, and the same switch:
/// <c>GOLDEN_REGEN=1 dotnet test --filter Regenerate</c> rewrites these together with the
/// planar vectors (ADR-28 decision 5: the motor gets its own file; <c>movement.json</c>
/// keeps describing the protocol 2 path).
/// </summary>
public class World3DGoldenVectorGenerator
{
    [SkippableFact]
    public void Regenerate()
    {
        Skip.If(Environment.GetEnvironmentVariable("GOLDEN_REGEN") != "1",
            "Set GOLDEN_REGEN=1 to rewrite the golden vectors from the current implementation.");

        File.WriteAllText(GoldenVectors.PathTo("motor3d.json"), GoldenVectors.Serialize(BuildMotor()));
        File.WriteAllText(GoldenVectors.PathTo("projectile.json"), GoldenVectors.Serialize(BuildProjectile()));
        File.WriteAllText(GoldenVectors.PathTo("hitbox_rewind.json"), GoldenVectors.Serialize(BuildHitbox()));
    }

    private static readonly float Dt = MovementSystem.DeltaTimeForTickRate(GameConstants.DefaultTickRate);
    private static readonly MapBounds Wide = MapBounds.FromSize(100f, 100f);
    private static readonly MapBounds Tight = MapBounds.FromSize(10f, 10f);

    // Low step (0.3 <= StepHeight 0.4), a crate too tall to step (1.0) but under the jump
    // apex (1.6), a wall taller than the apex, and a slab overhead for ceiling bumps.
    private static readonly StaticBox LowStep = new(2f, -2f, 0f, 4f, 2f, 0.3f);
    private static readonly StaticBox Crate = new(2f, -2f, 0f, 4f, 2f, 1f);
    private static readonly StaticBox Wall = new(2f, -10f, 0f, 2.5f, 10f, 3f);
    private static readonly StaticBox Ceiling = new(-2f, -2f, 2.5f, 2f, 2f, 3f);

    /// <summary>
    /// Ramps along +X from x = 0 to x = 5, covering y in [-2, 2]: rise 0.5 per unit
    /// (walkable) or 3 per unit (blocked by the default slope limit of 1). Flat z = 0
    /// outside, per HeightField's contract.
    /// </summary>
    private static HeightField Ramp(float risePerUnit) =>
        new(0f, -2f, 1f, 6, 5, RampHeights(risePerUnit));

    private static float[] RampHeights(float risePerUnit)
    {
        var h = new float[6 * 5];
        for (int row = 0; row < 5; row++)
            for (int col = 0; col < 6; col++)
                h[row * 6 + col] = col * risePerUnit;
        return h;
    }

    // ── Motor ────────────────────────────────────────────────────────────────

    internal static List<MotorCase> BuildMotor()
    {
        var p = MotorParams.Default;
        var cases = new List<MotorCase>();

        void Add(string name, MapBounds bounds, StaticBox[] boxes, HeightField? hf,
                 float x, float y, float z, float vz, bool grounded,
                 float mx, float my, float speed, float dt, int ticks, int jumpTicks)
        {
            var c = new MotorCase
            {
                name = name,
                gravity = GoldenVectors.Hex(p.Gravity),
                jumpSpeed = GoldenVectors.Hex(p.JumpSpeed),
                stepHeight = GoldenVectors.Hex(p.StepHeight),
                maxSlopeRise = GoldenVectors.Hex(p.MaxSlopeRise),
                capsuleRadius = GoldenVectors.Hex(p.CapsuleRadius),
                capsuleHeight = GoldenVectors.Hex(p.CapsuleHeight),
                posX = GoldenVectors.Hex(x),
                posY = GoldenVectors.Hex(y),
                posZ = GoldenVectors.Hex(z),
                velZ = GoldenVectors.Hex(vz),
                grounded = grounded,
                moveX = GoldenVectors.Hex(mx),
                moveY = GoldenVectors.Hex(my),
                speed = GoldenVectors.Hex(speed),
                dt = GoldenVectors.Hex(dt),
                ticks = ticks,
                jumpTicks = jumpTicks,
            };
            World3DGolden.SetGeometry(c, bounds, boxes, hf);

            MoveResult r = World3DGolden.RunMotor(c, out MotorState s);
            c.expectedResult = r.ToString();
            c.expectedX = GoldenVectors.Hex(s.Position.X);
            c.expectedY = GoldenVectors.Hex(s.Position.Y);
            c.expectedZ = GoldenVectors.Hex(s.Position.Z);
            c.expectedVelZ = GoldenVectors.Hex(s.VelocityZ);
            c.expectedGrounded = s.Grounded;
            cases.Add(c);
        }

        var none = Array.Empty<StaticBox>();

        // Flat ground: must match the planar path's travel exactly.
        Add("flat_walk_x_one_tick", Wide, none, null, 0f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 1, 0);
        Add("flat_walk_x_ten_ticks", Wide, none, null, 0f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 10, 0);
        Add("flat_diagonal_clamped", Wide, none, null, 0f, 0f, 0f, 0f, true, 1f, 1f, 5f, Dt, 3, 0);
        Add("flat_analog_partial", Wide, none, null, 1.25f, -3.5f, 0f, 0f, true, 0.5f, 0.25f, 5f, Dt, 4, 0);
        Add("flat_idle_none", Wide, none, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 1, 0);
        Add("rejected_input_grounded", Wide, none, null, 0f, 0f, 0f, 0f, true, float.NaN, 0f, 5f, Dt, 1, 0);
        Add("rejected_input_still_falls", Wide, none, null, 0f, 0f, 2f, 0f, false, 100f, 0f, 5f, Dt, 3, 0);
        Add("zero_speed_blocked_still_falls", Wide, none, null, 0f, 0f, 2f, 0f, false, 1f, 0f, 0f, Dt, 3, 0);
        Add("zero_dt_blocked", Wide, none, null, 1f, 1f, 0f, 0f, true, 1f, 0f, 5f, 0f, 1, 0);
        Add("dt_above_max_clamped", Wide, none, null, 0f, 0f, 0f, 0f, true, 1f, 0f, 5f, 10f, 1, 0);

        // Jump arc: airtime is 2 * 8 / 20 = 0.8 s = 12 ticks at 15 Hz.
        Add("jump_first_tick", Wide, none, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 1, 1);
        Add("jump_near_apex", Wide, none, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 6, 1);
        Add("jump_falling", Wide, none, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 10, 1);
        Add("jump_landed", Wide, none, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 14, 1);
        Add("jump_while_moving", Wide, none, null, 0f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 8, 1);
        Add("jump_held_in_air_ignored", Wide, none, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 6, 6);
        Add("jump_airborne_ignored", Wide, none, null, 0f, 0f, 1f, 0f, false, 0f, 0f, 5f, Dt, 1, 1);
        Add("fall_from_air_spawn_lands", Wide, none, null, 0f, 0f, 3f, 0f, false, 0f, 0f, 5f, Dt, 20, 0);

        // Boxes.
        Add("step_up_low_box", Wide, new[] { LowStep }, null, 1f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 6, 0);
        Add("blocked_by_crate", Wide, new[] { Crate }, null, 1f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 6, 0);
        Add("jump_onto_crate", Wide, new[] { Crate }, null, 1f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 10, 1);
        Add("jump_across_crate_and_off", Wide, new[] { Crate }, null, 1f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 14, 1);
        Add("blocked_by_wall", Wide, new[] { Wall }, null, 0f, 0f, 0f, 0f, true, 1f, 0f, 5f, Dt, 10, 0);
        Add("wall_slide_diagonal", Wide, new[] { Wall }, null, 1.5f, 0f, 0f, 0f, true, 1f, 1f, 5f, Dt, 5, 0);
        Add("walk_off_ledge_falling", Wide, new[] { Crate }, null, 3.8f, 0f, 1f, 0f, true, 1f, 0f, 5f, Dt, 3, 0);
        Add("walk_off_ledge_landed", Wide, new[] { Crate }, null, 3.8f, 0f, 1f, 0f, true, 1f, 0f, 5f, Dt, 12, 0);
        Add("ceiling_bump", Wide, new[] { Ceiling }, null, 0f, 0f, 0f, 0f, true, 0f, 0f, 5f, Dt, 4, 1);

        // Terrain.
        Add("slope_gentle_climb", Wide, none, Ramp(0.5f), 0.5f, 0f, 0.25f, 0f, true, 1f, 0f, 5f, Dt, 6, 0);
        Add("slope_gentle_descend", Wide, none, Ramp(0.5f), 4f, 0f, 2f, 0f, true, -1f, 0f, 5f, Dt, 6, 0);
        Add("slope_steep_blocked", Wide, none, Ramp(3f), 0.5f, 0f, 1.5f, 0f, true, 1f, 0f, 5f, Dt, 3, 0);
        Add("slope_diagonal_climb", Wide, none, Ramp(0.5f), 0.5f, -1f, 0.25f, 0f, true, 1f, 1f, 5f, Dt, 4, 0);

        // Bounds.
        Add("bounds_clamp_max_x", Tight, none, null, 4.9f, 0f, 0f, 0f, true, 1f, 0f, 50f, Dt, 1, 0);
        Add("bounds_corner_slide", Tight, none, null, 4.9f, 4.9f, 0f, 0f, true, 1f, 1f, 50f, Dt, 2, 0);

        return cases;
    }

    // ── Projectile ───────────────────────────────────────────────────────────

    internal static List<ProjectileCase> BuildProjectile()
    {
        var cases = new List<ProjectileCase>();
        var none = Array.Empty<StaticBox>();

        void Spawn(string name, Vec3 origin, Vec3 aim, float speed, float radius, float range)
        {
            bool ok = ProjectileLogic.Spawn(origin, aim, speed, radius, range, out ProjectileState s);
            var c = new ProjectileCase
            {
                name = name, kind = "spawn",
                ax = GoldenVectors.Hex(origin.X), ay = GoldenVectors.Hex(origin.Y), az = GoldenVectors.Hex(origin.Z),
                bx = GoldenVectors.Hex(aim.X), by = GoldenVectors.Hex(aim.Y), bz = GoldenVectors.Hex(aim.Z),
                speed = GoldenVectors.Hex(speed), radius = GoldenVectors.Hex(radius), range = GoldenVectors.Hex(range),
                expectedOk = ok,
                expectedX = GoldenVectors.Hex(s.Position.X), expectedY = GoldenVectors.Hex(s.Position.Y), expectedZ = GoldenVectors.Hex(s.Position.Z),
                expectedVx = GoldenVectors.Hex(s.Velocity.X), expectedVy = GoldenVectors.Hex(s.Velocity.Y), expectedVz = GoldenVectors.Hex(s.Velocity.Z),
                expectedRemaining = GoldenVectors.Hex(s.RemainingRange),
            };
            cases.Add(c);
        }

        void Step(string name, MapBounds bounds, StaticBox[] boxes, HeightField? hf,
                  Vec3 pos, Vec3 vel, float radius, float range, float dt, int ticks)
        {
            var c = new ProjectileCase
            {
                name = name, kind = "step",
                ax = GoldenVectors.Hex(pos.X), ay = GoldenVectors.Hex(pos.Y), az = GoldenVectors.Hex(pos.Z),
                bx = GoldenVectors.Hex(vel.X), by = GoldenVectors.Hex(vel.Y), bz = GoldenVectors.Hex(vel.Z),
                radius = GoldenVectors.Hex(radius), range = GoldenVectors.Hex(range), dt = GoldenVectors.Hex(dt),
                ticks = ticks,
            };
            World3DGolden.SetGeometry(c, bounds, boxes, hf);
            int executed = World3DGolden.RunProjectile(c, out ProjectileState s, out bool alive, out bool hitWorld);
            c.expectedOk = alive;
            c.expectedHitWorld = hitWorld;
            c.expectedTicks = executed;
            c.expectedX = GoldenVectors.Hex(s.Position.X);
            c.expectedY = GoldenVectors.Hex(s.Position.Y);
            c.expectedZ = GoldenVectors.Hex(s.Position.Z);
            c.expectedRemaining = GoldenVectors.Hex(s.RemainingRange);
            cases.Add(c);
        }

        void Seg(string name, Vec3 a, Vec3 b, float radius, Vec3 capBase, float capR, float capH)
        {
            bool hit = ProjectileLogic.SegmentCapsuleHit(a, b, radius, capBase, capR, capH, out float t);
            cases.Add(new ProjectileCase
            {
                name = name, kind = "segment_capsule",
                ax = GoldenVectors.Hex(a.X), ay = GoldenVectors.Hex(a.Y), az = GoldenVectors.Hex(a.Z),
                bx = GoldenVectors.Hex(b.X), by = GoldenVectors.Hex(b.Y), bz = GoldenVectors.Hex(b.Z),
                cx = GoldenVectors.Hex(capBase.X), cy = GoldenVectors.Hex(capBase.Y), cz = GoldenVectors.Hex(capBase.Z),
                radius = GoldenVectors.Hex(radius),
                capsuleRadius = GoldenVectors.Hex(capR), capsuleHeight = GoldenVectors.Hex(capH),
                expectedOk = hit,
                expectedT = GoldenVectors.Hex(t),
            });
        }

        void Sweep(string name, MapBounds bounds, StaticBox[] boxes, HeightField? hf, Vec3 a, Vec3 b, float radius)
        {
            var c = new ProjectileCase
            {
                name = name, kind = "sweep",
                ax = GoldenVectors.Hex(a.X), ay = GoldenVectors.Hex(a.Y), az = GoldenVectors.Hex(a.Z),
                bx = GoldenVectors.Hex(b.X), by = GoldenVectors.Hex(b.Y), bz = GoldenVectors.Hex(b.Z),
                radius = GoldenVectors.Hex(radius),
            };
            World3DGolden.SetGeometry(c, bounds, boxes, hf);
            bool hit = World3DGolden.Geometry(c).SweepSphere(a, b, radius, out Vec3 hp, out float t);
            c.expectedOk = hit;
            c.expectedX = GoldenVectors.Hex(hp.X);
            c.expectedY = GoldenVectors.Hex(hp.Y);
            c.expectedZ = GoldenVectors.Hex(hp.Z);
            c.expectedT = GoldenVectors.Hex(t);
            cases.Add(c);
        }

        // Spawn.
        Spawn("spawn_horizontal", new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 20f, 0.25f, 30f);
        Spawn("spawn_diagonal_irrational", new Vec3(1.5f, -2.25f, 1.2f), new Vec3(7.3f, 4.1f, 0.4f), 18f, 0.3f, 25f);
        Spawn("spawn_upward", new Vec3(0f, 0f, 1f), new Vec3(0f, 3f, 5f), 15f, 0.2f, 20f);
        Spawn("spawn_zero_length_refused", new Vec3(1f, 2f, 1f), new Vec3(1f, 2f, 1f), 20f, 0.25f, 30f);
        Spawn("spawn_nan_aim_refused", new Vec3(0f, 0f, 1f), new Vec3(float.NaN, 0f, 1f), 20f, 0.25f, 30f);
        Spawn("spawn_zero_speed_refused", new Vec3(0f, 0f, 1f), new Vec3(5f, 0f, 1f), 0f, 0.25f, 30f);

        // Step.
        var v20x = new Vec3(20f, 0f, 0f);
        Step("travel_flat_one_tick", Wide, none, null, new Vec3(0f, 0f, 1f), v20x, 0.25f, 30f, Dt, 1);
        Step("travel_until_range", Wide, none, null, new Vec3(0f, 0f, 1f), v20x, 0.25f, 10f, Dt, 100);
        Step("hits_wall", Wide, new[] { Wall }, null, new Vec3(0f, 0f, 1f), v20x, 0.25f, 30f, Dt, 100);
        Step("hits_ground_descending", Wide, none, null, new Vec3(0f, 0f, 2f), new Vec3(15f, 0f, -6f), 0.2f, 30f, Dt, 100);
        Step("hits_heightfield_ramp", Wide, none, Ramp(0.5f), new Vec3(0.2f, 0f, 1f), new Vec3(18f, 0f, 0f), 0.2f, 30f, Dt, 100);
        Step("passes_over_crate", Wide, new[] { Crate }, null, new Vec3(0f, 0f, 1.6f), v20x, 0.25f, 8f, Dt, 100);
        Step("leaves_bounds", Tight, none, null, new Vec3(0f, 0f, 1f), v20x, 0.25f, 30f, Dt, 100);

        // Sweep, directly.
        Sweep("sweep_box_face", Wide, new[] { Wall }, null, new Vec3(0f, 0f, 1f), new Vec3(4f, 0f, 1f), 0.25f);
        Sweep("sweep_miss_over_box", Wide, new[] { Crate }, null, new Vec3(0f, 0f, 2f), new Vec3(6f, 0f, 2f), 0.25f);
        Sweep("sweep_starts_inside", Wide, new[] { Crate }, null, new Vec3(3f, 0f, 0.5f), new Vec3(6f, 0f, 0.5f), 0.1f);
        Sweep("sweep_flat_ground", Wide, none, null, new Vec3(0f, 0f, 1f), new Vec3(2f, 0f, -1f), 0.1f);
        Sweep("sweep_ramp", Wide, none, Ramp(0.5f), new Vec3(0f, 0f, 1.3f), new Vec3(5f, 0f, 1.3f), 0.1f);

        // Segment vs capsule (default character capsule 0.4 x 1.8).
        float r = CharacterMotor.DefaultCapsuleRadius;
        float h = CharacterMotor.DefaultCapsuleHeight;
        Seg("capsule_direct_hit", new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 0.25f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_offset_hit", new Vec3(0f, 0.3f, 1.1f), new Vec3(10f, 0.45f, 0.9f), 0.2f, new Vec3(6.3f, 0.1f, 0f), r, h);
        Seg("capsule_miss_above", new Vec3(0f, 0f, 2.5f), new Vec3(10f, 0f, 2.5f), 0.25f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_miss_side", new Vec3(0f, 1f, 1f), new Vec3(10f, 1f, 1f), 0.25f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_touch_counts", new Vec3(0f, 0.65f, 1f), new Vec3(10f, 0.65f, 1f), 0.25f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_head_endcap", new Vec3(5f, 0f, 4f), new Vec3(5f, 0f, 1.5f), 0.1f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_start_inside", new Vec3(5f, 0f, 1f), new Vec3(9f, 0f, 1f), 0.1f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_zero_length_segment", new Vec3(5.5f, 0f, 1f), new Vec3(5.5f, 0f, 1f), 0.2f, new Vec3(5f, 0f, 0f), r, h);
        Seg("capsule_short_degenerates_to_sphere", new Vec3(0f, 0f, 0.3f), new Vec3(10f, 0f, 0.3f), 0.1f, new Vec3(5f, 0f, 0f), 0.4f, 0.5f);
        Seg("capsule_beyond_segment_end", new Vec3(0f, 0f, 1f), new Vec3(3f, 0f, 1f), 0.25f, new Vec3(5f, 0f, 0f), r, h);

        return cases;
    }

    // ── Hitbox rewind ────────────────────────────────────────────────────────

    internal static List<HitboxCase> BuildHitbox()
    {
        var cases = new List<HitboxCase>();
        int max = HitboxHistory.MaxRewindMs;

        void Clamp(string name, long current, long render, float alpha, int rate, int maxMs)
        {
            HitboxHistory.ClampRewind((ulong)current, (ulong)render, alpha, rate, maxMs, out ulong tick, out float a);
            cases.Add(new HitboxCase
            {
                name = name, kind = "clamp",
                currentTick = current, renderTick = render, renderAlpha = GoldenVectors.Hex(alpha),
                tickRate = rate, maxRewindMs = maxMs,
                expectedTick = (long)tick, expectedAlpha = GoldenVectors.Hex(a),
            });
        }

        void Interp(string name, Vec3 a, Vec3 b, bool hasNext, float alpha)
        {
            var c = new HitboxCase
            {
                name = name, kind = "interp",
                renderTick = 100, renderAlpha = GoldenVectors.Hex(alpha), tickRate = 15, maxRewindMs = max,
                ax = GoldenVectors.Hex(a.X), ay = GoldenVectors.Hex(a.Y), az = GoldenVectors.Hex(a.Z),
                bx = GoldenVectors.Hex(b.X), by = GoldenVectors.Hex(b.Y), bz = GoldenVectors.Hex(b.Z),
                hasNext = hasNext,
            };
            c.expectedFound = RunInterp(c, out Vec3 pos);
            c.expectedX = GoldenVectors.Hex(pos.X);
            c.expectedY = GoldenVectors.Hex(pos.Y);
            c.expectedZ = GoldenVectors.Hex(pos.Z);
            cases.Add(c);
        }

        // 200 ms at 15 Hz = 3 whole ticks; at 60 Hz = 12.
        Clamp("clamp_no_rewind_when_zero", 1000, 0, 0.5f, 15, max);
        Clamp("clamp_future_render_tick_ignored", 1000, 1005, 0.5f, 15, max);
        Clamp("clamp_current_tick_drops_alpha", 1000, 1000, 0.5f, 15, max);
        Clamp("clamp_within_window", 1000, 998, 0.25f, 15, max);
        Clamp("clamp_at_window_edge", 1000, 997, 0.75f, 15, max);
        Clamp("clamp_beyond_window", 1000, 990, 0.75f, 15, max);
        Clamp("clamp_60hz_within", 5000, 4990, 0.4f, 60, max);
        Clamp("clamp_60hz_beyond", 5000, 4900, 0.4f, 60, max);
        Clamp("clamp_nan_alpha", 1000, 999, float.NaN, 15, max);
        Clamp("clamp_alpha_one_saturates", 1000, 999, 1f, 15, max);
        Clamp("clamp_early_ticks", 2, 1, 0.5f, 15, max);

        Interp("interp_alpha_zero", new Vec3(1f, 2f, 0f), new Vec3(1.33f, 2.1f, 0f), true, 0f);
        Interp("interp_alpha_quarter", new Vec3(1f, 2f, 0f), new Vec3(1.33f, 2.1f, 0.5f), true, 0.25f);
        Interp("interp_alpha_irrational", new Vec3(-12.7f, 40.3f, 1.1f), new Vec3(-12.35f, 40.05f, 1.45f), true, 0.6180339f);
        Interp("interp_missing_next_holds", new Vec3(3f, 4f, 0.5f), new Vec3(0f, 0f, 0f), false, 0.5f);

        return cases;
    }

    /// <summary>Driver for an <c>interp</c> case; shared with the replay.</summary>
    internal static bool RunInterp(HitboxCase c, out Vec3 pos)
    {
        var history = new HitboxHistory(c.tickRate, 4, c.maxRewindMs);
        ulong t = (ulong)c.renderTick;
        history.Record(t, 7, new Vec3(GoldenVectors.Float(c.ax), GoldenVectors.Float(c.ay), GoldenVectors.Float(c.az)));
        if (c.hasNext)
            history.Record(t + 1, 7, new Vec3(GoldenVectors.Float(c.bx), GoldenVectors.Float(c.by), GoldenVectors.Float(c.bz)));
        return history.TryGetAt(7, t, GoldenVectors.Float(c.renderAlpha), out pos);
    }
}
