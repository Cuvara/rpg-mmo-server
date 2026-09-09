using System;

namespace GameServer.Net;

/// <summary>
/// Converts between an angle in radians and the wire's biased 16-bit binary-radian
/// facing value. Mirrors <c>shared/messages/facing.go</c> (Go) and
/// <c>Runtime/Protocol/FacingCodec.cs</c> (Unity client).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the encoding is biased.</b> The obvious wire type is <c>float facing</c> in
/// radians, and it is wrong here: proto3 elides a zero float and 0.0 radians is a
/// perfectly ordinary facing — due east. A sender meaning "east" and a sender predating
/// the field would put IDENTICAL BYTES on the wire, and no receiver rule can separate
/// them. <c>speed</c> has exactly that ambiguity and has to document its way around it,
/// because a zero speed is genuinely meaningful and float is the natural type. Facing
/// has no such excuse, so the ambiguity is designed out: every representable angle maps
/// to a NON-ZERO wire value, and zero is reserved permanently.
/// </para>
/// <para>
/// It is also 1-3 bytes of varint rather than a float's fixed 5, on the hottest message
/// in the protocol — the same class of saving as the entity-type enum and id interning.
/// </para>
/// <para>
/// <b>Why this is not in <c>Shared.GameLogic</c>.</b> Encoding a direction vector into
/// an angle needs <c>MathF.Atan2</c>, which ADR-10 forbids inside the shared library
/// because it is implementation-defined across NativeAOT x64 and IL2CPP ARM64. Facing
/// encoding is a wire concern in any case, and entity-id interning is kept out of that
/// library for the same kind of reason. The client mirrors the decode half; it never
/// needs the encode half, so no <c>Atan2</c> ever has to agree across runtimes.
/// </para>
/// </remarks>
public static class FacingCodec
{
    /// <summary>
    /// Representable directions in a full turn. One step is 360/65536 = 0.0055 degrees,
    /// far below anything a player can perceive.
    /// </summary>
    public const int BradSteps = 65536;

    /// <summary>
    /// The wire value meaning "no facing to report". Reserved permanently; a real
    /// facing is always >= 1.
    /// </summary>
    public const uint NotSent = 0;

    private const float TwoPi = 6.28318530717958647692f;

    /// <summary>
    /// Encode an angle (radians, counter-clockwise from +X) as a biased wire value in
    /// [1, <see cref="BradSteps"/>].
    /// </summary>
    /// <remarks>
    /// Any angle is accepted — it is wrapped into one turn first, so callers need not
    /// normalise. A non-finite angle encodes as <see cref="NotSent"/>: "NaN" is not a
    /// direction, and silently shipping one would put an unrenderable value on the wire
    /// rather than an honestly absent one.
    /// </remarks>
    public static uint FromRadians(float radians)
    {
        if (float.IsNaN(radians) || float.IsInfinity(radians)) return NotSent;

        // Wrap into [0, 2*PI). The % operator keeps the sign of the dividend, so a
        // negative angle needs one addition afterwards.
        double wrapped = radians % TwoPi;
        if (wrapped < 0) wrapped += TwoPi;

        long step = (long)Math.Round(wrapped / TwoPi * BradSteps);

        // Rounding can land exactly on BradSteps (an angle just under a full turn rounds
        // up to the turn itself). That is the same direction as 0, so fold it down rather
        // than emitting an out-of-range value.
        if (step >= BradSteps) step = 0;

        // The +1 bias: step 0 (due east) becomes wire value 1, leaving 0 free for
        // "not sent".
        return (uint)step + 1;
    }

    /// <summary>
    /// Encode a direction vector as a biased wire value. A zero-length vector has no
    /// direction and encodes as <see cref="NotSent"/>.
    /// </summary>
    /// <remarks>
    /// Server-side only — this is the half that needs <c>MathF.Atan2</c>. The client
    /// never encodes a facing.
    /// </remarks>
    public static uint FromDirection(float x, float y)
    {
        if (x == 0f && y == 0f) return NotSent;
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
            return NotSent;

        return FromRadians(MathF.Atan2(y, x));
    }

    /// <summary>
    /// Decode a wire facing value. Returns false when the sender supplied none.
    /// </summary>
    /// <remarks>
    /// A caller MUST honour a false return rather than using the zeroed
    /// <paramref name="radians"/>: it should keep the entity's last known facing, or
    /// derive one from movement. Snapping to east instead means every entity from an old
    /// server points the same way, which reads as a content bug and gets debugged as one.
    /// An out-of-range value is refused for the same reason — a wrong facing is much
    /// harder to notice than an absent one.
    /// </remarks>
    public static bool TryToRadians(uint brad, out float radians)
    {
        if (brad == NotSent || brad > BradSteps)
        {
            radians = 0f;
            return false;
        }

        radians = (float)((brad - 1) / (double)BradSteps * TwoPi);
        return true;
    }
}
