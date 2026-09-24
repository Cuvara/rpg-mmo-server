using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// The single rule for advancing an entity's <see cref="EntityAction"/> and its
    /// retrigger counter together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why one function rather than two assignments at each call site.</b> The counter is
    /// only useful if every writer agrees on when it moves, and there are five places in the
    /// server that set an action. Five hand-written increments is five chances to increment
    /// on a continuous state — which retriggers the walk animation on every tick of
    /// movement — or to forget one on an instantaneous state, which is the exact bug the
    /// counter exists to fix, reintroduced silently.
    /// </para>
    /// <para>
    /// <b>The rule is not "increment on change".</b> Two attacks in a row are two separate
    /// occurrences and must retrigger, so the counter has to move even though the action did
    /// not. Two ticks of walking are one continuous state and must not retrigger, so it must
    /// not move even though the writer ran again. What separates them is whether the action
    /// is instantaneous or continuous, which is a property of the action — see
    /// <see cref="IsRetriggerable"/>.
    /// </para>
    /// </remarks>
    public static class ActionStateLogic
    {
        /// <summary>
        /// Whether re-entering this action while already in it is a NEW occurrence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="EntityAction.Attacking"/> is instantaneous: each swing is its own
        /// event and a renderer must play the animation again. Idle, Moving and Dead are
        /// continuous: an entity that is still walking has not started walking again, and an
        /// entity that is still dead has certainly not died again.
        /// </para>
        /// <para>
        /// <see cref="EntityAction.Unspecified"/> is false because it is not an action at
        /// all — it is the absence of one.
        /// </para>
        /// </remarks>
        public static bool IsRetriggerable(EntityAction action) => action == EntityAction.Attacking;

        /// <summary>
        /// Sets <paramref name="action"/> to <paramref name="next"/> and advances
        /// <paramref name="seq"/> if that constitutes entering an action. Returns true when
        /// the counter moved.
        /// </summary>
        /// <remarks>
        /// The counter is allocated from 1 and SKIPS ZERO on wrap: zero is reserved on the
        /// wire for "this sender does not send a retrigger counter", so a counter that
        /// wrapped to it would tell every client to stop retriggering that entity's
        /// animations until it wrapped again — four billion actions later. The skip costs
        /// one comparison on a path that runs once per action change.
        /// </remarks>
        public static bool Advance(ref EntityAction action, ref uint seq, EntityAction next)
        {
            bool entering = action != next || IsRetriggerable(next);

            action = next;

            if (!entering)
                return false;

            seq = seq == uint.MaxValue ? 1u : seq + 1u;
            return true;
        }
    }
}
