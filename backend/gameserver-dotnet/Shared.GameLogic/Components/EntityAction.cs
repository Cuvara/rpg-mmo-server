namespace Shared.GameLogic.Components
{
    /// <summary>
    /// A coarse, level-triggered description of what an entity is doing, for a
    /// renderer to pick an animation from. Mirrors the <c>EntityAction</c> enum in
    /// <c>shared/proto/wire.proto</c>; the numeric values are on the wire and are
    /// FROZEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero is reserved for "not sent" and <see cref="Idle"/> is 1, deliberately.</b>
    /// proto3 elides a zero enum, so making idle the zero value would put "this entity
    /// is standing still" and "this sender does not know about actions" on the wire as
    /// identical bytes. That is the ambiguity documented at length on
    /// <see cref="EntitySnapshotData.Speed"/>, and here it is avoidable for free.
    /// <c>EntityType</c> already reserves zero the same way, so this is the established
    /// idiom rather than a new rule.
    /// </para>
    /// <para>
    /// <b>Level-triggered, not edge-triggered.</b> It says what state the entity is in,
    /// not that a state was entered. A renderer that needs to retrigger the same action
    /// twice in a row (attack, attack) cannot get that edge from this value alone —
    /// that needs a sequence number, which is an animation-system concern and is
    /// deliberately out of scope. See <c>shared/docs/DESIGN.md</c>, "Entity facing and
    /// action state on the wire".
    /// </para>
    /// <para>
    /// This enum is pure data with no arithmetic, so it sits inside the ADR-10 shared
    /// boundary without difficulty. The <i>encoding</i> of facing does not — see the
    /// wire-layer facing codec, which is deliberately not in this library.
    /// </para>
    /// </remarks>
    public enum EntityAction
    {
        /// <summary>Not sent / unknown. NEVER "idle" — a consumer must keep whatever it was showing.</summary>
        Unspecified = 0,

        Idle = 1,

        Moving = 2,

        Attacking = 3,

        Dead = 4,
    }
}
