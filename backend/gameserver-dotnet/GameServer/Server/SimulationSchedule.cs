using System;
using System.Collections.Generic;
using GameServer.World;

namespace GameServer.Server;

/// <summary>
/// The multi-rate scheduler: an ordered set of systems per <see cref="SimulationGroup"/>,
/// run against one canonical base-tick timeline.
///
/// <para><b>Where scheduling lives.</b> All of it is here and in
/// <see cref="SimulationRates"/>. No gameplay system counts ticks, tests <c>tick % 4</c>, or
/// reads a configured frequency; a system declares <see cref="IEcsSystem.Group"/> and is
/// handed the dt of that group at construction. That is the property that makes the rate
/// model auditable — you can answer "what runs at what rate" by reading two files, and
/// changing a rate is a configuration change rather than a code change.</para>
///
/// <para><b>Order across groups is fixed: Critical, then World, then Background.</b> It is
/// not configurable and should not become so. The ordering encodes a write-ownership rule:
/// on a tick where several groups are due, the faster group's writes land before the slower
/// group reads them, so a slower group can never overwrite a newer value from a faster one
/// with a staler one computed earlier in the same tick. Reversing it would let the 5Hz group
/// clobber a 60Hz combat result — the exact cross-rate hazard multi-rate scheduling
/// introduces. Within a group, order is each system's declared
/// <see cref="IEcsSystem.Order"/>, checked for ambiguity exactly as before.</para>
///
/// <para><b>Groups may be empty, and that is a real state rather than a gap.</b> The
/// background group ships with no production systems: nothing in the current simulation can
/// tolerate a 200ms scheduling delay without a visible behaviour change, and inventing a
/// tenant for the group to look complete would be shipping a regression to satisfy a
/// diagram. The infrastructure is here, tested, and documented with the rule for what may
/// go in it (<c>docs/DESIGN.md</c>).</para>
/// </summary>
public sealed class SimulationSchedule
{
    private static readonly SimulationGroup[] GroupOrder =
    {
        SimulationGroup.Critical,
        SimulationGroup.World,
        SimulationGroup.Background,
    };

    private readonly SystemSchedule[] _byGroup;

    public SimulationSchedule(SimulationRates rates, params IEcsSystem[] systems)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(systems);

        Rates = rates;

        _byGroup = new SystemSchedule[GroupOrder.Length];
        for (int g = 0; g < GroupOrder.Length; g++)
        {
            var members = new List<IEcsSystem>();
            foreach (IEcsSystem system in systems)
            {
                if (system.Group == GroupOrder[g]) members.Add(system);
            }

            // One SystemSchedule per group, so the duplicate-Order check is per group:
            // two systems in different groups never run in the same pass and so cannot be
            // ambiguous with each other, and forcing their Order values to be globally
            // unique would couple unrelated groups' numbering for no benefit.
            _byGroup[g] = new SystemSchedule(members.ToArray());
        }
    }

    /// <summary>The rate configuration this schedule runs against.</summary>
    public SimulationRates Rates { get; }

    /// <summary>The groups, in the order they run on a tick where several are due.</summary>
    public static IReadOnlyList<SimulationGroup> Groups => GroupOrder;

    /// <summary>The systems in <paramref name="group"/>, in the order they run.</summary>
    public IReadOnlyList<IEcsSystem> SystemsIn(SimulationGroup group) =>
        _byGroup[(int)group].Systems;

    /// <summary>Whether <paramref name="group"/> is due on <paramref name="baseTick"/>.</summary>
    public bool IsDue(SimulationGroup group, ulong baseTick) => Rates.RunsOn(group, baseTick);

    /// <summary>
    /// Whether any group with at least one system is due on <paramref name="baseTick"/>.
    /// The caller uses this to avoid taking a world write scope for a tick that would run
    /// nothing.
    /// </summary>
    public bool AnyDue(ulong baseTick)
    {
        for (int g = 0; g < GroupOrder.Length; g++)
        {
            if (_byGroup[g].Systems.Count > 0 && IsDue(GroupOrder[g], baseTick)) return true;
        }
        return false;
    }

    /// <summary>
    /// Run every group due on <paramref name="baseTick"/>, in group order, inside the
    /// caller's write scope.
    /// </summary>
    /// <param name="writer">The world write scope the caller already opened.</param>
    /// <param name="baseTick">The canonical base tick.</param>
    /// <param name="onGroupRan">
    /// Optional per-group observer, called after each group that ran with the group and its
    /// elapsed <see cref="System.Diagnostics.Stopwatch"/> timestamp pair. Kept as a callback
    /// rather than as a metrics dependency so this type stays free of observability
    /// concerns and remains trivially testable.
    /// </param>
    public void RunDue(
        WorldWriter writer,
        ulong baseTick,
        Action<SimulationGroup, long, long>? onGroupRan = null)
    {
        for (int g = 0; g < GroupOrder.Length; g++)
        {
            SimulationGroup group = GroupOrder[g];
            if (_byGroup[g].Systems.Count == 0) continue;
            if (!IsDue(group, baseTick)) continue;

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _byGroup[g].Run(writer, baseTick);
            onGroupRan?.Invoke(group, start, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }
}
