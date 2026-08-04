using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameServer.Server;

/// <summary>
/// Lightweight HS256 JWT validator. No external dependencies (AOT-friendly).
/// Matches the Go gateway's JWT verification using a shared secret.
/// </summary>
public static partial class JwtValidator
{
    /// <summary>Decoded JWT claims relevant to the game server.</summary>
    public record JwtClaims(string UserId, string ServerId, long Exp);

    /// <summary>Internal JSON model for the JWT claims payload.</summary>
    private sealed class JwtClaimsJson
    {
        [JsonPropertyName("sub")]
        public string? UserId { get; set; }

        [JsonPropertyName("sid")]
        public string? ServerId { get; set; }

        [JsonPropertyName("exp")]
        public long Exp { get; set; }
    }

    /// <summary>Internal JSON model for the JWT header.</summary>
    private sealed class JwtHeaderJson
    {
        [JsonPropertyName("alg")]
        public string? Alg { get; set; }

        [JsonPropertyName("typ")]
        public string? Typ { get; set; }
    }

    [JsonSerializable(typeof(JwtClaimsJson))]
    [JsonSerializable(typeof(JwtHeaderJson))]
    private partial class JwtJsonContext : JsonSerializerContext;

    /// <summary>
    /// Verify an HS256 JWT token and extract claims.
    /// Returns null if the token is invalid, expired, or uses a different algorithm.
    /// </summary>
    public static JwtClaims? Verify(string token, string secret)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            // Verify header declares HS256
            var headerBytes = Base64UrlDecode(parts[0]);
            var header = JsonSerializer.Deserialize(headerBytes, JwtJsonContext.Default.JwtHeaderJson);
            if (header?.Alg != "HS256") return null;

            // Verify signature
            byte[] signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
            byte[] expectedSig;

            using (var hmac = new HMACSHA256(secretBytes))
            {
                expectedSig = hmac.ComputeHash(signingInput);
            }

            byte[] actualSig = Base64UrlDecode(parts[2]);
            if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
                return null;

            // Decode claims
            var claimsBytes = Base64UrlDecode(parts[1]);
            var claims = JsonSerializer.Deserialize(claimsBytes, JwtJsonContext.Default.JwtClaimsJson);
            if (claims == null || string.IsNullOrEmpty(claims.UserId))
                return null;

            // Check expiration
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (claims.Exp > 0 && now > claims.Exp)
                return null;

            return new JwtClaims(claims.UserId, claims.ServerId ?? "", claims.Exp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decode a Base64Url-encoded string to bytes.</summary>
    private static byte[] Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
