using System.Net;
using System.Text;
using GameServer.Net.Transport;
using GameServer.Tests.Infrastructure;
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
    [InlineData("kcp", "kcp")]
    [InlineData("KCP", "kcp")]
    [InlineData("  tcp  ", "tcp")]
    // The empty string no longer means anything — in particular it no longer means TCP.
    // Normalize leaves it empty; only the startup flag resolution treats "unset" as kcp.
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_TrimsAndLowercases_AndNeverInventsTcp(string? input, string expected)
    {
        Assert.Equal(expected, TransportKind.Normalize(input));
    }

    [Theory]
    [InlineData("kcp", true)]
    [InlineData(" KCP ", true)]
    // Empty is not a transport: on the wire and in the registry it is a hard error.
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("tcp", false)]
    [InlineData("udp", false)]
    [InlineData("quic", false)]
    public void IsValid_AcceptsOnlyKcp(string? kind, bool expected)
    {
        Assert.Equal(expected, TransportKind.IsValid(kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kcp")]
    [InlineData("KCP")]
    public void ResolveConfigured_UnsetOrKcp_IsKcp(string? configured)
    {
        Assert.True(TransportKind.ResolveConfigured(configured, out string resolved, out string? error));
        Assert.Equal(TransportKind.Kcp, resolved);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("TCP")]
    [InlineData("udp")]
    [InlineData("websocket")]
    public void ResolveConfigured_AnythingElse_IsFatal_WithTheContractMessage(string configured)
    {
        Assert.False(TransportKind.ResolveConfigured(configured, out _, out string? error));
        // The exact text is part of the cross-repo contract (ADR-32, backend/docs/NETWORKING.md).
        Assert.Equal($"GAMESERVER_TRANSPORT={configured} is not supported: realtime gameplay is KCP/UDP only", error);
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

        // "Actually encrypted", asserted where it can be. A ciphertext equals its plaintext
        // whenever every keystream byte over it is zero; Seal draws a fresh nonce per packet,
        // so for ONE byte that is 1 run in 256 -- this assertion failed CI exactly that way
        // (payloadLength 1: expected not [0], actual [0]) on a PR that touched no crypto.
        // From 8 bytes the coincidence is 2^-64, so a single comparison means something.
        if (payloadLength >= 8) Assert.NotEqual(payload, packet[KcpCrypto.HeaderSize..]);

        var opened = crypto.Open(packet);
        Assert.Equal(payload, opened.ToArray());

        // Below 8 bytes one sample proves nothing, so take 64. A Seal that did not encrypt
        // returns the plaintext every time; a working one does so in all 64 with probability
        // 256^-64 at one byte. This keeps the short payloads covered instead of exempt.
        if (payloadLength is > 0 and < 8)
        {
            int unchanged = 0;
            for (int n = 0; n < 64; n++)
            {
                var again = new byte[KcpCrypto.HeaderSize + payloadLength];
                payload.CopyTo(again.AsSpan(KcpCrypto.HeaderSize));
                crypto.Seal(again);
                if (again.AsSpan(KcpCrypto.HeaderSize).SequenceEqual(payload)) unchanged++;
            }
            Assert.True(unchanged < 64, "64 seals of a short payload all returned the plaintext: Seal is not encrypting");
        }
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

    [Theory]
    [InlineData("quic")]
    [InlineData("tcp")]   // removed as a gameplay transport: no listener is ever built for it
    [InlineData("")]      // empty no longer defaults to anything
    public void Factory_RejectsEverythingButKcp(string kind)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TransportFactory.Listen(kind, "127.0.0.1:0", "", NullLogger.Instance));
        Assert.Contains("KCP/UDP only", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(TestKeyHex)]
    public async Task Factory_KcpListenerAcceptsAndStreams(string key)
    {
        using var listener = TransportFactory.Listen("kcp", "127.0.0.1:0", key, NullLogger.Instance);
        Assert.Equal("kcp", listener.Kind);

        var (_, port) = TransportFactory.ParseAddr(listener.LocalEndPoint);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = listener.AcceptAsync(cts.Token);
        using var client = new KcpTestClient(key);
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);

        using var accepted = await accept;
        await client.GetStream().WriteAsync("ping"u8.ToArray(), cts.Token);

        var buf = new byte[4];
        int n = await accepted.Stream.ReadAsync(buf, cts.Token);
        Assert.Equal("ping", Encoding.UTF8.GetString(buf, 0, n));
        Assert.Equal(1, listener.Stats.SessionsCreated);
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
        // Zero bytes is EOF, so Connection's read loop treats a closed KCP session like
        // any ended stream.
        Assert.Equal(0, await stream.ReadAsync(buf, cts.Token));
    }
}
