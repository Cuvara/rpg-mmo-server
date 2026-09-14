using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace GameServer.Net.Sealed;

/// <summary>
/// The primitives, per ADR-22: ChaCha20-Poly1305 (RFC 8439), X25519 (RFC 7748) and
/// HKDF-SHA256 (RFC 5869), all from BouncyCastle.Cryptography 2.7.0.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here implements a cipher, a MAC or a curve.</b> Writing any of them by hand
/// is how a subtly wrong implementation ships: it round-trips against itself perfectly and
/// reports nothing. That is why the tests use published RFC vectors and cross-checks
/// against the Go side rather than round-trips alone.
/// </para>
/// <para>
/// <b>Why BouncyCastle for all three when .NET 10 has two of them.</b> .NET has
/// <c>ChaCha20Poly1305</c> and <c>HKDF</c> built in, and both are measured working on
/// 10.0.10 — but it has <b>no X25519 at all</b> (<c>ECDiffieHellman</c> rejects
/// <c>curve25519</c>, <c>X25519</c> and <c>Curve25519</c> as friendly names), so
/// BouncyCastle has to be here regardless. Using one library for all three means the
/// server and the Unity client run the *same* implementation, which removes a class of
/// interop question rather than answering it three times.
/// </para>
/// <para>
/// The cost is not free and is worth knowing: BouncyCastle's ChaCha20-Poly1305 is managed
/// code, while .NET's is the platform's (OpenSSL/CNG) and hardware-assisted. If the AEAD
/// ever shows up in a tick profile, swapping <see cref="SealedAead"/> alone for the
/// built-in type is a contained change — the wire format does not care which library
/// produced the bytes, which is the whole point of having RFC vectors on both.
/// </para>
/// </remarks>
public static class SealedCrypto
{
    /// <summary>ChaCha20-Poly1305 key length.</summary>
    public const int KeySize = 32;

    /// <summary>X25519 public and private key length.</summary>
    public const int X25519KeySize = 32;

    // Domain-separation labels. Part of the wire contract byte for byte: two peers that
    // disagree derive different keys, no session forms, and nothing says why.
    private const string KeyLabelC2S = "cuvara/sealed-key/c2s/v1";
    private const string KeyLabelS2C = "cuvara/sealed-key/s2c/v1";
    private const string BindingLabel = "cuvara/sealed-binding/v1";

    /// <summary>HKDF-SHA256 expand, matching Go's <c>hkdf.New(sha256.New, ...)</c>.</summary>
    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length)
    {
        var generator = new HkdfBytesGenerator(new Sha256Digest());
        generator.Init(new HkdfParameters(ikm.ToArray(), salt.ToArray(), Encoding.UTF8.GetBytes(info)));

        var output = new byte[length];
        generator.GenerateBytes(output, 0, length);
        return output;
    }

    /// <summary>
    /// Expand the X25519 shared secret into the two one-direction AEAD keys, salted by the
    /// handshake transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two keys, not one, and this is what makes the bare counter nonce safe:</b> the
    /// client's sequence 7 and the server's sequence 7 are encrypted under different keys,
    /// so the (key, nonce) pair never repeats across directions. Collapsing these into one
    /// key breaks the scheme, not merely this method.
    /// </para>
    /// <para>
    /// Salting with the transcript binds the keys to the exact exchange that produced them
    /// — the jti, both ephemeral publics, the protocol label. Two runs that agreed on a
    /// shared secret but disagreed about anything else derive different keys and fail,
    /// rather than proceeding on a half-agreed view of what was negotiated.
    /// </para>
    /// </remarks>
    public static (byte[] ClientToServer, byte[] ServerToClient) DeriveDirectionKeys(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> transcript)
    {
        if (sharedSecret.Length == 0)
            throw new ArgumentException("empty shared secret", nameof(sharedSecret));

        return (Hkdf(sharedSecret, transcript, KeyLabelC2S, KeySize),
                Hkdf(sharedSecret, transcript, KeyLabelS2C, KeySize));
    }

    /// <summary>Derive the handshake binding key from the join-token secret and the jti.</summary>
    /// <remarks>
    /// This is the proof-of-possession ADR-22 requires. The join token itself proves
    /// nothing — its claims are base64 and an attacker replays what they read — whereas
    /// this key cannot be computed without <c>JOIN_TOKEN_SECRET</c>.
    /// </remarks>
    public static byte[] DeriveBindingKey(string joinTokenSecret, string jti)
    {
        if (string.IsNullOrEmpty(joinTokenSecret))
            throw new ArgumentException("no join-token secret", nameof(joinTokenSecret));
        if (string.IsNullOrEmpty(jti))
            throw new ArgumentException("no jti", nameof(jti));

        return Hkdf(Encoding.UTF8.GetBytes(joinTokenSecret), Encoding.UTF8.GetBytes(jti),
                    BindingLabel, KeySize);
    }
}

/// <summary>ChaCha20-Poly1305 over BouncyCastle, adapted to <see cref="ISealedAead"/>.</summary>
public sealed class SealedAead : ISealedAead
{
    private readonly KeyParameter _key;

    /// <summary>Build the AEAD for ONE direction from a 32-byte key.</summary>
    public SealedAead(ReadOnlySpan<byte> key)
    {
        if (key.Length != SealedCrypto.KeySize)
            throw new ArgumentException($"key must be {SealedCrypto.KeySize} bytes", nameof(key));
        _key = new KeyParameter(key.ToArray());
    }

    /// <inheritdoc />
    public int NonceSize => SealedFrame.NonceSize;

    /// <inheritdoc />
    public int TagSize => SealedFrame.TagSize;

    /// <inheritdoc />
    public int Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, Span<byte> destination)
    {
        var cipher = new ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(_key, SealedFrame.TagSize * 8, nonce.ToArray(), aad.ToArray()));

        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int written = cipher.ProcessBytes(plaintext.ToArray(), 0, plaintext.Length, output, 0);
        written += cipher.DoFinal(output, written);

        output.AsSpan(0, written).CopyTo(destination);
        return written;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every failure returns the same <c>false</c>. BouncyCastle raises
    /// <c>InvalidCipherTextException</c> for a bad tag; catching it and reporting the same
    /// result as any other failure is deliberate — a peer must not learn <i>why</i> a
    /// frame was refused.
    /// </remarks>
    public bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> aad, Span<byte> destination, out int written)
    {
        written = 0;
        if (ciphertext.Length < SealedFrame.TagSize) return false;

        try
        {
            var cipher = new ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(_key, SealedFrame.TagSize * 8, nonce.ToArray(), aad.ToArray()));

            var output = new byte[cipher.GetOutputSize(ciphertext.Length)];
            int produced = cipher.ProcessBytes(ciphertext.ToArray(), 0, ciphertext.Length, output, 0);
            produced += cipher.DoFinal(output, produced);

            if (produced > destination.Length) return false;
            output.AsSpan(0, produced).CopyTo(destination);
            written = produced;
            return true;
        }
        catch (Exception)
        {
            // Includes InvalidCipherTextException (bad tag) and every malformed-input
            // case. One answer for all of them, on purpose.
            return false;
        }
    }
}

/// <summary>An ephemeral X25519 key pair.</summary>
/// <remarks>
/// Ephemeral per connection: reusing one across sessions forfeits the forward secrecy that
/// is the entire reason ADR-22 supersedes the derived-key scheme.
/// </remarks>
public sealed class SealedKeyPair
{
    private readonly X25519PrivateKeyParameters _private;

    private SealedKeyPair(X25519PrivateKeyParameters priv)
    {
        _private = priv;
        Public = priv.GeneratePublicKey().GetEncoded();
    }

    /// <summary>Public key. Safe to send in the clear; that is what it is for.</summary>
    public byte[] Public { get; }

    /// <summary>Generate a fresh pair from a cryptographic RNG.</summary>
    public static SealedKeyPair Generate()
        => new(new X25519PrivateKeyParameters(SecureRandom.GetNextBytes(new SecureRandom(), SealedCrypto.X25519KeySize)));

    /// <summary>Build from existing private key material. Tests and vectors only.</summary>
    internal static SealedKeyPair FromPrivate(ReadOnlySpan<byte> privateKey)
        => new(new X25519PrivateKeyParameters(privateKey.ToArray()));

    /// <summary>
    /// Compute the X25519 shared secret with a peer's public key.
    /// </summary>
    /// <remarks>
    /// A low-order peer key forces a shared secret the attacker knows and both sides agree
    /// on — a complete break that looks like a successful handshake. BouncyCastle's
    /// agreement raises on those points; this reports it as a refusal rather than letting
    /// an all-zero secret through.
    /// </remarks>
    public bool TryAgree(ReadOnlySpan<byte> peerPublic, out byte[] sharedSecret)
    {
        sharedSecret = Array.Empty<byte>();
        if (peerPublic.Length != SealedCrypto.X25519KeySize) return false;

        try
        {
            var agreement = new X25519Agreement();
            agreement.Init(_private);

            var output = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublic.ToArray()), output, 0);

            sharedSecret = output;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
