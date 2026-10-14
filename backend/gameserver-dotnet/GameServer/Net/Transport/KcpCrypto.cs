using System.Security.Cryptography;

namespace GameServer.Net.Transport;

/// <summary>
/// Packet-level encryption compatible with <c>github.com/xtaci/kcp-go/v5</c>'s
/// <c>NewAESBlockCrypt</c>, which is what the Go side selects in
/// <c>backend/shared/transport/crypto.go</c>.
/// </summary>
/// <remarks>
/// <para>
/// kcp-go's block ciphers are NOT a standard AEAD. Every outgoing datagram is
/// laid out as
/// </para>
/// <code>
/// | nonce (16B, random) | crc32-IEEE of the KCP bytes (4B, little endian) | KCP bytes |
/// </code>
/// <para>
/// and then the WHOLE buffer (nonce included) is encrypted with AES in CFB mode
/// using a hard-coded initialisation vector — kcp-go's <c>initialVector</c>. The
/// random nonce is what makes the first ciphertext block differ per packet
/// despite the fixed IV; it carries no other meaning and is discarded after
/// decryption. The trailing partial block is XORed against the keystream block
/// with no padding, exactly like textbook CFB.
/// </para>
/// <para>
/// Consequences that matter operationally: there is no negotiation, no key id
/// and no downgrade path. A peer with the wrong key produces bytes that decrypt
/// to noise, fail the CRC, and are dropped as a malformed datagram — the session
/// simply never forms. That is the "fail closed" behaviour the Go tests assert
/// and the C# side reproduces.
/// </para>
/// </remarks>
public sealed class KcpCrypto : IDisposable
{
    /// <summary>Size of the per-packet random nonce, in bytes (kcp-go <c>nonceSize</c>).</summary>
    public const int NonceSize = 16;

    /// <summary>Size of the CRC32 field, in bytes (kcp-go <c>crcSize</c>).</summary>
    public const int CrcSize = 4;

    /// <summary>Total bytes kcp-go prepends to every encrypted datagram.</summary>
    public const int HeaderSize = NonceSize + CrcSize;

    /// <summary>Derived AES key length: AES-256.</summary>
    public const int KeySize = 32;

    /// <summary>
    /// Domain separation string for HKDF. Must stay byte-identical to
    /// <c>hkdfInfo</c> in <c>backend/shared/transport/crypto.go</c> or the two
    /// halves derive different keys from the same passphrase.
    /// </summary>
    private const string HkdfInfo = "rpg-mmo/transport/kcp/aes-256";

    /// <summary>
    /// kcp-go's fixed CFB initialisation vector (<c>initialVector</c> in crypt.go).
    /// </summary>
    private static ReadOnlySpan<byte> InitialVector =>
        [167, 115, 79, 156, 18, 172, 27, 1, 164, 21, 242, 193, 252, 120, 230, 107];

    private readonly Aes _aes;

    private KcpCrypto(byte[] key)
    {
        _aes = Aes.Create();
        _aes.Key = key;
        _aes.Mode = CipherMode.ECB;      // one-shot calls below pick their own mode
        _aes.Padding = PaddingMode.None;
    }

    /// <summary>
    /// Builds the crypto layer for a transport key, or <c>null</c> when the key is
    /// unset — the plaintext dev default, mirroring Go's <c>blockCrypt("")</c>.
    /// </summary>
    public static KcpCrypto? TryCreate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new KcpCrypto(DeriveKey(key));
    }

    /// <summary>
    /// Turns an operator-supplied <c>TRANSPORT_KEY</c> into the 32-byte AES-256 key,
    /// using the same two accepted forms as the Go side:
    /// 64 hex characters are decoded verbatim, anything else is stretched with
    /// HKDF-SHA256 (no salt, fixed info string).
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty or whitespace.</exception>
    public static byte[] DeriveKey(string key)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) throw new ArgumentException("derive transport key: empty key", nameof(key));

        if (k.Length == 2 * KeySize)
        {
            // Convert.FromHexString is case-insensitive and rejects non-hex, which is
            // exactly Go's hex.DecodeString behaviour including the fall-through below.
            try { return Convert.FromHexString(k); }
            catch (FormatException) { /* not hex despite the length — treat as passphrase */ }
        }

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: System.Text.Encoding.UTF8.GetBytes(k),
            outputLength: KeySize,
            salt: null,
            info: System.Text.Encoding.UTF8.GetBytes(HkdfInfo));
    }

    /// <summary>
    /// Seals <paramref name="packet"/> in place. The caller must have reserved
    /// <see cref="HeaderSize"/> bytes at the front for the nonce and checksum; the
    /// KCP bytes start at that offset.
    /// </summary>
    public void Seal(Span<byte> packet)
    {
        RandomNumberGenerator.Fill(packet[..NonceSize]);
        uint crc = Crc32.Compute(packet[HeaderSize..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(NonceSize, CrcSize), crc);
        CfbEncrypt(packet);
    }

    /// <summary>
    /// Opens <paramref name="packet"/> in place and returns the KCP byte range, or
    /// an empty span when the datagram is too short or fails the checksum (wrong
    /// key, corruption, or a stray datagram from an unrelated sender).
    /// </summary>
    public Span<byte> Open(Span<byte> packet)
    {
        if (packet.Length < HeaderSize) return default;
        CfbDecrypt(packet);
        var body = packet[HeaderSize..];
        uint want = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(NonceSize, CrcSize));
        return Crc32.Compute(body) == want ? body : default;
    }

    /// <summary>
    /// Largest datagram sealed or opened on the stack. Datagrams are capped at the MTU
    /// (1500 bytes); anything bigger takes the pooled path.
    /// </summary>
    private const int StackLimit = 2048;

    /// <summary>CFB-128 encryption with kcp-go's fixed IV, trailing bytes unpadded.</summary>
    /// <remarks>
    /// The full blocks go through one bulk <see cref="SymmetricAlgorithm.EncryptCfb(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte}, PaddingMode, int)"/>
    /// call. The first version called <c>EncryptEcb</c> once per 16-byte block - about 90
    /// one-shot cipher setups per MTU datagram, ~340 us each datagram under load - and at 95
    /// players sealing alone took about two cores, under the session lock (#449). The trailing partial block is XORed against
    /// E(last ciphertext block), which is what CFB would produce for it, so the bytes are
    /// identical to the per-block form and to kcp-go.
    /// </remarks>
    private void CfbEncrypt(Span<byte> buf)
    {
        int full = buf.Length & ~15;
        Span<byte> keystream = stackalloc byte[16];
        if (full > 0)
        {
            byte[]? rented = null;
            Span<byte> tmp = full <= StackLimit ? stackalloc byte[full] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(full)).AsSpan(0, full);
            _aes.EncryptCfb(buf[..full], InitialVector, tmp, PaddingMode.None, 128);
            tmp.CopyTo(buf);
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
        if (full == buf.Length) return;
        _aes.EncryptEcb(full > 0 ? buf.Slice(full - 16, 16) : InitialVector, keystream, PaddingMode.None);
        for (int j = 0; full + j < buf.Length; j++) buf[full + j] ^= keystream[j];
    }

    /// <summary>CFB-128 decryption, the exact inverse of <see cref="CfbEncrypt"/>.</summary>
    private void CfbDecrypt(Span<byte> buf)
    {
        int full = buf.Length & ~15;
        Span<byte> keystream = stackalloc byte[16];
        // The trailing keystream comes from the last full CIPHERTEXT block, so take it
        // before the bulk decryption overwrites that block.
        if (full != buf.Length)
        {
            _aes.EncryptEcb(full > 0 ? buf.Slice(full - 16, 16) : InitialVector, keystream, PaddingMode.None);
        }
        if (full > 0)
        {
            byte[]? rented = null;
            Span<byte> tmp = full <= StackLimit ? stackalloc byte[full] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(full)).AsSpan(0, full);
            _aes.DecryptCfb(buf[..full], InitialVector, tmp, PaddingMode.None, 128);
            tmp.CopyTo(buf);
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
        for (int j = 0; full + j < buf.Length; j++) buf[full + j] ^= keystream[j];
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>
/// CRC32-IEEE, the checksum kcp-go puts in the crypt header. .NET ships
/// <c>System.IO.Hashing.Crc32</c> but only in a separate package; the table is
/// eight lines, so we keep the dependency tree where it is.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Computes the CRC32-IEEE checksum of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
