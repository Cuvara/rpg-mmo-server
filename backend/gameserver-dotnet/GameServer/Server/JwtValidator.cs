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
    public record JwtClaims(string UserId, string ServerId, long Exp, string Jti = "");

    /// <summary>Clock skew tolerance for expiration checks (seconds).</summary>
    public const int ClockSkewSeconds = 5;

    /// <summary>Internal JSON model for the JWT claims payload.</summary>
    private sealed class JwtClaimsJson
    {
        [JsonPropertyName("sub")]
        public string? UserId { get; set; }

        [JsonPropertyName("sid")]
        public string? ServerId { get; set; }

        [JsonPropertyName("jti")]
        public string? Jti { get; set; }

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
    /// Why a verification failed. The distinction matters for
    /// <see cref="JwtKeyring"/>: a signature that matched a key but whose
    /// <c>exp</c> has passed is a definitive answer, so the keyring must stop
    /// instead of retrying the remaining keys. Mirrors the short-circuit in the
    /// Go <c>shared/jwt</c> Keyring.Verify.
    /// </summary>
    public enum VerifyStatus
    {
        /// <summary>Token verified.</summary>
        Ok,
        /// <summary>Empty token/secret, wrong segment count, bad base64, bad JSON, wrong alg, or missing sub.</summary>
        Invalid,
        /// <summary>Well-formed token, but the HMAC did not match this secret.</summary>
        BadSignature,
        /// <summary>Signature matched this secret, but the token has expired.</summary>
        Expired
    }

    /// <summary>
    /// Verify an HS256 JWT token and extract claims.
    /// Returns null if the token is invalid, expired, or uses a different algorithm.
    /// </summary>
    public static JwtClaims? Verify(string token, string secret) => Verify(token, secret, out _);

    /// <summary>
    /// Verify an HS256 JWT token and extract claims, reporting why it failed.
    /// </summary>
    public static JwtClaims? Verify(string token, string secret, out VerifyStatus status)
    {
        status = VerifyStatus.Invalid;

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
            {
                status = VerifyStatus.BadSignature;
                return null;
            }

            // Decode claims
            var claimsBytes = Base64UrlDecode(parts[1]);
            var claims = JsonSerializer.Deserialize(claimsBytes, JwtJsonContext.Default.JwtClaimsJson);
            if (claims == null || string.IsNullOrEmpty(claims.UserId))
                return null;

            // Check expiration with clock skew tolerance
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (claims.Exp > 0 && now - ClockSkewSeconds > claims.Exp)
            {
                status = VerifyStatus.Expired;
                return null;
            }

            status = VerifyStatus.Ok;
            return new JwtClaims(claims.UserId, claims.ServerId ?? "", claims.Exp, claims.Jti ?? "");
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
