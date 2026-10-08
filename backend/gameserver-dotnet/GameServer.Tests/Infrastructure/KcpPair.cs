using System.Net;
using GameServer.Net.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// A connected KCP pair on loopback: a real <see cref="KcpListener"/>, a
/// <see cref="KcpTestClient"/> dialled into it, and the accepted server side wrapped as the
/// <see cref="ITransportConnection"/> a <c>Connection</c> runs on. For unit tests that need
/// a real transport under a <c>Connection</c> without a whole game server.
/// </summary>
internal sealed class KcpPair : IDisposable
{
    public required KcpListener Listener { get; init; }
    public required KcpTestClient Client { get; init; }
    public required ITransportConnection ServerSide { get; init; }

    /// <summary>The client's end of the byte stream.</summary>
    public Stream ClientStream => Client.GetStream();

    public static async Task<KcpPair> CreateAsync(KcpListenerOptions? limits = null)
    {
        var listener = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), "", NullLogger.Instance,
            limits ?? KcpListenerOptions.Default);
        var client = new KcpTestClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var accept = listener.AcceptAsync(cts.Token);
            await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndPoint.Port, cts.Token);
            var session = await accept;
            return new KcpPair { Listener = listener, Client = client, ServerSide = new KcpTransportConnection(session) };
        }
        catch
        {
            client.Dispose();
            listener.Dispose();
            throw;
        }
    }

    /// <summary>Synchronous form for constructors and helpers that cannot await.</summary>
    public static KcpPair Create(KcpListenerOptions? limits = null) => CreateAsync(limits).GetAwaiter().GetResult();

    public void Dispose()
    {
        Client.Dispose();
        ServerSide.Dispose();
        Listener.Dispose();
    }
}
