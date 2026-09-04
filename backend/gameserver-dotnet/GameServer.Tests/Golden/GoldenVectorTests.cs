using Shared.GameLogic;

namespace GameServer.Tests.Golden;

/// <summary>
/// ADR-10 conformance gate. Replays the committed golden vectors through
/// <c>Shared.GameLogic</c> and compares every float bit-for-bit. The Unity Test
/// Runner reads the same fixture files from the package path, so a divergence
/// between server and client fails CI on whichever side moved.
///
/// <para>
/// These tests are intentionally dumb replays: they assert nothing the fixtures
/// do not say. Fixtures are regenerated with
/// <c>GOLDEN_REGEN=1 dotnet test --filter Regenerate</c>; a fixture diff in a PR
/// means the client's predicted outcome changed.
/// </para>
/// </summary>
public class GoldenVectorTests
{
    public static TheoryData<string> Vec2Names() => Names(GoldenVectors.Load<Vec2Case>("vec2.json"), c => c.name);
    public static TheoryData<string> MovementNames() => Names(GoldenVectors.Load<MovementCase>("movement.json"), c => c.name);
    public static TheoryData<string> CombatNames() => Names(GoldenVectors.Load<CombatCase>("combat.json"), c => c.name);
    public static TheoryData<string> ValidationNames() => Names(GoldenVectors.Load<ValidationCase>("validation.json"), c => c.name);

    private static TheoryData<string> Names<T>(T[] cases, Func<T, string> name)
    {
        var data = new TheoryData<string>();
        foreach (T c in cases) data.Add(name(c));
        return data;
    }

    /// <summary>
    /// The <c>MathF.Sqrt</c> sites, pinned bit-for-bit. A NativeAOT-x64 vs
    /// IL2CPP-ARM64 divergence surfaces at a sqrt before it surfaces anywhere else,
    /// and <c>Vec2.Magnitude</c> / <c>Vec2.Normalized</c> / <c>Vec2.Distance</c> have
    /// no other vector reaching them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Vec2Names))]
    public void Vector(string name)
    {
        Vec2Case c = Array.Find(GoldenVectors.Load<Vec2Case>("vec2.json"), v => v.name == name)!;

        var a = new Vec2(GoldenVectors.Float(c.ax), GoldenVectors.Float(c.ay));
        var b = new Vec2(GoldenVectors.Float(c.bx), GoldenVectors.Float(c.by));
        Vec2 n = a.Normalized;

        GoldenVectors.AssertBitEqual(c.expectedSqrMagnitudeA, a.SqrMagnitude, name + ".sqrMagnitude");
        GoldenVectors.AssertBitEqual(c.expectedMagnitudeA, a.Magnitude, name + ".magnitude");
        GoldenVectors.AssertBitEqual(c.expectedNormalizedX, n.X, name + ".normalized.x");
        GoldenVectors.AssertBitEqual(c.expectedNormalizedY, n.Y, name + ".normalized.y");
        GoldenVectors.AssertBitEqual(c.expectedDistanceSq, Vec2.DistanceSq(a, b), name + ".distanceSq");
        GoldenVectors.AssertBitEqual(c.expectedDistance, Vec2.Distance(a, b), name + ".distance");
    }

    [Theory]
    [MemberData(nameof(MovementNames))]
    public void Movement(string name)
    {
        MovementCase c = Array.Find(GoldenVectors.Load<MovementCase>("movement.json"), v => v.name == name)!;

        var entity = new EntityState
        {
            Id = "e",
            Type = "player",
            Position = new Vec2(GoldenVectors.Float(c.posX), GoldenVectors.Float(c.posY)),
            Speed = GoldenVectors.Float(c.speed),
            Dead = c.dead,
            Hp = c.dead ? 0 : 100,
            MaxHp = 100,
        };

        var bounds = new MapBounds(
            GoldenVectors.Float(c.minX), GoldenVectors.Float(c.minY),
            GoldenVectors.Float(c.maxX), GoldenVectors.Float(c.maxY));

        MoveResult result = MovementSystem.TryMove(
            entity,
            GoldenVectors.Float(c.moveX),
            GoldenVectors.Float(c.moveY),
            GoldenVectors.Float(c.dt),
            bounds,
            out Vec2 pos);

        Assert.Equal(c.expectedResult, result.ToString());
        GoldenVectors.AssertBitEqual(c.expectedX, pos.X, c.name + ".x");
        GoldenVectors.AssertBitEqual(c.expectedY, pos.Y, c.name + ".y");
    }

    [Theory]
    [MemberData(nameof(CombatNames))]
    public void Combat(string name)
    {
        CombatCase c = Array.Find(GoldenVectors.Load<CombatCase>("combat.json"), v => v.name == name)!;

        switch (c.kind)
        {
            case "damage":
            {
                var attacker = GoldenVectorGenerator.Entity("a", 0f, 0f, attack: c.attackerAttack);
                var defender = GoldenVectorGenerator.Entity("b", 1f, 0f, defense: c.defenderDefense);
                Assert.Equal(c.expectedDamage, CombatLogic.CalculateDamage(attacker, defender));
                break;
            }

            case "death":
            {
                var e = GoldenVectorGenerator.Entity("a", 0f, 0f);
                e.Hp = c.hp;
                e.Dead = c.alreadyDead;
                bool died = CombatLogic.HandleDeath(ref e);
                Assert.Equal(c.expectedDied, died);
                Assert.Equal(c.expectedHp, e.Hp);
                Assert.Equal(c.expectedDead, e.Dead);
                break;
            }

            case "simultaneous_kill":
            {
                var attacker = GoldenVectorGenerator.Entity("a", 0f, 0f, attack: c.attackerAttack, defense: c.attackerDefense);
                attacker.Hp = c.attackerHp;
                var target = GoldenVectorGenerator.Entity("b", 1f, 0f, attack: c.targetAttack, defense: c.defenderDefense);
                target.Hp = c.targetHp;
                int dmgToTarget = CombatLogic.CalculateDamage(attacker, target);
                target.Hp -= dmgToTarget;
                CombatLogic.HandleDeath(ref target);
                int dmgToAttacker = CombatLogic.CalculateDamage(target, attacker);
                attacker.Hp -= dmgToAttacker;
                CombatLogic.HandleDeath(ref attacker);
                Assert.Equal(c.expectedAttackerHp, attacker.Hp);
                Assert.Equal(c.expectedTargetHp, target.Hp);
                Assert.Equal(c.expectedAttackerDead, attacker.Dead);
                Assert.Equal(c.expectedTargetDead, target.Dead);
                break;
            }

            case "validate_attack":
            {
                var attacker = GoldenVectorGenerator.Entity(
                    "a", GoldenVectors.Float(c.attackerX), GoldenVectors.Float(c.attackerY));
                attacker.CooldownUntilTick = (ulong)c.cooldownUntilTick;

                var target = GoldenVectorGenerator.Entity(
                    "b", GoldenVectors.Float(c.targetX), GoldenVectors.Float(c.targetY));
                target.Dead = c.targetDead;

                string? err = CombatLogic.ValidateAttack(attacker, target, (ulong)c.currentTick);
                Assert.Equal(c.expectedValid, err == null);
                Assert.Equal(c.expectedErrorPrefix, GoldenVectorGenerator.Prefix(err));
                break;
            }

            default:
                throw new InvalidDataException($"unknown combat vector kind '{c.kind}' in {c.name}");
        }
    }

    [Theory]
    [MemberData(nameof(ValidationNames))]
    public void Validation(string name)
    {
        ValidationCase c = Array.Find(GoldenVectors.Load<ValidationCase>("validation.json"), v => v.name == name)!;

        var entity = GoldenVectorGenerator.Entity(
            "a", GoldenVectors.Float(c.attackerX), GoldenVectors.Float(c.attackerY));
        entity.Dead = c.dead;
        entity.CooldownUntilTick = (ulong)c.cooldownUntilTick;

        var target = GoldenVectorGenerator.Entity(
            "b", GoldenVectors.Float(c.targetX), GoldenVectors.Float(c.targetY));
        target.Dead = c.targetDead;
        bool present = c.targetPresent;

        var input = new InputData(
            1,
            GoldenVectors.Float(c.moveX),
            GoldenVectors.Float(c.moveY),
            string.IsNullOrEmpty(c.attackTargetId) ? null : c.attackTargetId);

        string? err = ValidationLogic.ValidateInput(
            entity, input, id => present && id == "b" ? target : (EntityState?)null, (ulong)c.currentTick);

        Assert.Equal(c.expectedValid, err == null);
        Assert.Equal(c.expectedErrorPrefix, GoldenVectorGenerator.Prefix(err));
    }

    /// <summary>
    /// The committed fixtures must match what the generator produces right now.
    /// This is what turns "someone changed the logic and forgot the vectors" into a
    /// failing build rather than a client-side prediction bug found weeks later.
    /// </summary>
    [Fact]
    public void CommittedFixturesAreUpToDate()
    {
        AssertFileMatches("vec2.json", GoldenVectors.Serialize(GoldenVectorGenerator.BuildVec2()));
        AssertFileMatches("movement.json", GoldenVectors.Serialize(GoldenVectorGenerator.BuildMovement()));
        AssertFileMatches("combat.json", GoldenVectors.Serialize(GoldenVectorGenerator.BuildCombat()));
        AssertFileMatches("validation.json", GoldenVectors.Serialize(GoldenVectorGenerator.BuildValidation()));
    }

    private static void AssertFileMatches(string file, string expected)
    {
        string actual = File.ReadAllText(GoldenVectors.PathTo(file)).Replace("\r\n", "\n");
        Assert.True(expected.Replace("\r\n", "\n") == actual,
            $"{file} is stale — regenerate with: GOLDEN_REGEN=1 dotnet test --filter Regenerate");
    }

    /// <summary>
    /// The fixtures are only useful if they stay inside the Unity-readable subset:
    /// one top-level object, one `cases` array, flat objects, no nesting.
    /// </summary>
    [Theory]
    [InlineData("vec2.json")]
    [InlineData("movement.json")]
    [InlineData("combat.json")]
    [InlineData("validation.json")]
    public void FixtureShapeIsUnityJsonUtilityCompatible(string file)
    {
        string json = File.ReadAllText(GoldenVectors.PathTo(file));
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateObject());

        var cases = doc.RootElement.GetProperty("cases");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, cases.ValueKind);
        Assert.NotEmpty(cases.EnumerateArray());

        foreach (var c in cases.EnumerateArray())
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Object, c.ValueKind);
            foreach (var prop in c.EnumerateObject())
            {
                Assert.True(
                    prop.Value.ValueKind is System.Text.Json.JsonValueKind.String
                        or System.Text.Json.JsonValueKind.Number
                        or System.Text.Json.JsonValueKind.True
                        or System.Text.Json.JsonValueKind.False,
                    $"{file}: '{prop.Name}' is {prop.Value.ValueKind}; nested values are outside the JsonUtility subset");
            }
        }
    }

    /// <summary>
    /// Every float in the fixtures is an 8-digit IEEE-754 bit pattern, not decimal
    /// text — decimal text does not round-trip identically through two serializers.
    /// </summary>
    [Fact]
    public void FloatsAreStoredAsBitPatterns()
    {
        foreach (var c in GoldenVectors.Load<MovementCase>("movement.json"))
        {
            foreach (string hex in new[] { c.posX, c.posY, c.moveX, c.moveY, c.speed, c.dt, c.minX, c.minY, c.maxX, c.maxY, c.expectedX, c.expectedY })
            {
                Assert.Matches("^0x[0-9A-F]{8}$", hex);
            }
        }
    }

    /// <summary>Hex encoding round-trips every float bit-for-bit, NaN included.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(0.1f)]
    [InlineData(3.3333333f)]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void HexRoundTripsExactly(float value)
    {
        Assert.Equal(
            BitConverter.SingleToInt32Bits(value),
            BitConverter.SingleToInt32Bits(GoldenVectors.Float(GoldenVectors.Hex(value))));
    }

    /// <summary>
    /// Negative zero survives the round trip and is distinguishable from +0. A
    /// decimal-text fixture would collapse the two; the sign bit is exactly the kind
    /// of difference these vectors exist to notice.
    /// </summary>
    [Fact]
    public void NegativeZeroIsDistinctFromPositiveZero()
    {
        Assert.NotEqual(GoldenVectors.Hex(0f), GoldenVectors.Hex(-0.0f));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(-0.0f),
            BitConverter.SingleToInt32Bits(GoldenVectors.Float(GoldenVectors.Hex(-0.0f))));
    }
}
