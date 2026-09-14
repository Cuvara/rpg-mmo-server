using System.Net;

namespace GameServer.Net.Transport;

/// <summary>
/// What this listener actually does to the bytes on the wire, as one computed fact rather
/// than as something a reader has to infer from two environment variables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Encryption here is off by default twice — the transport defaults
/// to TCP, which has no packet encryption at all, and <c>TRANSPORT_KEY</c> defaults to
/// empty. Before this type, the server warned about exactly two of the four combinations:
/// KCP without a key, and a key set on TCP. <b>The default configuration — TCP, no key, no
/// encryption whatsoever — logged nothing.</b> A deployment that believed it was encrypted
/// and was not had nothing anywhere telling it so, which is the silent-divergence class this
/// repository keeps recording. Every combination is now reported, on every boot, and
/// published on <c>/status</c> where it can be read without grepping logs.
/// </para>
/// <para>
/// <b>Encrypted and authenticated are separate fields on purpose.</b> The KCP path is
/// AES-256-CFB with a CRC32 (see <see cref="KcpCrypto"/>). CFB gives confidentiality; the
/// CRC32 is a linear checksum and <b>is not a MAC</b>, so an attacker who can modify
/// datagrams can make controlled changes to the plaintext and repair the checksum. Folding
/// both into one "secure" boolean would let an operator read "encrypted: true" and believe
/// they are protected against tampering, which they are not. <see cref="Authenticated"/> is
/// therefore <c>false</c> on every path this server can currently be configured for — and it
/// is published anyway, precisely so that becoming <c>true</c> is a visible event rather
/// than an assumption.
/// </para>
/// <para>
/// Pure and allocation-light: computed once at startup from configuration, then read.
/// </para>
/// </remarks>
public readonly struct TransportPosture
{
    /// <summary>Cipher name reported when nothing encrypts the traffic.</summary>
    public const string CipherNone = "none";

    /// <summary>Cipher name for the KCP packet-crypt path (<see cref="KcpCrypto"/>).</summary>
    public const string CipherAesCfb = "aes-256-cfb";

    /// <summary>Normalised transport kind — <c>tcp</c> or <c>kcp</c>.</summary>
    public string Transport { get; }

    /// <summary><c>TRANSPORT_KEY</c> holds a non-empty value, whether or not it is used.</summary>
    public bool KeyConfigured { get; }

    /// <summary>Packets leave this process as ciphertext.</summary>
    public bool Encrypted { get; }

    /// <summary>
    /// Tampering with a packet in flight is detectable. <b>False on every currently
    /// reachable configuration</b> — see the remarks on this type.
    /// </summary>
    public bool Authenticated { get; }

    /// <summary>Cipher actually in force, or <see cref="CipherNone"/>.</summary>
    public string Cipher { get; }

    /// <summary>
    /// A key is configured but the selected transport cannot use it, so it is silently
    /// doing nothing. Called out separately because it is the configuration most likely to
    /// be mistaken for working encryption.
    /// </summary>
    public bool KeyIgnored => KeyConfigured && !Encrypted;

    /// <summary>
    /// The listener is bound somewhere other than loopback, so unencrypted traffic is
    /// reachable from off the machine. Reported, not enforced — refusing to start is a
    /// separate, operational decision.
    /// </summary>
    public bool BindsBeyondLoopback { get; }

    /// <summary>One line an operator can act on, used for the startup log and <c>/status</c>.</summary>
    public string Summary { get; }

    private TransportPosture(string transport, bool keyConfigured, bool encrypted,
        bool authenticated, string cipher, bool bindsBeyondLoopback, string summary)
    {
        Transport = transport;
        KeyConfigured = keyConfigured;
        Encrypted = encrypted;
        Authenticated = authenticated;
        Cipher = cipher;
        BindsBeyondLoopback = bindsBeyondLoopback;
        Summary = summary;
    }

    /// <summary>
    /// Derive the posture from the configuration as given.
    /// </summary>
    /// <param name="transport">Transport kind; normalised, so <c>""</c> means TCP.</param>
    /// <param name="transportKey">Value of <c>TRANSPORT_KEY</c>, possibly null or blank.</param>
    /// <param name="addr">Listen address, e.g. <c>:9000</c> or <c>127.0.0.1:9000</c>.</param>
    public static TransportPosture For(string? transport, string? transportKey, string? addr)
    {
        string kind = TransportKind.Normalize(transport);
        bool keyConfigured = !string.IsNullOrWhiteSpace(transportKey);

        // Only the KCP path has a packet-crypt layer. TCP ignores the key entirely, which
        // is the case worth naming rather than leaving to be discovered.
        bool encrypted = kind == TransportKind.Kcp && keyConfigured;
        string cipher = encrypted ? CipherAesCfb : CipherNone;

        // Deliberately hard-coded false, not derived. Nothing this server can be configured
        // for authenticates its packets; when an AEAD lands this becomes a real expression
        // and the change is visible in a diff and on /status.
        const bool authenticated = false;

        bool beyondLoopback = BindsBeyondLoopback_(addr);

        string summary;
        if (encrypted)
        {
            summary = $"ENCRYPTED ({CipherAesCfb}) but NOT AUTHENTICATED -- CFB with a CRC32 is not a MAC, " +
                      "so a modified packet is not detectable";
        }
        else if (kind == TransportKind.Tcp && keyConfigured)
        {
            summary = $"PLAINTEXT -- transport is TCP, which has no packet encryption; {TransportKind.KeyEnvVar} " +
                      "is set but IGNORED";
        }
        else if (kind == TransportKind.Tcp)
        {
            summary = "PLAINTEXT -- transport is TCP, which has no packet encryption";
        }
        else
        {
            summary = $"PLAINTEXT -- transport is KCP but {TransportKind.KeyEnvVar} is not set";
        }

        if (!encrypted && beyondLoopback)
        {
            summary += "; the listener is bound beyond loopback, so this traffic is readable off-host";
        }

        return new TransportPosture(kind, keyConfigured, encrypted, authenticated, cipher,
            beyondLoopback, summary);
    }

    /// <summary>
    /// Whether <paramref name="addr"/> puts the listener anywhere other than loopback.
    /// </summary>
    /// <remarks>
    /// A wildcard bind — <c>:9000</c>, <c>0.0.0.0:9000</c>, <c>[::]:9000</c> — counts as
    /// beyond loopback, because it accepts on every interface. That is the shape the
    /// container images use, so treating "no host part" as safe would exempt exactly the
    /// deployments this reporting is for. Anything unparseable is also treated as beyond
    /// loopback: the honest default for a security posture is the pessimistic one.
    /// </remarks>
    private static bool BindsBeyondLoopback_(string? addr)
    {
        string a = (addr ?? "").Trim();
        if (a.Length == 0) return true; // no address configured -> wildcard bind

        // Split off the port from the right, so IPv6 literals in brackets survive.
        string host = a;
        int close = a.LastIndexOf(']');
        int colon = a.LastIndexOf(':');
        if (colon > close) host = a.Substring(0, colon);

        host = host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];

        if (host.Length == 0 || host == "*" || host == "0.0.0.0" || host == "::") return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;

        return !(IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip));
    }
}
