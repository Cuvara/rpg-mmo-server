using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;

namespace GameServer.Net.Sealed;

/// <summary>
/// Authenticates the handshake transcript with HMAC-SHA256 under a key derived from the
/// join-token secret. Mirrors Go's <c>sealed.NewTranscriptSigner</c>.
/// </summary>
public sealed class SealedTranscriptSigner : ITranscriptSigner
{
    private readonly byte[] _key;

    /// <summary>Derive the binding key from the join-token secret and the session's jti.</summary>
    public SealedTranscriptSigner(string joinTokenSecret, string jti)
        => _key = SealedCrypto.DeriveBindingKey(joinTokenSecret, jti);

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> transcript)
    {
        var mac = new HMac(new Sha256Digest());
        mac.Init(new KeyParameter(_key));

        byte[] input = transcript.ToArray();
        mac.BlockUpdate(input, 0, input.Length);

        var output = new byte[mac.GetMacSize()];
        mac.DoFinal(output, 0);
        return output;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="Arrays.FixedTimeEquals(byte[], byte[])"/> is a constant-time comparison.
    /// A plain <c>SequenceEqual</c> would leak the position of the first mismatch through
    /// timing, which is enough to forge a tag one byte at a time against a peer that keeps
    /// answering — the peer here answers every handshake attempt, so that is not a
    /// theoretical channel.
    /// </remarks>
    public bool Verify(ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> binding)
        => binding.Length == SealedHandshake.BindingSize
           && Arrays.FixedTimeEquals(Sign(transcript), binding.ToArray());
}
