using System;

namespace GameServer.Server;

/// <summary>
/// Marks a mutable field on a simulation phase or system as <b>scratch</b>: a reusable
/// buffer, not simulation state.
///
/// <para>Simulation state belongs in the world, on components, where it can be inspected,
/// snapshotted, persisted and reset with everything else. A counter or an accumulator kept
/// in a field is invisible to all of that, and
/// <c>SimulationStateArchitectureTests</c> fails the build for one. But a growable scratch
/// buffer is a different thing, and banning it outright would just push allocation into
/// the tick.</para>
///
/// <para><b>The criterion, and it is strict.</b> A field qualifies only if discarding it
/// between ticks would change nothing but allocation — it must be fully rewritten before
/// it is read, and the simulation must produce identical results if it were reset to its
/// default at any tick boundary. If resetting it would change behaviour, it is state and
/// it belongs in the world.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class SimulationScratchAttribute : Attribute
{
}
