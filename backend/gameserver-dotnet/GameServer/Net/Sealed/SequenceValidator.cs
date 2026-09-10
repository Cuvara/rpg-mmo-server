namespace GameServer.Net.Sealed;

/// <summary>
/// Decides whether a frame's sequence number is fresh. Mirrors
/// <c>backend/shared/sealed</c>.
/// </summary>
/// <remarks>
/// <para>
/// An interface because the answer depends on a transport property still being measured:
/// whether the ARQ can deliver two frames out of order at this layer. If it cannot, a
/// strict counter is enough and is the cheapest and most obviously correct thing. If it
/// can, a strict counter drops legitimate frames and a window is required. Both live here
/// so the finding lands as a one-line change at the call site rather than a rewrite — and
/// so the choice comes from a measurement rather than from whichever was written first.
/// </para>
/// <para>
/// <b>Call this only after the tag verifies.</b> The sequence is cleartext, so accepting
/// it before the AEAD succeeds lets an attacker advance a peer's window with forged frames
/// and lock out the real sender — denial of service that costs nothing to mount.
/// </para>
/// </remarks>
public interface ISequenceValidator
{
    /// <summary>
    /// Record <paramref name="sequence"/> as seen and report whether it was fresh. False
    /// means drop the frame and end the session: there is no benign replay on this
    /// protocol.
    /// </summary>
    bool Accept(ulong sequence);

    /// <summary>Largest sequence accepted so far. Diagnostics only.</summary>
    ulong Highest { get; }
}

/// <summary>
/// Accepts strictly increasing sequences and nothing else. Correct when the transport
/// below cannot reorder: TCP by definition, and KCP in the reliable ordered mode this
/// project configures.
/// </summary>
public sealed class StrictMonotonicSequence : ISequenceValidator
{
    private ulong _highest;
    private bool _seen;

    /// <inheritdoc />
    public ulong Highest => _highest;

    /// <inheritdoc />
    public bool Accept(ulong sequence)
    {
        if (_seen && sequence <= _highest) return false;
        _highest = sequence;
        _seen = true;
        return true;
    }
}

/// <summary>
/// Accepts any sequence not already seen and not older than the window behind the highest
/// accepted — the rule IPsec and DTLS use.
/// </summary>
/// <remarks>
/// Required if the transport can reorder; harmless if it cannot, at the cost of one word
/// of state per direction. That makes it the safer default if the measurement is
/// ambiguous.
/// </remarks>
public sealed class SlidingWindowSequence : ISequenceValidator
{
    /// <summary>
    /// Default width in frames. 64 is one machine word, so the bitmap is a single
    /// <see cref="ulong"/> and membership is two instructions. It is also far wider than
    /// any reordering a reliable ARQ produces — if 64 is ever not enough, the transport is
    /// not delivering what this layer assumes, and that is the thing to fix.
    /// </summary>
    public const int DefaultWidth = 64;

    private readonly int _width;
    private ulong _highest;
    private ulong _bitmap; // bit i set => (highest - i) accepted
    private bool _seen;

    /// <summary>A window of the default width.</summary>
    public SlidingWindowSequence() : this(DefaultWidth) { }

    /// <summary>A window of an explicit width, capped at 64 because the bitmap is one word.</summary>
    public SlidingWindowSequence(int width)
        => _width = width is <= 0 or > DefaultWidth ? DefaultWidth : width;

    /// <inheritdoc />
    public ulong Highest => _highest;

    /// <inheritdoc />
    public bool Accept(ulong sequence)
    {
        if (!_seen)
        {
            _seen = true;
            _highest = sequence;
            _bitmap = 1;
            return true;
        }

        if (sequence > _highest)
        {
            // Advance. Frames between the old and new high water are still acceptable if
            // they arrive later, so the bitmap shifts rather than clearing — that is the
            // whole difference from a strict counter.
            ulong shift = sequence - _highest;
            _bitmap = shift >= (ulong)_width ? 0 : _bitmap << (int)shift;
            _bitmap |= 1;
            _highest = sequence;
            return true;
        }

        if (sequence == _highest) return false;

        ulong behind = _highest - sequence;
        // Too old to judge. Refused rather than accepted: a validator that cannot prove a
        // frame is fresh must not claim that it is.
        if (behind >= (ulong)_width) return false;

        ulong mask = 1UL << (int)behind;
        if ((_bitmap & mask) != 0) return false;

        _bitmap |= mask;
        return true;
    }
}
