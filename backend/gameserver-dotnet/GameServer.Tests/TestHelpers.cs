using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.GameLogic.Components;

namespace GameServer.Tests;

internal static class TestHelpers
{
    public static EntityState CreatePlayer(
        string id, float x = 0, float y = 0,
        int hp = 100, int atk = 10, int def = 5, float speed = 1.0f)
    {
        return new EntityState
        {
            Id = id,
            Type = "player",
            Position = new Vec2(x, y),
            Hp = hp,
            MaxHp = 100,
            Attack = atk,
            Defense = def,
            Speed = speed
        };
    }

    public static EntityState CreateMob(
        string id, float x, float y,
        int hp = 50, int atk = 8, int def = 3, float speed = 0.5f)
    {
        return new EntityState
        {
            Id = id,
            Type = "mob",
            Position = new Vec2(x, y),
            Hp = hp,
            MaxHp = 50,
            Attack = atk,
            Defense = def,
            Speed = speed
        };
    }

    /// <summary>
    /// Create a valid HS256 JWT for testing, including a unique JTI claim.
    /// </summary>
    public static string CreateTestJwt(string userId, string serverId, string secret, long? exp = null,
        string? jti = null)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["sid"] = serverId,
            // Callers that need to know the jti (the duplicate-login kick tests
            // match on it) pass one explicitly; everyone else gets a fresh one.
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (exp.HasValue)
        {
            payload["exp"] = exp.Value;
        }
        else
        {
            // Default: expires in 1 hour
            payload["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        }

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{headerB64}.{payloadB64}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var signatureB64 = Base64UrlEncode(signature);

        return $"{headerB64}.{payloadB64}.{signatureB64}";
    }

    /// <summary>
    /// Create an expired JWT for testing.
    /// </summary>
    public static string CreateExpiredJwt(string userId, string serverId, string secret)
    {
        var expiredAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        return CreateTestJwt(userId, serverId, secret, expiredAt);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
