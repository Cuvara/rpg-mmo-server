using System.Text.Json;
using GameServer.Net.Security;
using GameServer.Observability;

namespace GameServer.Tests.Net;

/// <summary>
/// Tests for the per-session key: that the two implementations agree, that it is really
/// per-session, and above all that it cannot leak through a surface that renders it.
/// </summary>
public class SessionKeyTests
{
    private const string Secret = "join-secret-for-tests";

    /// <summary>
    /// <b>The cross-implementation vector.</b> Two implementations that each round-trip
    /// against themselves can still disagree with each other, and a disagreement here
    /// produces no error anywhere — the client encrypts with one key, the server decrypts
    /// with another, and the session simply never forms. This value is produced by
    /// <c>shared/sessionkey</c>'s <c>TestGoldenVector</c> in Go; the two must move together
    /// or not at all.
    /// </summary>
    [Fact]
    public void GoldenVector_MatchesTheGoImplementation()
    {
        SessionKey key = SessionKey.Derive("golden-secret", "golden-jti");

        Assert.Equal(
            "3d79a9aa8d3284392d4d5cde4e968e30e2fb819689e5e0e38c51ec86e334176d",
            key.ToHex());
    }

    [Fact]
    public void Derivation_IsDeterministicAndPerSession()
    {
        SessionKey a = SessionKey.Derive(Secret, "jti-one");
        SessionKey again = SessionKey.Derive(Secret, "jti-one");
        SessionKey other = SessionKey.Derive(Secret, "jti-two");

        // Determinism is what lets both ends compute the key without transmitting it.
        Assert.Equal(a.ToHex(), again.ToHex());
        // Per-session is the entire point: a fresh jti per join means a fresh key.
        Assert.NotEqual(a.ToHex(), other.ToHex());
        Assert.Equal(SessionKey.Size, a.ToArray().Length);
    }

    [Fact]
    public void Derivation_DependsOnTheSecret()
    {
        Assert.NotEqual(
            SessionKey.Derive(Secret, "jti-one").ToHex(),
            SessionKey.Derive("a-different-secret", "jti-one").ToHex());
    }

    /// <summary>
    /// A rotation list must derive from the CURRENT entry — the one the gateway signs and
    /// derives with — or the two ends disagree for the length of the rotation.
    /// </summary>
    [Fact]
    public void Derivation_UsesTheCurrentKeyOfARotationList()
    {
        string single = SessionKey.Derive("current", "jti-one").ToHex();

        Assert.Equal(single, SessionKey.Derive("current,previous", "jti-one").ToHex());
        Assert.Equal(single, SessionKey.Derive("  current , previous ", "jti-one").ToHex());
    }

    /// <summary>
    /// No secret or no jti yields an empty key — and specifically NOT a weaker one. A
    /// derivation that invented a fallback would be a downgrade path, which is the failure
    /// this whole design is arranged to avoid.
    /// </summary>
    [Fact]
    public void Derivation_RefusesRatherThanFallingBack()
    {
        Assert.True(SessionKey.Derive(null, "jti").IsEmpty);
        Assert.True(SessionKey.Derive("", "jti").IsEmpty);
        Assert.True(SessionKey.Derive("   ", "jti").IsEmpty);
        Assert.True(SessionKey.Derive(Secret, null).IsEmpty);
        Assert.True(SessionKey.Derive(Secret, "").IsEmpty);
    }

    /// <summary>
    /// The realistic leak is not a deliberate log call — it is a value interpolated into a
    /// message by code that did not know it held a secret.
    /// </summary>
    [Fact]
    public void Key_NeverRendersItsMaterial()
    {
        SessionKey key = SessionKey.Derive(Secret, "jti-one");
        string material = key.ToHex();

        string interpolated = $"session key is {key}";
        string formatted = string.Format("{0}", key);
        string concatenated = "key=" + key;

        foreach ((string where, string rendered) in new[]
        {
            ("ToString", key.ToString()),
            ("interpolation", interpolated),
            ("string.Format", formatted),
            ("concatenation", concatenated),
        })
        {
            Assert.False(rendered.Contains(material, StringComparison.OrdinalIgnoreCase),
                $"{where} leaked the key material: {rendered}");
            Assert.Contains(SessionKey.Redacted, rendered);
        }
    }

    /// <summary>
    /// <c>/status</c> is the surface most likely to grow a field by accident later, and it
    /// is public on the metrics port. Serialise the real payload and require the key to be
    /// absent from it — not merely absent from the fields we remembered to check.
    /// </summary>
    [Fact]
    public void StatusPayload_CarriesNoKeyMaterial()
    {
        SessionKey key = SessionKey.Derive(Secret, "jti-one");
        string material = key.ToHex();

        var status = new ServerStatus
        {
            Ok = true,
            Transport = "kcp",
            TransportKeyConfigured = true,
            TransportEncrypted = true,
            TransportCipher = "aes-256-cfb",
            TransportPostureSummary = "ENCRYPTED but NOT AUTHENTICATED",
        };

        string json = JsonSerializer.Serialize(status);

        Assert.DoesNotContain(material, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session_key", json, StringComparison.OrdinalIgnoreCase);
        // The posture fields say WHETHER a key is in force; they must never say which.
        Assert.Contains("transport_key_configured", json);
    }
}
