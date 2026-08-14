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
    /// <para><b>Nothing calls this to schedule work yet</b> — the schedule runs serially.
    /// It is here, and tested, because it is the predicate a parallel step would need,
    /// and writing it now is what proves the declared sets are sufficient to express it.
    /// Two further preconditions are <i>not</i> captured by this predicate and are
    /// recorded in <see cref="SystemSchedule"/>.</para>
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
/// <para><b>Why it runs serially, and what a parallel version would still need.</b>
/// <see cref="ComponentAccess.IsDisjointFrom"/> answers the component half of "can these
/// two run together". Two things it does not answer, both verified in the current code and
/// both blocking:</para>
/// <list type="number">
/// <item><description><c>EcsWorld._structural</c> is a plain <c>List&lt;StructuralOp&gt;</c>
/// with no lock of its own. It is safe today only because exactly one thread mutates it
/// while holding the write lock. Concurrent systems would race on the queue itself, which
/// is why <see cref="ComponentAccess.Structural"/> excludes a system from concurrency
/// outright rather than trying to reason about which components it touches.</description></item>
/// <item><description><c>EcsWorld</c>'s iteration-depth guard is <c>[ThreadStatic]</c>, so
/// "is anything iterating right now" would become a per-worker fact rather than a property
/// of the world — and that flag is what decides whether a spawn or despawn applies
/// immediately or is deferred. Under workers, one worker could take the immediate path
/// while another is mid-iteration.</description></item>
/// </list>
/// <para>Both must be fixed before a system runs on another thread. Neither is fixed here,
/// because nothing runs on another thread here.</para>
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
