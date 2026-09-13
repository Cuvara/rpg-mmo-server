using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GameServer.Nakama;

/// <summary>
/// How this server authenticates Nakama when the meta hop runs TLS (ADR-24):
/// either the platform trust store, or one exact certificate pinned here.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no "accept any certificate" option</b>, and this is the third
/// place in the system that says so — the netcode package's <c>TlsOptions</c> for the
/// gateway hop, the Unity client's Nakama handler, and here. ADR-24 decision 4 is a rule
/// with no dev exemption: validation that accepts anything is indistinguishable from
/// validation that works, for as long as only a good certificate is ever presented.
/// </para>
/// <para>
/// So the two modes, and nothing between them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No pin</b> (<c>NAKAMA_TLS_PIN</c> unset) — .NET's own validation decides, with no
/// callback installed at all. Correct when Nakama holds a certificate from a CA the pod
/// already trusts, and it correctly REFUSES a self-signed one.
/// </description></item>
/// <item><description>
/// <b>Pinned</b> — the leaf Nakama presents must equal the pinned DER byte for byte.
/// Chain, expiry, hostname and CA are then all irrelevant <i>because a stricter check
/// already passed</i>: an attacker has to present this exact certificate, which needs its
/// private key. This is the mode for a dev or staging Nakama holding a self-signed
/// certificate, and it is stricter than the public trust store, not looser.
/// </description></item>
/// </list>
/// </remarks>
public static class NakamaTlsPin
{
    /// <summary>
    /// Reads the first certificate out of a PEM file as DER bytes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The file carries no <c>-----BEGIN CERTIFICATE-----</c> block, or its Base64 body does
    /// not decode. Throwing is the point: a pin that silently ends up null falls back to
    /// platform validation, which against a self-signed Nakama produces a message about the
    /// certificate rather than about the pin that was never loaded.
    /// </exception>
    public static byte[] LoadDerFromPemFile(string path) => ParsePem(File.ReadAllText(path));

    /// <summary>Reads the first certificate out of a PEM string as DER bytes.</summary>
    /// <exception cref="ArgumentException">As <see cref="LoadDerFromPemFile"/>.</exception>
    public static byte[] ParsePem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new ArgumentException("certificate PEM is empty", nameof(pem));
        }

        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";

        int start = pem.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new ArgumentException("no BEGIN CERTIFICATE block in the PEM text", nameof(pem));
        }

        start += begin.Length;
        int stop = pem.IndexOf(end, start, StringComparison.Ordinal);
        if (stop < 0)
        {
            throw new ArgumentException("no END CERTIFICATE line in the PEM text", nameof(pem));
        }

        string body = pem[start..stop]
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        try
        {
            byte[] der = Convert.FromBase64String(body);
            if (der.Length == 0)
            {
                throw new ArgumentException("the PEM block decodes to zero bytes", nameof(pem));
            }

            return der;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("the PEM body is not valid Base64: " + ex.Message, nameof(pem), ex);
        }
    }

    /// <summary>
    /// Whether a presented leaf is the pinned one. An empty pin matches NOTHING, which is
    /// what makes "pinned to nothing" a closed door rather than an open one.
    /// </summary>
    public static bool Matches(byte[]? pinnedDer, byte[]? presentedDer)
    {
        if (pinnedDer is null || pinnedDer.Length == 0) return false;
        if (presentedDer is null || presentedDer.Length == 0) return false;

        // Length-safe: FixedTimeEquals returns false on a length mismatch rather than throwing.
        return CryptographicOperations.FixedTimeEquals(pinnedDer, presentedDer);
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that accepts one certificate and no other.
    /// </summary>
    /// <remarks>
    /// The callback ignores the chain and the <see cref="System.Net.Security.SslPolicyErrors"/>
    /// on purpose — they are the platform's verdict about a CA path this certificate does not
    /// have. What replaces them is a stricter question, not a weaker one: is this the exact
    /// certificate we were given out of band?
    /// </remarks>
    /// <exception cref="ArgumentException">The pin is empty. There is no accept-anything handler.</exception>
    public static HttpMessageHandler CreatePinnedHandler(byte[] pinnedDer)
    {
        if (pinnedDer is null || pinnedDer.Length == 0)
        {
            throw new ArgumentException(
                "refusing to build a TLS handler with an empty pin -- that would be " +
                "InsecureSkipVerify with a different name (ADR-24 decision 4)", nameof(pinnedDer));
        }

        byte[] pin = (byte[])pinnedDer.Clone();
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => Matches(pin, cert?.RawData),
        };
    }

    /// <summary>
    /// A short, log-safe fingerprint of a pin, so two sides can be compared without pasting
    /// certificates around. SHA-256 over the DER, first 16 hex characters.
    /// </summary>
    public static string Fingerprint(byte[] der) =>
        Convert.ToHexString(SHA256.HashData(der))[..16].ToLowerInvariant();

    /// <summary>
    /// The same fingerprint for a certificate object, so the enable recipe's
    /// <c>openssl x509 -fingerprint</c> output has something to be compared against.
    /// </summary>
    public static string Fingerprint(X509Certificate2 certificate) => Fingerprint(certificate.RawData);
}
