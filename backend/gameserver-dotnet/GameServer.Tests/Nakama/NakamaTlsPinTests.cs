using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GameServer.Nakama;

namespace GameServer.Tests.Nakama;

/// <summary>
/// The pin for the meta hop (ADR-24). <b>These tests are mostly about refusal</b>, because
/// a pin that accepts is trivially observable in a working deploy and a pin that accepts too
/// much is not observable at all: a handler returning true for everything looks exactly like
/// a handler that works, for as long as only the right certificate is ever presented. So the
/// certificate that must be rejected is generated here, presented here, and asserted on.
/// </summary>
public class NakamaTlsPinTests
{
    private static X509Certificate2 SelfSigned(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void PinnedCertificateIsAccepted()
    {
        using var cert = SelfSigned("nakama.test");
        Assert.True(NakamaTlsPin.Matches(cert.RawData, cert.RawData));
    }

    [Fact]
    public void ADifferentCertificateIsRejected()
    {
        using var pinned = SelfSigned("nakama.test");
        using var impostor = SelfSigned("nakama.test"); // same subject, different key

        Assert.False(NakamaTlsPin.Matches(pinned.RawData, impostor.RawData));
    }

    [Fact]
    public void AByteFlippedCertificateIsRejected()
    {
        using var cert = SelfSigned("nakama.test");
        byte[] tampered = (byte[])cert.RawData.Clone();
        tampered[^1] ^= 0x01;

        Assert.False(NakamaTlsPin.Matches(cert.RawData, tampered));
    }

    [Fact]
    public void AnEmptyOrMissingPinMatchesNothing()
    {
        using var cert = SelfSigned("nakama.test");

        Assert.False(NakamaTlsPin.Matches(null, cert.RawData));
        Assert.False(NakamaTlsPin.Matches(Array.Empty<byte>(), cert.RawData));
        Assert.False(NakamaTlsPin.Matches(cert.RawData, null));
        Assert.False(NakamaTlsPin.Matches(cert.RawData, Array.Empty<byte>()));
    }

    [Fact]
    public void ThereIsNoAcceptAnythingHandler()
    {
        Assert.Throws<ArgumentException>(() => NakamaTlsPin.CreatePinnedHandler(Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => NakamaTlsPin.CreatePinnedHandler(null!));
    }

    [Fact]
    public void PemRoundTripsToTheSameDer()
    {
        using var cert = SelfSigned("nakama.test");
        string pem =
            "-----BEGIN CERTIFICATE-----\n" +
            Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks) +
            "\n-----END CERTIFICATE-----\n";

        Assert.Equal(cert.RawData, NakamaTlsPin.ParsePem(pem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a certificate at all")]
    [InlineData("-----BEGIN CERTIFICATE-----\nnot base64!!\n-----END CERTIFICATE-----")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIB\n")] // no END line
    public void AnUnusablePemThrowsRatherThanPinningNothing(string pem)
    {
        Assert.Throws<ArgumentException>(() => NakamaTlsPin.ParsePem(pem));
    }

    [Fact]
    public void FingerprintDistinguishesTwoCertificates()
    {
        using var a = SelfSigned("nakama.test");
        using var b = SelfSigned("nakama.test");

        Assert.NotEqual(NakamaTlsPin.Fingerprint(a), NakamaTlsPin.Fingerprint(b));
        Assert.Equal(NakamaTlsPin.Fingerprint(a), NakamaTlsPin.Fingerprint(a.RawData));
        Assert.Equal(16, NakamaTlsPin.Fingerprint(a).Length);
    }
}
