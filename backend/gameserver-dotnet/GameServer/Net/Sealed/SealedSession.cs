namespace GameServer.Net.Sealed;

/// <summary>
/// The authenticated cipher the sealed format is built around. No implementation exists
/// yet: ADR-22 has not settled which library provides ChaCha20-Poly1305 on all three
/// runtimes, and a placeholder that "worked" would let every test above it pass while
/// proving nothing about the bytes.
/// </summary>
public interface ISealedAead
{
    /// <summary>Nonce length; must be <see cref="SealedFrame.NonceSize"/>.</summary>
    int NonceSize { get; }

    /// <summary>Tag length; must be <see cref="SealedFrame.TagSize"/>.</summary>
    int TagSize { get; }

    /// <summary>Encrypt, appending ciphertext and tag. Returns bytes written.</summary>
    int Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, Span<byte> destination);

    /// <summary>
    /// Decrypt and verify. Must fail identically — in message and in timing — however it
    /// failed: a peer must not learn <i>why</i>.
    /// </summary>
    bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> aad, Span<byte> destination, out int written);
}

/// <summary>Why a sealed frame was not accepted.</summary>
public enum SealedOpenResult
{
    /// <summary>Accepted.</summary>
    Ok = 0,

    /// <summary>
    /// Not a sealed frame. Distinguished from failure because the caller treats it
    /// differently: during a handshake it is expected; afterwards it is a peer sending
    /// cleartext where a sealed frame is required, which must end the session.
    /// </summary>
    NotSealed,

    /// <summary>
    /// Rejected. Deliberately one value for "tag did not verify" and "replayed": the
    /// caller's response is identical — end the session — and distinguishing them tells an
    /// attacker whether a forged tag reached the replay window.
    /// </summary>
    Rejected,
}

/// <summary>
/// Seals and opens frames for one direction, and is the reason the "check the sequence
/// only after the tag verifies" rule cannot be got wrong. Mirrors
/// <c>shared/sealed.Session</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists rather than a documented convention.</b> The rule is easy to
/// state and easy to lose. The sequence number is cleartext and sits right there in the
/// header, so the natural-looking implementation reads it, checks the replay window, and
/// only then spends CPU on the AEAD — which is exactly backwards. That ordering lets an
/// attacker replay a captured header with garbage after it and advance the peer's window
/// without holding any key, locking out the real sender. It costs nothing to mount and
/// would present as a connectivity bug, in the wrong layer, for as long as it took someone
/// to suspect it.
/// </para>
/// <para>
/// A comment does not survive an optimisation pass. <see cref="Open"/> performs both steps
/// itself, in the only correct order, and exposes no way to do one without the other:
/// there is no call site left that can reorder them.
/// </para>
/// <para>
/// The AEAD must be keyed for ONE direction. One key serving both makes the counter nonce
/// repeat across them, which is the catastrophic case documented on
/// <see cref="SealedFrame.WriteNonce"/>.
/// </para>
/// </remarks>
public sealed class SealedSession
{
    private readonly ISealedAead _aead;
    private readonly ISequenceValidator _validator;
    private ulong _sendSequence;

    /// <summary>Build a session around a one-direction AEAD and a replay validator.</summary>
    public SealedSession(ISealedAead aead, ISequenceValidator validator)
    {
        ArgumentNullException.ThrowIfNull(aead);
        ArgumentNullException.ThrowIfNull(validator);

        if (aead.NonceSize != SealedFrame.NonceSize)
            throw new ArgumentException(
                $"AEAD nonce size {aead.NonceSize}, want {SealedFrame.NonceSize}", nameof(aead));
        if (aead.TagSize != SealedFrame.TagSize)
            throw new ArgumentException(
                $"AEAD tag size {aead.TagSize}, want {SealedFrame.TagSize}", nameof(aead));

        _aead = aead;
        _validator = validator;
    }

    /// <summary>Sequence the next <see cref="Seal"/> will use. Diagnostics.</summary>
    public ulong NextSendSequence => _sendSequence;

    /// <summary>Highest sequence accepted. Diagnostics.</summary>
    public ulong HighestReceived => _validator.Highest;

    /// <summary>Wrap one Envelope's bytes as a sealed frame body.</summary>
    public byte[] Seal(ReadOnlySpan<byte> envelope)
    {
        ulong sequence = _sendSequence;
        // Incremented before use, deliberately: an early return must not leave the counter
        // reusable. A nonce reused after a failed send is the same catastrophe as one
        // reused on purpose.
        _sendSequence++;

        var frame = new byte[SealedFrame.HeaderSize + envelope.Length + SealedFrame.TagSize];
        SealedFrame.WriteHeader(frame, sequence);

        Span<byte> nonce = stackalloc byte[SealedFrame.NonceSize];
        SealedFrame.WriteNonce(nonce, sequence);

        int written = _aead.Seal(
            nonce, envelope, frame.AsSpan(0, SealedFrame.HeaderSize),
            frame.AsSpan(SealedFrame.HeaderSize));

        return written == envelope.Length + SealedFrame.TagSize
            ? frame
            : throw new InvalidOperationException("AEAD wrote an unexpected number of bytes");
    }

    /// <summary>
    /// Verify and unwrap a sealed frame body, then check it for replay.
    /// </summary>
    /// <remarks>
    /// The order is the whole point of this type and must not be rearranged: the AEAD runs
    /// FIRST, and the sequence reaches the validator only once the tag has proved the
    /// header was not forged.
    /// </remarks>
    public SealedOpenResult Open(ReadOnlySpan<byte> body, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();

        if (!SealedFrame.TryReadHeader(body, out ulong sequence, out SealedFrameError headerError))
        {
            return headerError == SealedFrameError.NotSealed
                ? SealedOpenResult.NotSealed
                : SealedOpenResult.Rejected;
        }

        ReadOnlySpan<byte> aad = body[..SealedFrame.HeaderSize];
        ReadOnlySpan<byte> ciphertext = body[SealedFrame.HeaderSize..];

        Span<byte> nonce = stackalloc byte[SealedFrame.NonceSize];
        SealedFrame.WriteNonce(nonce, sequence);

        var buffer = new byte[ciphertext.Length - SealedFrame.TagSize];

        // STEP 1: authenticate. Nothing below may act on anything the header claimed until
        // this succeeds.
        if (!_aead.TryOpen(nonce, ciphertext, aad, buffer, out int written))
            return SealedOpenResult.Rejected;

        // STEP 2: only now is the sequence a fact rather than a claim.
        if (!_validator.Accept(sequence))
            return SealedOpenResult.Rejected;

        plaintext = written == buffer.Length ? buffer : buffer[..written];
        return SealedOpenResult.Ok;
    }
}
