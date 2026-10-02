using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Shared.GameLogic.Gameplay
{
    // Hand-written encoders/decoders for every message in gameplay.proto. Each one:
    //
    //   - Write(ProtoWriter) appends the message's fields in field-number order with proto3
    //     defaults elided, which is what makes the bytes identical to generated code;
    //   - TryRead(ReadOnlySpan<byte>, out T) decodes a complete payload and answers false,
    //     never throws, on malformed input;
    //   - MergeFrom(ReadOnlySpan<byte>) is the protobuf merge the decoder is built on: scalar
    //     fields last-one-wins, repeated fields append, a repeated singular message merges.
    //
    // Messages are mutable classes like generated code, because they are built field by
    // field by handlers and are not on a per-tick path. String properties are never null:
    // assigning null stores "" (the proto3 default), as generated code does by throwing.

    /// <summary>Opcode 1 request. No fields.</summary>
    public sealed class InventoryRequest
    {
        /// <summary>Writes nothing: the message has no fields.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
        }

        /// <summary>Encodes to a new array (always empty).</summary>
        public byte[] ToByteArray() => Array.Empty<byte>();

        /// <summary>Merges <paramref name="data"/> into this message. False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out _, out ProtoWireType wt)) return false;
                if (!r.TrySkip(wt)) return false;
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out InventoryRequest? message)
        {
            var m = new InventoryRequest();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>Opcode 2 request: pick up a world item entity.</summary>
    public sealed class PickupRequest
    {
        private string _entityId = string.Empty;

        /// <summary>Field 1: id of the world item entity.</summary>
        public string EntityId { get => _entityId; set => _entityId = value ?? string.Empty; }

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteStringField(1, _entityId);
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>Merges <paramref name="data"/> into this message. False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;
                if (field == 1 && wt == ProtoWireType.LengthDelimited)
                {
                    if (!r.TryReadString(out string s)) return false;
                    _entityId = s;
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PickupRequest? message)
        {
            var m = new PickupRequest();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>Opcode 3 request: wear a bag item in an equipment slot.</summary>
    public sealed class EquipRequest
    {
        private string _instanceId = string.Empty;
        private string _slot = string.Empty;

        /// <summary>Field 1: the bag item's instance id.</summary>
        public string InstanceId { get => _instanceId; set => _instanceId = value ?? string.Empty; }

        /// <summary>Field 2: lowercase equipment slot name ("weapon", "head", ...).</summary>
        public string Slot { get => _slot; set => _slot = value ?? string.Empty; }

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteStringField(1, _instanceId);
            writer.WriteStringField(2, _slot);
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>Merges <paramref name="data"/> into this message. False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;
                if (wt == ProtoWireType.LengthDelimited && (field == 1 || field == 2))
                {
                    if (!r.TryReadString(out string s)) return false;
                    if (field == 1) _instanceId = s; else _slot = s;
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out EquipRequest? message)
        {
            var m = new EquipRequest();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>Opcode 4 request: move what is worn in a slot back to the bag.</summary>
    public sealed class UnequipRequest
    {
        private string _slot = string.Empty;

        /// <summary>Field 1: lowercase equipment slot name.</summary>
        public string Slot { get => _slot; set => _slot = value ?? string.Empty; }

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteStringField(1, _slot);
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>Merges <paramref name="data"/> into this message. False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;
                if (field == 1 && wt == ProtoWireType.LengthDelimited)
                {
                    if (!r.TryReadString(out string s)) return false;
                    _slot = s;
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out UnequipRequest? message)
        {
            var m = new UnequipRequest();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>Opcode 5 request: use (consume) a bag item.</summary>
    public sealed class UseItemRequest
    {
        private string _instanceId = string.Empty;

        /// <summary>Field 1: the bag item's instance id.</summary>
        public string InstanceId { get => _instanceId; set => _instanceId = value ?? string.Empty; }

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteStringField(1, _instanceId);
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>Merges <paramref name="data"/> into this message. False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;
                if (field == 1 && wt == ProtoWireType.LengthDelimited)
                {
                    if (!r.TryReadString(out string s)) return false;
                    _instanceId = s;
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out UseItemRequest? message)
        {
            var m = new UseItemRequest();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>One stack of items a character holds, in the bag or worn.</summary>
    public sealed class ItemStack
    {
        private string _instanceId = string.Empty;
        private string _itemId = string.Empty;
        private string _container = string.Empty;
        private string _slot = string.Empty;

        /// <summary>Field 1: server-assigned id of this stack, unique per character.</summary>
        public string InstanceId { get => _instanceId; set => _instanceId = value ?? string.Empty; }

        /// <summary>Field 2: content item id (<see cref="Content.ItemDefinition.Id"/>).</summary>
        public string ItemId { get => _itemId; set => _itemId = value ?? string.Empty; }

        /// <summary>Field 3: stack size.</summary>
        public uint Quantity { get; set; }

        /// <summary>Field 4: <see cref="ItemContainers.Bag"/> or <see cref="ItemContainers.Equipped"/>.</summary>
        public string Container { get => _container; set => _container = value ?? string.Empty; }

        /// <summary>Field 5: equipment slot when equipped; empty in the bag.</summary>
        public string Slot { get => _slot; set => _slot = value ?? string.Empty; }

        /// <summary>Field 6: bag position when in the bag; 0 when equipped.</summary>
        public uint BagIndex { get; set; }

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.WriteStringField(1, _instanceId);
            writer.WriteStringField(2, _itemId);
            writer.WriteUInt32Field(3, Quantity);
            writer.WriteStringField(4, _container);
            writer.WriteStringField(5, _slot);
            writer.WriteUInt32Field(6, BagIndex);
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>Merges <paramref name="data"/> into this message. False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;

                if (wt == ProtoWireType.LengthDelimited && (field == 1 || field == 2 || field == 4 || field == 5))
                {
                    if (!r.TryReadString(out string s)) return false;
                    switch (field)
                    {
                        case 1: _instanceId = s; break;
                        case 2: _itemId = s; break;
                        case 4: _container = s; break;
                        default: _slot = s; break;
                    }
                }
                else if (wt == ProtoWireType.Varint && (field == 3 || field == 6))
                {
                    if (!r.TryReadUInt32(out uint v)) return false;
                    if (field == 3) Quantity = v; else BagIndex = v;
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ItemStack? message)
        {
            var m = new ItemStack();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>
    /// The complete inventory (bag and equipment). Result of opcodes 1 and 2; carried by push
    /// 100. Always complete, never a delta.
    /// </summary>
    public sealed class InventoryView
    {
        /// <summary>Field 1: every stack, in the order the server lists them.</summary>
        public List<ItemStack> Items { get; } = new List<ItemStack>();

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            for (int i = 0; i < Items.Count; i++)
            {
                ItemStack item = Items[i] ?? throw new InvalidOperationException("InventoryView.Items contains null.");
                int token = writer.BeginLengthDelimited(1);
                item.Write(writer);
                writer.EndLengthDelimited(token);
            }
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>Merges <paramref name="data"/> into this message (appending items). False on malformed input.</summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;
                if (field == 1 && wt == ProtoWireType.LengthDelimited)
                {
                    if (!r.TryReadLengthDelimited(out ReadOnlySpan<byte> body)) return false;
                    if (!ItemStack.TryRead(body, out ItemStack? item)) return false;
                    Items.Add(item);
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out InventoryView? message)
        {
            var m = new InventoryView();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>Push opcode 100: the inventory changed; carries the complete new view.</summary>
    public sealed class InventoryChanged
    {
        /// <summary>Field 1: the new inventory. Null means "not set" and is not written.</summary>
        public InventoryView? View { get; set; }

        /// <summary>Appends this message's fields to <paramref name="writer"/>.</summary>
        public void Write(ProtoWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (View == null) return;
            int token = writer.BeginLengthDelimited(1);
            View.Write(writer);
            writer.EndLengthDelimited(token);
        }

        /// <summary>Encodes to a new array.</summary>
        public byte[] ToByteArray() => GameplayCodec.Encode(Write);

        /// <summary>
        /// Merges <paramref name="data"/> into this message. A repeated <c>view</c> field
        /// merges into the existing view, as protobuf requires. False on malformed input.
        /// </summary>
        public bool MergeFrom(ReadOnlySpan<byte> data)
        {
            var r = new ProtoReader(data);
            while (!r.IsAtEnd)
            {
                if (!r.TryReadTag(out int field, out ProtoWireType wt)) return false;
                if (field == 1 && wt == ProtoWireType.LengthDelimited)
                {
                    if (!r.TryReadLengthDelimited(out ReadOnlySpan<byte> body)) return false;
                    var view = View ?? new InventoryView();
                    if (!view.MergeFrom(body)) return false;
                    View = view;
                }
                else if (!r.TrySkip(wt))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Decodes a payload. False, with <paramref name="message"/> null, on malformed input.</summary>
        public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out InventoryChanged? message)
        {
            var m = new InventoryChanged();
            message = m.MergeFrom(data) ? m : null;
            return message != null;
        }
    }

    /// <summary>Helpers shared by the gameplay message types.</summary>
    public static class GameplayCodec
    {
        /// <summary>Runs <paramref name="write"/> against a fresh writer and returns the bytes.</summary>
        public static byte[] Encode(Action<ProtoWriter> write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            var w = new ProtoWriter();
            write(w);
            return w.ToArray();
        }
    }
}
