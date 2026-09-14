using GameServer.Net.Transport;

namespace GameServer.Tests.Net;

/// <summary>
/// Tests for the transport confidentiality posture the server reports at startup and on
/// <c>/status</c>.
/// </summary>
/// <remarks>
/// The case that matters most is <see cref="DefaultConfiguration_IsPlaintext_AndSaysSo"/>:
/// before this type the server warned about KCP-without-a-key and about a key set on TCP,
/// and said <b>nothing at all</b> about the default — TCP with no key, no encryption of any
/// kind. The configuration most likely to be deployed by accident was the one that produced
/// no signal.
/// </remarks>
public class TransportPostureTests
{
    [Fact]
    public void DefaultConfiguration_IsPlaintext_AndSaysSo()
    {
        // Exactly the stock container: transport unset (=> tcp), no key, wildcard bind.
        var p = TransportPosture.For(transport: null, transportKey: null, addr: ":9000");

        Assert.Equal(TransportKind.Tcp, p.Transport);
        Assert.False(p.Encrypted);
        Assert.False(p.Authenticated);
        Assert.False(p.KeyConfigured);
        Assert.Equal(TransportPosture.CipherNone, p.Cipher);
        Assert.Contains("PLAINTEXT", p.Summary);
        Assert.True(p.BindsBeyondLoopback);
        Assert.Contains("readable off-host", p.Summary);
    }

    [Fact]
    public void KeyOnTcp_IsReportedAsIgnored_NotAsEncryption()
    {
        // The configuration most easily mistaken for working encryption: a key is set, and
        // it does nothing at all.
        var p = TransportPosture.For(TransportKind.Tcp, "00112233445566778899aabbccddeeff", ":9000");

        Assert.True(p.KeyConfigured);
        Assert.False(p.Encrypted);
        Assert.True(p.KeyIgnored);
        Assert.Contains("IGNORED", p.Summary);
    }

    [Fact]
    public void KcpWithoutKey_IsPlaintext()
    {
        var p = TransportPosture.For(TransportKind.Kcp, "", ":9000");

        Assert.Equal(TransportKind.Kcp, p.Transport);
        Assert.False(p.Encrypted);
        Assert.False(p.KeyConfigured);
        Assert.Contains("PLAINTEXT", p.Summary);
        Assert.Contains(TransportKind.KeyEnvVar, p.Summary);
    }

    [Fact]
    public void KcpWithKey_IsEncrypted_ButNotAuthenticated()
    {
        var p = TransportPosture.For(TransportKind.Kcp, "00112233445566778899aabbccddeeff", ":9000");

        Assert.True(p.Encrypted);
        Assert.Equal(TransportPosture.CipherAesCfb, p.Cipher);
        Assert.False(p.KeyIgnored);

        // The whole reason these are two fields. AES-CFB with a CRC32 is confidentiality
        // without integrity: the CRC is linear and is not a MAC, so a modified packet is
        // not detectable. If this ever starts asserting true, an AEAD landed and the claim
        // must be re-earned rather than assumed.
        Assert.False(p.Authenticated);
        Assert.Contains("NOT AUTHENTICATED", p.Summary);
    }

    /// <summary>
    /// Whitespace is not a key. A variable set to spaces by a templating mistake must not
    /// read as encryption configured.
    /// </summary>
    [Fact]
    public void BlankKey_DoesNotCountAsConfigured()
    {
        var p = TransportPosture.For(TransportKind.Kcp, "   ", ":9000");

        Assert.False(p.KeyConfigured);
        Assert.False(p.Encrypted);
    }

    [Theory]
    // Loopback: unencrypted here is a local-dev choice, not an exposure.
    [InlineData("127.0.0.1:9000", false)]
    [InlineData("localhost:9000", false)]
    [InlineData("[::1]:9000", false)]
    // Wildcard binds accept on every interface. This is the shape the container images
    // use, so treating "no host part" as safe would exempt exactly the deployments the
    // reporting exists for.
    [InlineData(":9000", true)]
    [InlineData("0.0.0.0:9000", true)]
    [InlineData("[::]:9000", true)]
    [InlineData("10.0.0.4:9000", true)]
    // Unparseable is treated as exposed: the honest default for a security posture is the
    // pessimistic one.
    [InlineData("not-an-address", true)]
    [InlineData("", true)]
    public void BindScope_IsClassifiedPessimistically(string addr, bool beyondLoopback)
    {
        var p = TransportPosture.For(TransportKind.Tcp, null, addr);
        Assert.Equal(beyondLoopback, p.BindsBeyondLoopback);
    }

    /// <summary>
    /// An encrypted listener bound beyond loopback is not scolded for it — the off-host
    /// note belongs only to traffic anyone can read.
    /// </summary>
    [Fact]
    public void EncryptedListener_IsNotWarnedAboutItsBindAddress()
    {
        var p = TransportPosture.For(TransportKind.Kcp, "00112233445566778899aabbccddeeff", "0.0.0.0:9000");

        Assert.True(p.BindsBeyondLoopback);
        Assert.DoesNotContain("readable off-host", p.Summary);
    }
}
