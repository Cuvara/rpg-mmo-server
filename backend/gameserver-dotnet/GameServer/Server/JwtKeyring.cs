namespace GameServer.Server;

/// <summary>
/// An ordered set of HS256 secrets that makes secret rotation non-disruptive.
/// C# counterpart of the Go <c>shared/jwt.Keyring</c> (backend/shared/jwt/keyring.go);
/// the two must stay behaviourally identical or a rotation will drop joins.
///
/// <para>
/// Rotation without a keyring means every live token signed with the old secret is
/// rejected the moment the new secret is deployed. With a keyring the operator
/// deploys <c>JOIN_TOKEN_SECRET=new,old</c>, waits for the join-token TTL to elapse,
/// then deploys <c>JOIN_TOKEN_SECRET=new</c>.
/// </para>
///
/// Rules (identical to the Go side):
/// <list type="bullet">
///   <item>The FIRST secret signs (the gateway's job; this server only verifies).</item>
///   <item>EVERY secret verifies, tried in order, so the old population drains.</item>
///   <item>An empty keyring verifies nothing — it fails closed, never open.</item>
///   <item>An expired token short-circuits: a valid signature with a dead
///         <c>exp</c> is never going to become valid under a different key.</item>
/// </list>
/// </summary>
public sealed class JwtKeyring
{
    /// <summary>Separator used in the JWT_SECRET / JOIN_TOKEN_SECRET env vars.</summary>
    public const char Separator = ',';

    /// <summary>A keyring holding no secrets. Rejects every token.</summary>
    public static readonly JwtKeyring Empty = new([]);

    private readonly string[] _secrets;

    private JwtKeyring(string[] secrets) => _secrets = secrets;

    /// <summary>How many secrets the keyring holds. &gt; 1 means a rotation is in progress.</summary>
    public int Count => _secrets.Length;

    /// <summary>True when the keyring can verify anything at all.</summary>
    public bool IsValid => _secrets.Length > 0;

    /// <summary>
    /// The secret new tokens would be signed with (the first entry), or "" when empty.
    /// The game server never signs; this exists for parity and diagnostics.
    /// </summary>
    public string Signing => _secrets.Length == 0 ? "" : _secrets[0];

    /// <summary>
    /// Build a keyring from a comma-separated secret list ("current,previous").
    /// Whitespace around each entry is trimmed and empty entries are dropped, so
    /// "new, old", "new,old" and "new,old," are equivalent — matching Go's
    /// <c>jwt.ParseKeyring</c> exactly.
    /// Returns <see cref="Empty"/> (which rejects everything) when the spec holds
    /// no usable secret; callers decide whether that is fatal or merely logged.
    /// </summary>
    public static JwtKeyring Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Empty;

        var secrets = spec
            .Split(Separator)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();

        return secrets.Length == 0 ? Empty : new JwtKeyring(secrets);
    }

    /// <summary>
    /// Verify a token against every secret in order and return the claims of the
    /// first secret that accepts it, or null if none does.
    /// An empty keyring returns null for every token (fail closed).
    /// An expired token returns null immediately without trying further keys.
    /// </summary>
    public JwtValidator.JwtClaims? Verify(string token)
    {
        if (!IsValid) return null;

        foreach (var secret in _secrets)
        {
            var claims = JwtValidator.Verify(token, secret, out var status);
            if (status == JwtValidator.VerifyStatus.Ok) return claims;

            // Definitive answer: the signature matched this key, the token is
            // simply too old. Retrying the remaining keys cannot change that.
            if (status == JwtValidator.VerifyStatus.Expired) return null;
        }

        return null;
    }
}
