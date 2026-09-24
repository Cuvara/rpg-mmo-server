using GameServer.Input;
using GameServer.Net;
using GameServer.Scaffolding;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// Synthetic players: that they exist, move, fight, draw the enemy AI to them — and that
/// they never reach the player store.
///
/// <para><b>The persistence assertions are the ones that matter most.</b> A bot is a
/// player-type entity on purpose, so every default in the system treats it as a player.
/// That is right for AOI, snapshots, rendering and the enemy AI, and catastrophic for the
/// save sweep, which writes a row per player entity keyed by id and reports success. That
/// failure compiles, passes every other test, and is discovered by somebody reading the
/// table months later.</para>
/// </summary>
public class BotPlayerTests
{
    private const int TickRate = 15;

    private static BotSettings Bots(int count, float spread = 30f, float engage = 40f, int? hp = null)
    {
        var env = new Dictionary<string, string?>
        {
            [BotSettings.EnvCount] = count.ToString(),
            [BotSettings.EnvSpread] = spread.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [BotSettings.EnvEngageRange] = engage.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (hp is not null)
        {
            env[BotSettings.EnvHp] = hp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.True(
            BotSettings.TryCreate(n => env.GetValueOrDefault(n), MapBounds.Default,
                out BotSettings? s, out string? err),
            err);
        return s!;
    }

    private static (EcsWorld World, TickLoop Loop, BotPlayerSpawner Bots, EnemySpawner Enemies)
        NewWorld(BotSettings bots, EnemyAiSettings? enemies = null)
    {
        var world = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(world, NullLogger.Instance, null, TickRate, MapBounds.Default);
        // Peaceful enemies by default, and that is a deliberate isolation rather than a
        // convenience. Every test in this file is about where entities ARE — bot spread,
        // player-relative spawning, the drift trap, the population cap. With enemy attacks
        // on, a player parked for thirty seconds of simulated time and never fighting back
        // dies and respawns at the spawn point, so the position under assertion stops being
        // the position the test placed and the failure reads as a placement bug. The tests
        // whose subject IS enemy combat pass their own settings, and
        // EnemyAttackTests covers the rule itself.
        var enemyPhase = new EnemySpawner(
            world, TickRate, enemies ?? Peaceful(), NullLogger.Instance);
        var botPhase = new BotPlayerSpawner(
            world, SimulationRates.Uniform(TickRate), bots, NullLogger.Instance);
        var composite = new CompositeSimulationPhase(enemyPhase, botPhase);
        var loop = new TickLoop(
            world, handler, connections, TickRate, GameConstants.DefaultAoiRadius,
            NullLogger.Instance, metrics: null,
            keyframeInterval: GameConstants.DefaultKeyframeInterval, simulationPhase: composite);

        return (world, loop, botPhase, enemyPhase);
    }

    /// <summary>
    /// <see cref="EnemyAiSettings.Default"/> with enemy-side combat off — see
    /// <c>NewWorld</c> for why that is this file's default.
    /// </summary>
    private static EnemyAiSettings Peaceful()
    {
        var lookup = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvAttacksEnabled] = "off",
        };

        Assert.True(
            EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), MapBounds.Default,
                out EnemyAiSettings? s, out string? err),
            err);
        return s!;
    }

    /// <summary>
    /// Bots are killable now, and a demo whose crowd drains away is a demo that ends
    /// empty. A dead bot never acts again for the life of the process — nothing reaps it,
    /// nothing revives it and <c>BotBrainSystem</c> skips it — so without the respawn rule
    /// the synthetic population is a slowly emptying room with <c>bots_alive</c> still
    /// reporting its full count.
    ///
    /// <para>Asserted as a pair, because the count alone cannot tell "nothing died" from
    /// "everything died and came back": the control arm has to show bots actually dying
    /// when the rule is off, and the live arm has to show the respawn counter moving.</para>
    /// </summary>
    [Fact]
    public void BotsUnderAttackAreRespawnedRatherThanDrainingAway()
    {
        (int Living, long Respawns) Run(bool respawn)
        {
            var lookup = new Dictionary<string, string?>
            {
                // A lethal fight on purpose: enough attackers and a short enough window
                // that bots at 6 HP die inside the run, so both arms reach the state the
                // rule is about.
                [EnemyAiSettings.EnvAttackersPerTarget] = "8",
                [EnemyAiSettings.EnvAttackIntervalSec] = "0.1",
                [EnemyAiSettings.EnvRespawnPlayers] = respawn ? "on" : "off",
            };
            Assert.True(
                EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), MapBounds.Default,
                    out EnemyAiSettings? enemies, out string? err),
                err);

            var (world, loop, _, phase) = NewWorld(Bots(8, spread: 5f, hp: 6), enemies);
            using (world)
            {
                for (int t = 0; t < 15 * 40; t++) loop.TickOnce();

                int living = BotStates(world).FindAll(b => !b.Dead).Count;
                return (living, phase.Attacks.Respawns);
            }
        }

        (int Living, long Respawns) dead = Run(respawn: false);
        (int Living, long Respawns) alive = Run(respawn: true);

        Assert.True(dead.Living < 8,
            "no bot died even with the rule off, so this test never reached its subject");
        Assert.Equal(0, dead.Respawns);

        Assert.Equal(8, alive.Living);
        Assert.True(alive.Respawns > 0, "bots died but nothing was counted as a respawn");
    }

    private static float Dist(in Vec2 a, in Vec2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static List<EntityState> BotStates(EcsWorld world) =>
        world.PlayerStates().FindAll(p => p.Id.StartsWith(BotSettings.IdPrefix, StringComparison.Ordinal));

    // ── Existence and identity ───────────────────────────────────────────────

    [Fact]
    public void BotsSpawnOnceAndAreRealPlayerEntities()
    {
        var (world, loop, bots, _) = NewWorld(Bots(12));
        using (world)
        {
            for (int t = 0; t < 30; t++) loop.TickOnce();

            Assert.Equal(12, bots.AliveCount);
            Assert.Equal(12, world.CountWith<BotTag>());

            // A player everywhere it should be: in the player archetype, so AOI, snapshots
            // and the enemy AI all see it without knowing bots exist.
            Assert.Equal(12, world.CountWith<PlayerTag>());
            Assert.All(BotStates(world), b => Assert.Equal("player", b.Type));

            // They spawn once, not in waves.
            for (int t = 0; t < 200; t++) loop.TickOnce();
            Assert.Equal(12, bots.AliveCount);
        }
    }

    [Fact]
    public void BotsAreSpreadOut_NotStackedOnTheOrigin()
    {
        var (world, loop, _, _) = NewWorld(Bots(40, spread: 120f));
        using (world)
        {
            for (int t = 0; t < 5; t++) loop.TickOnce();

            List<EntityState> placed = BotStates(world);
            Assert.Equal(40, placed.Count);

            var radii = placed.ConvertAll(b => Dist(b.Position, default));

            // Spread over the disc, not on one ring and not in one pile. Both bounds
            // matter: the max catches a cluster at the centre, the spread between min and
            // max catches the ring shape LoadTestSpawner produces.
            Assert.True(radii.Max() > 60f, $"bots never reach the outside: max radius {radii.Max()}");
            Assert.True(radii.Max() - radii.Min() > 40f, "bots are all at one radius, i.e. on a ring");
        }
    }

    // ── Persistence: the silent failure ──────────────────────────────────────

    /// <summary>
    /// The guard. <c>AsyncSaver.SaveAllAsync</c> sweeps
    /// <see cref="EcsWorld.PersistablePlayerStates"/>, and a bot must not appear in it —
    /// while still appearing in <see cref="EcsWorld.PlayerStates"/>, because that is what
    /// makes it a player to everything else.
    /// </summary>
    [Fact]
    public void BotsAreInPlayerStates_ButNeverInThePersistableSweep()
    {
        var (world, loop, _, _) = NewWorld(Bots(8));
        using (world)
        {
            world.AddEntity(TestHelpers.CreatePlayer("real-user", 5f, 5f));
            for (int t = 0; t < 30; t++) loop.TickOnce();

            // Seen as a player by everything that asks the world for players...
            List<EntityState> all = world.PlayerStates();
            Assert.Equal(9, all.Count);
            Assert.Equal(8, all.Count(p => p.Id.StartsWith(BotSettings.IdPrefix, StringComparison.Ordinal)));

            // ...and invisible to the one caller that writes rows to a database.
            List<EntityState> persistable = world.PersistablePlayerStates();
            Assert.Single(persistable);
            Assert.Equal("real-user", persistable[0].Id);
        }
    }

    /// <summary>
    /// The exclusion is by <see cref="BotTag"/>, not by id prefix. Asserted explicitly
    /// because a prefix convention is the obvious shortcut, it passes every test above,
    /// and it breaks silently the first time somebody names a bot something else — or the
    /// first time a real user id happens to start with the prefix.
    /// </summary>
    [Fact]
    public void ARealPlayerNamedLikeABotIsStillPersisted()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer($"{BotSettings.IdPrefix}impostor", 1f, 1f));

        List<EntityState> persistable = world.PersistablePlayerStates();

        Assert.Single(persistable);
        Assert.Equal($"{BotSettings.IdPrefix}impostor", persistable[0].Id);
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Bots move. Through the real input path — the brain emits a direction and
    /// <c>InputHandler</c> integrates it — so a bot that moves proves the whole path
    /// works, not just that a position was written.
    /// </summary>
    [Fact]
    public void BotsMove()
    {
        var (world, loop, _, _) = NewWorld(Bots(10), enemies: null);
        using (world)
        {
            for (int t = 0; t < 10; t++) loop.TickOnce();
            var start = BotStates(world).ToDictionary(b => b.Id, b => b.Position);
            Assert.NotEmpty(start);

            for (int t = 0; t < 90; t++) loop.TickOnce();

            int moved = BotStates(world).Count(b => Dist(b.Position, start[b.Id]) > 0.5f);
            Assert.True(moved > start.Count / 2,
                $"only {moved} of {start.Count} bots moved at all");
        }
    }

    /// <summary>
    /// Bots close on enemies and kill them. The kill is the assertion that matters: it can
    /// only happen through <c>CombatLogic</c>, so it proves the bot is fighting on the same
    /// path a player does rather than on a private damage routine.
    /// </summary>
    [Fact]
    public void BotsEngageAndKillEnemies()
    {
        // Enemies capped low and spawning fast, so the fight resolves inside the test.
        var enemyEnv = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvMaxEnemies] = "6",
            [EnemyAiSettings.EnvMaxEnemiesPerPlayer] = "0",
            [EnemyAiSettings.EnvWaveSize] = "6",
            [EnemyAiSettings.EnvWaveSizePerPlayer] = "0",
        };
        Assert.True(EnemyAiSettings.TryCreate(
            n => enemyEnv.GetValueOrDefault(n), MapBounds.Default,
            out EnemyAiSettings? enemies, out string? err), err);

        var (world, loop, _, enemyPhase) = NewWorld(Bots(6, spread: 10f), enemies);
        using (world)
        {
            // Distinct enemy ids seen over the run, NOT the standing population.
            //
            // The standing count is the wrong instrument and an earlier version of this
            // test used it: "the population reached zero" is trivially true for the 22
            // ticks before the first wave, so the assertion passed with the bots' attack
            // deliberately removed. Enemy ids are monotonic and never reused, so counting
            // distinct ids measures TURNOVER, and with bots present the only thing that
            // removes an enemy is death — the centre despawn applies only when there is
            // nothing to chase, and a bot is something to chase.
            var everSeen = new HashSet<string>(StringComparer.Ordinal);
            int peakEnemies = 0;
            for (int t = 0; t < 15 * 60; t++)
            {
                loop.TickOnce();
                peakEnemies = Math.Max(peakEnemies, enemyPhase.AliveCount);
                foreach (EntityState e in world.GetEntitiesInRange(new Vec2(0, 0), 10_000f))
                {
                    if (e.Type == "mob") everSeen.Add(e.Id);
                }
            }

            Assert.True(peakEnemies > 0, "no enemies ever spawned, so nothing was tested");

            // Capped at 6. Without kills the same 6 ids live for the whole run.
            Assert.True(everSeen.Count > 12,
                $"only {everSeen.Count} distinct enemies existed across the run at a cap of 6 " +
                $"(peak alive {peakEnemies}) — nothing is dying, so the bots are not fighting");
        }
    }

    /// <summary>
    /// Bots are what the enemy spawner anchors on. This is the compounding effect that
    /// makes the map feel inhabited: more players means more enemies AND enemies spread
    /// across where the players are.
    /// </summary>
    [Fact]
    public void EnemiesTreatBotsAsPlayers_ForBothPopulationAndPlacement()
    {
        var (world, loop, _, enemyPhase) = NewWorld(Bots(4, spread: 150f));
        using (world)
        {
            for (int t = 0; t < 15 * 120; t++) loop.TickOnce();

            // 30 base + 4 bots x 45 = 210, not the 30 an empty server gets.
            Assert.True(enemyPhase.AliveCount > EnemyAiTuning.MaxEnemies,
                $"enemy population is {enemyPhase.AliveCount}, so bots are not counted as players");

            // And they are not all in one place.
            var mobs = world.GetEntitiesInRange(new Vec2(0, 0), 10_000f).FindAll(e => e.Type == "mob");
            var radii = mobs.ConvertAll(e => Dist(e.Position, default));
            Assert.True(radii.Max() - radii.Min() > 50f,
                "every enemy is at the same distance from the origin, i.e. still on one ring");
        }
    }

    /// <summary>
    /// The cap published on <c>/status</c> must be counted from the world's player
    /// entities, never from the connection count.
    ///
    /// <para><b>This is a regression test for a defect this feature introduced and a live
    /// run caught.</b> <c>enemy_ai_max_now</c> was derived from <c>players_online</c>,
    /// which counts connections. Bots hold none, so a probe server running 24 bots
    /// published a cap of 30 while the spawner was actually running 1110 — a field that
    /// looked exact and was wrong by a factor of 37, on the very endpoint added so an
    /// operator could ask a pod what it was doing.</para>
    /// </summary>
    [Fact]
    public void ThePublishedCapCountsBots_BecauseTheSpawnerDoes()
    {
        var (world, loop, _, enemyPhase) = NewWorld(Bots(6, spread: 150f));
        using (world)
        {
            for (int t = 0; t < 15 * 120; t++) loop.TickOnce();

            // What /status publishes: the cap for the world's player entities.
            int published = EnemyAiSettings.Default.EffectiveMaxEnemies(world.CountWith<PlayerTag>());
            Assert.Equal(EnemyAiTuning.MaxEnemies + (EnemyAiTuning.MaxEnemiesPerPlayer * 6), published);

            // The world runs at that cap — but NOT exactly at it, and an earlier version of
            // this test asserted equality and failed intermittently at 299 of 300. The
            // reason is the feature working: bots kill enemies continuously, so the standing
            // population is the cap minus whatever has died since the last wave. The honest
            // assertion is therefore "saturated to within one wave", and it still separates
            // a 300-cap world from the 30-cap one the connection count would have published.
            int wave = EnemyAiSettings.Default.EffectiveWaveSize(6);
            Assert.InRange(enemyPhase.AliveCount, published - wave, published);

            // And the number the field used to be derived from. No connection exists in
            // this test, so the connection count is zero and would publish the base cap —
            // the exact wrong answer the live run found.
            int fromConnections = EnemyAiSettings.Default.EffectiveMaxEnemies(0);
            Assert.NotEqual(fromConnections, published);
            Assert.Equal(EnemyAiTuning.MaxEnemies, fromConnections);
        }
    }

    // ── The drift trap ───────────────────────────────────────────────────────

    /// <summary>
    /// The failure the lead hit on the previous enemy work, asserted directly rather than
    /// reasoned about.
    ///
    /// <para>Before this change, enemies lived in a disc about the world <b>origin</b>
    /// while the AOI is 50 units about the <b>player</b>, so a player whose persisted
    /// position had drifted past roughly 63 units saw zero enemies — permanently,
    /// correctly, with every counter clean. Player position is persisted per device id, so
    /// walking into it takes nothing more than playing once and coming back.</para>
    ///
    /// <para>Player-relative spawning removes the failure rather than mitigating it: the
    /// enemies come to wherever the player actually is. This test puts a player 500 units
    /// out — eight AOI radii, far past the old cliff — and asserts they are in a fight.</para>
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]        // the origin, the only place that used to work
    [InlineData(63f, 0f)]       // exactly the old cliff edge
    [InlineData(500f, -500f)]   // hopelessly far out under the old rules
    public void AFreshlyJoinedPlayerAtADriftedPositionIsInAFight(float x, float y)
    {
        var (world, loop, _, _) = NewWorld(BotSettings.Disabled);
        using (world)
        {
            world.AddEntity(TestHelpers.CreatePlayer("drifted", x, y));

            for (int t = 0; t < 15 * 30; t++) loop.TickOnce();

            var self = new Vec2(x, y);

            // Inside the player's own area of interest, which is what they can actually
            // see — not merely "somewhere in the world".
            List<EntityState> visible = world
                .GetEntitiesInRange(self, GameConstants.DefaultAoiRadius)
                .FindAll(e => e.Type == "mob");

            Assert.True(visible.Count > 0,
                $"a player at ({x},{y}) sees no enemies inside their {GameConstants.DefaultAoiRadius}-unit AOI");

            // And they are closing, not milling about at a fixed distance.
            Assert.Contains(visible, e => Dist(e.Position, self) < GameConstants.AttackRange + 1f);
        }
    }

    // ── Off by default ───────────────────────────────────────────────────────

    /// <summary>
    /// Unset means off, and off means nothing at all: no entities, no phase behaviour, no
    /// change to a deployment that never heard of this feature.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void BotsAreOffUnlessAskedFor(string? raw)
    {
        var env = new Dictionary<string, string?> { [BotSettings.EnvCount] = raw };

        Assert.True(BotSettings.TryCreate(
            n => env.GetValueOrDefault(n), MapBounds.Default,
            out BotSettings? s, out string? err), err);

        Assert.False(s!.Enabled);
        Assert.Equal(0, s.Count);
        Assert.Equal("off", s.ToString());
    }

    [Theory]
    [InlineData(BotSettings.EnvCount, "8O")]
    [InlineData(BotSettings.EnvCount, "-1")]
    [InlineData(BotSettings.EnvCount, "999999")]
    [InlineData(BotSettings.EnvSpread, "wide")]
    [InlineData(BotSettings.EnvSpread, "NaN")]
    [InlineData(BotSettings.EnvSpeed, "0")]
    [InlineData(BotSettings.EnvSpeed, "-1")]
    [InlineData(BotSettings.EnvEngageRange, "0")]
    [InlineData(BotSettings.EnvHp, "0")]
    [InlineData(BotSettings.EnvAttack, "-1")]
    public void UnusableBotValuesAreRefused_NotDefaulted(string name, string raw)
    {
        var env = new Dictionary<string, string?> { [name] = raw };

        Assert.False(BotSettings.TryCreate(
            n => env.GetValueOrDefault(n), MapBounds.Default, out BotSettings? s, out string? err),
            $"{name}={raw} was accepted");
        Assert.Null(s);
        Assert.Contains(name, err);
    }

    // ── Composition ──────────────────────────────────────────────────────────

    /// <summary>
    /// The composite runs its phases in argument order, and that order is load-bearing:
    /// the bot brain reads the enemies the enemy phase created this tick.
    /// </summary>
    [Fact]
    public void CompositeRunsPhasesInOrder()
    {
        var order = new List<string>();
        var composite = new CompositeSimulationPhase(
            new RecordingPhase("first", order),
            new RecordingPhase("second", order));

        composite.Tick(1);
        composite.Tick(2);

        Assert.Equal(new[] { "first", "second", "first", "second" }, order);
    }

    private sealed class RecordingPhase : ISimulationPhase
    {
        private readonly string _name;
        private readonly List<string> _log;

        public RecordingPhase(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public void Tick(ulong currentTick) => _log.Add(_name);
    }
}
