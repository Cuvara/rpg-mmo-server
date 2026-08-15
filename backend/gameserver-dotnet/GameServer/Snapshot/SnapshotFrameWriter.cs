using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using RpgMmo.Wire.V1;

namespace GameServer.Snapshot;

/// <summary>
/// Serializes a snapshot into a length-prefixed wire frame using buffers it keeps and
/// reuses, instead of allocating a fresh array per snapshot per tick.
///
/// <para>The path this replaces allocated four arrays for every snapshot sent:
/// <c>SnapshotMessage.ToByteArray()</c> for the payload, a <c>ByteString</c> copy of it
/// into the envelope, <c>Envelope.ToByteArray()</c> for the body, and the framed array
/// with the length prefix. At 200 players that was 44 280 B/tick. Here the payload and
/// the frame each go into a grown-once buffer, and the envelope wraps the payload with
/// <see cref="UnsafeByteOperations.UnsafeWrap"/> rather than copying it.</para>
///
/// <para><b>The bytes are unchanged by construction.</b> Both messages are still written
/// by the generated protobuf writers — this only changes where the bytes land. Nothing
/// here hand-rolls a tag or a varint, which is the version of this optimisation that
/// would have put the wire at risk. <c>SnapshotByteIdentityTests</c> is the guard.</para>
///
/// <para><b>Threading and lifetime:</b> one instance per connection, used only by that
/// connection's write task — the same single-threaded ownership as
/// <see cref="SnapshotDeltaState"/>. The returned memory is valid until the next call on
/// this instance, so the caller must finish writing it before asking for another frame.
/// The write task awaits its send before looping, which is exactly that.</para>
/// </summary>
public sealed class SnapshotFrameWriter
{
    private readonly GrowingBufferWriter _payload = new();
    private readonly GrowingBufferWriter _frame = new();

    /// <summary>
    /// Reused envelope wrapper. Only <c>Type</c> and <c>Payload</c> are ever set, and both
    /// are overwritten on every call, so no state survives between frames.
    /// </summary>
    private readonly Envelope _envelope = new();

    /// <summary>
    /// Serialize <paramref name="snapshot"/> as message <paramref name="type"/> into a
    /// length-prefixed frame. The result points into a buffer this instance owns and is
    /// valid until the next call.
    /// </summary>
    public ReadOnlyMemory<byte> WriteFrame(byte type, SnapshotMessage snapshot)
    {
        _payload.Reset(reserve: 0);
        snapshot.WriteTo(_payload);

        _envelope.Type = type;
        // Wrap, do not copy: the ByteString is a view over the payload buffer, and it is
        // replaced on the next call — well inside that buffer's validity window.
        _envelope.Payload = UnsafeByteOperations.UnsafeWrap(_payload.WrittenMemory);

        // Reserve the 4-byte length prefix, write the body after it, then fill the prefix
        // in once the body's length is known.
        _frame.Reset(reserve: 4);
        _envelope.WriteTo(_frame);

        int bodySize = _frame.Written - 4;
        BinaryPrimitives.WriteInt32BigEndian(_frame.Buffer.AsSpan(0, 4), bodySize);
        return _frame.Buffer.AsMemory(0, _frame.Written);
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
