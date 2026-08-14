using GameServer.AI;
using GameServer.Input;
using GameServer.Net;
using GameServer.Server;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.AI;

/// <summary>
/// Characterization of the enemy AI, written <b>before</b> it was split into systems and
/// left unchanged across that split.
///
/// <para>The AI had no tests at all. Refactoring untested behaviour is how behaviour
/// changes without anyone deciding to change it, so these were written against the
/// original <c>EnemySpawner.Tick(get, set, tick)</c> and pass unmodified against the
/// system split — that equivalence is the whole point, and any edit to this file during
/// the refactor would have destroyed the evidence.</para>
///
/// <para>They deliberately assert the constants and the arithmetic as they are, not as
/// they ought to be. The move step is <b>not</b> <c>MovementSystem.Integrate</c> — the AI
/// carries its own copy, unclamped by map bounds — and that is pinned here rather than
/// quietly unified, because unifying it would move enemies onto different pixels and the
/// wire must not change.</para>
///
/// <para>Spawn <i>angles</i> are not pinned: they come from <c>Random.Shared</c>. The
/// spawn <i>radius</i>, cadence, wave size and cap are all deterministic and are.</para>
/// </summary>
public class EnemyAiCharacterizationTests
{
    private const int TickRate = 15;
    private const float Dt = 1.0f / TickRate;

    // Mirrors of the AI's private tuning constants. Duplicated on purpose: if someone
    // changes the constant, these tests must fail rather than follow it.
    private const float SpawnRadius = 13.0f;
    private const int EnemiesPerWave = 2;
    private const float WaveIntervalSec = 1.5f;
    private const int MaxEnemies = 30;
    private const float EnemySpeed = 2.5f;
    private const float DespawnRadius = 2.5f;
    private const int EnemyHp = 30;

    /// <summary>Ticks until the spawn accumulator first reaches the wave interval.</summary>
    private static int TicksToFirstWave()
    {
        float acc = 0f;
        for (int t = 1; ; t++)
        {
            acc += Dt;
            if (acc >= WaveIntervalSec) return t;
        }
    }

    private static (TickLoop loop, EcsWorld world, EnemySpawner ai) NewLoop()
    {
        var world = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(world, NullLogger.Instance, null, TickRate, MapBounds.Default);
        var ai = new EnemySpawner(world, TickRate, NullLogger.Instance);
        var loop = new TickLoop(
            world, handler, connections, TickRate, GameConstants.DefaultAoiRadius,
            NullLogger.Instance, metrics: null,
            keyframeInterval: GameConstants.DefaultKeyframeInterval, enemySpawner: ai);
        return (loop, world, ai);
    }

    private static List<EntityState> Enemies(EcsWorld world) =>
        world.GetEntitiesInRange(new Vec2(0, 0), 10_000f)
             .FindAll(e => e.Type == "mob");

    // ── Spawn cadence and shape ──────────────────────────────────────────────

    [Fact]
    public void NoEnemiesSpawnBeforeTheFirstWaveInterval()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave() - 1; t++) loop.TickOnce();

            Assert.Equal(0, ai.AliveCount);
            Assert.Empty(Enemies(world));
        }
    }

    [Fact]
    public void FirstWaveSpawnsExactlyOnTheIntervalTick()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();

            Assert.Equal(EnemiesPerWave, ai.AliveCount);
            Assert.Equal(EnemiesPerWave, Enemies(world).Count);
        }
    }

    [Fact]
    public void SpawnedEnemiesCarryTheDocumentedStatsAndType()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();

            foreach (EntityState e in Enemies(world))
            {
                Assert.Equal("mob", e.Type);
                Assert.Equal(EnemyHp, e.MaxHp);
                Assert.Equal(EnemySpeed, e.Speed);
                Assert.False(e.Dead);
                Assert.StartsWith("enemy-", e.Id);
            }
        }
    }

    /// <summary>
    /// Enemies spawn on a circle of radius 13 — and then <b>move on the same tick they
    /// spawn</b>, because the original spawned into the same list the move loop was about
    /// to walk. That one-tick detail is observable in the snapshot, so it is pinned.
    /// </summary>
    [Fact]
    public void EnemiesSpawnOnTheSpawnCircle_AndHaveAlreadyMovedOneStepThatTick()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();

            float step = EnemySpeed * Dt;
            foreach (EntityState e in Enemies(world))
            {
                float r = MathF.Sqrt(e.Position.X * e.Position.X + e.Position.Y * e.Position.Y);
                Assert.Equal(SpawnRadius - step, r, precision: 3);
            }
        }
    }

    [Fact]
    public void WavesRepeatOnTheInterval()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            int first = TicksToFirstWave();
            for (int t = 0; t < first; t++) loop.TickOnce();
            Assert.Equal(EnemiesPerWave, ai.AliveCount);

            // A second full interval must add exactly one more wave.
            int ticksPerInterval = (int)MathF.Ceiling(WaveIntervalSec / Dt);
            for (int t = 0; t < ticksPerInterval; t++) loop.TickOnce();
            Assert.Equal(EnemiesPerWave * 2, ai.AliveCount);
        }
    }

    // ── Movement ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick of enemy movement is <c>speed * dt</c> toward the origin, computed with
    /// the AI's own arithmetic. Asserted bit-exactly against a re-evaluation of that
    /// exact expression, so a "tidy-up" that routes it through
    /// <c>MovementSystem.Integrate</c> — which clamps to map bounds and splits its
    /// multiply differently — fails here instead of silently moving every enemy.
    /// </summary>
    [Fact]
    public void OneTickOfMovement_IsBitExactAgainstTheAisOwnArithmetic()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();

            var before = new Dictionary<string, Vec2>();
            foreach (EntityState e in Enemies(world)) before[e.Id] = e.Position;

            loop.TickOnce();

            foreach (EntityState e in Enemies(world))
            {
                Vec2 p = before[e.Id];

                float dx = -p.X;
                float dy = -p.Y;
                float distSq = dx * dx + dy * dy;
                Vec2 expected = p;
                if (distSq > 0.01f)
                {
                    float invDist = 1.0f / MathF.Sqrt(distSq);
                    expected = new Vec2(
                        p.X + dx * invDist * EnemySpeed * Dt,
                        p.Y + dy * invDist * EnemySpeed * Dt);
                }

                Assert.Equal(
                    BitConverter.SingleToInt32Bits(expected.X),
                    BitConverter.SingleToInt32Bits(e.Position.X));
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(expected.Y),
                    BitConverter.SingleToInt32Bits(e.Position.Y));
            }
        }
    }

    [Fact]
    public void EnemiesMoveMonotonicallyTowardTheOrigin()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();

            float Radius(EntityState e) =>
                MathF.Sqrt(e.Position.X * e.Position.X + e.Position.Y * e.Position.Y);

            var last = new Dictionary<string, float>();
            foreach (EntityState e in Enemies(world)) last[e.Id] = Radius(e);

            for (int t = 0; t < 10; t++)
            {
                loop.TickOnce();
                foreach (EntityState e in Enemies(world))
                {
                    if (!last.TryGetValue(e.Id, out float prev)) continue; // newly spawned
                    Assert.True(Radius(e) < prev, $"{e.Id} did not approach the origin");
                    last[e.Id] = Radius(e);
                }
            }
        }
    }

    // ── Reap: centre zone ────────────────────────────────────────────────────

    /// <summary>
    /// An enemy that reaches the centre zone is gone from the world in the <b>same
    /// tick</b>, before snapshots are broadcast — not on the following tick. The original
    /// achieved that by having the tick loop drain a pending-removal list immediately
    /// after the AI ran, so a client never saw an enemy inside the despawn radius.
    /// </summary>
    [Fact]
    public void EnemyReachingTheCentre_IsGoneTheSameTick_AndNeverObservedInsideTheZone()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();
            Assert.Equal(EnemiesPerWave, ai.AliveCount);

            // Walk until the first wave is gone, checking every tick that nothing is
            // ever observable inside the despawn radius.
            float despawnSq = DespawnRadius * DespawnRadius;
            var everSeen = new HashSet<string>();

            for (int t = 0; t < 400; t++)
            {
                loop.TickOnce();
                foreach (EntityState e in Enemies(world))
                {
                    everSeen.Add(e.Id);
                    float dSq = e.Position.X * e.Position.X + e.Position.Y * e.Position.Y;
                    Assert.True(dSq > despawnSq,
                        $"{e.Id} observable at distance^2 {dSq} inside the despawn zone");
                }
            }

            // Reaping happened iff some id that existed earlier is now gone. Counting
            // AliveCount down would not show it — fresh waves keep the count up.
            var stillAlive = new HashSet<string>();
            foreach (EntityState e in Enemies(world)) stillAlive.Add(e.Id);
            Assert.True(everSeen.Count > stillAlive.Count,
                "no enemy ever reached the centre and was reaped in 400 ticks");
        }
    }

    // ── Reap: death ──────────────────────────────────────────────────────────

    [Fact]
    public void DeadEnemy_IsReapedAndStopsMoving()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < TicksToFirstWave(); t++) loop.TickOnce();

            EntityState victim = Enemies(world)[0];
            Vec2 positionWhenKilled = victim.Position;

            victim.Dead = true;
            victim.Hp = 0;
            world.AddEntity(victim);

            int aliveBefore = ai.AliveCount;
            loop.TickOnce();

            Assert.Null(world.GetEntity(victim.Id));
            Assert.Equal(aliveBefore - 1, ai.AliveCount);

            // And it was not moved on the tick it was reaped.
            Assert.Equal(
                BitConverter.SingleToInt32Bits(positionWhenKilled.X),
                BitConverter.SingleToInt32Bits(positionWhenKilled.X));
        }
    }

    [Fact]
    public void ReapedEnemyIdsAreNotReused()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            var seen = new HashSet<string>();
            for (int t = 0; t < 300; t++)
            {
                loop.TickOnce();
                foreach (EntityState e in Enemies(world)) seen.Add(e.Id);
            }

            // Ids are monotonic, so the highest suffix seen equals the number of distinct
            // ids ever issued — a reused id would break that equality.
            int highest = 0;
            foreach (string id in seen)
            {
                int n = int.Parse(id.Substring("enemy-".Length));
                if (n > highest) highest = n;
            }
            Assert.Equal(highest, seen.Count);
        }
    }

    // ── Cap ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EnemyCountNeverExceedsTheCap()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < 600; t++)
            {
                loop.TickOnce();
                Assert.True(ai.AliveCount <= MaxEnemies, $"alive={ai.AliveCount}");
                Assert.True(Enemies(world).Count <= MaxEnemies);
            }
        }
    }

    [Fact]
    public void AliveCountTracksTheWorld()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            for (int t = 0; t < 200; t++)
            {
                loop.TickOnce();
                Assert.Equal(Enemies(world).Count, ai.AliveCount);
            }
        }
    }

    // ── Isolation from non-AI entities ───────────────────────────────────────

    /// <summary>
    /// The AI drives only the entities it spawned. A "mob" created by anything else —
    /// tests do this constantly — must sit still, so enemy-ness cannot be inferred from
    /// <c>Type == "mob"</c>.
    /// </summary>
    [Fact]
    public void MobsNotSpawnedByTheAi_AreNeverMovedOrReaped()
    {
        var (loop, world, ai) = NewLoop();
        using (world)
        {
            world.AddEntity(TestHelpers.CreateMob("static-mob", x: 4f, y: 0f));
            world.AddEntity(TestHelpers.CreatePlayer("p1", x: 1f, y: 1f));

            for (int t = 0; t < 200; t++) loop.TickOnce();

            EntityState? mob = world.GetEntity("static-mob");
            Assert.NotNull(mob);
            Assert.Equal(4f, mob!.Value.Position.X);
            Assert.Equal(0f, mob.Value.Position.Y);

            EntityState? player = world.GetEntity("p1");
            Assert.NotNull(player);
            Assert.Equal(1f, player!.Value.Position.X);
        }
    }

    [Fact]
    public void DisabledSpawner_LeavesTheWorldAlone()
    {
        using var world = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(world, NullLogger.Instance, null, TickRate, MapBounds.Default);
        var loop = new TickLoop(world, handler, connections, TickRate,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        world.AddEntity(TestHelpers.CreateMob("m1", 5, 5));
        for (int t = 0; t < 100; t++) loop.TickOnce();

        Assert.Equal(1, world.EntityCount);
        Assert.Equal(0, loop.EnemiesAlive);
    }
}
