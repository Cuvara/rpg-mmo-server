using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GameServer.Net;

/// <summary>
/// Represents a single player connection. Manages read/write loops over a TCP stream
/// using a bounded channel for the send queue (capacity 64, drops oldest on full).
/// </summary>
public sealed class Connection : IDisposable
{
    /// <summary>User ID associated with this connection (set after JWT validation).</summary>
    public string UserId { get; }

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<Envelope> _sendChannel;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger _logger;
    private int _disposed;

    public Connection(string userId, TcpClient tcp, ILogger logger)
    {
        UserId = userId;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _logger = logger;
        _cts = new CancellationTokenSource();

        _sendChannel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Enqueue an envelope for sending. Non-blocking; drops oldest if full.</summary>
    public void Send(Envelope env)
    {
        if (_cts.IsCancellationRequested) return;
        _sendChannel.Writer.TryWrite(env);
    }

    /// <summary>
    /// Read loop: continuously reads envelopes from the wire and dispatches them via the handler.
    /// Returns when the connection is closed or an error occurs.
    /// </summary>
    public async Task ReadLoopAsync(Func<Connection, Envelope, Task> handler)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var env = await WireProtocol.DecodeAsync(_stream, _cts.Token);
                if (env == null) break; // clean EOF

                await handler(this, env);
            }
        }
        catch (OperationCanceledException) { /* expected on close */ }
        catch (IOException) { /* peer disconnect */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read loop error for user {UserId}", UserId);
        }
    }

    /// <summary>
    /// Write loop: dequeues envelopes from the send channel and writes them to the wire.
    /// Returns when the channel is completed or the connection is closed.
    /// </summary>
    public async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var env in _sendChannel.Reader.ReadAllAsync(_cts.Token))
            {
                byte[] frame = WireProtocol.Encode(env);
                await _stream.WriteAsync(frame, _cts.Token);
                await _stream.FlushAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { /* expected on close */ }
        catch (IOException) { /* peer disconnect */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write loop error for user {UserId}", UserId);
        }
    }

    /// <summary>Read a single envelope from the wire (used during handshake).</summary>
    public Task<Envelope?> ReadOneAsync()
    {
        return WireProtocol.DecodeAsync(_stream, _cts.Token);
    }

    /// <summary>Write a single envelope to the wire (used during handshake).</summary>
    public async Task WriteOneAsync(Envelope env)
    {
        byte[] frame = WireProtocol.Encode(env);
        await _stream.WriteAsync(frame, _cts.Token);
        await _stream.FlushAsync(_cts.Token);
    }

    /// <summary>Close the connection. Idempotent.</summary>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _cts.Cancel();
        _sendChannel.Writer.TryComplete();
        try { _stream.Close(); } catch { /* ignore */ }
        try { _tcp.Close(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Close();
        _cts.Dispose();
    }
}
