using System;
using System.Collections.Generic;
using GameServer.World;

namespace GameServer.Server;

/// <summary>
/// The component types a system touches, declared rather than inferred.
///
/// <para>Declaring these buys two things. Today: a reader can see what a system does
/// without reading its body, and <see cref="SystemSchedule"/> can reject an ordering
/// whose systems obviously conflict. Later: it is the <b>only</b> thing that makes
/// "these two systems can run at the same time" expressible — two systems may run
/// concurrently exactly when neither writes a component the other reads or writes. A
/// scheduler without these sets cannot answer that question and would have to be
/// rewritten to get there, which is why they exist before anything is parallel.</para>
/// </summary>
public readonly struct ComponentAccess
{
    /// <summary>Component types the system reads but does not modify.</summary>
    public readonly Type[] Reads;

    /// <summary>Component types the system modifies.</summary>
    public readonly Type[] Writes;

    /// <summary>
    /// True when the system creates or destroys entities. Structural work serialises
    /// against everything: it goes through the world's deferred queue, which is a plain
    /// list guarded only by the write lock, so two systems doing it concurrently would
    /// race on the queue itself rather than on any component.
    /// </summary>
    public readonly bool Structural;

    public ComponentAccess(Type[]? reads = null, Type[]? writes = null, bool structural = false)
    {
        Reads = reads ?? Array.Empty<Type>();
        Writes = writes ?? Array.Empty<Type>();
        Structural = structural;
    }

    /// <summary>
    /// Whether two systems could run concurrently, on component access alone.
    ///
    /// <para><b>Nothing calls this to schedule work yet</b> — the schedule runs serially,
    /// for the reason given on <see cref="SystemSchedule"/>: every pair of systems in it
    /// currently conflicts, so there is nothing for a parallel step to overlap. It is here,
    /// and tested, because it is the predicate a parallel step would need, and writing it
    /// now is what proves the declared sets are sufficient to express it.</para>
    /// </summary>
    public bool IsDisjointFrom(in ComponentAccess other)
    {
        if (Structural || other.Structural) return false;

        return !Intersects(Writes, other.Writes)
            && !Intersects(Writes, other.Reads)
            && !Intersects(Reads, other.Writes);
    }

    private static bool Intersects(Type[] a, Type[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                if (a[i] == b[j]) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// One unit of simulation work with a declared name, position and component access.
/// </summary>
public interface IEcsSystem
{
    /// <summary>Stable name, used in ordering errors and in logs.</summary>
    string Name { get; }

    /// <summary>
    /// The rate group this system belongs to.
    ///
    /// <para>A system declares its group and nothing else about frequency: it never counts
    /// ticks, never tests <c>tick % n</c>, and never reads the configured Hz. The scheduler
    /// decides when it runs and hands it the dt of its group. That is the difference
    /// between a rate model you can audit in one place and one that is smeared across
    /// gameplay code — see <see cref="SimulationRates"/>.</para>
    ///
    /// <para>Defaulted to <see cref="SimulationGroup.World"/>: world simulation is what a
    /// system did before groups existed, so an implementation that says nothing keeps its
    /// old cadence rather than being silently promoted to the base rate.</para>
    /// </summary>
    SimulationGroup Group => SimulationGroup.World;

    /// <summary>
    /// Position in the schedule. Ordering is <b>declared here</b>, not implied by the
    /// order someone happened to write the calls in — which is what it was before.
    /// </summary>
    int Order { get; }

    /// <summary>What this system reads and writes.</summary>
    ComponentAccess Access { get; }

    /// <summary>Run one tick of this system inside the schedule's write scope.</summary>
    void Run(WorldWriter writer, ulong currentTick);
}

/// <summary>
/// An ordered set of systems run inside one world write scope.
///
/// <para>Deliberately a list with a contract rather than a framework: it sorts by declared
/// <see cref="IEcsSystem.Order"/>, rejects duplicate orders at construction, and runs.
/// The value is that ordering and component access are now <i>declared</i> and therefore
/// checkable, where before they were the order of three method calls in a private
/// method.</para>
///
/// <para><b>Why it still runs serially.</b> Not because the world cannot take workers —
/// it now can. The two preconditions this comment used to record as blocking are fixed in
/// <c>EcsWorld</c>: the deferred-structural queue is per worker slot and drains in slot
/// order, and the deferral decision is a world-level flag rather than the
/// <c>[ThreadStatic]</c> iteration depth. <c>EcsWorld.UpdateComponentsParallel</c> runs a
/// body on N threads, and <c>ParallelRegionDeterminismTests</c> demonstrates that the
/// resulting world does not depend on how the workers were scheduled.</para>
///
/// <para>It runs serially because <b>there is nothing here to run in parallel</b>. Of the
/// three systems in the schedule today, two declare
/// <see cref="ComponentAccess.Structural"/> and are excluded from concurrency by the first
/// line of <see cref="ComponentAccess.IsDisjointFrom"/>; the third has nothing left to pair
/// with. Every pair conflicts, so a parallel step would run them one at a time and pay for
/// the threads. Decision 7 of ADR-12 — speed is not claimed without measurement — cuts
/// against building it before there is a workload that benefits.</para>
///
/// <para>The condition to revisit this is concrete: two or more non-structural systems
/// whose component sets are disjoint. At that point <see cref="ComponentAccess"/> already
/// answers which pairs may run together, and the world already guarantees the result does
/// not depend on the order they finish.</para>
/// </summary>
public sealed class SystemSchedule
{
    private readonly IEcsSystem[] _systems;

    public SystemSchedule(params IEcsSystem[] systems)
    {
        ArgumentNullException.ThrowIfNull(systems);

        var ordered = new List<IEcsSystem>(systems);
        ordered.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Order == ordered[i - 1].Order)
            {
                throw new ArgumentException(
                    $"Systems '{ordered[i - 1].Name}' and '{ordered[i].Name}' both declare " +
                    $"Order {ordered[i].Order}. Ordering must be total: an ambiguous pair " +
                    "would run in whatever order the array happened to arrive in, which is " +
                    "the implicit ordering this type exists to remove.");
            }
        }

        _systems = ordered.ToArray();
    }

    /// <summary>The systems, in the order they run.</summary>
    public IReadOnlyList<IEcsSystem> Systems => _systems;

    /// <summary>Run every system once, in declared order.</summary>
    public void Run(WorldWriter writer, ulong currentTick)
    {
        for (int i = 0; i < _systems.Length; i++)
        {
            _systems[i].Run(writer, currentTick);
        }
    }
}
