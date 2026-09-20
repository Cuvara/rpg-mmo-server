namespace Shared.GameLogic.Components
{
    /// <summary>
    /// Player input for one simulation tick. Ported from Go InputMessage.
    ///
    /// <para>
    /// This is a <b>simulation</b> type, not a wire type: nothing serializes it.
    /// The wire carries <c>RpgMmo.Wire.V1.InputMessage</c> (Protobuf, ADR-9); the
    /// server decodes that and constructs an <see cref="InputData"/> directly.
    /// </para>
    ///
    /// <para>
    /// <b>MoveX/MoveY are a direction, not a displacement.</b> The server integrates
    /// <c>direction * speed * dt</c> once per tick; a vector with magnitude &gt; 1 is
    /// normalized (so diagonals are not faster than cardinals) and a grossly invalid
    /// vector is dropped. Sending more input packets does not move the player further.
    /// </para>
    /// </summary>
    public readonly struct InputData
    {
        public readonly ulong Tick;

        public readonly float MoveX;

        public readonly float MoveY;

        public readonly string? AttackTargetId;

        /// <summary>
        /// Content id of the ability the player is trying to use this tick, or 0 for none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Zero means "no ability".</b> Ability ids are allocated from 1 by the content
        /// validator for exactly this reason: proto3 elides a zero, so id 0 and "this
        /// client sent no ability" would be identical bytes and the server could not tell a
        /// missing field from a real ability.
        /// </para>
        /// <para>
        /// This is a REQUEST, not a result. The server validates it against the content
        /// set, the caster's state and its cooldown; the only thing the client learns about
        /// the outcome is what comes back in the snapshot.
        /// </para>
        /// <para>
        /// <b>Abilities are not predicted.</b> Prediction covers movement only: movement is
        /// a pure function of input the client already has, while an ability outcome depends
        /// on cooldowns, content and other entities' state the client can only guess at. A
        /// mispredicted ability is visible as a cast that plays and then un-happens, which
        /// is worse than a cast that starts one round trip late.
        /// </para>
        /// </remarks>
        public readonly uint AbilityId;

        /// <summary>
        /// Target entity for a targeted ability, null for self- and ground-targeted ones.
        /// Server-side entity id, in the same space as <see cref="AttackTargetId"/>.
        /// </summary>
        public readonly string? AbilityTargetId;

        /// <summary>
        /// Aim point in world coordinates for a ground-targeted ability. Only meaningful
        /// when <see cref="AbilityId"/> is non-zero.
        /// </summary>
        /// <remarks>
        /// A POINT in world space, unlike <see cref="MoveX"/>/<see cref="MoveY"/> which are
        /// a DIRECTION. Keeping them separate is not redundancy: an input that both moves
        /// and aims elsewhere is ordinary — a player strafing while dropping an area effect
        /// behind them. The world origin is a legitimate aim point, so (0,0) does not mean
        /// "not aimed"; <see cref="AbilityId"/> is what says whether this is meaningful.
        /// </remarks>
        public readonly Vec2 Aim;

        public InputData(ulong tick, float moveX, float moveY, string? attackTargetId)
            : this(tick, moveX, moveY, attackTargetId, 0, null, default)
        {
        }

        public InputData(
            ulong tick,
            float moveX,
            float moveY,
            string? attackTargetId,
            uint abilityId,
            string? abilityTargetId,
            Vec2 aim)
        {
            Tick = tick;
            MoveX = moveX;
            MoveY = moveY;
            AttackTargetId = attackTargetId;
            AbilityId = abilityId;
            AbilityTargetId = abilityTargetId;
            Aim = aim;
        }

        /// <summary>True when this input is asking for an ability this tick.</summary>
        public bool HasAbility => AbilityId != 0;
    }
}
