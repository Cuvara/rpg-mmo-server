using System.Text.Json;
using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>The same fixed-clock JWT contract is exercised by Go and C#.</summary>
public class JwtGoldenVectorTests
{
    public static IEnumerable<object[]> Vectors()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? fixture = null;
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "backend", "shared", "testdata", "jwt", "vectors.json");
            if (File.Exists(candidate)) { fixture = candidate; break; }
            directory = directory.Parent;
        }
        if (fixture == null)
            throw new FileNotFoundException("Shared JWT vectors not found above " + AppContext.BaseDirectory);

        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        var root = document.RootElement;
        Assert.Equal("rpg-mmo.jwt-vectors/1", root.GetProperty("schema").GetString());
        Assert.Equal(JwtValidator.ClockSkewSeconds, root.GetProperty("skew_seconds").GetInt32());
        long now = root.GetProperty("now").GetInt64();
        string secret = root.GetProperty("secret").GetString()!;
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        {
            yield return new object[]
            {
                vector.GetProperty("name").GetString()!,
                vector.GetProperty("rule").GetString()!,
                vector.GetProperty("token").GetString()!,
                vector.TryGetProperty("secret", out var overrideSecret) ? overrideSecret.GetString()! : secret,
                now,
                vector.GetProperty("verdict").GetString()!,
                vector.TryGetProperty("claims", out var claims) ? claims.GetRawText() : "null"
            };
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Verify_MatchesSharedContract(string name, string rule, string token, string secret,
        long now, string verdict, string expectedClaims)
    {
        var claims = JwtValidator.Verify(token, secret, now, out var status);
        string context = $"{name} ({rule}): expected {verdict}, actual {status}";
        switch (verdict)
        {
            case "accept":
                Assert.True(claims != null && status == JwtValidator.VerifyStatus.Ok, context);
                using (var document = JsonDocument.Parse(expectedClaims))
                {
                    var expected = document.RootElement;
                    Assert.Equal(new JwtValidator.JwtClaims(
                        expected.GetProperty("sub").GetString()!,
                        expected.GetProperty("sid").GetString()!,
                        expected.GetProperty("exp").GetInt64(),
                        expected.GetProperty("jti").GetString()!,
                        expected.GetProperty("cid").GetString()!,
                        expected.GetProperty("gid").GetString()!), claims);
                }
                break;
            case "refuse-expired":
                Assert.Null(claims);
                Assert.True(status == JwtValidator.VerifyStatus.Expired, context);
                break;
            case "refuse":
                Assert.Null(claims);
                Assert.True(status is JwtValidator.VerifyStatus.Invalid or JwtValidator.VerifyStatus.BadSignature, context);
                break;
            default:
                Assert.Fail("Unknown shared JWT verdict: " + verdict);
                break;
        }
    }
}
