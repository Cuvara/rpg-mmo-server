using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace GameServer.Net.Transport;

/// <summary>
/// The transport kinds the server can listen with. Values must match Go's
/// <c>transport.KindTCP</c> / <c>KindKCP</c>, because the same strings travel to
/// the client through the registry and <c>EnterWorldResponse.Transport</c>.
/// </summary>
public static class TransportKind
{
    /// <summary>TCP, the default.</summary>
    public const string Tcp = "tcp";

    /// <summary>KCP over UDP.</summary>
    public const string Kcp = "kcp";

    /// <summary>Environment variable holding the pre-shared AES key, shared with the Go side.</summary>
    public const string KeyEnvVar = "TRANSPORT_KEY";

    /// <summary>
    /// Lowercases a kind and maps the empty string to <see cref="Tcp"/> — the
    /// backward-compatible "unset" spelling used on the wire and in the registry.
    /// </summary>
    public static string Normalize(string? kind)
    {
        string k = (kind ?? "").Trim().ToLowerInvariant();
        return k.Length == 0 ? Tcp : k;
    }

    /// <summary>Reports whether <paramref name="kind"/> names a supported transport.</summary>
    public static bool IsValid(string? kind) => Normalize(kind) is Tcp or Kcp;
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
}

/// <summary>Builds listeners for a transport kind.</summary>
public static class TransportFactory
{
    /// <summary>
    /// Starts a listener of <paramref name="kind"/> on <paramref name="addr"/>
    /// (":9000" or "host:port").
    /// </summary>
    /// <param name="transportKey">
    /// Pre-shared AES key for KCP. Ignored for TCP, where TLS or the cluster
    /// network is the answer instead — the same rule the Go side follows.
    /// </param>
    /// <exception cref="ArgumentException">The kind is not supported.</exception>
    public static ITransportListener Listen(string kind, string addr, string? transportKey, ILogger logger)
    {
        var (host, port) = ParseAddr(addr);
        var ip = string.IsNullOrEmpty(host) ? IPAddress.Any : IPAddress.Parse(host);
        var bind = new IPEndPoint(ip, port);

        return TransportKind.Normalize(kind) switch
        {
            TransportKind.Tcp => new TcpTransportListener(bind),
            TransportKind.Kcp => new KcpTransportListener(bind, transportKey, logger),
            _ => throw new ArgumentException(
                $"unknown transport \"{kind}\" (want \"{TransportKind.Tcp}\" or \"{TransportKind.Kcp}\")", nameof(kind))
        };
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

/// <summary>TCP listener adapter.</summary>
internal sealed class TcpTransportListener : ITransportListener
{
    private readonly TcpListener _listener;

    public TcpTransportListener(IPEndPoint bind)
    {
        _listener = new TcpListener(bind.Address, bind.Port);
        _listener.Start();
        LocalEndPoint = _listener.LocalEndpoint.ToString()!;
    }

    public string LocalEndPoint { get; }
    public string Kind => TransportKind.Tcp;

    public async Task<ITransportConnection> AcceptAsync(CancellationToken ct)
    {
        var tcp = await _listener.AcceptTcpClientAsync(ct);
        return new TcpTransportConnection(tcp);
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>A single accepted TCP connection.</summary>
internal sealed class TcpTransportConnection(TcpClient tcp) : ITransportConnection
{
    private readonly NetworkStream _stream = tcp.GetStream();
    private int _closed;

    public Stream Stream => _stream;
    public string RemoteEndPoint => tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _stream.Close(); } catch { /* ignore */ }
        try { tcp.Close(); } catch { /* ignore */ }
    }

    public void Dispose() => Close();
}

/// <summary>KCP listener adapter.</summary>
internal sealed class KcpTransportListener : ITransportListener
{
    private readonly KcpListener _listener;

    public KcpTransportListener(IPEndPoint bind, string? transportKey, ILogger logger)
    {
        _listener = new KcpListener(bind, transportKey, logger);
        LocalEndPoint = _listener.LocalEndPoint.ToString();
    }

    public string LocalEndPoint { get; }
    public string Kind => TransportKind.Kcp;

    /// <summary>True when a transport key is set and every datagram is encrypted.</summary>
    public bool IsEncrypted => _listener.IsEncrypted;

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
