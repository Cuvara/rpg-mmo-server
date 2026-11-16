using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Games;

namespace GameServer.Server;

/// <summary>
/// Lightweight HS256 JWT validator. No external dependencies (AOT-friendly).
/// Matches the Go gateway's JWT verification using a shared secret.
/// </summary>
public static class JwtValidator
{
    /// <summary>Decoded JWT claims relevant to the game server.</summary>
    /// <param name="UserId">The <c>sub</c> claim: the authenticated account.</param>
    /// <param name="ServerId">The <c>sid</c> claim: the game server this join token names.</param>
    /// <param name="Exp">The <c>exp</c> claim, Unix seconds.</param>
    /// <param name="Jti">The <c>jti</c> claim: single-use join-token id (ADR-20).</param>
    /// <param name="CharacterId">
    /// The <c>cid</c> claim (ADR-31): the roster character this connection plays, put in
    /// the gateway token by Nakama after an ownership check and copied into the join token
    /// by the gateway. Empty when the claim is absent, which means the account's default
    /// character — every token minted before protocol 3.
    /// </param>
    /// <param name="GameId">The <c>gid</c> claim (ADR-33); empty when absent means the default game.</param>
    public record JwtClaims(string UserId, string ServerId, long Exp, string Jti = "", string CharacterId = "",
        string GameId = "");

    /// <summary>Clock skew tolerance for exp, iat and nbf checks (seconds).</summary>
    public const int ClockSkewSeconds = 5;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
        /// <summary>Malformed encoding, unsupported header, invalid claims, or empty token/secret.</summary>
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
        => Verify(token, secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out status);

    internal static JwtClaims? Verify(string token, string secret, long nowUnixSeconds, out VerifyStatus status)
    {
        status = VerifyStatus.Invalid;

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            // Refuse alternate encodings and unsupported headers before HMAC work,
            // matching Go shared/jwt and the shared C1-C7 golden contract.
            var headerBytes = Base64UrlDecode(parts[0]);
            var claimsBytes = Base64UrlDecode(parts[1]);
            var actualSig = Base64UrlDecode(parts[2]);
            if (actualSig.Length != 32 || !VerifyHeader(headerBytes)) return null;

            // Verify signature
            byte[] signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
            byte[] expectedSig;

            using (var hmac = new HMACSHA256(secretBytes))
            {
                expectedSig = hmac.ComputeHash(signingInput);
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            {
                status = VerifyStatus.BadSignature;
                return null;
            }

            var claims = ReadClaims(claimsBytes, nowUnixSeconds);
            if (claims == null) return null;

            // Only otherwise valid claims may report Expired: keyrings stop on it.
            if (nowUnixSeconds - ClockSkewSeconds > claims.Exp)
            {
                status = VerifyStatus.Expired;
                return null;
            }

            status = VerifyStatus.Ok;
            return claims;
        }
        catch (Exception error) when (error is JsonException or FormatException or DecoderFallbackException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool VerifyHeader(byte[] json)
    {
        var reader = ReadObject(json);
        var names = new HashSet<string>(StringComparer.Ordinal);
        string alg = "", typ = "";
        while (ReadMember(ref reader, names, out string name))
        {
            switch (name)
            {
                case "alg": alg = ReadString(ref reader); break;
                case "typ": typ = ReadString(ref reader); break;
                case "crit": return false;
                default: reader.Skip(); break;
            }
        }
        return alg == "HS256" && typ == "JWT";
    }

    private static JwtClaims? ReadClaims(byte[] json, long nowUnixSeconds)
    {
        var reader = ReadObject(json);
        var names = new HashSet<string>(StringComparer.Ordinal);
        string sub = "", sid = "", jti = "", cid = "", gid = "";
        long exp = 0, iat = 0, nbf = long.MinValue;
        while (ReadMember(ref reader, names, out string name))
        {
            switch (name)
            {
                case "sub": sub = ReadString(ref reader); break;
                case "sid": sid = ReadString(ref reader); break;
                case "jti": jti = ReadString(ref reader); break;
                case "cid": cid = ReadString(ref reader); break;
                case "gid": gid = ReadString(ref reader); break;
                case "exp": exp = ReadInteger(ref reader); break;
                case "iat": iat = ReadInteger(ref reader); break;
                case "nbf": nbf = ReadInteger(ref reader); break;
                default: reader.Skip(); break;
            }
        }

        if (sub.Length == 0 || exp <= 0) return null;
        // Empty gid is retained for compatibility with JwtCharacterClaimTests;
        // issuers omit it for the default game (ADR-33).
        if (gid.Length != 0 && (!GameId.IsValidGameId(gid) || gid == GameId.DefaultGameId)) return null;
        long latest = nowUnixSeconds + ClockSkewSeconds;
        if (iat < 0 || iat > latest || nbf > latest) return null;
        return new JwtClaims(sub, sid, exp, jti, cid, gid);
    }

    // A reader walk keeps type and duplicate checks explicit without reflection
    // or generated model defaults hiding missing/null claims under NativeAOT.
    private static Utf8JsonReader ReadObject(byte[] json)
    {
        // Skip() does not decode unknown strings, so validate their UTF-8 too.
        StrictUtf8.GetCharCount(json);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 10_000 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("JWT header and payload must be JSON objects.");
        return reader;
    }

    private static bool ReadMember(ref Utf8JsonReader reader, HashSet<string> names, out string name)
    {
        name = "";
        if (!reader.Read()) throw new JsonException("Unclosed JWT object.");
        if (reader.TokenType == JsonTokenType.EndObject)
        {
            if (reader.Read()) throw new JsonException("Trailing JSON after JWT object.");
            return false;
        }
        if (reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException("Expected JWT member name.");
        name = reader.GetString()!;
        if (!names.Add(name) || !reader.Read())
            throw new JsonException("Duplicate or incomplete JWT member.");
        return true;
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("JWT claim must be a string.");
        return reader.GetString()!;
    }

    private static long ReadInteger(ref Utf8JsonReader reader)
    {
        // TryGetInt64 also refuses fractional and exponent JSON number forms.
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value))
            throw new JsonException("JWT date must be a signed 64-bit JSON integer.");
        return value;
    }

    /// <summary>Decode canonical unpadded base64url, including zero unused bits.</summary>
    private static byte[] Base64UrlDecode(string input)
    {
        foreach (char c in input)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                throw new FormatException("JWT segment contains a non-base64url character.");
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        byte[] decoded = Convert.FromBase64String(s);
        string canonical = Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (canonical != input) throw new FormatException("Non-canonical base64url segment.");
        return decoded;
    }
}
