using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using RpgMmo.Wire.V1;

namespace GameServer.Snapshot;

/// <summary>
/// Serializes a snapshot into a length-prefixed wire frame using buffers it keeps and
/// reuses, instead of allocating a fresh array per snapshot per tick.
///
/// <para>The generated snapshot serializer writes directly into a reusable protobuf
/// output stream. The envelope fields use the protobuf library's <see cref="CodedOutputStream"/>
/// primitives, so the payload is length-prefixed in place without a per-frame
/// <see cref="ByteString"/> wrapper.</para>
///
/// <para><b>The bytes are unchanged by construction.</b> The snapshot is still written by its
/// generated serializer, while the envelope fields use the protobuf library's tag, enum and
/// length writers. <c>SnapshotByteIdentityTests</c> and
/// <c>WriteFrame_IsByteIdenticalToTheAllocatingPath</c> guard the wire bytes.</para>
///
/// <para><b>Threading and lifetime:</b> one instance per connection, used only by that
/// connection's write task — the same single-threaded ownership as
/// <see cref="SnapshotDeltaState"/>. The returned memory is valid until the next call on
/// this instance, so the caller must finish writing it before asking for another frame.
/// The write task awaits its send before looping, which is exactly that.</para>
/// </summary>
public sealed class SnapshotFrameWriter
{
    private readonly GrowingBufferWriter _frame = new();

    /// <summary>
    /// The stream and protobuf output buffer are reused across frames by this connection.
    /// </summary>
    private readonly FrameBufferStream _frameStream;
    private readonly CodedOutputStream _output;

    public SnapshotFrameWriter()
    {
        _frameStream = new FrameBufferStream(_frame);
        _output = new CodedOutputStream(_frameStream, 256, leaveOpen: true);
    }

    /// <summary>
    /// Serialize <paramref name="snapshot"/> as message <paramref name="type"/> into a
    /// length-prefixed frame. The result points into a buffer this instance owns and is
    /// valid until the next call.
    /// </summary>
    public ReadOnlyMemory<byte> WriteFrame(byte type, SnapshotMessage snapshot)
    {
        // Reserve the 4-byte length prefix, write the protobuf body after it, then fill
        // the prefix once the body's length is known.
        _frame.Reset(reserve: 4);
        int payloadSize = snapshot.CalculateSize();
        if (type != 0)
        {
            _output.WriteTag(1, WireFormat.WireType.Varint);
            _output.WriteEnum(type);
        }
        if (payloadSize != 0)
        {
            _output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            _output.WriteLength(payloadSize);
            snapshot.WriteTo(_output);
        }
        _output.Flush();

        int bodySize = _frame.Written - 4;
        BinaryPrimitives.WriteInt32BigEndian(_frame.Buffer.AsSpan(0, 4), bodySize);
        return _frame.Buffer.AsMemory(0, _frame.Written);
    }

    private sealed class FrameBufferStream(GrowingBufferWriter buffer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] destination, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] source, int offset, int count) =>
            Write(source.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> source)
        {
            source.CopyTo(buffer.GetSpan(source.Length));
            buffer.Advance(source.Length);
        }
    }

    /// <summary>
    /// A minimal <see cref="IBufferWriter{T}"/> over one array that is kept and grown,
    /// never handed out and never returned to a shared pool — the array's only owner is
    /// this writer, and its only user is one connection's write task.
    /// </summary>
    private sealed class GrowingBufferWriter : IBufferWriter<byte>
    {
        public byte[] Buffer = new byte[256];
        public int Written;

        public ReadOnlyMemory<byte> WrittenMemory => Buffer.AsMemory(0, Written);

        /// <summary>Start a new message, keeping <paramref name="reserve"/> bytes free at the front.</summary>
        public void Reset(int reserve)
        {
            Written = 0;
            EnsureCapacity(reserve);
            Written = reserve;
        }

        public void Advance(int count) => Written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(Written + Math.Max(sizeHint, 1));
            return Buffer.AsMemory(Written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        /// <summary>
        /// Grow by doubling rather than to the exact size, so a stream whose frames creep
        /// upward by a byte at a time does not reallocate on almost every tick.
        /// </summary>
        private void EnsureCapacity(int needed)
        {
            if (Buffer.Length >= needed) return;

            int capacity = Buffer.Length == 0 ? 256 : Buffer.Length;
            while (capacity < needed) capacity *= 2;

            var grown = new byte[capacity];
            Buffer.AsSpan(0, Written).CopyTo(grown);
            Buffer = grown;
        }
    }
}
