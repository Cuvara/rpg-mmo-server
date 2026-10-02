using System;
using System.Text;

namespace Shared.GameLogic.Gameplay
{
    /// <summary>
    /// A minimal, dependency-free proto3 decoder over a span (ADR-30.4), the counterpart of
    /// <see cref="ProtoWriter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never throws on input.</b> Payloads arrive from clients, so every method answers
    /// <c>false</c> on malformed bytes — a truncated varint, a varint longer than 10 bytes, a
    /// length running past the end, field number 0, a group wire type, an unknown wire type —
    /// and the caller turns that into <see cref="GameplayErrors.InvalidPayload"/>. Exceptions
    /// are reserved for programming errors and are never driven by the bytes.
    /// </para>
    /// <para>
    /// <b>Compatible decoding rules</b>, matching Google.Protobuf: a known field arriving with
    /// an unexpected wire type is skipped as unknown; unknown fields are skipped; a repeated
    /// scalar field keeps the last value; a 32-bit field read from a longer varint keeps the
    /// low 32 bits. Strings decode as UTF-8 with replacement characters for invalid sequences.
    /// </para>
    /// <para>
    /// A <c>ref struct</c> over <see cref="ReadOnlySpan{T}"/>: reading allocates nothing but
    /// the strings it decodes. Embedded messages are read by taking their bytes with
    /// <see cref="TryReadLengthDelimited"/> and starting a new reader over them.
    /// </para>
    /// </remarks>
    public ref struct ProtoReader
    {
        /// <summary>Largest legal field number, 2^29 - 1.</summary>
        public const int MaxFieldNumber = (1 << 29) - 1;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        /// <summary>Starts reading at the beginning of <paramref name="data"/>.</summary>
        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        /// <summary>True when every byte has been consumed.</summary>
        public bool IsAtEnd => _pos >= _data.Length;

        /// <summary>Bytes consumed so far.</summary>
        public int Position => _pos;

        /// <summary>
        /// Reads the next field tag. False at the end of input (check <see cref="IsAtEnd"/>
        /// first to tell that apart) or when the tag is malformed: a bad varint, field number
        /// 0 or above <see cref="MaxFieldNumber"/>, or a wire type outside 0-5.
        /// </summary>
        public bool TryReadTag(out int fieldNumber, out ProtoWireType wireType)
        {
            fieldNumber = 0;
            wireType = ProtoWireType.Varint;
            if (!TryReadVarint(out ulong tag)) return false;
            if (tag > uint.MaxValue) return false;

            ulong field = tag >> 3;
            uint wt = (uint)(tag & 7);
            if (field == 0 || field > MaxFieldNumber || wt > 5) return false;

            fieldNumber = (int)field;
            wireType = (ProtoWireType)wt;
            return true;
        }

        /// <summary>Reads a varint of up to 10 bytes.</summary>
        public bool TryReadVarint(out ulong value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                if (_pos >= _data.Length) return false;
                byte b = _data[_pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }

            // An 11th continuation byte: not a varint any encoder produces.
            return false;
        }

        /// <summary>Reads a varint and keeps its low 32 bits (<c>uint32</c>, <c>int32</c>, <c>enum</c>).</summary>
        public bool TryReadUInt32(out uint value)
        {
            bool ok = TryReadVarint(out ulong v);
            value = (uint)v;
            return ok;
        }

        /// <summary>Reads an <c>int32</c> (a sign-extended varint).</summary>
        public bool TryReadInt32(out int value)
        {
            bool ok = TryReadVarint(out ulong v);
            value = unchecked((int)v);
            return ok;
        }

        /// <summary>Reads a zigzag <c>sint32</c>.</summary>
        public bool TryReadSInt32(out int value)
        {
            bool ok = TryReadVarint(out ulong v);
            uint u = (uint)v;
            value = (int)(u >> 1) ^ -(int)(u & 1);
            return ok;
        }

        /// <summary>Reads a zigzag <c>sint64</c>.</summary>
        public bool TryReadSInt64(out long value)
        {
            bool ok = TryReadVarint(out ulong v);
            value = (long)(v >> 1) ^ -(long)(v & 1);
            return ok;
        }

        /// <summary>Reads a <c>bool</c>: any non-zero varint is true.</summary>
        public bool TryReadBool(out bool value)
        {
            bool ok = TryReadVarint(out ulong v);
            value = v != 0;
            return ok;
        }

        /// <summary>Reads 4 bytes little-endian.</summary>
        public bool TryReadFixed32(out uint value)
        {
            value = 0;
            if (_data.Length - _pos < 4) return false;
            value = _data[_pos]
                    | (uint)_data[_pos + 1] << 8
                    | (uint)_data[_pos + 2] << 16
                    | (uint)_data[_pos + 3] << 24;
            _pos += 4;
            return true;
        }

        /// <summary>Reads 8 bytes little-endian.</summary>
        public bool TryReadFixed64(out ulong value)
        {
            value = 0;
            if (_data.Length - _pos < 8) return false;
            TryReadFixed32(out uint lo);
            TryReadFixed32(out uint hi);
            value = lo | (ulong)hi << 32;
            return true;
        }

        /// <summary>Reads a <c>float</c> (fixed32), bit-exact.</summary>
        public bool TryReadFloat(out float value)
        {
            bool ok = TryReadFixed32(out uint bits);
            value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return ok;
        }

        /// <summary>
        /// Reads a length prefix and returns that many bytes, without copying. False when the
        /// length is malformed or runs past the end.
        /// </summary>
        public bool TryReadLengthDelimited(out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            if (!TryReadVarint(out ulong len)) return false;
            if (len > (ulong)(_data.Length - _pos)) return false;

            bytes = _data.Slice(_pos, (int)len);
            _pos += (int)len;
            return true;
        }

        /// <summary>Reads a UTF-8 <c>string</c>. The only allocating read.</summary>
        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadLengthDelimited(out ReadOnlySpan<byte> bytes)) return false;
            if (bytes.IsEmpty) return true;
            value = Utf8.GetString(bytes);
            return true;
        }

        /// <summary>
        /// Skips the value of a field whose tag has just been read. False for group wire types
        /// (3, 4), which proto3 never produces, and for truncated values.
        /// </summary>
        public bool TrySkip(ProtoWireType wireType)
        {
            switch (wireType)
            {
                case ProtoWireType.Varint:
                    return TryReadVarint(out _);
                case ProtoWireType.Fixed64:
                    if (_data.Length - _pos < 8) return false;
                    _pos += 8;
                    return true;
                case ProtoWireType.LengthDelimited:
                    return TryReadLengthDelimited(out _);
                case ProtoWireType.Fixed32:
                    if (_data.Length - _pos < 4) return false;
                    _pos += 4;
                    return true;
                default:
                    return false;
            }
        }
    }
}
