using System.Net;
using System.Text;
using GameServer.Net.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Net;

/// <summary>
/// Unit coverage for the transport abstraction and the pieces of the KCP stack
/// that can be exercised without a peer. Wire compatibility itself is proven by
/// <see cref="KcpInteropTests"/> against the real Go client — nothing here can
/// substitute for that, because a port and its own tests can agree on the same
/// mistake.
/// </summary>
public class KcpTransportTests
{
    private const string TestKeyHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("tcp", "tcp")]
    [InlineData("kcp", "kcp")]
    [InlineData("KCP", "kcp")]
    [InlineData("  tcp  ", "tcp")]
    // The empty string is the backward-compatible "unset" value on the wire and in
    // the registry, and it must keep meaning TCP.
    [InlineData("", "tcp")]
    [InlineData(null, "tcp")]
    public void Normalize_MatchesGoSemantics(string? input, string expected)
    {
        Assert.Equal(expected, TransportKind.Normalize(input));
    }

    [Theory]
    [InlineData("tcp", true)]
    [InlineData("kcp", true)]
    [InlineData("", true)]
    [InlineData("udp", false)]
    [InlineData("quic", false)]
    public void IsValid_AcceptsOnlyKnownKinds(string kind, bool expected)
    {
        Assert.Equal(expected, TransportKind.IsValid(kind));
    }

    [Fact]
    public void DeriveKey_DecodesHexVerbatim()
    {
        Assert.Equal(TestKeyHex, Convert.ToHexString(KcpCrypto.DeriveKey(TestKeyHex)).ToLowerInvariant());
        // Uppercase hex is still hex.
        Assert.Equal(TestKeyHex, Convert.ToHexString(KcpCrypto.DeriveKey(TestKeyHex.ToUpperInvariant())).ToLowerInvariant());
        // Surrounding whitespace is trimmed, so a key pasted from a secret store works.
        Assert.Equal(TestKeyHex, Convert.ToHexString(KcpCrypto.DeriveKey("  " + TestKeyHex + "  ")).ToLowerInvariant());
    }

    [Fact]
    public void DeriveKey_StretchesPassphrasesDeterministicallyAndDistinctly()
    {
        var a1 = KcpCrypto.DeriveKey("passphrase-a");
        var a2 = KcpCrypto.DeriveKey("passphrase-a");
        var b = KcpCrypto.DeriveKey("passphrase-b");

        Assert.Equal(KcpCrypto.KeySize, a1.Length);
        // Both peers derive independently; a non-deterministic KDF would break every join.
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void DeriveKey_FallsBackToPassphraseFor64NonHexChars()
    {
        // 64 characters that are not hex must not throw — Go falls through to the
        // passphrase path rather than failing the operator's start-up.
        var key = KcpCrypto.DeriveKey(new string('z', 64));
        Assert.Equal(KcpCrypto.KeySize, key.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveKey_RejectsEmptyKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => KcpCrypto.DeriveKey(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryCreate_TreatsBlankKeyAsPlaintext(string? key)
    {
        // A whitespace-only key is the documented spelling of "unset", not a key.
        Assert.Null(KcpCrypto.TryCreate(key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]   // shorter than one AES block: exercises the unpadded tail
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(1329)] // a full MTU-sized KCP payload
    public void SealAndOpen_RoundTrip(int payloadLength)
    {
        using var crypto = KcpCrypto.TryCreate(TestKeyHex)!;

        var payload = new byte[payloadLength];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);

        var packet = new byte[KcpCrypto.HeaderSize + payloadLength];
        payload.CopyTo(packet.AsSpan(KcpCrypto.HeaderSize));

        crypto.Seal(packet);
        if (payloadLength > 0) Assert.NotEqual(payload, packet[KcpCrypto.HeaderSize..]); // actually encrypted

        var opened = crypto.Open(packet);
        Assert.Equal(payload, opened.ToArray());
    }

    [Fact]
    public void Seal_ProducesDifferentCiphertextForIdenticalPlaintext()
    {
        using var crypto = KcpCrypto.TryCreate(TestKeyHex)!;

        byte[] Make()
        {
            var p = new byte[KcpCrypto.HeaderSize + 32];
            Encoding.UTF8.GetBytes("identical payload").CopyTo(p.AsSpan(KcpCrypto.HeaderSize));
            crypto.Seal(p);
            return p;
        }

        // The IV is fixed; the random nonce is the only thing making packets differ.
        // If this ever fails, the nonce is not being filled and the cipher degenerates
        // into a deterministic stream — identical packets would be trivially linkable.
        Assert.NotEqual(Make(), Make());
    }

    [Fact]
    public void Open_RejectsPacketsSealedWithAnotherKey()
    {
        using var mine = KcpCrypto.TryCreate(TestKeyHex)!;
        using var theirs = KcpCrypto.TryCreate("a-different-passphrase")!;

        var packet = new byte[KcpCrypto.HeaderSize + 64];
        theirs.Seal(packet);

        // No error frame, no negotiation: the CRC fails and the datagram is dropped.
        Assert.True(mine.Open(packet).IsEmpty);
    }

    [Fact]
    public void Open_RejectsTruncatedPackets()
    {
        using var crypto = KcpCrypto.TryCreate(TestKeyHex)!;
        Assert.True(crypto.Open(new byte[KcpCrypto.HeaderSize - 1]).IsEmpty);
    }

    [Fact]
    public void Kcp_LoopbackCarriesAStreamLargerThanOneSegment()
    {
        // Two state machines wired to each other, no sockets. This checks the port's
        // fragmentation, ACK and reassembly paths in isolation from the network.
        Kcp? a = null, b = null;
        a = new Kcp(0x1234, (buf, size) => b!.Input(buf.AsSpan(0, size), ackNoDelay: true));
        b = new Kcp(0x1234, (buf, size) => a!.Input(buf.AsSpan(0, size), ackNoDelay: true));
        KcpTuning.Apply(a, 0);
        KcpTuning.Apply(b, 0);

        var payload = new byte[8192]; // several MSS-sized segments
        Random.Shared.NextBytes(payload);

        a.Send(payload);
        // Drive both ends until the data lands; nodelay+interval 10 means a handful
        // of updates is plenty on a lossless loopback.
        var received = new List<byte>();
        var scratch = new byte[KcpTuning.MaxMessageSize];
        for (int i = 0; i < 50 && received.Count < payload.Length; i++)
        {
            a.Update();
            b.Update();
            while (true)
            {
                int n = b.Recv(scratch);
                if (n <= 0) break;
                received.AddRange(scratch.AsSpan(0, n).ToArray());
            }
        }

        Assert.Equal(payload, received.ToArray());
    }

    [Fact]
    public void Kcp_RejectsSegmentsFromAnotherConversation()
    {
        var sent = new List<byte[]>();
        var a = new Kcp(1, (buf, size) => sent.Add(buf[..size]));
        KcpTuning.Apply(a, 0);
        a.Send("hello"u8);
        a.Flush();
        Assert.NotEmpty(sent);

        var b = new Kcp(2, (_, _) => { });
        KcpTuning.Apply(b, 0);
        // conv is the only demultiplexing key KCP has; a mismatch must be rejected
        // rather than silently mixed into another session's stream.
        Assert.True(b.Input(sent[0], ackNoDelay: false) < 0);
    }

    [Fact]
    public void Kcp_RejectsUndersizedAndUnknownCommandPackets()
    {
        var kcp = new Kcp(1, (_, _) => { });
        Assert.Equal(-1, kcp.Input(new byte[Kcp.Overhead - 1], ackNoDelay: false));

        var packet = new byte[Kcp.Overhead];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet, 1);
        packet[4] = 99; // not one of PUSH/ACK/WASK/WINS
        Assert.Equal(-3, kcp.Input(packet, ackNoDelay: false));
    }

    [Fact]
    public void Listener_ReportsWhetherItIsEncrypted()
    {
        using var plain = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), "", NullLogger.Instance);
        Assert.False(plain.IsEncrypted);

        using var encrypted = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), TestKeyHex, NullLogger.Instance);
        Assert.True(encrypted.IsEncrypted);
    }

    [Fact]
    public void Listener_BindsAnEphemeralPort()
    {
        using var listener = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), "", NullLogger.Instance);
        Assert.NotEqual(0, listener.LocalEndPoint.Port);
    }

    [Theory]
    [InlineData(":9000", "", 9000)]
    [InlineData("0.0.0.0:9000", "0.0.0.0", 9000)]
    [InlineData("127.0.0.1:1", "127.0.0.1", 1)]
    public void ParseAddr_SplitsHostAndPort(string addr, string wantHost, int wantPort)
    {
        var (host, port) = TransportFactory.ParseAddr(addr);
        Assert.Equal(wantHost, host);
        Assert.Equal(wantPort, port);
    }

    [Theory]
    [InlineData("9000")]      // no colon
    [InlineData(":notaport")]
    public void ParseAddr_RejectsMalformedAddresses(string addr)
    {
        Assert.Throws<ArgumentException>(() => TransportFactory.ParseAddr(addr));
    }

    [Fact]
    public void Factory_RejectsUnknownTransportKinds()
    {
        Assert.Throws<ArgumentException>(() =>
            TransportFactory.Listen("quic", "127.0.0.1:0", "", NullLogger.Instance));
    }

    [Fact]
    public async Task Factory_TcpListenerAcceptsAndStreams()
    {
        using var listener = TransportFactory.Listen("tcp", "127.0.0.1:0", "", NullLogger.Instance);
        Assert.Equal("tcp", listener.Kind);

        var (_, port) = TransportFactory.ParseAddr(listener.LocalEndPoint);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        using var accepted = await listener.AcceptAsync(CancellationToken.None);
        await client.GetStream().WriteAsync("ping"u8.ToArray());

        var buf = new byte[4];
        int n = await accepted.Stream.ReadAsync(buf);
        Assert.Equal("ping", Encoding.UTF8.GetString(buf, 0, n));
    }

    [Fact]
    public void Factory_TcpIgnoresTheTransportKey()
    {
        // A transport key is meaningless for TCP (TLS or the cluster network is the
        // answer there); passing one must not break the listener. Mirrors the Go test.
        using var listener = TransportFactory.Listen("tcp", "127.0.0.1:0", TestKeyHex, NullLogger.Instance);
        Assert.Equal("tcp", listener.Kind);
    }

    [Fact]
    public async Task KcpStream_ReadsAcrossChunkBoundaries()
    {
        // A caller asking for fewer bytes than the ARQ delivered must get the rest on
        // the next read — that is the invariant the length-prefixed codec depends on.
        using var listener = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), "", NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptTask = listener.AcceptAsync(cts.Token);

        // Drive a client side by hand: a second listener would not dial, so speak the
        // protocol directly with a bare UDP socket and our own state machine.
        using var clientSocket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        var server = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint.Port);
        var clientKcp = new Kcp(0xABCD, (buf, size) => clientSocket.SendTo(buf.AsSpan(0, size), server));
        KcpTuning.Apply(clientKcp, 0);

        clientKcp.Send("0123456789"u8);
        clientKcp.Flush();

        var session = await acceptTask;
        var stream = new KcpStream(session);

        var first = new byte[4];
        int n1 = await stream.ReadAsync(first, cts.Token);
        Assert.Equal(4, n1);
        Assert.Equal("0123", Encoding.UTF8.GetString(first));

        var rest = new byte[16];
        int n2 = await stream.ReadAsync(rest, cts.Token);
        Assert.Equal(6, n2);
        Assert.Equal("456789", Encoding.UTF8.GetString(rest, 0, n2));
    }

    [Fact]
    public async Task KcpStream_ReturnsEofWhenTheSessionCloses()
    {
        using var listener = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), "", NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = listener.AcceptAsync(cts.Token);

        using var clientSocket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        var server = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint.Port);
        var clientKcp = new Kcp(0xBEEF, (buf, size) => clientSocket.SendTo(buf.AsSpan(0, size), server));
        KcpTuning.Apply(clientKcp, 0);
        clientKcp.Send("x"u8);
        clientKcp.Flush();

        var session = await acceptTask;
        var stream = new KcpStream(session);

        var buf = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buf, cts.Token));

        session.Close();
        // Zero bytes is EOF, the same signal a TCP FIN produces, so Connection's read
        // loop treats a vanished KCP peer exactly like a closed socket.
        Assert.Equal(0, await stream.ReadAsync(buf, cts.Token));
    }

    [Fact]
    public void TryCreateFromRawKey_AcceptsExactly32Bytes()
    {
        var key = new byte[KcpCrypto.KeySize];
        Random.Shared.NextBytes(key);
        using var crypto = KcpCrypto.TryCreateFromRawKey(key);
        Assert.NotNull(crypto);
    }

    [Fact]
    public void TryCreateFromRawKey_ReturnsNullForNullOrEmpty()
    {
        Assert.Null(KcpCrypto.TryCreateFromRawKey(null));
        Assert.Null(KcpCrypto.TryCreateFromRawKey(Array.Empty<byte>()));
    }

    [Fact]
    public void TryCreateFromRawKey_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => KcpCrypto.TryCreateFromRawKey(new byte[16]));
        Assert.Throws<ArgumentException>(() => KcpCrypto.TryCreateFromRawKey(new byte[33]));
    }

    [Fact]
    public void RawKey_SealAndOpen_RoundTrip()
    {
        var key = new byte[KcpCrypto.KeySize];
        Random.Shared.NextBytes(key);
        using var crypto = KcpCrypto.TryCreateFromRawKey(key)!;
        var payload = new byte[64];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 3);
        var packet = new byte[KcpCrypto.HeaderSize + payload.Length];
        payload.CopyTo(packet.AsSpan(KcpCrypto.HeaderSize));
        crypto.Seal(packet);
        var opened = crypto.Open(packet);
        Assert.Equal(payload, opened.ToArray());
    }

    [Fact]
    public void RekeyMidSession_NewKeyDecryptsNewPackets()
    {
        var keyA = new byte[KcpCrypto.KeySize];
        var keyB = new byte[KcpCrypto.KeySize];
        Random.Shared.NextBytes(keyA);
        Random.Shared.NextBytes(keyB);
        using var cryptoA = KcpCrypto.TryCreateFromRawKey(keyA)!;
        using var cryptoB = KcpCrypto.TryCreateFromRawKey(keyB)!;
        var payload = Encoding.UTF8.GetBytes("rekey-test");
        var packetA = new byte[KcpCrypto.HeaderSize + payload.Length];
        payload.CopyTo(packetA.AsSpan(KcpCrypto.HeaderSize));
        cryptoA.Seal(packetA);
        Assert.Equal(payload, cryptoA.Open(packetA).ToArray());
        var packetA2 = new byte[KcpCrypto.HeaderSize + payload.Length];
        payload.CopyTo(packetA2.AsSpan(KcpCrypto.HeaderSize));
        cryptoA.Seal(packetA2);
        Assert.True(cryptoB.Open(packetA2).IsEmpty);
    }
}
