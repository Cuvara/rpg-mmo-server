using System.Text;

namespace GameServer.Net.Sealed;

/// <summary>
/// Shape of the authenticated X25519 exchange, and the transcript both peers sign.
/// Mirrors <c>backend/shared/sealed</c>.
/// </summary>
/// <remarks>
/// No curve and no MAC here: ADR-22 has not settled the library, and the transcript is the
/// part that is cipher-agnostic and must be identical everywhere.
/// </remarks>
public static class SealedHandshake
{
    /// <summary>Length of an X25519 public key.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Length of the handshake binding tag (HMAC-SHA256).</summary>
    public const int BindingSize = 32;

    /// <summary>
    /// Domain-separation label mixed into every transcript. Part of the wire contract,
    /// byte for byte, exactly like <c>KcpCrypto</c>'s HKDF string: two peers that disagree
    /// derive different bindings and the handshake fails with no indication of why.
    /// </summary>
    public const string TranscriptLabel = "cuvara/sealed-handshake/v1";

    /// <summary>
    /// Build the bytes both peers authenticate:
    /// <c>label || 0x00 || jti || 0x00 || clientPublic(32) || serverPublic(32)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The NUL separators matter.</b> Without them the transcript is a concatenation
    /// whose pieces can be re-split: a jti ending in one byte of what should be the next
    /// field produces the same bytes as a different (jti, key) pair, and a MAC over it
    /// authenticates both readings equally. That is a real attack on length-ambiguous
    /// transcripts, and it costs two bytes to remove.
    /// </para>
    /// <para>
    /// <b>Why the binding is over this and not over the join token.</b> The token is
    /// readable by anyone on the wire — its claims are base64, not encrypted — so
    /// "present the token" proves nothing: an attacker replays what they read. What an
    /// attacker cannot do is compute a MAC keyed by material derived from
    /// <c>JOIN_TOKEN_SECRET</c>. Binding that MAC to the two EPHEMERAL public keys is what
    /// defeats the man-in-the-middle: an attacker who substitutes their own key changes
    /// the transcript, so a replayed binding no longer verifies.
    /// </para>
    /// </remarks>
    public static byte[] Transcript(string jti, ReadOnlySpan<byte> clientPublic, ReadOnlySpan<byte> serverPublic)
    {
        if (string.IsNullOrEmpty(jti))
            throw new ArgumentException("handshake needs the join token's jti", nameof(jti));
        if (clientPublic.Length != PublicKeySize)
            throw new ArgumentException($"public key must be {PublicKeySize} bytes", nameof(clientPublic));
        if (serverPublic.Length != PublicKeySize)
            throw new ArgumentException($"public key must be {PublicKeySize} bytes", nameof(serverPublic));

        byte[] label = Encoding.UTF8.GetBytes(TranscriptLabel);
        byte[] id = Encoding.UTF8.GetBytes(jti);

        var output = new byte[label.Length + 1 + id.Length + 1 + (2 * PublicKeySize)];
        int at = 0;
        label.CopyTo(output, at); at += label.Length;
        output[at++] = 0x00;
        id.CopyTo(output, at); at += id.Length;
        output[at++] = 0x00;
        clientPublic.CopyTo(output.AsSpan(at)); at += PublicKeySize;
        serverPublic.CopyTo(output.AsSpan(at));

        return output;
    }
}

/// <summary>
/// Computes and verifies the handshake binding. No implementation yet — the MAC is a
/// cryptographic primitive and ADR-22 has not settled the library.
/// </summary>
/// <remarks>
/// <see cref="Verify"/> MUST compare in constant time. A byte-by-byte comparison leaks the
/// position of the first mismatch through timing, which is enough to forge a tag one byte
/// at a time against a peer that keeps answering.
/// </remarks>
public interface ITranscriptSigner
{
    /// <summary>Binding for a transcript, keyed by join-token-derived material.</summary>
    byte[] Sign(ReadOnlySpan<byte> transcript);

    /// <summary>Whether a binding is valid for a transcript. Constant-time.</summary>
    bool Verify(ReadOnlySpan<byte> transcript, ReadOnlySpan<byte> binding);
}
