using System.Collections.Generic;
using System.Linq;
using GameServer.Server;
using GameServer.World;

namespace GameServer.Tests.Server;

/// <summary>
/// The multi-rate scheduler's contract: which groups run on which base tick, in what order,
/// and that a system never has to know any of it.
/// </summary>
public class SimulationScheduleTests
{
    private sealed class RecordingSystem : IEcsSystem
    {
        private readonly List<string> _log;

        public RecordingSystem(string name, int order, SimulationGroup group, List<string> log)
        {
            Name = name;
            Order = order;
            Group = group;
            _log = log;
        }

        public string Name { get; }
        public int Order { get; }
        public SimulationGroup Group { get; }
        public ComponentAccess Access => default;
        public void Run(WorldWriter writer, ulong currentTick) => _log.Add(Name);
    }

    private static SimulationSchedule Build(SimulationRates rates, List<string> log) =>
        new(rates,
            new RecordingSystem("critical.a", 0, SimulationGroup.Critical, log),
            new RecordingSystem("critical.b", 1, SimulationGroup.Critical, log),
            new RecordingSystem("world.a", 0, SimulationGroup.World, log),
            new RecordingSystem("background.a", 0, SimulationGroup.Background, log));

    /// <summary>
    /// The spec's headline assertion: 60 base ticks produces 60 / 15 / 5 executions. Counted
    /// from what the systems themselves recorded, so it tests dispatch rather than the rate
    /// arithmetic that <see cref="SimulationRatesTests"/> already covers.
    /// </summary>
    [Fact]
    public void SixtyBaseTicks_Run60CriticalPasses_15World_5Background()
    {
        var log = new List<string>();
        SimulationSchedule schedule = Build(SimulationRates.Default, log);

        for (ulong tick = 1; tick <= 60; tick++)
        {
            schedule.RunDue(null!, tick);
        }

        Assert.Equal(60, log.Count(n => n == "critical.a"));
        Assert.Equal(60, log.Count(n => n == "critical.b"));
        Assert.Equal(15, log.Count(n => n == "world.a"));
        Assert.Equal(5, log.Count(n => n == "background.a"));
    }

    [Fact]
    public void TwelveBaseTicks_Run12Critical_3World_1Background()
    {
        var log = new List<string>();
        SimulationSchedule schedule = Build(SimulationRates.Default, log);

        for (ulong tick = 1; tick <= 12; tick++)
        {
            schedule.RunDue(null!, tick);
        }

        Assert.Equal(12, log.Count(n => n == "critical.a"));
        Assert.Equal(3, log.Count(n => n == "world.a"));
        Assert.Equal(1, log.Count(n => n == "background.a"));
    }

    /// <summary>
    /// Group order is fixed Critical -> World -> Background, and it is a correctness rule,
    /// not a preference: on a tick where several groups are due, the faster group's writes
    /// must land before the slower group reads them, so a slower group can never overwrite
    /// newer state with state it computed from an older read.
    /// </summary>
    [Fact]
    public void OnATickWhereAllGroupsAreDue_TheyRunFastestFirst()
    {
        var log = new List<string>();
        SimulationSchedule schedule = Build(SimulationRates.Default, log);

        schedule.RunDue(null!, 1); // tick 1: every group is due

        Assert.Equal(
            new[] { "critical.a", "critical.b", "world.a", "background.a" },
            log);
    }

    /// <summary>Within a group, order is still the declared <c>Order</c>.</summary>
    [Fact]
    public void WithinAGroup_DeclaredOrderStillDecides()
    {
        var log = new List<string>();
        var schedule = new SimulationSchedule(
            SimulationRates.Default,
            new RecordingSystem("second", 20, SimulationGroup.Critical, log),
            new RecordingSystem("first", 10, SimulationGroup.Critical, log));

        schedule.RunDue(null!, 1);

        Assert.Equal(new[] { "first", "second" }, log);
    }

    /// <summary>
    /// Two systems in different groups may share an Order value: they never run in the same
    /// pass, so there is nothing ambiguous about them, and forcing globally unique numbers
    /// would couple unrelated groups' numbering for no benefit.
    /// </summary>
    [Fact]
    public void SameOrderInDifferentGroups_IsNotAConflict()
    {
        var log = new List<string>();

        SimulationSchedule schedule = new(
            SimulationRates.Default,
            new RecordingSystem("critical.zero", 0, SimulationGroup.Critical, log),
            new RecordingSystem("world.zero", 0, SimulationGroup.World, log));

        schedule.RunDue(null!, 1);

        Assert.Equal(new[] { "critical.zero", "world.zero" }, log);
    }

    /// <summary>Duplicate Order within one group is still rejected at construction.</summary>
    [Fact]
    public void SameOrderInTheSameGroup_IsStillRejected()
    {
        var log = new List<string>();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => new SimulationSchedule(
            SimulationRates.Default,
            new RecordingSystem("a", 7, SimulationGroup.World, log),
            new RecordingSystem("b", 7, SimulationGroup.World, log)));

        Assert.Contains("Order 7", ex.Message);
    }

    /// <summary>
    /// A tick where nothing is due must not be reported as due, so the caller can skip
    /// taking the world write lock entirely. At 60/15/5 that is three ticks in four.
    /// </summary>
    [Fact]
    public void AnyDue_IsFalseOnATickWithNoDueSystems()
    {
        var log = new List<string>();
        var schedule = new SimulationSchedule(
            SimulationRates.Default,
            new RecordingSystem("world.only", 0, SimulationGroup.World, log));

        Assert.True(schedule.AnyDue(1));
        Assert.False(schedule.AnyDue(2));
        Assert.False(schedule.AnyDue(3));
        Assert.False(schedule.AnyDue(4));
        Assert.True(schedule.AnyDue(5));
    }

    /// <summary>An empty group is a real state, and it costs nothing on a tick it is due.</summary>
    [Fact]
    public void AnEmptyGroupRunsNothingAndIsNeverDue()
    {
        var log = new List<string>();
        var schedule = new SimulationSchedule(
            SimulationRates.Default,
            new RecordingSystem("critical.only", 0, SimulationGroup.Critical, log));

        Assert.Empty(schedule.SystemsIn(SimulationGroup.Background));

        for (ulong tick = 1; tick <= 24; tick++) schedule.RunDue(null!, tick);

        Assert.Equal(24, log.Count);
        Assert.All(log, n => Assert.Equal("critical.only", n));
    }

    /// <summary>
    /// The per-group observer fires once per group that ran, which is what makes the
    /// per-group metrics possible without any system knowing it is being measured.
    /// </summary>
    [Fact]
    public void TheObserverFiresOncePerGroupThatRan()
    {
        var log = new List<string>();
        SimulationSchedule schedule = Build(SimulationRates.Default, log);

        var observed = new List<SimulationGroup>();
        for (ulong tick = 1; tick <= 12; tick++)
        {
            schedule.RunDue(null!, tick, (group, _, _) => observed.Add(group));
        }

        Assert.Equal(12, observed.Count(g => g == SimulationGroup.Critical));
        Assert.Equal(3, observed.Count(g => g == SimulationGroup.World));
        Assert.Equal(1, observed.Count(g => g == SimulationGroup.Background));
    }

    /// <summary>
    /// A uniform configuration runs every group on every tick — the single-rate server.
    /// </summary>
    [Fact]
    public void UniformRates_RunEveryGroupEveryTick()
    {
        var log = new List<string>();
        SimulationSchedule schedule = Build(SimulationRates.Uniform(15), log);

        for (ulong tick = 1; tick <= 10; tick++) schedule.RunDue(null!, tick);

        Assert.Equal(10, log.Count(n => n == "world.a"));
        Assert.Equal(10, log.Count(n => n == "background.a"));
    }

    /// <summary>
    /// The enemy systems declare a group, and it is World for all three — including the
    /// reap system, which looks like cleanup but is not demotable: it is what stops a dead
    /// or centre-arrived enemy from being observable in the snapshot built later in the
    /// same tick.
    /// </summary>
    [Fact]
    public void TheEnemySystems_AllDeclareTheWorldGroup()
    {
        var world = new EcsWorld();
        var spawner = new GameServer.Scaffolding.EnemySpawner(
            world, SimulationRates.Default,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.NotEmpty(spawner.Systems);
        Assert.All(spawner.Systems, s => Assert.Equal(SimulationGroup.World, s.Group));
        Assert.Empty(spawner.Schedule.SystemsIn(SimulationGroup.Critical));
        Assert.Empty(spawner.Schedule.SystemsIn(SimulationGroup.Background));
    }
}
