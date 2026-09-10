using GameServer.Net.Sealed;

namespace GameServer.Tests.Net;

/// <summary>
/// Tests for the sealed wire format, the replay rule and the refusal policy. The
/// cross-implementation vector is the important one: the Go, C# and Unity halves must
/// agree byte for byte, and a disagreement is silent — the handshake simply never
/// completes and nothing says why.
/// </summary>
public class SealedTests
{
    // --- frame ---

    /// <summary>
    /// The marker must not collide with what the codec sniffs to pick an encoding. If it
    /// does, a sealed frame is parsed as a cleartext Envelope and the failure surfaces as
    /// a malformed message rather than as an encryption problem.
    /// </summary>
    [Fact]
    public void Marker_CannotBeConfusedWithAnEncoding()
    {
        const byte protobufFirstByte = 0x08; // Envelope field 1; type 0 is refused
        const byte jsonFirstByte = 0x7B;     // '{'

        Assert.NotEqual(protobufFirstByte, SealedFrame.Marker);
        Assert.NotEqual(jsonFirstByte, SealedFrame.Marker);

        Assert.False(SealedFrame.TryReadHeader(new byte[] { protobufFirstByte, 0, 0 }, out _, out var e1));
        Assert.Equal(SealedFrameError.NotSealed, e1);
        Assert.False(SealedFrame.TryReadHeader(new byte[] { jsonFirstByte, 0, 0 }, out _, out var e2));
        Assert.Equal(SealedFrameError.NotSealed, e2);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(255UL)]
    [InlineData(4294967296UL)]
    [InlineData(ulong.MaxValue)]
    public void Header_RoundTrips(ulong sequence)
    {
        var body = new byte[SealedFrame.HeaderSize + SealedFrame.TagSize];
        SealedFrame.WriteHeader(body, sequence);

        Assert.True(SealedFrame.TryReadHeader(body, out ulong got, out SealedFrameError error));
        Assert.Equal(SealedFrameError.None, error);
        Assert.Equal(sequence, got);
    }

    [Fact]
    public void Header_RefusesShortAndFutureFrames()
    {
        var tooShort = new byte[SealedFrame.HeaderSize];
        SealedFrame.WriteHeader(tooShort, 1);
        Assert.False(SealedFrame.TryReadHeader(tooShort, out _, out SealedFrameError shortErr));
        Assert.Equal(SealedFrameError.ShortFrame, shortErr);

        var future = new byte[SealedFrame.HeaderSize + SealedFrame.TagSize];
        SealedFrame.WriteHeader(future, 1);
        future[1] = SealedFrame.Version + 1;
        Assert.False(SealedFrame.TryReadHeader(future, out _, out SealedFrameError versionErr));
        Assert.Equal(SealedFrameError.UnsupportedVersion, versionErr);
    }

    /// <summary>
    /// Distinct sequences must give distinct nonces. Reusing a (key, nonce) pair with
    /// either candidate AEAD leaks the XOR of two plaintexts and can expose the Poly1305
    /// key.
    /// </summary>
    [Fact]
    public void Nonce_IsUniquePerSequence()
    {
        var seen = new HashSet<string>();
        foreach (ulong sequence in new ulong[] { 0, 1, 2, 256, 65536, 1UL << 40, ulong.MaxValue })
        {
            var nonce = new byte[SealedFrame.NonceSize];
            SealedFrame.WriteNonce(nonce, sequence);
            Assert.True(seen.Add(Convert.ToHexString(nonce)), $"sequence {sequence} reused a nonce");
            // First four bytes reserved for a future direction or rekey epoch.
            Assert.Equal(new byte[4], nonce[..4]);
        }
    }

    // --- replay ---

    [Fact]
    public void StrictMonotonic_RejectsAnythingNotIncreasing()
    {
        var v = new StrictMonotonicSequence();
        foreach (ulong s in new ulong[] { 1, 2, 3, 10 }) Assert.True(v.Accept(s));
        foreach (ulong s in new ulong[] { 10, 9, 1, 0 }) Assert.False(v.Accept(s));
        Assert.Equal(10UL, v.Highest);
    }

    /// <summary>
    /// Sequence 0 must be usable: counters start there, and a validator that silently
    /// rejected the first frame would look like a key mismatch.
    /// </summary>
    [Fact]
    public void Validators_AcceptSequenceZeroFirst()
    {
        foreach (ISequenceValidator v in new ISequenceValidator[]
                 { new StrictMonotonicSequence(), new SlidingWindowSequence() })
        {
            Assert.True(v.Accept(0));
            Assert.False(v.Accept(0));
        }
    }

    [Fact]
    public void SlidingWindow_AcceptsReorderingButNotReplay()
    {
        var v = new SlidingWindowSequence();
        foreach (ulong s in new ulong[] { 5, 3, 4, 8, 6 }) Assert.True(v.Accept(s));
        foreach (ulong s in new ulong[] { 3, 4, 5, 6, 8 }) Assert.False(v.Accept(s));
        // A gap left behind is still acceptable, which is the whole point.
        Assert.True(v.Accept(7));
    }

    /// <summary>
    /// A frame older than the window cannot be proved fresh, so it must be refused. A
    /// validator that accepted what it cannot judge is not a replay defence.
    /// </summary>
    [Fact]
    public void SlidingWindow_RefusesWhatItCannotJudge()
    {
        var v = new SlidingWindowSequence(8);
        Assert.True(v.Accept(100));
        Assert.False(v.Accept(92));  // exactly the width behind
        Assert.False(v.Accept(1));   // ancient
        Assert.True(v.Accept(99));   // inside the window
    }

    [Fact]
    public void SlidingWindow_HandlesLargeJumps()
    {
        var v = new SlidingWindowSequence();
        Assert.True(v.Accept(1));
        Assert.True(v.Accept(1_000_000));
        Assert.True(v.Accept(999_999));
        Assert.False(v.Accept(1_000_000));
    }

    // --- transcript ---

    /// <summary>
    /// <b>The cross-implementation vector.</b> Produced by
    /// <c>shared/sealed</c>'s <c>TestTranscriptGoldenVector</c> in Go. Two implementations
    /// that each round-trip against themselves can still disagree, and the failure is
    /// silent: the handshake never completes and nothing names the cause.
    /// </summary>
    [Fact]
    public void Transcript_MatchesTheGoImplementation()
    {
        var clientPublic = new byte[SealedHandshake.PublicKeySize];
        var serverPublic = new byte[SealedHandshake.PublicKeySize];
        for (int i = 0; i < SealedHandshake.PublicKeySize; i++)
        {
            clientPublic[i] = (byte)i;
            serverPublic[i] = (byte)(0x80 + i);
        }

        byte[] transcript = SealedHandshake.Transcript("golden-jti", clientPublic, serverPublic);

        Assert.Equal(
            "6375766172612F7365616C65642D68616E647368616B652F763100676F6C64656E2D6A74690000" +
            "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
            "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F",
            Convert.ToHexString(transcript));
    }

    /// <summary>
    /// The NUL separators are what stop two different (jti, key) pairs producing the same
    /// transcript, and the key order is what stops a reflected binding verifying.
    /// </summary>
    [Fact]
    public void Transcript_IsUnambiguous()
    {
        var a = new byte[SealedHandshake.PublicKeySize];
        var b = new byte[SealedHandshake.PublicKeySize];
        a[0] = 1; b[0] = 2;

        string ab = Convert.ToHexString(SealedHandshake.Transcript("ab", a, b));
        string justA = Convert.ToHexString(SealedHandshake.Transcript("a", a, b));
        string swapped = Convert.ToHexString(SealedHandshake.Transcript("ab", b, a));

        Assert.NotEqual(ab, justA);
        Assert.NotEqual(ab, swapped);
    }

    [Fact]
    public void Transcript_RequiresAJtiAndFullLengthKeys()
    {
        var ok = new byte[SealedHandshake.PublicKeySize];
        Assert.Throws<ArgumentException>(() => SealedHandshake.Transcript("", ok, ok));
        Assert.Throws<ArgumentException>(() => SealedHandshake.Transcript("jti", new byte[31], ok));
        Assert.Throws<ArgumentException>(() => SealedHandshake.Transcript("jti", ok, new byte[33]));
    }

    // --- refusal ---

    [Fact]
    public void Refusal_HasNoMiddleGround()
    {
        var sealedPeer = new SealedPeerCapabilities(SealedHandshakeCompleted: true, EncodingCanSeal: true);
        var cleartextPeer = new SealedPeerCapabilities(SealedHandshakeCompleted: false, EncodingCanSeal: true);
        var jsonPeer = new SealedPeerCapabilities(SealedHandshakeCompleted: false, EncodingCanSeal: false);

        Assert.False(SealedPolicy.RefusalFor(SealedRequirement.Disabled, cleartextPeer).Refused);
        Assert.False(SealedPolicy.RefusalFor(SealedRequirement.Required, sealedPeer).Refused);

        SealedRefusal cleartext = SealedPolicy.RefusalFor(SealedRequirement.Required, cleartextPeer);
        Assert.True(cleartext.Refused);
        Assert.Equal(SealedRefusalReason.NoSealedSession, cleartext.Reason);

        // The consequence ADR-22 makes unavoidable: a JSON client cannot seal, so once
        // encryption is required it is refused rather than served in the clear.
        SealedRefusal json = SealedPolicy.RefusalFor(SealedRequirement.Required, jsonPeer);
        Assert.True(json.Refused);
        Assert.Equal(SealedRefusalReason.EncodingCannotSeal, json.Reason);
    }
}

/// <summary>
/// Tests for <see cref="SealedSession"/>, whose reason for existing is that the ordering
/// rule — check the sequence only after the tag verifies — is enforced by structure
/// rather than by a comment that does not survive an optimisation pass.
/// </summary>
public class SealedSessionTests
{
    /// <summary>
    /// A TEST DOUBLE, not a cipher and not a stub of one. It performs no encryption
    /// whatsoever: Seal copies the plaintext and appends 16 zero bytes, TryOpen strips
    /// them. It exists solely so the ordering and plumbing can be tested without a real
    /// primitive, and it cannot be mistaken for one — the "ciphertext" is the plaintext,
    /// in the clear, and the "tag" is constant.
    /// </summary>
    private sealed class PassthroughAead : ISealedAead
    {
        public int OpenCalls { get; private set; }
        public bool FailOpen { get; set; }

        public int NonceSize => SealedFrame.NonceSize;
        public int TagSize => SealedFrame.TagSize;

        public int Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, Span<byte> destination)
        {
            plaintext.CopyTo(destination);
            destination.Slice(plaintext.Length, SealedFrame.TagSize).Clear();
            return plaintext.Length + SealedFrame.TagSize;
        }

        public bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> aad, Span<byte> destination, out int written)
        {
            OpenCalls++;
            written = 0;
            if (FailOpen || ciphertext.Length < SealedFrame.TagSize) return false;

            int length = ciphertext.Length - SealedFrame.TagSize;
            ciphertext[..length].CopyTo(destination);
            written = length;
            return true;
        }
    }

    /// <summary>Records whether it was consulted — how the ordering is tested, not asserted.</summary>
    private sealed class CountingValidator : ISequenceValidator
    {
        private readonly ISequenceValidator _inner = new StrictMonotonicSequence();
        public int Calls { get; private set; }
        public ulong Highest => _inner.Highest;

        public bool Accept(ulong sequence)
        {
            Calls++;
            return _inner.Accept(sequence);
        }
    }

    [Fact]
    public void Session_RoundTrips()
    {
        var send = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());
        var recv = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());

        for (int i = 0; i < 4; i++)
        {
            byte[] payload = { 0x08, (byte)i, (byte)'h', (byte)'i' };
            byte[] frame = send.Seal(payload);

            Assert.Equal(SealedFrame.Marker, frame[0]);
            Assert.Equal(SealedOpenResult.Ok, recv.Open(frame, out byte[] got));
            Assert.Equal(payload, got);
        }
    }

    /// <summary>
    /// <b>The ordering rule, tested structurally.</b> A frame whose tag does not verify
    /// must never reach the replay validator. If it did, an attacker could replay a
    /// captured header with garbage after it and advance the peer's window without holding
    /// any key — locking out the real sender at no cost, and presenting as a connectivity
    /// bug in the wrong layer.
    /// </summary>
    [Fact]
    public void ForgedFrame_NeverReachesTheReplayWindow()
    {
        var send = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());
        var aead = new PassthroughAead();
        var validator = new CountingValidator();
        var recv = new SealedSession(aead, validator);

        byte[] frame = send.Seal("payload"u8);

        aead.FailOpen = true;
        Assert.Equal(SealedOpenResult.Rejected, recv.Open(frame, out _));

        Assert.Equal(1, aead.OpenCalls);
        Assert.True(validator.Calls == 0,
            $"the replay validator was consulted {validator.Calls} times for a frame whose " +
            "tag did not verify; the sequence must not be acted on until the AEAD succeeds");
        Assert.Equal(0UL, recv.HighestReceived);

        // The genuine frame must still be accepted: the forgery must not have consumed
        // its sequence.
        aead.FailOpen = false;
        Assert.Equal(SealedOpenResult.Ok, recv.Open(frame, out _));
    }

    [Fact]
    public void AuthenticatedReplay_IsRefused()
    {
        var send = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());
        var recv = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());

        byte[] frame = send.Seal("payload"u8);

        Assert.Equal(SealedOpenResult.Ok, recv.Open(frame, out _));
        Assert.Equal(SealedOpenResult.Rejected, recv.Open(frame, out _));
    }

    /// <summary>
    /// The counter must advance even if a frame is never transmitted. A nonce reused after
    /// a failed send is the same catastrophe as one reused on purpose.
    /// </summary>
    [Fact]
    public void SendSequence_NeverRepeats()
    {
        var session = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());
        var seen = new HashSet<ulong>();

        for (int i = 0; i < 100; i++)
        {
            ulong before = session.NextSendSequence;
            Assert.True(seen.Add(before), $"sequence {before} issued twice");
            session.Seal("x"u8);
            Assert.Equal(before + 1, session.NextSendSequence);
        }
    }

    /// <summary>
    /// A cleartext body is reported as NotSealed rather than Rejected, because the caller
    /// distinguishes them: during a handshake it is expected, afterwards it ends the
    /// session.
    /// </summary>
    [Fact]
    public void CleartextBody_IsDistinguishable()
    {
        var recv = new SealedSession(new PassthroughAead(), new StrictMonotonicSequence());
        Assert.Equal(SealedOpenResult.NotSealed, recv.Open(new byte[] { 0x08, 0x01, 0x02 }, out _));
    }

    /// <summary>
    /// An AEAD whose geometry does not match the frame layout is refused at construction,
    /// not discovered at the first frame.
    /// </summary>
    [Fact]
    public void Session_RefusesAMismatchedAead()
    {
        Assert.Throws<ArgumentNullException>(() => new SealedSession(null!, new StrictMonotonicSequence()));
        Assert.Throws<ArgumentNullException>(() => new SealedSession(new PassthroughAead(), null!));
        Assert.Throws<ArgumentException>(() => new SealedSession(new WrongGeometryAead(), new StrictMonotonicSequence()));
    }

    private sealed class WrongGeometryAead : ISealedAead
    {
        public int NonceSize => 8;
        public int TagSize => SealedFrame.TagSize;
        public int Seal(ReadOnlySpan<byte> n, ReadOnlySpan<byte> p, ReadOnlySpan<byte> a, Span<byte> d) => 0;
        public bool TryOpen(ReadOnlySpan<byte> n, ReadOnlySpan<byte> c, ReadOnlySpan<byte> a, Span<byte> d, out int w) { w = 0; return false; }
    }
}
