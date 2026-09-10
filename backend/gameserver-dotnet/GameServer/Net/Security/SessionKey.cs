using System.Security.Cryptography;
using System.Text;

namespace GameServer.Net.Security;

/// <summary>
/// The per-session key that encrypts one client's gameplay hop, and the derivation that
/// lets this server compute it without ever being sent it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> Transport encryption used ONE pre-shared key: the same value
/// in every client binary and every server. Extracting it from a single client decrypted
/// every player's traffic for ever, and rotating it meant redeploying everything at once.
/// This key is per join.
/// </para>
/// <para>
/// <b>The derivation, and why nothing crosses the gameplay hop.</b>
/// </para>
/// <code>
/// key = HKDF-SHA256(ikm  = JOIN_TOKEN_SECRET,
///                   salt = join token's jti claim,
///                   info = "cuvara/session-key/v1",
///                   L    = 32)
/// </code>
/// <para>
/// The gateway computes this when it mints the join token and returns it to the client in
/// <c>EnterWorldResponse</c>. This server computes the SAME value from the secret it
/// already holds and the <c>jti</c> it reads out of the token it already verifies — so the
/// key is never transmitted here, never stored, and never placed in the token. It must
/// stay byte-identical with <c>backend/shared/sessionkey</c>; a golden vector shared with
/// the Go tests is what holds the two together, because two implementations that each
/// round-trip against themselves can still disagree with each other.
/// </para>
/// <para>
/// Rooting session keys in <c>JOIN_TOKEN_SECRET</c> adds no new class of failure: anyone
/// holding that secret can already mint a join token for any user, which is total
/// compromise. The <see cref="Info"/> string is the domain separation that keeps
/// derivation from interacting with signing.
/// </para>
/// <para>
/// <b>Limitation, stated here because it is easy to overstate what this buys.</b> The
/// client cannot derive the key — it has no secret — so the key must travel gateway →
/// client, and the gateway hop is the SAME transport stack as the gameplay hop, plaintext
/// TCP by default. In the default configuration an eavesdropper on the gateway hop reads
/// the key and can decrypt that session. This turns "compromise one binary, decrypt
/// everyone for ever" into "eavesdrop the gateway hop, decrypt one session": strictly
/// better, and not the end-to-end confidentiality the name suggests.
/// </para>
/// </remarks>
public readonly struct SessionKey
{
    /// <summary>Derived key length in bytes.</summary>
    public const int Size = 32;

    /// <summary>
    /// HKDF domain-separation string. Part of the wire contract: change it and the two
    /// ends derive different keys, no session forms, and nothing names the cause.
    /// </summary>
    public const string Info = "cuvara/session-key/v1";

    /// <summary>What a key renders as anywhere a string is expected.</summary>
    public const string Redacted = "[redacted session key]";

    private readonly byte[]? _material;

    private SessionKey(byte[] material) => _material = material;

    /// <summary>True when no key was derived.</summary>
    public bool IsEmpty => _material is null || _material.Length == 0;

    /// <summary>
    /// A copy of the key material. The copy is deliberate — a caller that mutated shared
    /// state would silently change the key for everyone holding it.
    /// </summary>
    public byte[] ToArray() => _material is null ? Array.Empty<byte>() : (byte[])_material.Clone();

    /// <summary>Lowercase hex. For tests and cross-implementation vectors only.</summary>
    public string ToHex() => _material is null ? "" : Convert.ToHexString(_material).ToLowerInvariant();

    /// <summary>
    /// Always the redacted marker, never the material.
    /// </summary>
    /// <remarks>
    /// The realistic way a secret escapes is not a deliberate log call — it is a value
    /// interpolated into a message, or a struct handed to a formatter by code that did not
    /// know it held a key. Overriding this closes the whole class.
    /// </remarks>
    public override string ToString() => Redacted;

    /// <summary>
    /// Derive the per-session key from the join-token secret and the token's jti claim.
    /// </summary>
    /// <remarks>
    /// <paramref name="joinTokenSecret"/> may be a comma-separated rotation list
    /// ("current,previous"), in which case the FIRST entry is used — the same one the
    /// gateway signs and derives with, so both ends move together during a rotation.
    /// Trying older entries would be a downgrade surface for no benefit: the jti is fresh
    /// per join, so there is never an old session key worth honouring.
    /// </remarks>
    /// <returns>
    /// An empty key when there is no secret or no jti. Callers decide what that means;
    /// this method does not invent one, and in particular does not fall back to a weaker
    /// key, because a negotiable path is a downgrade attack.
    /// </returns>
    public static SessionKey Derive(string? joinTokenSecret, string? jti)
    {
        string secret = CurrentSecret(joinTokenSecret);
        if (secret.Length == 0 || string.IsNullOrEmpty(jti)) return default;

        byte[] material = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(secret),
            outputLength: Size,
            salt: Encoding.UTF8.GetBytes(jti),
            info: Encoding.UTF8.GetBytes(Info));

        return new SessionKey(material);
    }

    /// <summary>The active entry of a comma-separated rotation list.</summary>
    private static string CurrentSecret(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "";
        int comma = spec.IndexOf(',');
        return (comma >= 0 ? spec[..comma] : spec).Trim();
    }
}
