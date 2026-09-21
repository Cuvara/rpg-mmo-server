using GameServer.Scaffolding;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// The behaviour added on top of the characterization suite: enemies that chase players,
/// spawn around them, scale with them, and stop despawning at a point.
///
/// <para><b>How this file relates to <see cref="EnemyAiCharacterizationTests"/>.</b> That
/// one pins what the AI did before, and it is unedited — every assertion in it still
/// passes, because every case in it has no live players and the no-player path is
/// unchanged by construction. This file only exercises worlds that <i>have</i> players,
/// which is precisely the set of worlds where the old AI and the new one differ. Together
/// they say: the fight changed, and nothing else did.</para>
///
/// <para><b>Why the AI phase is driven directly rather than through <c>TickLoop</c>.</b>
/// A chase test needs to know where the players are between ticks and nothing else may
/// move them. Going through the loop would also run input, combat and snapshotting, any
/// of which could move a player and turn a failed chase into a passing test for the wrong
/// reason.</para>
/// </summary>
public class EnemyBattleRoyaleTests
{
    private const int TickRate = 15;

    /// <summary>
    /// Settings that spawn one enemy per wave with nothing else scaling, so a test can
    /// reason about individual entities. Placement and chase are untouched — those are
    /// what is under test.
    /// </summary>
    private static EnemyAiSettings Quiet(
        int max = 1, int perPlayer = 0, int wave = 1, int wavePerPlayer = 0,
        bool chase = true, float spawnDistance = 13f, float minSpawn = 8f,
        float waveInterval = 1.5f)
    {
        var lookup = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvMaxEnemies] = max.ToString(),
            [EnemyAiSettings.EnvMaxEnemiesPerPlayer] = perPlayer.ToString(),
            [EnemyAiSettings.EnvWaveSize] = wave.ToString(),
            [EnemyAiSettings.EnvWaveSizePerPlayer] = wavePerPlayer.ToString(),
            [EnemyAiSettings.EnvWaveIntervalSec] = waveInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvSpawnDistance] = spawnDistance.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvMinSpawnDistance] = minSpawn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvChase] = chase ? "on" : "off",
        };

        Assert.True(
            EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), MapBounds.Default,
                out EnemyAiSettings? s, out string? err),
            err);
        return s!;
    }

    /// <summary>
    /// <see cref="EnemyAiSettings.Default"/> with the respawn rule off, for the tests
    /// whose subject is a player that stays dead.
    /// </summary>
    private static EnemyAiSettings NoRespawn()
    {
        var lookup = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvRespawnPlayers] = "off",
        };

        Assert.True(
            EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), MapBounds.Default,
                out EnemyAiSettings? s, out string? err),
            err);
        return s!;
    }

    private static List<EntityState> Enemies(EcsWorld world) =>
        world.GetEntitiesInRange(new Vec2(0, 0), 10_000f).FindAll(e => e.Type == "mob");

    private static float Dist(in Vec2 a, in Vec2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Run the AI phase for <paramref name="ticks"/> ticks.</summary>
    private static void Run(EnemySpawner ai, int ticks, ulong from = 1)
    {
        for (ulong t = from; t < from + (ulong)ticks; t++) ai.Tick(t);
    }

    // ── Chase ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The change itself. A player parked well away from the origin is what every enemy
    /// walks towards; the origin is no longer a destination.
    ///
    /// <para><b>This is the test that fails without the change.</b> Both assertions are
    /// one-sided against the old behaviour: the pre-change step moved enemies toward
    /// (0,0), which from a spawn ring centred on a player at (60,0) means the distance to
    /// the player grows and the distance to the origin shrinks — the exact opposite of
    /// both assertions, not merely a weaker version of them.</para>
    /// </summary>
    [Fact]
    public void EnemiesChaseThePlayer_AndNotTheOrigin()
    {
        using var world = new EcsWorld();
        var player = new Vec2(60f, 0f);
        world.AddEntity(TestHelpers.CreatePlayer("p1", player.X, player.Y));

        var ai = new EnemySpawner(world, TickRate, Quiet(max: 4, wave: 4), NullLogger.Instance);

        // One wave, then let it walk.
        Run(ai, 23);
        List<EntityState> spawned = Enemies(world);
        Assert.NotEmpty(spawned);

        var startToPlayer = spawned.ToDictionary(e => e.Id, e => Dist(e.Position, player));

        Run(ai, 30, from: 24);

        // Closing on the player, every tick of the way.
        foreach (EntityState e in Enemies(world))
        {
            Assert.True(Dist(e.Position, player) < startToPlayer[e.Id],
                $"{e.Id} did not close on the player: {startToPlayer[e.Id]} -> {Dist(e.Position, player)}");
        }

        // And the destination is the player, not the centre. Deliberately asserted as an
        // end state rather than as "distance to the origin grows": an enemy that spawned
        // on the far side of the player closes on it while its distance to the origin also
        // falls, so the per-tick form would be false for reasons that have nothing to do
        // with the behaviour under test.
        Run(ai, 15 * 60, from: 54);

        List<EntityState> settled = Enemies(world);
        Assert.Equal(4, settled.Count);   // under the old AI these reached (0,0) and were reaped
        foreach (EntityState e in settled)
        {
            Assert.True(Dist(e.Position, player) < 2f,
                $"{e.Id} is {Dist(e.Position, player)} from the player it was chasing");
            Assert.True(Dist(e.Position, default) > 50f,
                $"{e.Id} ended up at the origin ({Dist(e.Position, default)} away), not at the player");
        }
    }

    /// <summary>
    /// With two players far apart, every enemy commits to the one it spawned beside and
    /// never sets off across the map for the other. The separation (200 units) is more
    /// than an order of magnitude past the 13-unit spawn ring, so which player is nearer
    /// is never in doubt and no tie-break can decide it.
    /// </summary>
    [Fact]
    public void EnemiesCommitToTheNearestPlayer()
    {
        using var world = new EcsWorld();
        var left = new Vec2(-100f, 0f);
        var right = new Vec2(100f, 0f);
        world.AddEntity(TestHelpers.CreatePlayer("left", left.X, left.Y));
        world.AddEntity(TestHelpers.CreatePlayer("right", right.X, right.Y));

        // minSpawn 0: with the players this far apart no placement is ever near the other
        // one anyway, and 0 keeps the rejection loop out of the test's way.
        EnemyAiSettings settings = Quiet(max: 20, wave: 20, minSpawn: 0f);
        var ai = new EnemySpawner(world, TickRate, settings, NullLogger.Instance);

        Run(ai, 23);
        List<EntityState> spawned = Enemies(world);
        Assert.Equal(20, spawned.Count);

        // Which side each enemy started on. Both sides must be represented, or the
        // placement is not spreading across the player population and the rest of the
        // assertion is vacuous for one of them.
        var nearPlayer = spawned.ToDictionary(
            e => e.Id, e => Dist(e.Position, left) < Dist(e.Position, right) ? left : right);
        Assert.Contains(left, nearPlayer.Values);
        Assert.Contains(right, nearPlayer.Values);

        Run(ai, 15 * 60, from: 24);

        List<EntityState> settled = Enemies(world);
        Assert.Equal(20, settled.Count);
        foreach (EntityState e in settled)
        {
            Vec2 mine = nearPlayer[e.Id];
            Vec2 theirs = mine.X < 0 ? right : left;

            Assert.True(Dist(e.Position, mine) < 2f,
                $"{e.Id} did not reach the player it started beside: {Dist(e.Position, mine)}");
            Assert.True(Dist(e.Position, theirs) > 150f,
                $"{e.Id} crossed the map to the far player: {Dist(e.Position, theirs)}");
        }
    }

    /// <summary>
    /// Chasers stop at contact rather than walking through the player and oscillating.
    /// The bound is the contact range plus one step, because a chaser closes in whole
    /// steps and may stop just outside.
    /// </summary>
    [Fact]
    public void ChasersStopAtContactRange_AndDoNotOscillate()
    {
        using var world = new EcsWorld();
        var player = new Vec2(25f, -25f);
        world.AddEntity(TestHelpers.CreatePlayer("p1", player.X, player.Y));

        EnemyAiSettings settings = Quiet(max: 6, wave: 6);
        var ai = new EnemySpawner(world, TickRate, settings, NullLogger.Instance);

        Run(ai, 200);

        float step = settings.Speed / TickRate;
        List<EntityState> enemies = Enemies(world);
        Assert.NotEmpty(enemies);
        foreach (EntityState e in enemies)
        {
            float d = Dist(e.Position, player);
            Assert.True(d <= settings.ContactRange + step + 0.001f,
                $"{e.Id} never closed to contact: {d}");
            Assert.True(d > 0f, $"{e.Id} landed exactly on the player");
        }
    }

    // ── Despawn ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An enemy standing on the origin is not reaped while there is somebody to chase.
    /// This is the regression the change would otherwise introduce: a player who walks to
    /// (0,0) would watch everything attacking them evaporate.
    /// </summary>
    [Fact]
    public void ChasingEnemiesAreNotDespawnedAtTheCentre()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", 0f, 0f));

        var ai = new EnemySpawner(world, TickRate, Quiet(max: 5, wave: 5), NullLogger.Instance);

        // Long enough that every enemy has reached contact with a player sitting exactly
        // inside the old despawn radius.
        Run(ai, 300);

        Assert.Equal(5, ai.AliveCount);
        foreach (EntityState e in Enemies(world))
        {
            Assert.True(Dist(e.Position, default) < EnemyAiTuning.DespawnRadius,
                $"{e.Id} is not inside the old despawn radius, so this proves nothing");
        }
    }

    /// <summary>
    /// And the other side of the same rule: with nobody to chase, the centre despawn is
    /// exactly what it always was. Asserted here as well as in the characterization suite
    /// because the two conditions are now one branch, and a branch needs both arms.
    /// </summary>
    [Fact]
    public void TargetlessEnemiesStillDespawnAtTheCentre()
    {
        using var world = new EcsWorld();
        var ai = new EnemySpawner(world, TickRate, Quiet(max: 5, wave: 5), NullLogger.Instance);

        Run(ai, 300);

        Assert.Empty(Enemies(world).FindAll(
            e => Dist(e.Position, default) <= EnemyAiTuning.DespawnRadius));
    }

    /// <summary>
    /// A dead enemy is still reaped, chasing or not — despawn-on-death is the rule that
    /// replaced despawn-on-arrival, so it has to hold on the path that no longer has an
    /// arrival.
    /// </summary>
    [Fact]
    public void DeadChasersAreStillReaped()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", 40f, 40f));

        var ai = new EnemySpawner(world, TickRate, Quiet(max: 3, wave: 3), NullLogger.Instance);
        Run(ai, 23);
        Assert.Equal(3, ai.AliveCount);

        foreach (EntityState e in Enemies(world))
        {
            EntityState dead = e;
            dead.Hp = 0;
            dead.Dead = true;   // Health.Dead is a flag, not derived from Hp.
            world.AddEntity(dead); // AddEntity upserts by id.
        }

        Run(ai, 2, from: 24);
        Assert.Equal(0, ai.AliveCount);
    }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns land at the configured distance from <i>some</i> player, not on a single
    /// ring about the origin — the conveyor belt the owner was complaining about.
    /// </summary>
    [Fact]
    public void SpawnsAreAnchoredToPlayers_NotToTheOrigin()
    {
        using var world = new EcsWorld();
        var a = new Vec2(-80f, 30f);
        var b = new Vec2(80f, -30f);
        world.AddEntity(TestHelpers.CreatePlayer("a", a.X, a.Y));
        world.AddEntity(TestHelpers.CreatePlayer("b", b.X, b.Y));

        EnemyAiSettings settings = Quiet(max: 40, wave: 40);
        var ai = new EnemySpawner(world, TickRate, settings, NullLogger.Instance);

        Run(ai, 23);
        List<EntityState> enemies = Enemies(world);
        Assert.Equal(40, enemies.Count);

        // One step has already been taken on the spawn tick, so allow for it.
        float step = settings.Speed / TickRate;
        foreach (EntityState e in enemies)
        {
            float best = MathF.Min(Dist(e.Position, a), Dist(e.Position, b));
            Assert.True(best <= settings.SpawnDistance + 0.001f,
                $"{e.Id} spawned {best} from the nearest player, beyond the {settings.SpawnDistance} spawn distance");
            Assert.True(best >= settings.MinSpawnDistance - step - 0.001f,
                $"{e.Id} spawned {best} from a player, inside the {settings.MinSpawnDistance} minimum");
        }

        // And they are not all on one ring about the origin, which is what the old
        // placement produced and what this assertion would catch if the anchor regressed.
        var radii = enemies.ConvertAll(e => Dist(e.Position, default));
        Assert.True(radii.Max() - radii.Min() > 1f,
            "every enemy is the same distance from the origin, i.e. still on one ring");
    }

    /// <summary>
    /// The minimum-separation rule holds for players the spawn was <b>not</b> anchored to.
    /// Two players standing close together is the case that needs it: an enemy placed at
    /// the spawn distance from one of them can be well inside the minimum of the other.
    /// </summary>
    [Fact]
    public void NoEnemySpawnsOnTopOfABystander()
    {
        using var world = new EcsWorld();
        var a = new Vec2(0f, 0f);
        var b = new Vec2(9f, 0f); // closer together than the 13-unit spawn distance
        world.AddEntity(TestHelpers.CreatePlayer("a", a.X, a.Y));
        world.AddEntity(TestHelpers.CreatePlayer("b", b.X, b.Y));

        EnemyAiSettings settings = Quiet(max: 60, wave: 60);
        var ai = new EnemySpawner(world, TickRate, settings, NullLogger.Instance);

        Run(ai, 23);
        float step = settings.Speed / TickRate;

        // The placement is best-effort by design — with two players 9 units apart and a
        // 13-unit ring there are clear points, but not on every sample — so this asserts
        // the rule holds overwhelmingly rather than absolutely, and that nothing lands in
        // anyone's lap.
        List<EntityState> enemies = Enemies(world);
        int violations = enemies.Count(e =>
            MathF.Min(Dist(e.Position, a), Dist(e.Position, b)) < settings.MinSpawnDistance - step - 0.001f);

        Assert.True(violations * 4 <= enemies.Count,
            $"{violations} of {enemies.Count} enemies spawned inside the minimum separation");
        Assert.All(enemies, e => Assert.True(
            MathF.Min(Dist(e.Position, a), Dist(e.Position, b)) > GameConstants.AttackRange,
            "an enemy spawned already within attack range of a player"));
    }

    // ── Population scaling ───────────────────────────────────────────────────

    /// <summary>
    /// The population the owner sees is the whole point, so it is asserted end to end —
    /// through the spawner and the world, not against the arithmetic on its own (which is
    /// covered separately in <see cref="EnemyAiSettingsTests"/>).
    /// </summary>
    [Theory]
    [InlineData(1, 75)]   // 30 + 45
    [InlineData(3, 165)]  // 30 + 3x45
    public void PopulationScalesWithLivePlayers(int players, int expectedCap)
    {
        using var world = new EcsWorld();
        for (int i = 0; i < players; i++)
        {
            // Spread far apart, and far from the origin, so a targetless enemy walking to
            // the centre cannot confuse a population count with a despawn.
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", 200f + (i * 200f), 0f));
        }

        var ai = new EnemySpawner(world, TickRate, EnemyAiSettings.Default, NullLogger.Instance);

        // Long enough to saturate: 2+6xplayers per wave, one wave per 1.5s.
        Run(ai, 15 * 200);

        Assert.Equal(expectedCap, ai.AliveCount);
    }

    /// <summary>
    /// With nobody online the world is the pre-change world: capped at 30, and never
    /// saturating, because a targetless enemy walks to the centre and is reaped there. The
    /// standing population is asserted as a bound rather than a value for exactly that
    /// reason — it is a flow, not a fill.
    /// </summary>
    [Fact]
    public void NobodyOnline_KeepsThePreChangePopulation()
    {
        using var world = new EcsWorld();
        var ai = new EnemySpawner(world, TickRate, EnemyAiSettings.Default, NullLogger.Instance);

        int peak = 0;
        for (ulong t = 1; t < 15 * 200; t++)
        {
            ai.Tick(t);
            peak = Math.Max(peak, ai.AliveCount);
        }

        Assert.True(peak > 0, "nothing ever spawned");
        Assert.True(peak <= EnemyAiTuning.MaxEnemies, $"population reached {peak}, past the pre-change cap");
    }

    /// <summary>
    /// A dead player is not somebody to fight, so it does not buy a bigger world. Without
    /// this, a server everybody died on keeps spawning at full rate forever.
    /// </summary>
    [Fact]
    public void DeadPlayersDoNotRaiseTheCap()
    {
        using var world = new EcsWorld();
        EntityState corpse = TestHelpers.CreatePlayer("ghost", 300f, 0f);
        corpse.Hp = 0;
        corpse.Dead = true;
        world.AddEntity(corpse);

        // Respawn OFF, deliberately: this test's premise is a player who STAYS dead, and
        // PlayerRespawnSystem's whole job is to abolish that state. With the default
        // settings the corpse is revived on the first world tick and buys the full
        // per-player allowance — correctly, because it is alive again. Turning the rule
        // off is what keeps this test about the spawner's dead-player rule rather than
        // about the respawn rule, and the spawner's rule still has a job: a player is dead
        // for up to one world tick before the revival, and any future timed respawn widens
        // that window rather than closing it.
        var ai = new EnemySpawner(world, TickRate, NoRespawn(), NullLogger.Instance);

        int peak = 0;
        for (ulong t = 1; t < 15 * 200; t++)
        {
            ai.Tick(t);
            peak = Math.Max(peak, ai.AliveCount);
        }

        // The bound, not the standing count, for the reason
        // NobodyOnline_KeepsThePreChangePopulation gives: with nothing to chase the
        // population is a flow through the centre rather than a fill to the cap.
        Assert.True(peak > 0, "nothing ever spawned");
        Assert.True(peak <= EnemyAiTuning.MaxEnemies,
            $"a dead player bought {peak} enemies, past the {EnemyAiTuning.MaxEnemies} base cap");

        // The control: the same world with the same player alive does pass the base cap,
        // so the assertion above is discriminating rather than merely satisfiable.
        using var living = new EcsWorld();
        living.AddEntity(TestHelpers.CreatePlayer("alive", 300f, 0f));
        var liveAi = new EnemySpawner(living, TickRate, NoRespawn(), NullLogger.Instance);
        Run(liveAi, 15 * 200);

        Assert.Equal(75, liveAi.AliveCount);
    }

    // ── The off switch ───────────────────────────────────────────────────────

    /// <summary>
    /// <c>GAMESERVER_ENEMY_CHASE=off</c> restores the pre-change AI in full, with players
    /// present. The escape hatch has to be complete or it is not a control arm.
    /// </summary>
    [Fact]
    public void ChaseOff_RestoresTheWalkToTheOrigin()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", 60f, 0f));

        var ai = new EnemySpawner(
            world, TickRate, Quiet(max: 4, wave: 4, chase: false), NullLogger.Instance);

        Run(ai, 23);
        List<EntityState> spawned = Enemies(world);
        Assert.NotEmpty(spawned);

        // Anchored to the origin, not to the player, because chase is off.
        foreach (EntityState e in spawned)
        {
            Assert.True(Dist(e.Position, default) < 13.1f,
                $"{e.Id} spawned around the player with chase off: {Dist(e.Position, default)}");
        }

        // And it walks in and despawns, as it always did.
        Run(ai, 15 * 30, from: 24);
        Assert.Empty(Enemies(world).FindAll(
            e => Dist(e.Position, default) <= EnemyAiTuning.DespawnRadius));
    }

    // ── Allocation ───────────────────────────────────────────────────────────

    /// <summary>
    /// The nearest-player search runs per enemy per tick and the placement runs per
    /// spawned enemy, so both are on the path that may not allocate. Measured at a
    /// saturated population with players present, which is the state that exercises the
    /// chase scan, the reap scan and the wave's early-out on a full world.
    /// </summary>
    /// <remarks>
    /// A budget rather than a hard zero: the world's own structural bookkeeping is not
    /// this change's to pin, and a runtime upgrade must not redden the suite over a
    /// handful of bytes. It is tight enough to fail if the player set is gathered into a
    /// fresh list, or the nearest search allocates a query, which are the two regressions
    /// worth catching — either costs kilobytes per tick at this population, not bytes.
    /// </remarks>
    [Fact]
    public void SteadyStateChaseDoesNotAllocatePerTick()
    {
        using var world = new EcsWorld();
        for (int i = 0; i < 8; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", 100f + (i * 40f), i * 10f));
        }

        var ai = new EnemySpawner(world, TickRate, EnemyAiSettings.Default, NullLogger.Instance);

        // Saturate first: spawning allocates (ids, EntityState) by design, and measuring
        // through the fill would be measuring the spawner's strings, not the chase.
        ulong tick = 1;
        for (; tick < 15 * 400; tick++) ai.Tick(tick);
        int saturated = ai.AliveCount;
        Assert.True(saturated > 300, $"did not saturate: {saturated} alive");

        // Warm-up pass, so first-call JIT and buffer growth are not counted as steady state.
        for (int i = 0; i < 60; i++) ai.Tick(tick++);

        const int Measured = 300;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Measured; i++) ai.Tick(tick++);
        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / Measured;

        Assert.True(ai.AliveCount == saturated,
            $"population moved during the measurement ({saturated} -> {ai.AliveCount}), so this " +
            "measured a spawning world rather than a steady one");
        Assert.True(perTick < 256,
            $"{perTick} bytes allocated per tick chasing {saturated} enemies with 8 players");
    }
}
