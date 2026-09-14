using System.Text;
using GameServer.Net.Sealed;

namespace GameServer.Tests.Net;

/// <summary>
/// The per-pod Ed25519 identity (ADR-25): that it signs, that it publishes, and — the part
/// that matters — that it REFUSES.
/// </summary>
/// <remarks>
/// A verifier that accepts everything is indistinguishable from one that works, so the
/// negative cases below are the real test and the positive one exists only to keep them
/// meaningful. ADR-25 decision 8 says this in those words.
/// </remarks>
public class ServerIdentityTests
{
    private static byte[] Transcript(string jti = "identity-test-jti", byte serverByte = 0xA0)
    {
        var client = new byte[SealedHandshake.PublicKeySize];
        var server = new byte[SealedHandshake.PublicKeySize];
        for (int i = 0; i < client.Length; i++)
        {
            client[i] = (byte)i;
            server[i] = (byte)(serverByte + i);
        }
        return SealedHandshake.Transcript(jti, client, server);
    }

    [Fact]
    public void Generate_ProducesA32ByteKeyAndA64ByteSignatureThatVerifies()
    {
        var identity = ServerIdentity.Generate();
        byte[] transcript = Transcript();

        Assert.Equal(SealedHandshake.IdentityKeySize, identity.PublicKey.Length);

        byte[] signature = identity.Sign(transcript);
        Assert.Equal(SealedHandshake.IdentitySignatureSize, signature.Length);
        Assert.True(ServerIdentity.Verify(identity.PublicKey, transcript, signature));
    }

    /// <summary>
    /// Two pods must not share an identity. Per-pod is the entire reason the private half is
    /// never mounted from a Secret, so a generator that returned a constant would quietly
    /// undo ADR-25 while every other test here still passed.
    /// </summary>
    [Fact]
    public void Generate_IsPerProcessAndNotAConstant()
    {
        var a = ServerIdentity.Generate();
        var b = ServerIdentity.Generate();

        Assert.NotEqual(a.PublicKeyBase64, b.PublicKeyBase64);
        Assert.False(a.PublicKey.SequenceEqual(b.PublicKey));
        // And the signatures do not cross: A's key must not verify B's signature.
        byte[] transcript = Transcript();
        Assert.False(ServerIdentity.Verify(a.PublicKey, transcript, b.Sign(transcript)));
    }

    /// <summary>
    /// THE MAN IN THE MIDDLE. He substitutes his own ephemeral key, which changes the
    /// transcript, and can only replay the signature he read. It must not verify.
    /// </summary>
    [Fact]
    public void Verify_RejectsASignatureOverATamperedTranscript()
    {
        var identity = ServerIdentity.Generate();
        byte[] real = Transcript();
        byte[] tampered = Transcript(serverByte: 0xB0);

        Assert.False(real.SequenceEqual(tampered)); // the premise of the test
        Assert.False(ServerIdentity.Verify(identity.PublicKey, tampered, identity.Sign(real)));
    }

    /// <summary>
    /// A jti change alone must break it too — the transcript anchors the session, so a
    /// signature lifted from one session must not authenticate another.
    /// </summary>
    [Fact]
    public void Verify_RejectsASignatureReplayedIntoAnotherSession()
    {
        var identity = ServerIdentity.Generate();
        byte[] session1 = Transcript(jti: "session-one");
        byte[] session2 = Transcript(jti: "session-two");

        Assert.False(ServerIdentity.Verify(identity.PublicKey, session2, identity.Sign(session1)));
    }

    /// <summary>
    /// A forged signature — every shape of forgery, each a different attacker — is rejected.
    /// </summary>
    [Fact]
    public void Verify_RejectsEveryForgedSignature()
    {
        var identity = ServerIdentity.Generate();
        var attacker = ServerIdentity.Generate();
        byte[] transcript = Transcript();
        byte[] good = identity.Sign(transcript);

        byte[] firstFlipped = (byte[])good.Clone();
        firstFlipped[0] ^= 0x01;
        byte[] lastFlipped = (byte[])good.Clone();
        lastFlipped[^1] ^= 0x80;

        // Signed by someone who runs a pod of his own: a perfectly valid Ed25519
        // signature, and still not ours. The question is never "is this a signature".
        byte[] wrongSigner = attacker.Sign(transcript);

        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, firstFlipped));
        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, lastFlipped));
        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, wrongSigner));
        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, new byte[SealedHandshake.IdentitySignatureSize]));
        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, good.AsSpan(0, 63).ToArray()));
        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, []));
        // Verified against the wrong key.
        Assert.False(ServerIdentity.Verify(attacker.PublicKey, transcript, good));
        // A malformed key must refuse, not throw.
        Assert.False(ServerIdentity.Verify(identity.PublicKey.AsSpan(0, 31).ToArray(), transcript, good));
    }

    /// <summary>
    /// The signed input wraps the transcript and does not touch it (ADR-25 decision 3).
    /// </summary>
    /// <remarks>
    /// This is the assertion that keeps ADR-22's cross-implementation vectors valid: the
    /// binding and the direction keys are computed over exactly these bytes, so an
    /// "improvement" to the transcript would break a deployed handshake rather than this
    /// new field.
    /// </remarks>
    [Fact]
    public void IdentityInput_IsLabelNulTranscriptNulKey_AndLeavesTheTranscriptAlone()
    {
        var identity = ServerIdentity.Generate();
        byte[] transcript = Transcript();

        byte[] input = ServerIdentity.IdentityInput(transcript, identity.PublicKey);
        byte[] label = Encoding.UTF8.GetBytes(SealedHandshake.IdentityLabel);

        var expected = new List<byte>();
        expected.AddRange(label);
        expected.Add(0x00);
        expected.AddRange(transcript);
        expected.Add(0x00);
        expected.AddRange(identity.PublicKey);

        Assert.Equal(expected, input);
        Assert.NotEqual(SealedHandshake.IdentityLabel, SealedHandshake.TranscriptLabel);
    }

    /// <summary>
    /// The identity key is inside the signed input, so a signature cannot be re-pointed at
    /// another key. Without this, "someone signed this exchange" would pass for "this key
    /// signed this exchange".
    /// </summary>
    [Fact]
    public void Verify_RejectsAGenuineSignatureThatNamesAnotherIdentity()
    {
        var identity = ServerIdentity.Generate();
        var other = ServerIdentity.Generate();
        byte[] transcript = Transcript();

        // Genuinely signed by `identity`, but over an input naming `other`'s key. Both
        // verifications must fail: `identity` because the input does not match what Verify
        // rebuilds, `other` because it did not sign.
        byte[] crossInput = ServerIdentity.IdentityInput(transcript, other.PublicKey);
        byte[] crossSignature = identity.SignInput(crossInput);

        Assert.False(ServerIdentity.Verify(identity.PublicKey, transcript, crossSignature));
        Assert.False(ServerIdentity.Verify(other.PublicKey, transcript, crossSignature));
    }

    /// <summary>
    /// The base64 the registry carries is the standard alphabet with padding — a
    /// cross-language contract with <c>sealed.DecodeIdentityKey</c> in Go.
    /// </summary>
    [Fact]
    public void PublicKeyBase64_IsStandardPaddedBase64OfThePublicKey()
    {
        var identity = ServerIdentity.Generate();

        Assert.Equal(Convert.ToBase64String(identity.PublicKey), identity.PublicKeyBase64);
        Assert.DoesNotContain('-', identity.PublicKeyBase64);
        Assert.DoesNotContain('_', identity.PublicKeyBase64);
        Assert.Equal(44, identity.PublicKeyBase64.Length); // 32 bytes padded
        Assert.Equal(identity.PublicKey, Convert.FromBase64String(identity.PublicKeyBase64));
    }

    [Fact]
    public void IdentityInput_RejectsBadShapes()
    {
        var identity = ServerIdentity.Generate();
        Assert.Throws<ArgumentException>(() => ServerIdentity.IdentityInput([], identity.PublicKey));
        Assert.Throws<ArgumentException>(() =>
            ServerIdentity.IdentityInput(Transcript(), identity.PublicKey.AsSpan(0, 31).ToArray()));
    }

}
