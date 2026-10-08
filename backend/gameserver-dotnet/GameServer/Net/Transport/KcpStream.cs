namespace GameServer.Net.Transport;

/// <summary>
/// Exposes a <see cref="KcpSession"/> as a <see cref="Stream"/>, so the
/// length-prefixed codec in <see cref="WireProtocol"/> runs unchanged over
/// KCP.
/// </summary>
/// <remarks>
/// KCP in stream mode is a reliable, ordered byte pipe, so nothing above this class
/// needs to know it is not a socket stream. Reads are chunk-buffered: the ARQ hands back whole
/// messages, callers ask for arbitrary counts, and the remainder is held here
/// until the next read.
/// </remarks>
public sealed class KcpStream(KcpSession session) : Stream
{
    private byte[]? _pending;
    private int _pendingOffset;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;

        if (_pending == null)
        {
            var chunk = await session.ReadChunkAsync(ct);
            if (chunk == null) return 0; // session closed: end of stream
            _pending = chunk;
            _pendingOffset = 0;
        }

        int available = _pending.Length - _pendingOffset;
        int n = Math.Min(available, buffer.Length);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        if (_pendingOffset >= _pending.Length) _pending = null;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // KCP.Send only queues into the ARQ and never blocks on the socket, so the one
        // place a write can wait is here: when the peer is not acknowledging, the send
        // queue reaches its soft limit and the writer is held until ACKs drain it — the
        // same backpressure a full TCP send buffer applies, and what lets the connection's
        // drop-the-oldest snapshot lane engage instead of KCP queueing without bound.
        await session.WaitForSendSpaceAsync(ct);
        session.Write(buffer.Span);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        session.WaitForSendSpaceAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.Write(buffer.AsSpan(offset, count));
    }

    public override void Flush() { /* Write already flushes the ARQ */ }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) session.Close();
        base.Dispose(disposing);
    }
}
