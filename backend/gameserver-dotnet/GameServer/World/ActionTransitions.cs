using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using GameServer.World.Components;

namespace GameServer.World;

/// <summary>
/// The one place <see cref="Locomotion.Action"/> is written on the server: the shared
/// action/counter rule, plus the one thing that rule cannot know about — the server's own
/// two-rate schedule.
///
/// <para><b>The counter rule is NOT reimplemented here.</b> It is
/// <see cref="ActionStateLogic.Advance"/>, in <c>Shared.GameLogic</c>, which is compiled
/// into the Unity client as a UPM package. A second copy on the server would be two
/// implementations of one wire contract, drifting silently: the client decides whether to
/// retrigger an animation by comparing counters the server produced, so a server that
/// advanced its counter under different rules would not fail anything — it would just
/// play the wrong animations. That is the class of defect ADR-10's shared-logic boundary
/// exists to remove, and this file stays on the right side of it.</para>
///
/// <para><b>What IS server-only, and why it lives here.</b> The latch. Actions are written
/// on the CRITICAL group and sampled by the snapshot gather on the WORLD group, so at the
/// 60/15 default only one write in four is ever observable. That is a fact about the
/// server's scheduler, not about the simulation, and a client has no schedule to apply it
/// to — putting it in <c>Shared.GameLogic</c> would export a server implementation detail
/// to every client and force an <c>sgl</c> release to change a server tick rate.</para>
/// </summary>
public static class ActionTransitions
{
    /// <summary>Death is terminal: it overrides a latched one-shot and latches nothing.</summary>
    /// <remarks>
    /// Not the same question as <see cref="ActionStateLogic.IsRetriggerable"/>. That asks
    /// whether re-entering an action is a new occurrence; this asks whether an action may
    /// interrupt one that is still being held for the wire. A corpse reported mid-swing
    /// would keep playing the attack, which is worse than losing a swing.
    /// </remarks>
    public static bool IsTerminal(EntityAction action) => action == EntityAction.Dead;

    /// <summary>
    /// Enter <paramref name="action"/> on <paramref name="locomotion"/>.
    /// </summary>
    /// <param name="locomotion">Component to mutate.</param>
    /// <param name="action">Action being entered.</param>
    /// <param name="baseTick">Current base (critical-group) tick.</param>
    /// <param name="holdTicks">
    /// How many base ticks a retriggerable action stays latched against being overwritten
    /// by a continuous one. The host passes the world group's <c>WorldEvery</c>, so a
    /// one-shot always survives long enough for at least one snapshot to sample it.
    /// Values &lt;= 1 disable the latch and restore the pre-latch behaviour exactly, which
    /// is what the fixtures that construct an <c>InputHandler</c> directly get by default.
    /// </param>
    /// <returns>True if the retrigger counter moved.</returns>
    public static bool Enter(
        ref Locomotion locomotion, EntityAction action, ulong baseTick, int holdTicks)
    {
        // A continuous action must not clobber a one-shot no snapshot has had a chance to
        // sample yet. Terminal actions are exempt; see IsTerminal.
        if (!IsTerminal(action)
            && !ActionStateLogic.IsRetriggerable(action)
            && baseTick < locomotion.ActionHoldUntilTick)
        {
            return false;
        }

        bool advanced = ActionStateLogic.Advance(
            ref locomotion.Action, ref locomotion.ActionSeq, action);

        locomotion.ActionHoldUntilTick =
            ActionStateLogic.IsRetriggerable(action) && holdTicks > 1
                ? baseTick + (ulong)holdTicks
                : 0UL;

        return advanced;
    }
}
