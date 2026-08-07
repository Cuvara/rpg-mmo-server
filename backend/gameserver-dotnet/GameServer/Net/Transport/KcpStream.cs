namespace GameServer.Net.Transport;

/// <summary>
/// Exposes a <see cref="KcpSession"/> as a <see cref="Stream"/>, so the
/// length-prefixed JSON codec in <see cref="WireProtocol"/> runs unchanged over
/// KCP.
/// </summary>
/// <remarks>
/// KCP in stream mode is a reliable, ordered byte pipe — the same contract
/// <c>NetworkStream</c> offers — so nothing above this class needs to know which
/// transport it is on. Reads are chunk-buffered: the ARQ hands back whole
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
            if (chunk == null) return 0; // session closed — EOF, same as a TCP FIN
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

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // KCP.Send only queues into the ARQ; it never blocks on the socket, so there
        // is nothing to await and no benefit to hopping threads here.
        session.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => session.Write(buffer.AsSpan(offset, count));

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
