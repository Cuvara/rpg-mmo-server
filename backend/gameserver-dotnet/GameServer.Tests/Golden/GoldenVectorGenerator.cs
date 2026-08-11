using Shared.GameLogic;

namespace GameServer.Tests.Golden;

/// <summary>
/// Builds the golden-vector fixtures by running the current implementation over a
/// fixed set of inputs. The expected values are therefore *observed*, never hand
/// computed — these vectors lock in today's behaviour so an unintended change is
/// caught on whichever side moved (ADR-10 decision 4).
///
/// <para>
/// Regeneration is deliberately manual and explicit:
/// <c>GOLDEN_REGEN=1 dotnet test --filter Regenerate</c>. A behavioural change is
/// expected to regenerate and commit the fixtures in the same change; a diff in
/// the fixtures is the review artefact that says the client's prediction changed.
/// </para>
/// </summary>
public class GoldenVectorGenerator
{
    [SkippableFact]
    public void Regenerate()
    {
        Skip.If(Environment.GetEnvironmentVariable("GOLDEN_REGEN") != "1",
            "Set GOLDEN_REGEN=1 to rewrite the golden vectors from the current implementation.");

        File.WriteAllText(GoldenVectors.PathTo("vec2.json"), GoldenVectors.Serialize(BuildVec2()));
        File.WriteAllText(GoldenVectors.PathTo("movement.json"), GoldenVectors.Serialize(BuildMovement()));
        File.WriteAllText(GoldenVectors.PathTo("combat.json"), GoldenVectors.Serialize(BuildCombat()));
        File.WriteAllText(GoldenVectors.PathTo("validation.json"), GoldenVectors.Serialize(BuildValidation()));
    }

    // ── Vec2 / sqrt ──────────────────────────────────────────────────────────

    internal static List<Vec2Case> BuildVec2()
    {
        // Values chosen so the sqrt is mostly irrational: an exact-square input
        // (3,4 -> 5) can agree between two runtimes by luck, so it is present as a
        // control rather than as the interesting case.
        var inputs = new (string name, float ax, float ay, float bx, float by)[]
        {
            ("sqrt_exact_square_3_4_5",      3f, 4f,          0f, 0f),
            ("sqrt_unit_x",                  1f, 0f,          0f, 0f),
            ("sqrt_unit_diagonal",           1f, 1f,          0f, 0f),
            ("sqrt_half_diagonal",           0.5f, 0.5f,      0f, 0f),
            ("sqrt_irrational_small",        0.1f, 0.3f,      0f, 0f),
            ("sqrt_irrational_typical",      12.5f, -7.25f,   3.75f, 18.5f),
            ("sqrt_two",                     1.4142135f, 0f,  0f, 0f),
            ("sqrt_large_coordinates",       499.9f, -499.9f, -499.9f, 499.9f),
            ("sqrt_tiny_magnitude",          1e-6f, 1e-6f,    0f, 0f),
            ("sqrt_below_normalize_epsilon", 1e-7f, 0f,       0f, 0f),
            ("sqrt_zero_vector",             0f, 0f,          0f, 0f),
            ("sqrt_negative_components",     -3.3f, -4.7f,    2.2f, -1.1f),
            ("sqrt_at_attack_range",         GameConstants.AttackRange, 0f, 0f, 0f),
            ("sqrt_at_aoi_radius",           GameConstants.DefaultAoiRadius, 0f, 0f, 0f),
            ("sqrt_max_input_magnitude",     1.5f, 0f,        0f, 0f),
        };

        var cases = new List<Vec2Case>();
        foreach (var i in inputs)
        {
            var a = new Vec2(i.ax, i.ay);
            var b = new Vec2(i.bx, i.by);
            Vec2 n = a.Normalized;

            cases.Add(new Vec2Case
            {
                name = i.name,
                ax = GoldenVectors.Hex(i.ax),
                ay = GoldenVectors.Hex(i.ay),
                bx = GoldenVectors.Hex(i.bx),
                by = GoldenVectors.Hex(i.by),
                expectedSqrMagnitudeA = GoldenVectors.Hex(a.SqrMagnitude),
                expectedMagnitudeA = GoldenVectors.Hex(a.Magnitude),
                expectedNormalizedX = GoldenVectors.Hex(n.X),
                expectedNormalizedY = GoldenVectors.Hex(n.Y),
                expectedDistanceSq = GoldenVectors.Hex(Vec2.DistanceSq(a, b)),
                expectedDistance = GoldenVectors.Hex(Vec2.Distance(a, b)),
            });
        }

        return cases;
    }

    // ── Movement ─────────────────────────────────────────────────────────────

    internal static List<MovementCase> BuildMovement()
    {
        var defaults = MapBounds.Default;
        var tight = MapBounds.FromSize(10f, 10f); // -5..5, to force bounds clamping
        float dt = MovementSystem.DeltaTimeForTickRate(GameConstants.DefaultTickRate);

        var inputs = new (string name, float px, float py, float mx, float my, float speed, float dt, MapBounds b, bool dead)[]
        {
            // deadzone
            ("deadzone_exact_zero",        0f, 0f,    0f,      0f,      5f, dt, defaults, false),
            ("deadzone_below_threshold",   0f, 0f,    1e-5f,   0f,      5f, dt, defaults, false),
            ("deadzone_at_threshold",      0f, 0f,    1e-4f,   0f,      5f, dt, defaults, false),
            // accepted (magnitude <= 1, partial speed preserved)
            ("accepted_just_above_deadzone", 0f, 0f,  1e-3f,   0f,      5f, dt, defaults, false),
            ("accepted_cardinal_x",        0f, 0f,    1f,      0f,      5f, dt, defaults, false),
            ("accepted_cardinal_negative_y", 0f, 0f,  0f,     -1f,      5f, dt, defaults, false),
            ("accepted_analog_half",       0f, 0f,    0.5f,    0f,      5f, dt, defaults, false),
            ("accepted_diagonal_within_unit", 0f, 0f, 0.5f,    0.5f,    5f, dt, defaults, false),
            ("accepted_from_offset_position", 12.5f, -7.25f, 1f, 0f,    5f, dt, defaults, false),
            // The magnitude-1 boundary, both sides of it. This is the branch that
            // decides whether MathF.Sqrt runs at all (MovementSystem.cs), so the
            // exact float behaviour at |v| == 1 is worth pinning rather than
            // reasoning about: (0.6, 0.8) is mathematically a unit vector but its
            // float SqrMagnitude lands just above 1, so it takes the normalize path.
            ("boundary_unit_axis_is_accepted",     0f, 0f, 1f, 0f,          5f, dt, defaults, false),
            ("boundary_just_below_one",            0f, 0f, 0.9999f, 0f,     5f, dt, defaults, false),
            ("boundary_just_above_one",            0f, 0f, 1.0001f, 0f,     5f, dt, defaults, false),
            ("boundary_pythagorean_unit_0_6_0_8",  0f, 0f, 0.6f, 0.8f,      5f, dt, defaults, false),
            ("boundary_normalized_diagonal",       0f, 0f, 0.70710678f, 0.70710678f, 5f, dt, defaults, false),
            // clamped (magnitude > 1 but <= MaxInputMagnitude): normalized
            ("clamped_raw_diagonal",       0f, 0f,    1f,      1f,      5f, dt, defaults, false),
            ("clamped_at_max_magnitude",   0f, 0f,    1.5f,    0f,      5f, dt, defaults, false),
            ("clamped_asymmetric",         0f, 0f,    1.2f,   -0.4f,    5f, dt, defaults, false),
            // rejected
            ("rejected_above_max_magnitude", 0f, 0f,  1.5001f, 0f,      5f, dt, defaults, false),
            ("rejected_far_above_max",     0f, 0f,    1000f,   1000f,   5f, dt, defaults, false),
            ("rejected_nan_x",             0f, 0f,    float.NaN, 0f,    5f, dt, defaults, false),
            ("rejected_infinity_y",        0f, 0f,    0f, float.PositiveInfinity, 5f, dt, defaults, false),
            // blocked
            ("blocked_dead_entity",        0f, 0f,    1f,      0f,      5f, dt, defaults, true),
            ("blocked_zero_speed",         0f, 0f,    1f,      0f,      0f, dt, defaults, false),
            ("blocked_negative_speed",     0f, 0f,    1f,      0f,     -5f, dt, defaults, false),
            ("blocked_infinite_speed",     0f, 0f,    1f,      0f, float.PositiveInfinity, dt, defaults, false),
            ("blocked_zero_dt",            0f, 0f,    1f,      0f,      5f, 0f, defaults, false),
            ("blocked_nan_dt",             0f, 0f,    1f,      0f,      5f, float.NaN, defaults, false),
            // bounds clamping
            ("clamped_to_bounds_max_x",    4.9f, 0f,  1f,      0f,     50f, dt, tight, false),
            ("clamped_to_bounds_min_y",    0f, -4.9f, 0f,     -1f,     50f, dt, tight, false),
            ("slides_along_edge",          5f, 0f,    1f,      1f,     20f, dt, tight, false),
            ("corner_clamps_both_axes",    4.9f, 4.9f, 1f,     1f,     50f, dt, tight, false),
            // dt clamping (MaxDeltaTime)
            ("dt_above_max_is_clamped",    0f, 0f,    1f,      0f,      5f, 10f, defaults, false),
            ("dt_at_max",                  0f, 0f,    1f,      0f,      5f, GameConstants.MaxDeltaTime, defaults, false),

            // Multiply-add intermediate-rounding discriminator.
            //
            // Integrate computes `position + direction * step`. Written as one
            // expression a runtime may evaluate it three ways: strictly in
            // float32, with a wider (double) intermediate, or contracted into a
            // single FMA instruction that rounds once instead of twice. The
            // split-multiply in Integrate denies all three by forcing a float
            // rounding between the multiply and the add.
            //
            // Every other movement case rounds identically under all three, so
            // that fix was covered by reasoning and by nothing executable. These
            // inputs separate the strict evaluation (0x401B4740, what the fixed
            // code produces) from the alternatives (0x401B473F), one ULP apart.
            //
            // WHAT THIS CASE DOES NOT PROVE. It cannot tell FMA contraction from
            // double widening — **both** predict 0x401B473F here, so a pass rules
            // out neither individually. Measured under Unity's Editor Mono JIT,
            // the mechanism is *widening*, not contraction: on
            // sqrt_negative_components an FMA would give the strict answer
            // (0x4203EB84) while Unity produced the wide one (0x4203EB85). FMA
            // contraction remains **unobserved** in that runtime, and entirely
            // unmeasured under IL2CPP, which is what ships. Do not read a green
            // suite as evidence that no runtime fuses.
            //
            // Deliberately isolates the multiply-add: magSq is 0x3E2833EB, which
            // is <= 1 so ResolveDirection returns Accepted with the raw vector and
            // no sqrt is involved, and the result lands well inside the bounds so
            // nothing clamps.
            //
            // Values are built from bit patterns rather than decimal literals
            // because a literal does not pin the bits, and the bits are the point.
            ("multiply_add_intermediate_rounding",
                BitConverter.Int32BitsToSingle(unchecked((int)0x4012A1D2)), 0f,
                BitConverter.Int32BitsToSingle(unchecked((int)0x3ECF8243)), 0f,
                5f, dt, defaults, false),
        };

        var cases = new List<MovementCase>();
        foreach (var i in inputs)
        {
            var entity = new EntityState
            {
                Id = "e",
                Type = "player",
                Position = new Vec2(i.px, i.py),
                Speed = i.speed,
                Dead = i.dead,
                Hp = i.dead ? 0 : 100,
                MaxHp = 100,
            };

            MoveResult result = MovementSystem.TryMove(entity, i.mx, i.my, i.dt, i.b, out Vec2 pos);

            cases.Add(new MovementCase
            {
                name = i.name,
                posX = GoldenVectors.Hex(i.px),
                posY = GoldenVectors.Hex(i.py),
                moveX = GoldenVectors.Hex(i.mx),
                moveY = GoldenVectors.Hex(i.my),
                speed = GoldenVectors.Hex(i.speed),
                dt = GoldenVectors.Hex(i.dt),
                minX = GoldenVectors.Hex(i.b.MinX),
                minY = GoldenVectors.Hex(i.b.MinY),
                maxX = GoldenVectors.Hex(i.b.MaxX),
                maxY = GoldenVectors.Hex(i.b.MaxY),
                dead = i.dead,
                expectedResult = result.ToString(),
                expectedX = GoldenVectors.Hex(pos.X),
                expectedY = GoldenVectors.Hex(pos.Y),
            });
        }

        return cases;
    }

    // ── Combat ───────────────────────────────────────────────────────────────

    internal static List<CombatCase> BuildCombat()
    {
        var cases = new List<CombatCase>();

        // CalculateDamage
        var damage = new (string name, int atk, int def)[]
        {
            ("damage_normal", 20, 5),
            ("damage_equal_stats_floors_to_min", 10, 10),
            ("damage_defense_exceeds_attack_floors_to_min", 5, 50),
            ("damage_zero_defense", 7, 0),
            ("damage_one_above_defense", 11, 10),
        };
        foreach (var d in damage)
        {
            var attacker = Entity("a", 0f, 0f, attack: d.atk);
            var defender = Entity("b", 1f, 0f, defense: d.def);
            cases.Add(new CombatCase
            {
                name = d.name,
                kind = "damage",
                attackerAttack = d.atk,
                defenderDefense = d.def,
                expectedDamage = CombatLogic.CalculateDamage(attacker, defender),
            });
        }

        // HandleDeath
        var death = new (string name, int hp, bool dead)[]
        {
            ("death_hp_zero_dies", 0, false),
            ("death_hp_negative_dies_and_clamps", -25, false),
            ("death_hp_positive_survives", 1, false),
            ("death_already_dead_is_not_reported_twice", 0, true),
        };
        foreach (var d in death)
        {
            var e = Entity("a", 0f, 0f);
            e.Hp = d.hp;
            e.Dead = d.dead;
            bool died = CombatLogic.HandleDeath(ref e);
            cases.Add(new CombatCase
            {
                name = d.name,
                kind = "death",
                hp = d.hp,
                alreadyDead = d.dead,
                expectedDied = died,
                expectedHp = e.Hp,
                expectedDead = e.Dead,
            });
        }

        // ValidateAttack
        float range = GameConstants.AttackRange;
        var validate = new (string name, float ax, float ay, float tx, float ty, bool targetDead, ulong tick, ulong cd)[]
        {
            ("attack_in_range_off_cooldown",      0f, 0f, 1f, 0f,      false, 100, 0),
            ("attack_exactly_at_range",           0f, 0f, range, 0f,   false, 100, 0),
            ("attack_just_out_of_range",          0f, 0f, range + 0.01f, 0f, false, 100, 0),
            ("attack_far_out_of_range",           0f, 0f, 50f, 50f,    false, 100, 0),
            ("attack_target_already_dead",        0f, 0f, 1f, 0f,      true,  100, 0),
            ("attack_on_cooldown",                0f, 0f, 1f, 0f,      false, 5, 8),
            ("attack_cooldown_expires_this_tick", 0f, 0f, 1f, 0f,      false, 8, 8),
            ("attack_diagonal_within_range",      0f, 0f, 2f, 2f,      false, 100, 0),
        };
        foreach (var v in validate)
        {
            var attacker = Entity("a", v.ax, v.ay);
            attacker.CooldownUntilTick = v.cd;
            var target = Entity("b", v.tx, v.ty);
            target.Dead = v.targetDead;

            string? err = CombatLogic.ValidateAttack(attacker, target, v.tick);
            cases.Add(new CombatCase
            {
                name = v.name,
                kind = "validate_attack",
                attackerX = GoldenVectors.Hex(v.ax),
                attackerY = GoldenVectors.Hex(v.ay),
                targetX = GoldenVectors.Hex(v.tx),
                targetY = GoldenVectors.Hex(v.ty),
                targetDead = v.targetDead,
                currentTick = (long)v.tick,
                cooldownUntilTick = (long)v.cd,
                expectedValid = err == null,
                expectedErrorPrefix = Prefix(err),
            });
        }

        return cases;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    internal static List<ValidationCase> BuildValidation()
    {
        var inputs = new (string name, bool dead, float mx, float my, string targetId, bool present,
                          float ax, float ay, float tx, float ty, bool targetDead, ulong tick, ulong cd)[]
        {
            ("valid_move_no_target",        false, 1f, 0f,   "",  false, 0f, 0f, 0f, 0f, false, 10, 0),
            ("valid_idle_no_target",        false, 0f, 0f,   "",  false, 0f, 0f, 0f, 0f, false, 10, 0),
            ("dead_entity_rejected",        true,  1f, 0f,   "",  false, 0f, 0f, 0f, 0f, false, 10, 0),
            ("invalid_move_vector_rejected", false, 1000f, 1000f, "", false, 0f, 0f, 0f, 0f, false, 10, 0),
            ("nan_move_vector_rejected",    false, float.NaN, 0f, "", false, 0f, 0f, 0f, 0f, false, 10, 0),
            ("clamped_move_is_valid",       false, 1f, 1f,   "",  false, 0f, 0f, 0f, 0f, false, 10, 0),
            ("attack_target_missing",       false, 0f, 0f,   "b", false, 0f, 0f, 1f, 0f, false, 10, 0),
            ("attack_target_present_valid", false, 0f, 0f,   "b", true,  0f, 0f, 1f, 0f, false, 10, 0),
            ("attack_and_move_together",    false, 1f, 0f,   "b", true,  0f, 0f, 1f, 0f, false, 10, 0),
            ("attack_target_out_of_range",  false, 0f, 0f,   "b", true,  0f, 0f, 40f, 0f, false, 10, 0),
            ("attack_target_dead",          false, 0f, 0f,   "b", true,  0f, 0f, 1f, 0f, true,  10, 0),
            ("attack_on_cooldown",          false, 0f, 0f,   "b", true,  0f, 0f, 1f, 0f, false, 5, 8),
        };

        var cases = new List<ValidationCase>();
        foreach (var i in inputs)
        {
            var entity = Entity("a", i.ax, i.ay);
            entity.Dead = i.dead;
            entity.CooldownUntilTick = i.cd;

            var target = Entity("b", i.tx, i.ty);
            target.Dead = i.targetDead;
            bool present = i.present;

            var input = new InputData(1, i.mx, i.my, string.IsNullOrEmpty(i.targetId) ? null : i.targetId);
            string? err = ValidationLogic.ValidateInput(
                entity, input, id => present && id == "b" ? target : (EntityState?)null, i.tick);

            cases.Add(new ValidationCase
            {
                name = i.name,
                dead = i.dead,
                moveX = GoldenVectors.Hex(i.mx),
                moveY = GoldenVectors.Hex(i.my),
                attackTargetId = i.targetId,
                targetPresent = i.present,
                attackerX = GoldenVectors.Hex(i.ax),
                attackerY = GoldenVectors.Hex(i.ay),
                targetX = GoldenVectors.Hex(i.tx),
                targetY = GoldenVectors.Hex(i.ty),
                targetDead = i.targetDead,
                currentTick = (long)i.tick,
                cooldownUntilTick = (long)i.cd,
                expectedValid = err == null,
                expectedErrorPrefix = Prefix(err),
            });
        }

        return cases;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static EntityState Entity(string id, float x, float y, int attack = 10, int defense = 0) => new()
    {
        Id = id,
        Type = "player",
        Position = new Vec2(x, y),
        Hp = 100,
        MaxHp = 100,
        Attack = attack,
        Defense = defense,
        Speed = 5f,
    };

    /// <summary>
    /// Reduce an error message to its float-free leading part. Only the range error
    /// embeds formatted numbers; comparing those across two runtimes would test
    /// number formatting rather than simulation behaviour.
    /// </summary>
    internal static string Prefix(string? error)
    {
        if (error == null) return "";
        if (error.StartsWith("target out of range", StringComparison.Ordinal)) return "target out of range";
        if (error.StartsWith("invalid move direction", StringComparison.Ordinal)) return "invalid move direction";
        return error;
    }
}
