using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;

namespace GameServer.Tests.Server;

/// <summary>
/// The scheduler's contract: ordering is <b>declared</b>, not implied by the order
/// someone wrote the calls in, and component access is declared well enough that
/// "could these two run at the same time" is answerable.
///
/// <para>The second half is the point of the declarations. Nothing runs in parallel yet
/// and nothing here makes it — but a scheduler that could not express disjointness would
/// have to be rewritten to get there, so the predicate is written and tested now, against
/// the real systems.</para>
/// </summary>
public class SystemScheduleTests
{
    private sealed class FakeSystem : IEcsSystem
    {
        private readonly List<string> _log;

        public FakeSystem(string name, int order, List<string> log, ComponentAccess access = default)
        {
            Name = name;
            Order = order;
            _log = log;
            Access = access;
        }

        public string Name { get; }
        public int Order { get; }
        public ComponentAccess Access { get; }
        public void Run(WorldWriter writer, ulong currentTick) => _log.Add(Name);
    }

    [Fact]
    public void RunsInDeclaredOrder_NotArgumentOrder()
    {
        var log = new List<string>();
        var schedule = new SystemSchedule(
            new FakeSystem("third", 30, log),
            new FakeSystem("first", 10, log),
            new FakeSystem("second", 20, log));

        using var world = new EcsWorld();
        world.UpdateComponents(w => schedule.Run(w, 1));

        Assert.Equal(new[] { "first", "second", "third" }, log);
    }

    [Fact]
    public void DuplicateOrder_IsRejectedAtConstruction()
    {
        var log = new List<string>();

        var ex = Assert.Throws<ArgumentException>(() => new SystemSchedule(
            new FakeSystem("a", 10, log),
            new FakeSystem("b", 10, log)));

        Assert.Contains("Order 10", ex.Message);
    }

    [Fact]
    public void SystemsProperty_ReportsTheRunOrder()
    {
        var log = new List<string>();
        var schedule = new SystemSchedule(
            new FakeSystem("b", 20, log),
            new FakeSystem("a", 10, log));

        Assert.Equal(new[] { "a", "b" }, schedule.Systems.Select(s => s.Name));
    }

    // ── Disjointness: the predicate a parallel step would need ───────────────

    [Fact]
    public void WriteWriteConflict_IsNotDisjoint()
    {
        var a = new ComponentAccess(writes: new[] { typeof(Position) });
        var b = new ComponentAccess(writes: new[] { typeof(Position) });

        Assert.False(a.IsDisjointFrom(b));
    }

    [Fact]
    public void ReadWriteConflict_IsNotDisjoint_InBothDirections()
    {
        var writer = new ComponentAccess(writes: new[] { typeof(Health) });
        var reader = new ComponentAccess(reads: new[] { typeof(Health) });

        Assert.False(writer.IsDisjointFrom(reader));
        Assert.False(reader.IsDisjointFrom(writer));
    }

    [Fact]
    public void DisjointComponentSets_AreDisjoint()
    {
        var a = new ComponentAccess(writes: new[] { typeof(Position) });
        var b = new ComponentAccess(writes: new[] { typeof(Health) }, reads: new[] { typeof(Locomotion) });

        Assert.True(a.IsDisjointFrom(b));
        Assert.True(b.IsDisjointFrom(a));
    }

    [Fact]
    public void ConcurrentReadsOfTheSameComponent_AreDisjoint()
    {
        var a = new ComponentAccess(reads: new[] { typeof(Position) });
        var b = new ComponentAccess(reads: new[] { typeof(Position) });

        Assert.True(a.IsDisjointFrom(b));
    }

    /// <summary>
    /// A structural system never runs concurrently with anything, whatever components it
    /// declares. `EcsWorld._structural` is a plain list guarded only by the write lock, so
    /// two systems spawning or despawning at once would race on the queue rather than on
    /// any component — a conflict component sets cannot express.
    /// </summary>
    [Fact]
    public void StructuralSystems_AreNeverDisjoint()
    {
        var structural = new ComponentAccess(writes: new[] { typeof(Position) }, structural: true);
        var unrelated = new ComponentAccess(writes: new[] { typeof(Health) });

        Assert.False(structural.IsDisjointFrom(unrelated));
        Assert.False(unrelated.IsDisjointFrom(structural));
        Assert.False(structural.IsDisjointFrom(structural));
    }

    /// <summary>
    /// The real schedule, checked against the predicate. Every pair conflicts today —
    /// spawn and reap are structural, and move writes the positions reap reads — so the
    /// serial order is not a temporary convenience, it is what the declarations say.
    /// If a future system pair does come out disjoint, that is the signal that a parallel
    /// step is worth building, and it will be visible here rather than guessed at.
    /// </summary>
    [Fact]
    public void TheEnemySchedule_HasNoConcurrentlyRunnablePair()
    {
        using var world = new EcsWorld();
        var phase = new GameServer.Scaffolding.EnemySpawner(
            world, 15, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var systems = phase.Systems;
        Assert.Equal(3, systems.Count);
        Assert.Equal(new[] { "enemy.spawn", "enemy.move", "enemy.reap" },
            systems.Select(s => s.Name));

        for (int i = 0; i < systems.Count; i++)
        {
            for (int j = i + 1; j < systems.Count; j++)
            {
                Assert.False(systems[i].Access.IsDisjointFrom(systems[j].Access),
                    $"{systems[i].Name} and {systems[j].Name} are declared disjoint — if that " +
                    "is true, the serial schedule is leaving parallelism on the table and this " +
                    "test should be updated deliberately rather than deleted.");
            }
        }
    }
}
