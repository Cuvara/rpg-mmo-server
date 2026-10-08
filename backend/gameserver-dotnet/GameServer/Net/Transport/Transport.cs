using System.Net;
using Microsoft.Extensions.Logging;

namespace GameServer.Net.Transport;

/// <summary>
/// The realtime gameplay transport. KCP over UDP is the only one: the TCP gameplay path was
/// removed when the project went KCP-only (see the module CHANGELOG). The string travels to
/// clients through the registry (<c>servers:id:{id}</c> field <c>transport</c>) and
/// <c>EnterWorldResponse.Transport</c>, so it must equal Go's <c>transport.KindKCP</c>.
/// </summary>
public static class TransportKind
{
    /// <summary>KCP over UDP — the only gameplay transport.</summary>
    public const string Kcp = "kcp";

    /// <summary>Environment variable holding the pre-shared AES key, shared with the Go side.</summary>
    public const string KeyEnvVar = "TRANSPORT_KEY";

    /// <summary>
    /// Trims and lowercases a configured kind. The empty string stays empty: it no longer
    /// means anything, and in particular it never means TCP. Callers that accept "unset"
    /// (the <c>--transport</c> flag and <c>GAMESERVER_TRANSPORT</c>) map null to
    /// <see cref="Kcp"/> themselves — see <see cref="ResolveConfigured"/>.
    /// </summary>
    public static string Normalize(string? kind) => (kind ?? "").Trim().ToLowerInvariant();

    /// <summary>Reports whether <paramref name="kind"/> is the gameplay transport, <c>kcp</c>.</summary>
    public static bool IsValid(string? kind) => Normalize(kind) == Kcp;

    /// <summary>
    /// Resolves the operator's <c>--transport</c> / <c>GAMESERVER_TRANSPORT</c> setting.
    /// Unset (null, empty or whitespace) and <c>kcp</c> resolve to <see cref="Kcp"/>;
    /// anything else — <c>tcp</c> included — is refused with the message the startup path
    /// logs before exiting.
    /// </summary>
    /// <returns>True and <c>kcp</c> when accepted; false and the fatal message otherwise.</returns>
    public static bool ResolveConfigured(string? configured, out string resolved, out string? error)
    {
        string k = Normalize(configured);
        if (k.Length == 0 || k == Kcp)
        {
            resolved = Kcp;
            error = null;
            return true;
        }
        resolved = "";
        error = UnsupportedMessage(configured);
        return false;
    }

    /// <summary>The fatal startup message for an unsupported transport value (shared contract text).</summary>
    public static string UnsupportedMessage(string? value) =>
        $"GAMESERVER_TRANSPORT={value} is not supported: realtime gameplay is KCP/UDP only";
}

/// <summary>An accepted client connection, independent of the transport underneath.</summary>
public interface ITransportConnection : IDisposable
{
    /// <summary>The reliable, ordered byte stream the wire codec reads and writes.</summary>
    Stream Stream { get; }

    /// <summary>The peer address, for logging.</summary>
    string RemoteEndPoint { get; }

    /// <summary>Closes the connection. Must be idempotent.</summary>
    void Close();
}

/// <summary>A listener that yields <see cref="ITransportConnection"/>s.</summary>
public interface ITransportListener : IDisposable
{
    /// <summary>The address actually bound, with any ephemeral port resolved.</summary>
    string LocalEndPoint { get; }

    /// <summary>The transport kind this listener speaks.</summary>
    string Kind { get; }

    /// <summary>Waits for the next client.</summary>
    Task<ITransportConnection> AcceptAsync(CancellationToken ct);

    /// <summary>Flood-protection and drop counters for this listener.</summary>
    KcpListenerStats Stats { get; }
}

/// <summary>Builds the gameplay listener.</summary>
public static class TransportFactory
{
    /// <summary>
    /// Starts a KCP listener on <paramref name="addr"/> (":9000" or "host:port").
    /// </summary>
    /// <param name="kind">Must be <c>kcp</c>. Anything else — including <c>tcp</c> and the
    /// empty string — throws, because there is no other gameplay transport.</param>
    /// <param name="transportKey">Pre-shared AES key for KCP; empty means plaintext.</param>
    /// <param name="limits">Flood and backpressure limits; null means
    /// <see cref="KcpListenerOptions.Default"/>.</param>
    /// <exception cref="ArgumentException">The kind is not <c>kcp</c>.</exception>
    public static ITransportListener Listen(string kind, string addr, string? transportKey, ILogger logger,
        KcpListenerOptions? limits = null)
    {
        if (!TransportKind.IsValid(kind))
            throw new ArgumentException(TransportKind.UnsupportedMessage(kind), nameof(kind));

        var (host, port) = ParseAddr(addr);
        var ip = string.IsNullOrEmpty(host) ? IPAddress.Any : IPAddress.Parse(host);
        var bind = new IPEndPoint(ip, port);
        return new KcpTransportListener(bind, transportKey, logger, limits ?? KcpListenerOptions.Default);
    }

    /// <summary>Splits ":9000" / "0.0.0.0:9000" into host and port.</summary>
    public static (string Host, int Port) ParseAddr(string addr)
    {
        int idx = addr.LastIndexOf(':');
        if (idx < 0) throw new ArgumentException($"invalid address \"{addr}\" (want [host]:port)", nameof(addr));
        string host = addr[..idx];
        if (!int.TryParse(addr[(idx + 1)..], out int port))
            throw new ArgumentException($"invalid port in address \"{addr}\"", nameof(addr));
        return (host, port);
    }
}

/// <summary>KCP listener adapter.</summary>
internal sealed class KcpTransportListener : ITransportListener
{
    private readonly KcpListener _listener;

    public KcpTransportListener(IPEndPoint bind, string? transportKey, ILogger logger, KcpListenerOptions limits)
    {
        _listener = new KcpListener(bind, transportKey, logger, limits);
        LocalEndPoint = _listener.LocalEndPoint.ToString();
    }

    public string LocalEndPoint { get; }
    public string Kind => TransportKind.Kcp;

    /// <summary>True when a transport key is set and every datagram is encrypted.</summary>
    public bool IsEncrypted => _listener.IsEncrypted;

    /// <inheritdoc />
    public KcpListenerStats Stats => _listener.Stats;

    public async Task<ITransportConnection> AcceptAsync(CancellationToken ct)
    {
        var session = await _listener.AcceptAsync(ct);
        return new KcpTransportConnection(session);
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>A single accepted KCP session.</summary>
internal sealed class KcpTransportConnection(KcpSession session) : ITransportConnection
{
    private readonly KcpStream _stream = new(session);

    public Stream Stream => _stream;
    public string RemoteEndPoint => session.Remote.ToString();

    public void Close() => session.Close();

    public void Dispose()
    {
        Close();
        session.Dispose();
    }
}
