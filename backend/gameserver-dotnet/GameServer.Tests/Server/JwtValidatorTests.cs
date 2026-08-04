using System.Security.Cryptography;
using System.Text;
using GameServer.Server;

namespace GameServer.Tests.Server;

public class JwtValidatorTests
{
    private const string TestSecret = "test-secret-key-for-jwt-validation-32b";
    private const string TestUserId = "user-123";
    private const string TestServerId = "server-abc";

    [Fact]
    public void Verify_ValidToken_ReturnsClaims()
    {
        var token = TestHelpers.CreateTestJwt(TestUserId, TestServerId, TestSecret);

        var claims = JwtValidator.Verify(token, TestSecret);

        Assert.NotNull(claims);
        Assert.Equal(TestUserId, claims.UserId);
        Assert.Equal(TestServerId, claims.ServerId);
    }

    [Fact]
    public void Verify_InvalidSignature_ReturnsNull()
    {
        var token = TestHelpers.CreateTestJwt(TestUserId, TestServerId, TestSecret);

        var claims = JwtValidator.Verify(token, "wrong-secret-key-completely-different");

        Assert.Null(claims);
    }

    [Fact]
    public void Verify_ExpiredToken_ReturnsNull()
    {
        var token = TestHelpers.CreateExpiredJwt(TestUserId, TestServerId, TestSecret);

        var claims = JwtValidator.Verify(token, TestSecret);

        Assert.Null(claims);
    }

    [Fact]
    public void Verify_MalformedToken_ReturnsNull()
    {
        Assert.Null(JwtValidator.Verify("not-a-jwt", TestSecret));
        Assert.Null(JwtValidator.Verify("", TestSecret));
        Assert.Null(JwtValidator.Verify("a.b", TestSecret));
        Assert.Null(JwtValidator.Verify("a.b.c.d", TestSecret));
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsNull()
    {
        var token = TestHelpers.CreateTestJwt(TestUserId, TestServerId, TestSecret);
        var parts = token.Split('.');

        // Tamper with the payload
        var payloadBytes = Convert.FromBase64String(
            parts[1].Replace('-', '+').Replace('_', '/').PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '='));
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        payloadJson = payloadJson.Replace(TestUserId, "hacker-999");
        var tamperedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

        var claims = JwtValidator.Verify(tamperedToken, TestSecret);
        Assert.Null(claims);
    }

    [Fact]
    public void Verify_NullOrEmptyToken_ReturnsNull()
    {
        Assert.Null(JwtValidator.Verify(null!, TestSecret));
        Assert.Null(JwtValidator.Verify("", TestSecret));
        Assert.Null(JwtValidator.Verify("   ", TestSecret));
    }

    [Fact]
    public void Verify_TokenExpiringNow_BehaviorDocumented()
    {
        // Token expiring at current time - behavior depends on implementation
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = TestHelpers.CreateTestJwt(TestUserId, TestServerId, TestSecret, exp: nowUnix);

        // Either valid (with leeway) or null (strict)
        _ = JwtValidator.Verify(token, TestSecret);
    }

    [Fact]
    public void Verify_FutureToken_IsValid()
    {
        var futureExp = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
        var token = TestHelpers.CreateTestJwt(TestUserId, TestServerId, TestSecret, exp: futureExp);

        var claims = JwtValidator.Verify(token, TestSecret);
        Assert.NotNull(claims);
    }

    [Fact]
    public void Verify_DifferentSecrets_DifferentResults()
    {
        var token = TestHelpers.CreateTestJwt(TestUserId, TestServerId, "secret-A-is-different-enough-32b");

        Assert.NotNull(JwtValidator.Verify(token, "secret-A-is-different-enough-32b"));
        Assert.Null(JwtValidator.Verify(token, "secret-B-is-different-enough-32b"));
    }
}
