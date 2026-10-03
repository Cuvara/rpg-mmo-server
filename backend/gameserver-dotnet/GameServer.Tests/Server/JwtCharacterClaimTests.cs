using System.Security.Cryptography;
using System.Text;
using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>
/// ADR-31 <c>cid</c> claim on the game-server side of the join-token contract: read when
/// present, empty when absent (every pre-protocol-3 token), carried through the keyring.
/// The payloads are written by hand in the exact shape Go's <c>shared/jwt</c> emits
/// (<c>cid</c> omitted when empty), so this pins the wire claim name, not a C# round trip.
/// </summary>
public class JwtCharacterClaimTests
{
    private const string Secret = "test-secret-key-for-jwt-validation-32b";

    private static string Sign(string payloadJson, string secret = Secret)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string header = B64(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        string payload = B64(Encoding.UTF8.GetBytes(payloadJson));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        string sig = B64(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{sig}";
    }

    private static long Exp => DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

    [Theory]
    [InlineData("""{"sub":"u1","sid":"gs-1","jti":"j1","cid":"char-7","iat":0,"exp":EXP}""", "char-7")]
    [InlineData("""{"sub":"u1","sid":"gs-1","jti":"j1","iat":0,"exp":EXP}""", "")]
    [InlineData("""{"sub":"u1","sid":"gs-1","jti":"j1","cid":"","iat":0,"exp":EXP}""", "")]
    public void Verify_ReadsCidClaim_AndToleratesAbsence(string payloadTemplate, string expectedCid)
    {
        string token = Sign(payloadTemplate.Replace("EXP", Exp.ToString()));

        var claims = JwtValidator.Verify(token, Secret);

        Assert.NotNull(claims);
        Assert.Equal("u1", claims!.UserId);
        Assert.Equal("gs-1", claims.ServerId);
        Assert.Equal("j1", claims.Jti);
        Assert.Equal(expectedCid, claims.CharacterId);
    }

    [Fact]
    public void Keyring_CarriesCidThroughRotation()
    {
        string token = Sign($$"""{"sub":"u1","sid":"gs-1","jti":"j2","cid":"char-9","iat":0,"exp":{{Exp}}}""", "previous-secret");
        var keyring = JwtKeyring.Parse($"current-secret,previous-secret");

        var claims = keyring.Verify(token);

        Assert.NotNull(claims);
        Assert.Equal("char-9", claims!.CharacterId);
    }

    [Fact]
    public void JwtClaims_PositionalConstruction_DefaultsCidToEmpty()
    {
        // Existing call sites build JwtClaims with four arguments; the new parameter must
        // default so they keep compiling and mean "default character".
        var claims = new JwtValidator.JwtClaims("u", "s", 0, "j");
        Assert.Equal("", claims.CharacterId);
    }
}
