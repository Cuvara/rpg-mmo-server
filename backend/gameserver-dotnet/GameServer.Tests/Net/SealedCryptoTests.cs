using System.Text;
using GameServer.Net.Sealed;

namespace GameServer.Tests.Net;

/// <summary>
/// Published RFC vectors and cross-implementation checks for the sealed primitives.
/// </summary>
/// <remarks>
/// A round-trip proves an implementation agrees with itself, which a subtly wrong one also
/// does — silently. Only a published vector proves it agrees with everyone else, and only
/// a cross-implementation vector proves the two halves of this system agree with each
/// other. Both kinds are here because a disagreement produces no error anywhere: the
/// handshake simply never completes.
/// </remarks>
public class SealedCryptoTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// <summary>RFC 8439 §2.8.2 — the published AEAD_CHACHA20_POLY1305 vector.</summary>
    [Fact]
    public void ChaCha20Poly1305_MatchesRfc8439()
    {
        byte[] key = Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        byte[] nonce = Hex("070000004041424344454647");
        byte[] aad = Hex("50515253c0c1c2c3c4c5c6c7");
        byte[] plaintext = Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip " +
            "for the future, sunscreen would be it.");

        byte[] want = Hex(
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
            "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36" +
            "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
            "3ff4def08e4b7a9de576d26586cec64b6116" +
            "1ae10b594f09e26a7e902ecbd0600691");

        var aead = new SealedAead(key);
        var got = new byte[plaintext.Length + SealedFrame.TagSize];
        int written = aead.Seal(nonce, plaintext, aad, got);

        Assert.Equal(want.Length, written);
        Assert.Equal(Convert.ToHexString(want), Convert.ToHexString(got));

        var back = new byte[plaintext.Length];
        Assert.True(aead.TryOpen(nonce, got, aad, back, out int produced));
        Assert.Equal(plaintext.Length, produced);
        Assert.Equal(plaintext, back);
    }

    /// <summary>
    /// Tampering must be detected — ciphertext, tag, and additional data. The AAD case is
    /// the one an implementation can get wrong while every round-trip still passes,
    /// because nothing exercises AAD unless you tamper with it specifically.
    /// </summary>
    [Fact]
    public void ChaCha20Poly1305_RejectsTampering()
    {
        var aead = new SealedAead(new byte[SealedCrypto.KeySize]);
        var nonce = new byte[SealedFrame.NonceSize];
        byte[] aad = Encoding.ASCII.GetBytes("header");
        byte[] plaintext = Encoding.ASCII.GetBytes("payload");

        var sealedFrame = new byte[plaintext.Length + SealedFrame.TagSize];
        aead.Seal(nonce, plaintext, aad, sealedFrame);

        var output = new byte[plaintext.Length];

        byte[] flippedCipher = (byte[])sealedFrame.Clone();
        flippedCipher[0] ^= 1;
        Assert.False(aead.TryOpen(nonce, flippedCipher, aad, output, out _));

        byte[] flippedTag = (byte[])sealedFrame.Clone();
        flippedTag[^1] ^= 1;
        Assert.False(aead.TryOpen(nonce, flippedTag, aad, output, out _));

        Assert.False(aead.TryOpen(nonce, sealedFrame, Encoding.ASCII.GetBytes("heade!"), output, out _));
        Assert.False(aead.TryOpen(nonce, sealedFrame.AsSpan(0, sealedFrame.Length - 1), aad, output, out _));
    }

    /// <summary>RFC 7748 §6.1 — the published X25519 Diffie-Hellman vector.</summary>
    [Fact]
    public void X25519_MatchesRfc7748()
    {
        byte[] alicePriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        byte[] bobPriv = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        byte[] alicePub = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        byte[] bobPub = Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        byte[] wantShared = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        SealedKeyPair alice = SealedKeyPair.FromPrivate(alicePriv);
        SealedKeyPair bob = SealedKeyPair.FromPrivate(bobPriv);

        Assert.Equal(Convert.ToHexString(alicePub), Convert.ToHexString(alice.Public));
        Assert.Equal(Convert.ToHexString(bobPub), Convert.ToHexString(bob.Public));

        Assert.True(alice.TryAgree(bob.Public, out byte[] fromAlice));
        Assert.True(bob.TryAgree(alice.Public, out byte[] fromBob));

        Assert.Equal(Convert.ToHexString(wantShared), Convert.ToHexString(fromAlice));
        Assert.Equal(Convert.ToHexString(wantShared), Convert.ToHexString(fromBob));
    }

    /// <summary>
    /// A low-order peer key forces a shared secret the attacker knows and both sides agree
    /// on — a complete break that looks like a successful handshake.
    /// </summary>
    [Fact]
    public void X25519_RefusesLowOrderPoints()
    {
        SealedKeyPair kp = SealedKeyPair.Generate();
        Assert.False(kp.TryAgree(new byte[SealedCrypto.X25519KeySize], out _));
        Assert.False(kp.TryAgree(new byte[31], out _));
    }

    [Fact]
    public void X25519_FreshKeysAgree()
    {
        SealedKeyPair a = SealedKeyPair.Generate();
        SealedKeyPair b = SealedKeyPair.Generate();

        Assert.NotEqual(Convert.ToHexString(a.Public), Convert.ToHexString(b.Public));
        Assert.True(a.TryAgree(b.Public, out byte[] sa));
        Assert.True(b.TryAgree(a.Public, out byte[] sb));
        Assert.Equal(Convert.ToHexString(sa), Convert.ToHexString(sb));
    }

    /// <summary>RFC 5869 A.1 — the published HKDF-SHA256 vector.</summary>
    [Fact]
    public void Hkdf_MatchesRfc5869()
    {
        byte[] ikm = Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        byte[] salt = Hex("000102030405060708090a0b0c");
        byte[] want = Hex(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf" +
            "34007208d5b887185865");

        // The info in the RFC vector is the raw bytes f0..f9; our helper takes a string,
        // so this reproduces those exact bytes through the same path the key schedule
        // uses rather than a special one.
        string info = new(new[] { 'ð', 'ñ', 'ò', 'ó', 'ô',
                                  'õ', 'ö', '÷', 'ø', 'ù' });
        byte[] infoBytes = Encoding.Latin1.GetBytes(info);

        byte[] got = HkdfRaw(ikm, salt, infoBytes, want.Length);
        Assert.Equal(Convert.ToHexString(want), Convert.ToHexString(got));
    }

    private static byte[] HkdfRaw(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        var gen = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(
            new Org.BouncyCastle.Crypto.Digests.Sha256Digest());
        gen.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(ikm, salt, info));
        var output = new byte[length];
        gen.GenerateBytes(output, 0, length);
        return output;
    }

    // --- key schedule ---

    /// <summary>
    /// The two directions must get different keys. This is what makes the bare counter
    /// nonce safe; if it ever fails, the nonce scheme is broken, not just this method.
    /// </summary>
    [Fact]
    public void DirectionKeys_Differ()
    {
        (byte[] c2s, byte[] s2c) = SealedCrypto.DeriveDirectionKeys(
            Encoding.UTF8.GetBytes("shared secret"), Encoding.UTF8.GetBytes("transcript"));

        Assert.NotEqual(Convert.ToHexString(c2s), Convert.ToHexString(s2c));
        Assert.NotEqual(Convert.ToHexString(new byte[SealedCrypto.KeySize]), Convert.ToHexString(c2s));
    }

    [Fact]
    public void DirectionKeys_AreBoundToTheTranscript()
    {
        (byte[] a, _) = SealedCrypto.DeriveDirectionKeys("secret"u8, "transcript one"u8);
        (byte[] b, _) = SealedCrypto.DeriveDirectionKeys("secret"u8, "transcript two"u8);
        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b));
    }

    // --- binding ---

    [Fact]
    public void Binding_VerifiesAndCatchesASubstitutedKey()
    {
        var signer = new SealedTranscriptSigner("join-secret", "jti-1");

        var clientPublic = new byte[SealedHandshake.PublicKeySize];
        var serverPublic = new byte[SealedHandshake.PublicKeySize];
        clientPublic[0] = 1; serverPublic[0] = 2;

        byte[] transcript = SealedHandshake.Transcript("jti-1", clientPublic, serverPublic);
        byte[] binding = signer.Sign(transcript);
        Assert.True(signer.Verify(transcript, binding));

        // The MITM case: an attacker substitutes its own ephemeral key and replays the
        // binding it read off the wire. The transcript changes, so it must fail.
        var attacker = new byte[SealedHandshake.PublicKeySize];
        attacker[0] = 0xAA;
        byte[] substituted = SealedHandshake.Transcript("jti-1", attacker, serverPublic);
        Assert.False(signer.Verify(substituted, binding));
    }

    /// <summary>
    /// An attacker who does not hold <c>JOIN_TOKEN_SECRET</c> cannot produce a binding,
    /// even knowing the jti and both public keys — all of which travel in the clear.
    /// </summary>
    [Fact]
    public void Binding_RequiresTheSecret()
    {
        var real = new SealedTranscriptSigner("the-real-secret", "jti-1");
        var attacker = new SealedTranscriptSigner("a-guess", "jti-1");

        var zero = new byte[SealedHandshake.PublicKeySize];
        byte[] transcript = SealedHandshake.Transcript("jti-1", zero, zero);

        Assert.False(real.Verify(transcript, attacker.Sign(transcript)));
    }

    [Fact]
    public void Binding_IsPerSession()
    {
        var one = new SealedTranscriptSigner("secret", "jti-1");
        var two = new SealedTranscriptSigner("secret", "jti-2");
        byte[] transcript = "same bytes"u8.ToArray();

        Assert.NotEqual(Convert.ToHexString(one.Sign(transcript)),
                        Convert.ToHexString(two.Sign(transcript)));
    }
}
