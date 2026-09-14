using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace GameServer.Net.Sealed;

/// <summary>
/// This pod's Ed25519 identity: the key it proves itself with on the gameplay hop (ADR-25).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per POD, not per fleet, and generated rather than configured.</b> The keypair is made
/// once at startup from a cryptographic RNG, lives in this process's memory, and dies with
/// the process. It is never written to a Secret, a ConfigMap, an environment variable or
/// disk, and there is deliberately no constructor that reads one from configuration —
/// making that impossible at compile time is cheaper than a rule nobody enforces.
/// </para>
/// <para>
/// <b>Rotation is pod replacement.</b> There is no key to roll, no overlap window and no
/// keyring, because the Fleet already replaces pods on every scale, rollout and crash. A
/// leaked private half buys an attacker the pod's remaining lifetime and only for the
/// sessions the gateway sends to that pod — and an attacker able to extract it is already
/// executing inside the process, where the player data is anyway. That small blast radius
/// is the whole reason this is not a fleet-wide key: the game server is the process most
/// exposed to player-controlled input, so a shared private key mounted into it would be the
/// worst-isolated secret in the system.
/// </para>
/// <para>
/// <b>What it does NOT buy, which must not be overstated.</b> The public half reaches the
/// client over the gateway hop, which is plaintext in every environment today (ADR-23's TLS
/// is implemented and off). An attacker positioned to man-in-the-middle the gameplay hop is
/// on that same path: he substitutes the key in <c>enter_world_resp</c> and the signature he
/// then forges verifies. This converts a free break into one that also requires owning the
/// gateway hop; it does not close it. The client is responsible for reporting that
/// distinction — see ADR-25 decision 6 and <c>sealed.ClientResult</c> in Go.
/// </para>
/// <para>
/// <b>NativeAOT.</b> BouncyCastle's Ed25519 is managed arithmetic with no reflection and no
/// dynamic loading, so nothing here needs a trimming root.
/// </para>
/// </remarks>
public sealed class ServerIdentity
{
    private readonly Ed25519PrivateKeyParameters _private;

    private ServerIdentity(Ed25519PrivateKeyParameters priv)
    {
        _private = priv;
        PublicKey = priv.GeneratePublicKey().GetEncoded();
        PublicKeyBase64 = Convert.ToBase64String(PublicKey);
    }

    /// <summary>
    /// The 32-byte Ed25519 public key. Safe to publish; that is what it is for.
    /// </summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// <see cref="PublicKey"/> as standard base64 WITH padding — the exact bytes that go
    /// into the <c>identity_key</c> field of this server's Redis registry entry.
    /// </summary>
    /// <remarks>
    /// The encoding is a cross-language contract: the Go gateway reads this hash field
    /// directly with <c>sealed.DecodeIdentityKey</c> and there is no translation layer
    /// between the two. Base64 rather than raw bytes because every other field in that hash
    /// is legible in <c>redis-cli</c>, and one binary field garbles the whole line for an
    /// operator reading an entry by hand.
    /// </remarks>
    public string PublicKeyBase64 { get; }

    /// <summary>Generate a fresh identity from a cryptographic RNG.</summary>
    public static ServerIdentity Generate()
        => new(new Ed25519PrivateKeyParameters(
            SecureRandom.GetNextBytes(new SecureRandom(), Ed25519PrivateKeyParameters.KeySize)));

    /// <summary>
    /// Build from existing private key material. TESTS AND CROSS-IMPLEMENTATION VECTORS
    /// ONLY — a production path that can load a key from somewhere is a production path
    /// that will eventually be given a fleet-wide one.
    /// </summary>
    internal static ServerIdentity FromPrivate(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != Ed25519PrivateKeyParameters.KeySize)
            throw new ArgumentException(
                $"private key must be {Ed25519PrivateKeyParameters.KeySize} bytes", nameof(privateKey));
        return new ServerIdentity(new Ed25519PrivateKeyParameters(privateKey.ToArray()));
    }

    /// <summary>
    /// Build the bytes signed for a handshake:
    /// <c>"cuvara/sealed-identity/v1" || 0x00 || transcript || 0x00 || identityPublic(32)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transcript is wrapped, never modified.</b> <paramref name="transcript"/> is
    /// exactly what <see cref="SealedHandshake.Transcript"/> returns. Key derivation and the
    /// HMAC binding keep reading those same bytes, so ADR-22's cross-implementation vectors
    /// stay valid and this signature gets vectors of its own rather than invalidating
    /// theirs. That is the only way to add an authenticated statement to a transcript other
    /// things already depend on byte for byte.
    /// </para>
    /// <para>
    /// <b>The identity key is inside the signed input</b> because a signature that does not
    /// name its signer authenticates a statement <i>about</i> a key rather than a key: pod
    /// A's signature over an exchange would otherwise serve anyone claiming to be pod B.
    /// The NUL separators are load-bearing for the re-splitting reason
    /// <see cref="SealedHandshake.Transcript"/> already gives.
    /// </para>
    /// </remarks>
    public static byte[] IdentityInput(ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> identityPublic)
    {
        if (transcript.Length == 0)
            throw new ArgumentException("empty transcript", nameof(transcript));
        if (identityPublic.Length != SealedHandshake.IdentityKeySize)
            throw new ArgumentException(
                $"identity key must be {SealedHandshake.IdentityKeySize} bytes", nameof(identityPublic));

        byte[] label = Encoding.UTF8.GetBytes(SealedHandshake.IdentityLabel);

        var output = new byte[label.Length + 1 + transcript.Length + 1 + SealedHandshake.IdentityKeySize];
        int at = 0;
        label.CopyTo(output, at); at += label.Length;
        output[at++] = 0x00;
        transcript.CopyTo(output.AsSpan(at)); at += transcript.Length;
        output[at++] = 0x00;
        identityPublic.CopyTo(output.AsSpan(at));

        return output;
    }

    /// <summary>Sign a handshake transcript with this pod's private key. 64 bytes.</summary>
    public byte[] Sign(ReadOnlySpan<byte> transcript)
        => SignInput(IdentityInput(transcript, PublicKey));

    /// <summary>
    /// Sign an ALREADY-BUILT input. Internal, and internal on purpose: the public
    /// <see cref="Sign"/> always wraps this pod's own key, which is what makes a signature
    /// naming somebody else's identity impossible to construct through the production API.
    /// Tests need to build exactly that forgery in order to assert it is rejected.
    /// </summary>
    internal byte[] SignInput(byte[] input)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, _private);
        signer.BlockUpdate(input, 0, input.Length);
        return signer.GenerateSignature();
    }

    /// <summary>
    /// Verify a signature against an identity public key. Used by tests and by any peer
    /// that holds the public half; the shipping verifier is the client.
    /// </summary>
    /// <remarks>
    /// No constant-time note here, unlike <see cref="SealedTranscriptSigner.Verify"/>: an
    /// Ed25519 verification compares nothing secret, because the key is public and the
    /// signature came off the wire. There is nothing a timing channel could leak.
    /// </remarks>
    public static bool Verify(
        ReadOnlySpan<byte> identityPublic, ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> signature)
    {
        if (identityPublic.Length != SealedHandshake.IdentityKeySize) return false;
        if (signature.Length != SealedHandshake.IdentitySignatureSize) return false;

        try
        {
            byte[] input = IdentityInput(transcript, identityPublic);
            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(identityPublic.ToArray()));
            verifier.BlockUpdate(input, 0, input.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (Exception)
        {
            // A malformed key point raises rather than returning false. A refusal is the
            // only correct answer either way, so it is normalised here.
            return false;
        }
    }
}
