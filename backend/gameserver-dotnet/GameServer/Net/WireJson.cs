using System.Buffers;
using System.Text.Json;
using RpgMmo.Wire.V1;

namespace GameServer.Net;

/// <summary>
/// Hand-written JSON serializers for the wire messages, matching the Go
/// <c>shared/messages</c> struct tags byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the generated Protobuf types can be the <i>only</i> message
/// classes in the game server while the legacy JSON encoding stays supported
/// during migration. The two alternatives were both worse: keeping a parallel
/// set of hand-written C# JSON classes reintroduces exactly the two-definitions
/// drift that <c>wire.proto</c> removes, and Protobuf's own
/// <c>JsonFormatter</c> emits camelCase and walks descriptors reflectively, so
/// it matches neither this wire format nor NativeAOT.
/// </para>
/// <para>
/// Field presence rules mirror Go's <c>omitempty</c>: empty strings, zero
/// <c>ack_tick</c>, false <c>full</c> and empty <c>removed</c> are omitted;
/// <c>ok</c>, <c>tick</c>, <c>entities</c> and every entity field are always
/// written. Deviating from this silently changes the bytes a pre-Protobuf client
/// sees, so <c>WireProtocolTests</c> pins it.
/// </para>
/// <para>No reflection, no serializer context: NativeAOT-safe by construction.</para>
/// </remarks>
internal static class JsonWriter
{
    internal static byte[] Write(JoinTokenResponse m)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok"u8, m.Ok);
            if (m.UserId.Length > 0) w.WriteString("user_id"u8, m.UserId);
            if (m.Error.Length > 0) w.WriteString("error"u8, m.Error);
            // Omitted when 0, matching protobuf's default-is-absent behaviour so the two
            // encodings carry the same information: absent means "not supplied", and a
            // client must refuse to predict rather than assume a rate (#93).
            if (m.TickRate > 0) w.WriteNumber("tick_rate"u8, m.TickRate);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(SnapshotMessage m)
    {
        // 64 bytes of frame + ~64 per entity is a close enough first guess to
        // avoid the writer growing its buffer on a typical AOI set.
        var buffer = new ArrayBufferWriter<byte>(64 + m.Entities.Count * 64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("tick"u8, m.Tick);
            if (m.AckTick != 0) w.WriteNumber("ack_tick"u8, m.AckTick);
            if (m.Full) w.WriteBoolean("full"u8, true);

            w.WriteStartArray("entities"u8);
            for (int i = 0; i < m.Entities.Count; i++)
            {
                var e = m.Entities[i];
                w.WriteStartObject();
                w.WriteString("id"u8, e.Id);
                // JSON keeps the string form: it is the legacy encoding and a
                // pre-enum client parses "type" as text. The enum is a Protobuf
                // wire optimisation, not a protocol-wide change of meaning.
                w.WriteString("type"u8, EntityTypes.NameOf(e));
                w.WriteNumber("x"u8, e.X);
                w.WriteNumber("y"u8, e.Y);
                w.WriteNumber("hp"u8, e.Hp);
                w.WriteNumber("max_hp"u8, e.MaxHp);
                w.WriteNumber("speed"u8, e.Speed);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (m.Removed.Count > 0)
            {
                w.WriteStartArray("removed"u8);
                for (int i = 0; i < m.Removed.Count; i++) w.WriteStringValue(m.Removed[i]);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(InputMessage m)
    {
        var buffer = new ArrayBufferWriter<byte>(96);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("tick"u8, m.Tick);
            w.WriteNumber("move_x"u8, m.MoveX);
            w.WriteNumber("move_y"u8, m.MoveY);
            if (m.AttackTargetId.Length > 0) w.WriteString("attack_target_id"u8, m.AttackTargetId);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(JoinTokenRequest m)
    {
        var buffer = new ArrayBufferWriter<byte>(m.Token.Length + 16);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("token"u8, m.Token);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(DisconnectMessage m)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (m.Reason.Length > 0) w.WriteString("reason"u8, m.Reason);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(TransferMapRequest m)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("map_id"u8, m.MapId);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(PingMessage m)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("timestamp"u8, m.Timestamp);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(TransferMapResponse m)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok"u8, m.Ok);
            if (m.Error.Length > 0) w.WriteString("error"u8, m.Error);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(PongMessage m)
    {
        var buffer = new ArrayBufferWriter<byte>(48);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("timestamp"u8, m.Timestamp);
            w.WriteNumber("server_time"u8, m.ServerTime);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(KickMessage m)
    {
        var buffer = new ArrayBufferWriter<byte>(48);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("reason"u8, m.Reason);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>
/// Hand-written JSON parsers, the inverse of <see cref="JsonWriter"/>.
/// </summary>
/// <remarks>
/// Unknown fields are skipped rather than rejected, matching Go's
/// <c>encoding/json</c>, so an old server tolerates a newer client's extra
/// fields. Missing fields keep the Protobuf default.
/// </remarks>
internal static class JsonReader
{
    internal static JoinTokenRequest ReadJoinTokenRequest(byte[] json)
    {
        var m = new JoinTokenRequest();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool token = r.ValueTextEquals("token"u8);
            if (!r.Read()) break;
            if (token) m.Token = r.GetString() ?? "";
            else r.Skip();
        }
        return m;
    }

    internal static JoinTokenResponse ReadJoinTokenResponse(byte[] json)
    {
        var m = new JoinTokenResponse();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool ok = r.ValueTextEquals("ok"u8);
            bool userId = r.ValueTextEquals("user_id"u8);
            bool error = r.ValueTextEquals("error"u8);
            bool tickRate = r.ValueTextEquals("tick_rate"u8);
            if (!r.Read()) break;
            if (ok) m.Ok = r.TokenType == JsonTokenType.True;
            else if (userId) m.UserId = r.GetString() ?? "";
            else if (error) m.Error = r.GetString() ?? "";
            else if (tickRate) m.TickRate = r.GetUInt32();
            else r.Skip();
        }
        return m;
    }

    internal static InputMessage ReadInputMessage(byte[] json)
    {
        var m = new InputMessage();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool tick = r.ValueTextEquals("tick"u8);
            bool moveX = r.ValueTextEquals("move_x"u8);
            bool moveY = r.ValueTextEquals("move_y"u8);
            bool target = r.ValueTextEquals("attack_target_id"u8);
            if (!r.Read()) break;
            if (tick) m.Tick = r.GetUInt64();
            else if (moveX) m.MoveX = r.GetSingle();
            else if (moveY) m.MoveY = r.GetSingle();
            else if (target) m.AttackTargetId = r.TokenType == JsonTokenType.Null ? "" : r.GetString() ?? "";
            else r.Skip();
        }
        return m;
    }

    internal static SnapshotMessage ReadSnapshotMessage(byte[] json)
    {
        var m = new SnapshotMessage();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool tick = r.ValueTextEquals("tick"u8);
            bool ackTick = r.ValueTextEquals("ack_tick"u8);
            bool full = r.ValueTextEquals("full"u8);
            bool entities = r.ValueTextEquals("entities"u8);
            bool removed = r.ValueTextEquals("removed"u8);
            if (!r.Read()) break;

            if (tick) m.Tick = r.GetUInt64();
            else if (ackTick) m.AckTick = r.GetUInt64();
            else if (full) m.Full = r.TokenType == JsonTokenType.True;
            else if (entities) ReadEntities(ref r, m);
            else if (removed) ReadRemoved(ref r, m);
            else r.Skip();
        }
        return m;
    }

    private static void ReadEntities(ref Utf8JsonReader r, SnapshotMessage m)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            var e = new EntitySnapshot();
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                bool id = r.ValueTextEquals("id"u8);
                bool type = r.ValueTextEquals("type"u8);
                bool x = r.ValueTextEquals("x"u8);
                bool y = r.ValueTextEquals("y"u8);
                bool hp = r.ValueTextEquals("hp"u8);
                bool maxHp = r.ValueTextEquals("max_hp"u8);
                bool speed = r.ValueTextEquals("speed"u8);
                if (!r.Read()) break;
                if (id) e.Id = r.GetString() ?? "";
                else if (type) EntityTypes.SetType(e, r.GetString());
                else if (x) e.X = r.GetSingle();
                else if (y) e.Y = r.GetSingle();
                else if (hp) e.Hp = r.GetInt32();
                else if (maxHp) e.MaxHp = r.GetInt32();
                else if (speed) e.Speed = r.GetSingle();
                else r.Skip();
            }
            m.Entities.Add(e);
        }
    }

    private static void ReadRemoved(ref Utf8JsonReader r, SnapshotMessage m)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.String) m.Removed.Add(r.GetString() ?? "");
        }
    }

    internal static TransferMapRequest ReadTransferMapRequest(byte[] json)
    {
        var m = new TransferMapRequest();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool mapId = r.ValueTextEquals("map_id"u8);
            if (!r.Read()) break;
            if (mapId) m.MapId = r.GetString() ?? "";
            else r.Skip();
        }
        return m;
    }

    internal static PingMessage ReadPingMessage(byte[] json)
    {
        var m = new PingMessage();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool timestamp = r.ValueTextEquals("timestamp"u8);
            if (!r.Read()) break;
            if (timestamp) m.Timestamp = r.GetInt64();
            else r.Skip();
        }
        return m;
    }

    internal static TransferMapResponse ReadTransferMapResponse(byte[] json)
    {
        var m = new TransferMapResponse();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool ok = r.ValueTextEquals("ok"u8);
            bool error = r.ValueTextEquals("error"u8);
            if (!r.Read()) break;
            if (ok) m.Ok = r.TokenType == JsonTokenType.True;
            else if (error) m.Error = r.GetString() ?? "";
            else r.Skip();
        }
        return m;
    }

    internal static PongMessage ReadPongMessage(byte[] json)
    {
        var m = new PongMessage();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool timestamp = r.ValueTextEquals("timestamp"u8);
            bool serverTime = r.ValueTextEquals("server_time"u8);
            if (!r.Read()) break;
            if (timestamp) m.Timestamp = r.GetInt64();
            else if (serverTime) m.ServerTime = r.GetInt64();
            else r.Skip();
        }
        return m;
    }

    internal static KickMessage ReadKickMessage(byte[] json)
    {
        var m = new KickMessage();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool reason = r.ValueTextEquals("reason"u8);
            if (!r.Read()) break;
            if (reason) m.Reason = r.GetString() ?? "";
            else r.Skip();
        }
        return m;
    }

    private static void Expect(ref Utf8JsonReader r, JsonTokenType type)
    {
        if (!r.Read() || r.TokenType != type)
            throw new IOException($"Malformed JSON: expected {type}");
    }
}
