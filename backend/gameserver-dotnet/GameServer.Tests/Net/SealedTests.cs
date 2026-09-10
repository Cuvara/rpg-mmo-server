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
