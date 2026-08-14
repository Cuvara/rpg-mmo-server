using GameServer.Input;
using GameServer.Tests.Golden;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.World;

/// <summary>
/// Guards for the component-level input path (<see cref="InputHandler.ProcessInput(WorldWriter, string, InputData, ulong, bool)"/>).
///
/// <para>That path no longer composes an <see cref="EntityState"/> to move an entity: it
/// hands <c>MovementSystem.TryMove</c> only the three fields <c>TryMove</c> reads
/// (position, speed, dead) and writes the resulting position straight into the
/// <c>Position</c> component. The arithmetic is therefore still literally
/// <c>TryMove</c>'s — nothing is re-derived — but the <b>selection</b> of fields is an
/// assumption, and an assumption that would fail silently: if <c>TryMove</c> ever starts
/// reading a fourth field, the server would keep moving entities, just with that field
/// zeroed.</para>
///
/// <para><see cref="Movement_ComponentPath_IsBitExactAgainstWholeEntityTryMove"/> is what
/// makes that failure loud. It replays every position/speed/move/bounds combination in
/// the ADR-10 movement fixture through the real handler and compares the stored position
/// bit-for-bit against <c>TryMove</c> called with a <b>fully populated</b>
/// <see cref="EntityState"/> — the exact call the old <c>get</c>/<c>set</c> path made.
/// Any divergence, in arithmetic or in field selection, fails here.</para>
/// </summary>
public class ComponentInputPathTests
{
    private const int TickRate = GameConstants.DefaultTickRate;

    private static float Dt => MovementSystem.DeltaTimeForTickRate(TickRate);

    private static InputHandler NewHandler(
        EcsWorld world, MapBounds bounds, InputHandler.DeathHandler? onDeath = null) =>
        new(world, NullLogger.Instance, onDeath, TickRate, bounds);

    private static void AssertBitEqual(float expected, float actual, string because)
    {
        int e = BitConverter.SingleToInt32Bits(expected);
        int a = BitConverter.SingleToInt32Bits(actual);
        if (e != a)
        {
            throw new Xunit.Sdk.XunitException(
                $"{because}: expected {GoldenVectors.Hex(expected)} ({expected}), " +
                $"got {GoldenVectors.Hex(actual)} ({actual})");
        }
    }

    /// <summary>
    /// Every golden movement vector, replayed through the component path and compared
    /// bit-for-bit against whole-<see cref="EntityState"/> <c>TryMove</c>.
    ///
    /// <para>The fixture's own <c>dt</c> is not used: the handler derives its timestep
    /// from the tick rate and there is no seam to inject one, so both sides run at the
    /// production timestep. What the fixture contributes is its coverage of positions,
    /// speeds, input vectors (deadzone, diagonal, oversized, NaN, infinite) and bounds —
    /// which is the coverage this test needs. The fixture's own expected outputs stay
    /// under <c>GoldenVectorTests.Movement</c>, which is untouched.</para>
    /// </summary>
    [Fact]
    public void Movement_ComponentPath_IsBitExactAgainstWholeEntityTryMove()
    {
        MovementCase[] cases = GoldenVectors.Load<MovementCase>("movement.json");
        Assert.NotEmpty(cases);

        int compared = 0;

        foreach (MovementCase c in cases)
        {
            // A dead entity never reaches the movement code: the handler returns at the
            // liveness gate. DeadEntity_DoesNotMove covers that branch instead.
            if (c.dead) continue;

            var bounds = new MapBounds(
                GoldenVectors.Float(c.minX), GoldenVectors.Float(c.minY),
                GoldenVectors.Float(c.maxX), GoldenVectors.Float(c.maxY));

            var position = new Vec2(GoldenVectors.Float(c.posX), GoldenVectors.Float(c.posY));
            float speed = GoldenVectors.Float(c.speed);
            float moveX = GoldenVectors.Float(c.moveX);
            float moveY = GoldenVectors.Float(c.moveY);

            // Reference: the whole-EntityState call the get/set path used to make, with
            // every field populated. If TryMove starts reading a field the component
            // path does not supply, these two stop agreeing.
            var reference = new EntityState
            {
                Id = "e",
                Type = "player",
                Position = position,
                Speed = speed,
                Dead = false,
                Hp = 100,
                MaxHp = 100,
                Attack = 10,
                Defense = 5,
                CooldownUntilTick = 7,
                LastInputTick = 3,
            };
            MoveResult expectedResult = MovementSystem.TryMove(
                in reference, moveX, moveY, Dt, in bounds, out Vec2 expectedPosition);

            using var world = new EcsWorld();
            var state = reference;
            world.AddEntity(state);
            var handler = NewHandler(world, bounds);

            handler.ProcessInput("e", new InputData(tick: 4, moveX: moveX, moveY: moveY, attackTargetId: null));

            Vec2 actual = world.GetEntity("e")!.Value.Position;

            // TryMove reports the new position for Accepted/Clamped and echoes the old
            // one otherwise; either way the stored position must equal it.
            Vec2 expected = expectedResult is MoveResult.Accepted or MoveResult.Clamped
                ? expectedPosition
                : position;

            AssertBitEqual(expected.X, actual.X, $"{c.name}.x ({expectedResult})");
            AssertBitEqual(expected.Y, actual.Y, $"{c.name}.y ({expectedResult})");
            compared++;
        }

        Assert.True(compared > 0, "no live movement vectors were compared");
    }

    [Fact]
    public void DeadEntity_DoesNotMoveAndDoesNotAdvanceInputCursor()
    {
        using var world = new EcsWorld();
        var corpse = TestHelpers.CreatePlayer("p1", x: 3, y: 4, speed: 5f);
        corpse.Dead = true;
        corpse.Hp = 0;
        world.AddEntity(corpse);

        NewHandler(world, MapBounds.Default)
            .ProcessInput("p1", new InputData(tick: 9, moveX: 1f, moveY: 0f, attackTargetId: null));

        var after = world.GetEntity("p1")!.Value;
        Assert.Equal(3f, after.Position.X);
        Assert.Equal(4f, after.Position.Y);
        Assert.Equal(0UL, after.LastInputTick);
    }

    [Fact]
    public void UnknownEntity_IsIgnored()
    {
        using var world = new EcsWorld();
        var handler = NewHandler(world, MapBounds.Default);

        Assert.Null(Record.Exception(() =>
            handler.ProcessInput("ghost", new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null))));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void StaleInputTick_IsRejectedAndNothingIsWritten()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", speed: 5f));
        var handler = NewHandler(world, MapBounds.Default);

        handler.ProcessInput("p1", new InputData(tick: 5, moveX: 1f, moveY: 0f, attackTargetId: null));
        float afterFirst = world.GetEntity("p1")!.Value.Position.X;

        handler.ProcessInput("p1", new InputData(tick: 5, moveX: 1f, moveY: 0f, attackTargetId: null));
        handler.ProcessInput("p1", new InputData(tick: 4, moveX: 1f, moveY: 0f, attackTargetId: null));

        var after = world.GetEntity("p1")!.Value;
        Assert.Equal(afterFirst, after.Position.X);
        Assert.Equal(5UL, after.LastInputTick);
    }

    /// <summary>
    /// Identity and type are spawn-time facts. The component path stopped rewriting them
    /// on every input; this pins that they still survive one.
    /// </summary>
    [Fact]
    public void IdentityAndStatsSurviveAnInput()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 1, y: 1, hp: 77, atk: 13, def: 4, speed: 5f));
        NewHandler(world, MapBounds.Default)
            .ProcessInput("p1", new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null));

        var after = world.GetEntity("p1")!.Value;
        Assert.Equal("p1", after.Id);
        Assert.Equal("player", after.Type);
        Assert.Equal(77, after.Hp);
        Assert.Equal(100, after.MaxHp);
        Assert.Equal(13, after.Attack);
        Assert.Equal(4, after.Defense);
        Assert.Equal(5f, after.Speed);
    }

    [Fact]
    public void Attack_AppliesDamageToTargetAndCooldownToAttacker()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("a", x: 0, y: 0, atk: 20, def: 0));
        world.AddEntity(TestHelpers.CreatePlayer("b", x: 1, y: 0, hp: 100, def: 5));
        var handler = NewHandler(world, MapBounds.Default);

        handler.ProcessInput("a", new InputData(tick: 1, moveX: 0, moveY: 0, attackTargetId: "b"), currentTick: 50);

        Assert.Equal(100 - 15, world.GetEntity("b")!.Value.Hp);
        Assert.Equal(50UL + (ulong)handler.CooldownTicks, world.GetEntity("a")!.Value.CooldownUntilTick);
        // The attacker's own health is untouched by its own attack.
        Assert.Equal(100, world.GetEntity("a")!.Value.Hp);
    }

    /// <summary>
    /// The death callback receives the killer as it is <i>after</i> this input: moved,
    /// input-acknowledged, and with the cooldown already applied. The get/set path
    /// produced that because it mutated a local copy before invoking the callback; the
    /// component path has to assemble it deliberately, so it is pinned here.
    /// </summary>
    [Fact]
    public void DeathCallback_SeesKillerWithThisTicksMovementAndCooldown()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("a", x: 0, y: 0, atk: 1000, speed: 5f));
        world.AddEntity(TestHelpers.CreatePlayer("b", x: 1, y: 0, hp: 5));

        EntityState victim = default, killer = default;
        int deaths = 0;
        var handler = NewHandler(world, MapBounds.Default, (v, k) => { victim = v; killer = k; deaths++; });

        handler.ProcessInput(
            "a", new InputData(tick: 11, moveX: 1f, moveY: 0f, attackTargetId: "b"), currentTick: 60);

        Assert.Equal(1, deaths);
        Assert.Equal("b", victim.Id);
        Assert.True(victim.Dead);
        Assert.Equal(0, victim.Hp);

        Assert.Equal("a", killer.Id);
        Assert.Equal(60UL + (ulong)handler.CooldownTicks, killer.CooldownUntilTick);
        Assert.Equal(11UL, killer.LastInputTick);
        Assert.Equal(world.GetEntity("a")!.Value.Position.X, killer.Position.X);
        Assert.True(killer.Position.X > 0f, "killer state should carry this tick's movement");

        Assert.True(world.GetEntity("b")!.Value.Dead);
    }

    /// <summary>
    /// A self-targeted attack applies the cooldown and fires the death callback, but the
    /// damage is discarded.
    ///
    /// <para>This is not a designed rule, it is the observable behaviour of the
    /// get/set path: it ended with <c>set(userId, attackerCopy)</c>, which overwrote the
    /// target write whenever attacker and target were the same entity. Component writes
    /// have no such last-writer-wins accident, so <c>ProcessInput</c> discards the write
    /// explicitly to keep the wire output identical. This test exists so that a later
    /// stage that decides to <i>fix</i> the underlying oddity has to delete a test that
    /// says what it is deleting.</para>
    /// </summary>
    [Fact]
    public void SelfTargetedAttack_KeepsCooldownButDiscardsDamage_MatchingPreviousBehaviour()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("a", x: 0, y: 0, hp: 100, atk: 30, def: 5));
        var handler = NewHandler(world, MapBounds.Default);

        handler.ProcessInput("a", new InputData(tick: 1, moveX: 0, moveY: 0, attackTargetId: "a"), currentTick: 10);

        var after = world.GetEntity("a")!.Value;
        Assert.Equal(100, after.Hp);
        Assert.False(after.Dead);
        Assert.Equal(10UL + (ulong)handler.CooldownTicks, after.CooldownUntilTick);
    }

    // ── Snapshot anchor ──────────────────────────────────────────────────────

    [Fact]
    public void SnapshotAnchor_MatchesTheComposedEntity()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 2, y: -3, speed: 5f));
        NewHandler(world, MapBounds.Default)
            .ProcessInput("p1", new InputData(tick: 42, moveX: 1f, moveY: 1f, attackTargetId: null));

        Assert.True(world.TryGetSnapshotAnchor("p1", out Vec2 position, out ulong ackTick));

        var composed = world.GetEntity("p1")!.Value;
        AssertBitEqual(composed.Position.X, position.X, "anchor.x");
        AssertBitEqual(composed.Position.Y, position.Y, "anchor.y");
        Assert.Equal(composed.LastInputTick, ackTick);
        Assert.Equal(42UL, ackTick);
    }

    [Fact]
    public void SnapshotAnchor_UnknownEntity_ReturnsFalseWithDefaults()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 9, y: 9));

        Assert.False(world.TryGetSnapshotAnchor("ghost", out Vec2 position, out ulong ackTick));
        Assert.Equal(0f, position.X);
        Assert.Equal(0f, position.Y);
        Assert.Equal(0UL, ackTick);
    }

    [Fact]
    public void SnapshotAnchor_AfterRemoval_ReturnsFalse()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.RemoveEntity("p1");

        Assert.False(world.TryGetSnapshotAnchor("p1", out _, out _));
    }

    // ── WorldWriter ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownRemovedAndNullIds_YieldInvalidHandles()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.RemoveEntity("p1");
        world.AddEntity(TestHelpers.CreatePlayer("p2"));

        world.UpdateComponents(writer =>
        {
            Assert.False(writer.Resolve("p1").IsValid);
            Assert.False(writer.Resolve("nope").IsValid);
            Assert.False(writer.Resolve(null!).IsValid);
            Assert.True(writer.Resolve("p2").IsValid);
        });
    }

    [Fact]
    public void SameAs_DistinguishesAliasedHandles()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.AddEntity(TestHelpers.CreatePlayer("p2"));

        world.UpdateComponents(writer =>
        {
            EntityHandle a1 = writer.Resolve("p1");
            EntityHandle a2 = writer.Resolve("p1");
            EntityHandle b = writer.Resolve("p2");

            Assert.True(a1.SameAs(in a2));
            Assert.False(a1.SameAs(in b));
            Assert.False(default(EntityHandle).SameAs(in a1));
            Assert.False(a1.SameAs(default));
        });
    }

    [Fact]
    public void ComponentWrites_AreVisibleThroughTheOldEntityStateApi()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, hp: 40));

        world.UpdateComponents(writer =>
        {
            EntityHandle h = writer.Resolve("p1");
            writer.PositionOf(in h).Value = new Vec2(11f, 12f);
            writer.HealthOf(in h).Hp = 13;
            writer.CombatOf(in h).CooldownUntilTick = 14;
            writer.LocomotionOf(in h).Speed = 15f;
            writer.InputCursorOf(in h).LastInputTick = 16;

            EntityState composed = writer.Compose(in h);
            Assert.Equal(11f, composed.Position.X);
            Assert.Equal(13, composed.Hp);
        });

        var after = world.GetEntity("p1")!.Value;
        Assert.Equal(11f, after.Position.X);
        Assert.Equal(12f, after.Position.Y);
        Assert.Equal(13, after.Hp);
        Assert.Equal(14UL, after.CooldownUntilTick);
        Assert.Equal(15f, after.Speed);
        Assert.Equal(16UL, after.LastInputTick);
        Assert.Equal("p1", after.Id);
        Assert.Equal("player", after.Type);
    }
}
