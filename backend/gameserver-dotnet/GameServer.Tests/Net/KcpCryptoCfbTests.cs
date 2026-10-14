using System.Security.Cryptography;
using GameServer.Net.Transport;

namespace GameServer.Tests.Net;

/// <summary>
/// #449: <see cref="KcpCrypto"/> moved from one <c>EncryptEcb</c> call per 16-byte block to
/// bulk CFB. These tests pin the bytes to the textbook per-block form (the form kcp-go uses
/// and the old code implemented) at every length around block boundaries, in both
/// directions, so the speed-up cannot change the wire format. The Go interop tests cover
/// kcp-go itself where a Go toolchain is available.
/// </summary>
public class KcpCryptoCfbTests
{
    private const string Key = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private static readonly byte[] InitialVector =
        [167, 115, 79, 156, 18, 172, 27, 1, 164, 21, 242, 193, 252, 120, 230, 107];

    /// <summary>The per-block CFB-128 the server shipped before #449, kept as the reference.</summary>
    private static void ReferenceCfb(Aes aes, Span<byte> buf, bool encrypt)
    {
        Span<byte> tbl = stackalloc byte[16];
        Span<byte> next = stackalloc byte[16];
        aes.EncryptEcb(InitialVector, tbl, PaddingMode.None);
        int i = 0;
        for (; i + 16 <= buf.Length; i += 16)
        {
            var blk = buf.Slice(i, 16);
            if (encrypt)
            {
                for (int j = 0; j < 16; j++) blk[j] ^= tbl[j];
                aes.EncryptEcb(blk, next, PaddingMode.None);
            }
            else
            {
                aes.EncryptEcb(blk, next, PaddingMode.None);
                for (int j = 0; j < 16; j++) blk[j] ^= tbl[j];
            }
            next.CopyTo(tbl);
        }
        for (int j = 0; i + j < buf.Length; j++) buf[i + j] ^= tbl[j];
    }

    public static IEnumerable<object[]> Lengths()
    {
        // Every KCP body length from empty to past three blocks, then the large sizes
        // (the MTU and beyond the stack limit) that real snapshots use.
        for (int n = 0; n <= 52; n++) yield return [n];
        foreach (int n in new[] { 1356, 1376, 1400, 1480, 3000 }) yield return [n];
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Seal_MatchesThePerBlockCfb(int bodyLength)
    {
        using var crypto = KcpCrypto.TryCreate(Key)!;
        using var aes = Aes.Create();
        aes.Key = KcpCrypto.DeriveKey(Key);

        var body = new byte[bodyLength];
        new Random(bodyLength).NextBytes(body);
        var packet = new byte[KcpCrypto.HeaderSize + bodyLength];
        body.CopyTo(packet, KcpCrypto.HeaderSize);

        crypto.Seal(packet);

        // Decrypt with the reference: the body and the CRC come back intact.
        var plain = (byte[])packet.Clone();
        ReferenceCfb(aes, plain, encrypt: false);
        Assert.Equal(body, plain[KcpCrypto.HeaderSize..]);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Open_ReadsWhatThePerBlockCfbWrote(int bodyLength)
    {
        using var crypto = KcpCrypto.TryCreate(Key)!;
        using var aes = Aes.Create();
        aes.Key = KcpCrypto.DeriveKey(Key);

        var body = new byte[bodyLength];
        new Random(1000 + bodyLength).NextBytes(body);
        var packet = new byte[KcpCrypto.HeaderSize + bodyLength];
        RandomNumberGenerator.Fill(packet.AsSpan(0, KcpCrypto.NonceSize));
        body.CopyTo(packet, KcpCrypto.HeaderSize);
        // Same layout as Seal: crc32-IEEE of the body, little endian, after the nonce.
        using (var sealer = KcpCrypto.TryCreate(Key)!)
        {
            var probe = (byte[])packet.Clone();
            sealer.Seal(probe);
            ReferenceCfb(aes, probe, encrypt: false);
            probe.AsSpan(KcpCrypto.NonceSize, KcpCrypto.CrcSize).CopyTo(packet.AsSpan(KcpCrypto.NonceSize));
        }

        ReferenceCfb(aes, packet, encrypt: true);
        var opened = crypto.Open(packet);

        Assert.Equal(bodyLength, opened.Length);
        Assert.Equal(body, opened.ToArray());
    }

    [Fact]
    public void SealThenOpen_RoundTrips_AndAWrongKeyFailsClosed()
    {
        using var crypto = KcpCrypto.TryCreate(Key)!;
        using var other = KcpCrypto.TryCreate(new string('e', 64))!;
        var body = new byte[1376];
        new Random(449).NextBytes(body);
        var packet = new byte[KcpCrypto.HeaderSize + body.Length];
        body.CopyTo(packet, KcpCrypto.HeaderSize);
        crypto.Seal(packet);

        var copy = (byte[])packet.Clone();
        Assert.Equal(body, crypto.Open(packet).ToArray());
        Assert.True(other.Open(copy).IsEmpty, "a wrong key must fail the CRC");
    }
}
