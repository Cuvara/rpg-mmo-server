using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// Load-test simulation phase: bulk-spawns N entities at startup, orbits them within AOI
/// every tick so every connected client receives position deltas on every snapshot.
/// Activated by <c>LOADTEST_ENTITIES=N</c>.
/// </summary>
/// <remarks>
/// <para>Mirrors <see cref="EnemySpawner"/> in shape (implements <see cref="ISimulationPhase"/>,
/// wraps a <see cref="SimulationSchedule"/>) but differs in purpose: load-test entities
/// persist forever, orbit at fixed radius (never leave AOI), and spawn all at once rather
/// than in waves.</para>
/// <para>Mutually exclusive with <see cref="EnemySpawner"/> — the composition root picks one.</para>
/// </remarks>
public sealed class LoadTestSpawner : ISimulationPhase
{
    private readonly EcsWorld _world;
    private readonly SimulationSchedule _schedule;
    private readonly Action<ulong, WorldWriter> _runSchedule;
    private readonly Action<SimulationGroup, long, long>? _onGroupRan;
    private readonly int _targetCount;
    private readonly ILogger _logger;

    public LoadTestSpawner(
        EcsWorld world,
        SimulationRates rates,
        int targetEntityCount,
        ILogger logger,
        Action<SimulationGroup, long, long>? onGroupRan = null)
    {
        _world = world;
        _targetCount = targetEntityCount;
        _logger = logger;
        _onGroupRan = onGroupRan;

        float dt = rates.DeltaTimeFor(SimulationGroup.World);

        _schedule = new SimulationSchedule(
            rates,
            new LoadTestBulkSpawnSystem(targetEntityCount, logger),
            new LoadTestOrbitSystem(dt));

        _runSchedule = (tick, writer) => _schedule.RunDue(writer, tick, _onGroupRan);
    }

    public int AliveCount => _world.CountWith<EnemyAi>();

    public void Tick(ulong currentTick)
    {
        if (!_schedule.AnyDue(currentTick)) return;
        _world.UpdateComponents(currentTick, _runSchedule);
    }
}

/// <summary>
/// Bulk-spawns all load-test entities on the first tick. Runs once, then no-ops.
/// </summary>
internal sealed class LoadTestBulkSpawnSystem : IEcsSystem
{
    private readonly int _target;
    private readonly ILogger _logger;

    public LoadTestBulkSpawnSystem(int target, ILogger logger)
    {
        _target = target;
        _logger = logger;
    }

    public string Name => "loadtest.spawn";
    public int Order => 0;
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        writes: new[] { typeof(LoadTestState) },
        structural: true);

    public void Run(WorldWriter writer, ulong currentTick)
    {
        ref LoadTestState state = ref writer.Singleton<LoadTestState>();
        if (state.Spawned) return;
        state.Spawned = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < _target; i++)
        {
            // Uniform disc distribution within AOI radius (40 < 50 AOI).
            float angle = i * MathF.Tau / _target;
            float radius = 40f * MathF.Sqrt((i + 1f) / _target);
            float x = MathF.Cos(angle) * radius;
            float y = MathF.Sin(angle) * radius;

            writer.Spawn(
                new EntityState
                {
                    Id = string.Create(null, stackalloc char[32], $"lt-{i:D10}"),
                    Type = "mob",
                    Position = new Vec2(x, y),
                    Hp = 100,
                    MaxHp = 100,
                    Speed = 2.5f,
                    Attack = 5,
                    Defense = 2,
                },
                EntityTags.EnemyAi);
        }

        sw.Stop();
        _logger.LogInformation(
            "Load-test spawned {Count} entities in {Ms}ms",
            _target, sw.ElapsedMilliseconds);
    }
}

/// <summary>
/// Orbits every <see cref="EnemyAi"/>-tagged entity around its spawn radius each tick.
/// Generates a position delta on every entity every tick — worst-case for snapshot encoding.
/// </summary>
internal sealed class LoadTestOrbitSystem : IEcsSystem
{
    private readonly float _dt;
    private const float AngularSpeed = 0.5f; // rad/s — one full orbit every ~12.6s.

    public LoadTestOrbitSystem(float dt) => _dt = dt;

    public string Name => "loadtest.orbit";
    public int Order => 1;
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        writes: new[] { typeof(Position) });

    public void Run(WorldWriter writer, ulong currentTick)
    {
        var body = new OrbitBody(_dt);
        writer.VisitChunks<EnemyAi, OrbitBody>(ref body);
    }

    private struct OrbitBody : ISimChunkVisitor
    {
        private readonly float _cosD;
        private readonly float _sinD;

        public OrbitBody(float dt)
        {
            float dTheta = AngularSpeed * dt;
            _cosD = MathF.Cos(dTheta);
            _sinD = MathF.Sin(dTheta);
        }

        public void Visit(in SimChunk chunk)
        {
            Span<Position> positions = chunk.Positions;
            for (int i = 0; i < chunk.Count; i++)
            {
                float x = positions[i].Value.X;
                float y = positions[i].Value.Y;
                positions[i].Value = new Vec2(
                    x * _cosD - y * _sinD,
                    x * _sinD + y * _cosD);
            }
        }
    }
}

/// <summary>Singleton state for the load-test spawner.</summary>
[EcsComponent]
public struct LoadTestState
{
    public bool Spawned;
}
