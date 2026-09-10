using System.Buffers.Binary;

namespace GameServer.Net.Sealed;

/// <summary>
/// Wire format for an authenticated-encrypted frame. Mirrors
/// <c>backend/shared/sealed</c> byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the AEAD sits, and why not at the packet layer.</b> The existing frame is
/// <c>[4-byte big-endian length][Envelope protobuf]</c>. The KCP path has a packet-crypt
/// layer under the ARQ (<see cref="Transport.KcpCrypto"/>), but that layer is
/// per-<i>listener</i> — kcp-go takes one BlockCrypt for every datagram and offers no
/// per-remote key selection — so it cannot carry a per-session key; and TCP, the default
/// transport, has no such layer at all. Sealing therefore happens ABOVE the transport,
/// around the Envelope, which makes it identical on both and independent of the ARQ.
/// </para>
/// <para>
/// <b>No cipher lives here.</b> ADR-22 has not settled which library provides
/// ChaCha20-Poly1305 on all three runtimes, and a placeholder that "worked" would let
/// every test above it pass while proving nothing about the bytes.
/// </para>
/// </remarks>
public static class SealedFrame
{
    /// <summary>
    /// First byte of a sealed frame body.
    /// </summary>
    /// <remarks>
    /// The codec picks an encoding by sniffing <c>body[0]</c>: <c>0x08</c> is Protobuf (an
    /// Envelope always starts with field 1, <c>type</c>, and type 0 is refused precisely so
    /// that byte is stable) and <c>0x7B</c> is JSON's <c>{</c>. A sealed body begins with
    /// ciphertext, indistinguishable from either, so it needs its own marker.
    /// <c>0xC1</c> cannot begin a well-formed Envelope — as a protobuf tag it names field
    /// 24, and field 1 is mandatory.
    /// </remarks>
    public const byte Marker = 0xC1;

    /// <summary>
    /// Sealed-format version. Inside the authenticated data, so it cannot be edited in
    /// flight to roll a peer back to an older format.
    /// </summary>
    public const byte Version = 1;

    /// <summary>Marker + version + 8-byte sequence.</summary>
    public const int HeaderSize = 1 + 1 + 8;

    /// <summary>Nonce length both candidate AEADs take.</summary>
    public const int NonceSize = 12;

    /// <summary>Authentication tag length both candidate AEADs append.</summary>
    public const int TagSize = 16;

    /// <summary>Write the cleartext header. Every byte becomes additional authenticated data.</summary>
    public static void WriteHeader(Span<byte> destination, ulong sequence)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException($"need {HeaderSize} bytes", nameof(destination));

        destination[0] = Marker;
        destination[1] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(destination[2..HeaderSize], sequence);
    }

    /// <summary>
    /// Read the header from the front of a body.
    /// </summary>
    /// <remarks>
    /// This authenticates NOTHING. The header is cleartext, and everything it says is
    /// unverified until the AEAD tag over the whole frame verifies. Treat the sequence as
    /// a hint for reconstructing the nonce, never as a fact, and do not act on it — in
    /// particular do not advance a replay window with it — before Open succeeds.
    /// </remarks>
    public static bool TryReadHeader(ReadOnlySpan<byte> body, out ulong sequence, out SealedFrameError error)
    {
        sequence = 0;

        if (body.Length == 0 || body[0] != Marker)
        {
            error = SealedFrameError.NotSealed;
            return false;
        }
        if (body.Length < HeaderSize + TagSize)
        {
            error = SealedFrameError.ShortFrame;
            return false;
        }
        if (body[1] != Version)
        {
            error = SealedFrameError.UnsupportedVersion;
            return false;
        }

        sequence = BinaryPrimitives.ReadUInt64BigEndian(body[2..HeaderSize]);
        error = SealedFrameError.None;
        return true;
    }

    /// <summary>
    /// Build the AEAD nonce for a sequence number: four zero bytes, then the big-endian
    /// sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zero prefix is not padding for its own sake — it leaves room for an explicit
    /// direction or rekey epoch without changing the nonce length or the frame layout.
    /// </para>
    /// <para>
    /// <b>Why a bare counter is safe here, and when it would not be.</b> Reusing a
    /// (key, nonce) pair with any AEAD in this family is catastrophic: it leaks the XOR of
    /// two plaintexts and, for Poly1305, can expose the authentication key. A counter is
    /// safe only because the handshake gives each DIRECTION its own key, so the client's
    /// sequence 7 and the server's sequence 7 are encrypted under different keys. If a
    /// future change ever makes one key serve both directions, this method must grow a
    /// direction byte in the prefix on the same day, or the scheme is broken.
    /// </para>
    /// </remarks>
    public static void WriteNonce(Span<byte> destination, ulong sequence)
    {
        if (destination.Length < NonceSize)
            throw new ArgumentException($"need {NonceSize} bytes", nameof(destination));

        destination[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..NonceSize], sequence);
    }
}

/// <summary>Why a body could not be read as a sealed frame.</summary>
public enum SealedFrameError
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>
    /// Not a sealed frame. The caller decides what that means: during a handshake it is
    /// expected; afterwards it is a peer sending cleartext where a sealed frame is
    /// required, which must end the session rather than be accepted.
    /// </summary>
    NotSealed,

    /// <summary>Too small to hold a header and a tag, so it cannot be authenticated.</summary>
    ShortFrame,

    /// <summary>A sealed frame from a future format version.</summary>
    UnsupportedVersion,
}
