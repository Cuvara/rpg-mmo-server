using System;
using GameServer.Server;

namespace GameServer.Scaffolding;

/// <summary>
/// Runs several <see cref="ISimulationPhase"/>s in a fixed order as one phase.
///
/// <para><b>Why this is needed.</b> <c>ServerOptions.SimulationPhaseFactory</c> produces
/// exactly one phase, and the enemy AI and the bot players are two. Making the core accept
/// a list would widen a contract the core deliberately keeps at one member; composing on
/// the content side keeps "what the game is" where <see cref="ISimulationPhase"/> says it
/// belongs — in the composition root.</para>
///
/// <para><b>The cost, stated rather than hidden.</b> Each phase takes its own write scope,
/// so N phases take the world write lock N times per tick. <see cref="ISimulationPhase"/>
/// names this consequence already and names the fix if it ever matters (hand phases a
/// writer instead of widening the interface). It has not been measured to matter at two
/// phases, and it is not being pre-optimised on the strength of a guess.</para>
///
/// <para><b>Order is the argument order, and it is load-bearing.</b> Enemies run before
/// bots: the bot brain picks a target from the enemies that exist <i>now</i>, so running
/// it first would aim every bot at the previous tick's world and, on the first tick, at an
/// empty one.</para>
/// </summary>
public sealed class CompositeSimulationPhase : ISimulationPhase
{
    private readonly ISimulationPhase[] _phases;

    public CompositeSimulationPhase(params ISimulationPhase[] phases)
    {
        ArgumentNullException.ThrowIfNull(phases);

        foreach (ISimulationPhase phase in phases)
        {
            ArgumentNullException.ThrowIfNull(phase);
        }

        _phases = phases;
    }

    /// <summary>The phases this runs, in the order they run. Diagnostics and tests.</summary>
    public IReadOnlyList<ISimulationPhase> Phases => _phases;

    public void Tick(ulong currentTick)
    {
        for (int i = 0; i < _phases.Length; i++)
        {
            _phases[i].Tick(currentTick);
        }
    }
}
