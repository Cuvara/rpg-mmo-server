using System;
using System.Text;

namespace Shared.GameLogic.Gameplay
{
    /// <summary>Protobuf wire types (the low three bits of a field tag).</summary>
    public enum ProtoWireType
    {
        /// <summary>int32, int64, uint32, uint64, sint32, sint64, bool, enum.</summary>
        Varint = 0,

        /// <summary>fixed64, sfixed64, double.</summary>
        Fixed64 = 1,

        /// <summary>string, bytes, embedded messages, packed repeated fields.</summary>
        LengthDelimited = 2,

        /// <summary>Deprecated group start. Never written; refused when read.</summary>
        StartGroup = 3,

        /// <summary>Deprecated group end. Never written; refused when read.</summary>
        EndGroup = 4,

        /// <summary>fixed32, sfixed32, float.</summary>
        Fixed32 = 5,
    }

    /// <summary>
    /// A minimal, dependency-free proto3 encoder (ADR-30.4), byte-compatible with
    /// Google.Protobuf for every construct it supports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not Google.Protobuf.</b> The Unity client compiles <c>Shared.GameLogic</c> as
    /// source with no package dependencies, so the gameplay payloads need a codec that lives
    /// in this library. It covers what <c>gameplay.proto</c> uses and little more: varints,
    /// zigzag, fixed32/fixed64, length-delimited strings and bytes, and nested messages.
    /// <c>GameServer.Tests/Gameplay</c> proves its output equal to protoc-generated code.
    /// </para>
    /// <para>
    /// <b>proto3 defaults are elided</b> by the <c>Write*Field</c> methods (a zero number, a
    /// false bool, an empty string or byte string writes nothing), exactly as generated code
    /// does for singular proto3 fields. That is what makes the bytes identical, not merely
    /// equivalent. A message writes its fields in field-number order for the same reason.
    /// </para>
    /// <para>
    /// <b>Reuse.</b> The buffer grows as needed and survives <see cref="Reset"/>, so a writer
    /// kept per connection encodes without allocating once warm, apart from the final
    /// <see cref="ToArray"/> if the caller needs an owned copy.
    /// </para>
    /// </remarks>
    public sealed class ProtoWriter
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private byte[] _buffer;
        private int _length;

        /// <summary>Creates a writer with a small initial buffer.</summary>
        public ProtoWriter() : this(64)
        {
        }

        /// <summary>Creates a writer with <paramref name="initialCapacity"/> bytes of buffer.</summary>
        public ProtoWriter(int initialCapacity)
        {
            _buffer = new byte[initialCapacity < 16 ? 16 : initialCapacity];
        }

        /// <summary>Bytes written so far.</summary>
        public int Length => _length;

        /// <summary>The bytes written so far. Valid until the next write or <see cref="Reset"/>.</summary>
        public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(_buffer, 0, _length);

        /// <summary>Discards everything written, keeping the buffer.</summary>
        public void Reset() => _length = 0;

        /// <summary>Copies the bytes written so far into a new array.</summary>
        public byte[] ToArray()
        {
            if (_length == 0) return Array.Empty<byte>();
            var copy = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, copy, 0, _length);
            return copy;
        }

        // ── Raw primitives ─────────────────────────────────────────────────────────

        /// <summary>Writes a field tag: <c>(fieldNumber &lt;&lt; 3) | wireType</c> as a varint.</summary>
        public void WriteTag(int fieldNumber, ProtoWireType wireType)
        {
            if (fieldNumber < 1 || fieldNumber > ProtoReader.MaxFieldNumber)
                throw new ArgumentOutOfRangeException(nameof(fieldNumber), fieldNumber, "Field numbers are 1 to 2^29-1.");
            WriteVarint(((uint)fieldNumber << 3) | (uint)wireType);
        }

        /// <summary>Writes an unsigned varint (1-10 bytes).</summary>
        public void WriteVarint(ulong value)
        {
            Ensure(10);
            while (value >= 0x80)
            {
                _buffer[_length++] = (byte)(value | 0x80);
                value >>= 7;
            }

            _buffer[_length++] = (byte)value;
        }

        /// <summary>Writes 4 bytes little-endian.</summary>
        public void WriteFixed32(uint value)
        {
            Ensure(4);
            _buffer[_length++] = (byte)value;
            _buffer[_length++] = (byte)(value >> 8);
            _buffer[_length++] = (byte)(value >> 16);
            _buffer[_length++] = (byte)(value >> 24);
        }

        /// <summary>Writes 8 bytes little-endian.</summary>
        public void WriteFixed64(ulong value)
        {
            WriteFixed32((uint)value);
            WriteFixed32((uint)(value >> 32));
        }

        /// <summary>Writes a length prefix followed by <paramref name="bytes"/>.</summary>
        public void WriteLengthDelimited(ReadOnlySpan<byte> bytes)
        {
            WriteVarint((uint)bytes.Length);
            Ensure(bytes.Length);
            bytes.CopyTo(new Span<byte>(_buffer, _length, bytes.Length));
            _length += bytes.Length;
        }

        /// <summary>Writes a length prefix followed by the UTF-8 bytes of <paramref name="value"/>.</summary>
        public void WriteString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            int byteCount = Utf8.GetByteCount(value);
            WriteVarint((uint)byteCount);
            Ensure(byteCount);
            Utf8.GetBytes(value, 0, value.Length, _buffer, _length);
            _length += byteCount;
        }

        /// <summary>Zigzag-encodes a signed 32-bit value (proto <c>sint32</c>).</summary>
        public static uint ZigZag32(int value) => (uint)((value << 1) ^ (value >> 31));

        /// <summary>Zigzag-encodes a signed 64-bit value (proto <c>sint64</c>).</summary>
        public static ulong ZigZag64(long value) => (ulong)((value << 1) ^ (value >> 63));

        // ── Singular proto3 fields (default values elided) ───────────────────────────

        /// <summary><c>uint32</c> field; nothing when 0.</summary>
        public void WriteUInt32Field(int fieldNumber, uint value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint(value);
        }

        /// <summary><c>uint64</c> field; nothing when 0.</summary>
        public void WriteUInt64Field(int fieldNumber, ulong value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint(value);
        }

        /// <summary>
        /// <c>int32</c> field; nothing when 0. A negative value is sign-extended to 10 bytes,
        /// as the protobuf spec requires.
        /// </summary>
        public void WriteInt32Field(int fieldNumber, int value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint((ulong)(long)value);
        }

        /// <summary><c>int64</c> field; nothing when 0.</summary>
        public void WriteInt64Field(int fieldNumber, long value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint((ulong)value);
        }

        /// <summary><c>sint32</c> field (zigzag); nothing when 0.</summary>
        public void WriteSInt32Field(int fieldNumber, int value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint(ZigZag32(value));
        }

        /// <summary><c>sint64</c> field (zigzag); nothing when 0.</summary>
        public void WriteSInt64Field(int fieldNumber, long value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint(ZigZag64(value));
        }

        /// <summary><c>bool</c> field; nothing when false.</summary>
        public void WriteBoolField(int fieldNumber, bool value)
        {
            if (!value) return;
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint(1);
        }

        /// <summary>
        /// <c>float</c> field (fixed32); nothing when the value is +0.0. Like generated code,
        /// the test is on the BIT PATTERN, so -0.0 and NaN are written.
        /// </summary>
        public void WriteFloatField(int fieldNumber, float value)
        {
            uint bits = SingleToUInt32Bits(value);
            if (bits == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Fixed32);
            WriteFixed32(bits);
        }

        /// <summary><c>fixed32</c> field; nothing when 0.</summary>
        public void WriteFixed32Field(int fieldNumber, uint value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Fixed32);
            WriteFixed32(value);
        }

        /// <summary><c>fixed64</c> field; nothing when 0.</summary>
        public void WriteFixed64Field(int fieldNumber, ulong value)
        {
            if (value == 0) return;
            WriteTag(fieldNumber, ProtoWireType.Fixed64);
            WriteFixed64(value);
        }

        /// <summary><c>string</c> field; nothing when null or empty.</summary>
        public void WriteStringField(int fieldNumber, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
            WriteString(value!);
        }

        /// <summary><c>bytes</c> field; nothing when empty.</summary>
        public void WriteBytesField(int fieldNumber, ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty) return;
            WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
            WriteLengthDelimited(value);
        }

        // ── Nested messages ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts an embedded message (or any length-delimited field whose length is not known
        /// up front) as field <paramref name="fieldNumber"/>. Write its contents, then pass the
        /// returned token to <see cref="EndLengthDelimited"/>.
        /// </summary>
        /// <remarks>
        /// Unlike the scalar writers this ALWAYS writes the field, even when the message turns
        /// out empty — which is what generated code does for a set message field and for each
        /// element of a repeated message field. Leave a null singular message unwritten by not
        /// calling this at all.
        /// </remarks>
        public int BeginLengthDelimited(int fieldNumber)
        {
            WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
            // Reserve one byte for the length: right for any body under 128 bytes, and
            // EndLengthDelimited shifts the body when it is longer.
            Ensure(1);
            int token = _length;
            _buffer[_length++] = 0;
            return token;
        }

        /// <summary>Completes a field started by <see cref="BeginLengthDelimited"/>.</summary>
        public void EndLengthDelimited(int token)
        {
            if (token < 0 || token >= _length) throw new ArgumentOutOfRangeException(nameof(token));

            int bodyStart = token + 1;
            int bodyLength = _length - bodyStart;
            int prefix = VarintSize((uint)bodyLength);
            if (prefix > 1)
            {
                Ensure(prefix - 1);
                Buffer.BlockCopy(_buffer, bodyStart, _buffer, bodyStart + prefix - 1, bodyLength);
                _length += prefix - 1;
            }

            uint v = (uint)bodyLength;
            int at = token;
            while (v >= 0x80)
            {
                _buffer[at++] = (byte)(v | 0x80);
                v >>= 7;
            }

            _buffer[at] = (byte)v;
        }

        /// <summary>Bytes a varint encoding of <paramref name="value"/> takes.</summary>
        public static int VarintSize(ulong value)
        {
            int n = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                n++;
            }

            return n;
        }

        private void Ensure(int extra)
        {
            int needed = _length + extra;
            if (needed <= _buffer.Length) return;
            int size = _buffer.Length * 2;
            if (size < needed) size = needed;
            Array.Resize(ref _buffer, size);
        }

        private static uint SingleToUInt32Bits(float value) =>
            unchecked((uint)BitConverter.SingleToInt32Bits(value));
    }
}
