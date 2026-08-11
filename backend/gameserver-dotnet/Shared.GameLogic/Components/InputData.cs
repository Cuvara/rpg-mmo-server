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

        public InputData(ulong tick, float moveX, float moveY, string? attackTargetId)
        {
            Tick = tick;
            MoveX = moveX;
            MoveY = moveY;
            AttackTargetId = attackTargetId;
        }
    }
}
