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
            // Omitted when 0 for the same reason as tick_rate: absent must mean
            // "this server does not advertise a version", and a client reading 0
            // must not conclude "version zero". Unlike tick_rate this IS written
            // on a rejection, because a client refused for a version mismatch has
            // to be told which version it failed against.
            if (m.ProtocolVersion > 0) w.WriteNumber("protocol_version"u8, m.ProtocolVersion);
            // ADR-31, protocol 3. `character_id,omitempty` in Go: absent = the default character,
            // and the bytes a pre-slot server produced.
            if (m.CharacterId.Length > 0) w.WriteString("character_id"u8, m.CharacterId);
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
                // Omitted when zero, unlike speed. Zero is the reserved "not sent" value
                // for both of these (facing is biased by one precisely so that no real
                // angle is zero), so omitting a zero is not lossy here - it is the SAME
                // statement the Protobuf encoding makes by eliding the field. Writing an
                // explicit 0 would instead assert "the sender has a facing, and it is the
                // reserved value", which is not a thing.
                if (e.FacingBrad > 0) w.WriteNumber("facing_brad"u8, e.FacingBrad);
                if (e.Action != RpgMmo.Wire.V1.EntityAction.Unspecified)
                    w.WriteNumber("action"u8, (int)e.Action);
                // Same omit-when-zero rule, for the same reason: zero is this field's
                // reserved "not sent". Writing an explicit 0 would assert "there is a
                // retrigger counter and it holds the reserved value".
                //
                // It is NOT optional in the sense of "JSON clients do not need it". A JSON
                // client drives the same animator from the same level-triggered action
                // field and hits the same repeated-attack problem; omitting the counter
                // here would leave one encoding able to retrigger and the other not, which
                // is a difference discovered by a player on the wrong client.
                if (e.ActionSeq != 0) w.WriteNumber("action_seq"u8, e.ActionSeq);
                // JSON never interns and never sends a partial entity, but the field exists in
                // both schemas (`changed_fields,omitempty`), so a non-zero mask is not silently
                // dropped by this writer.
                if (e.ChangedFields != 0) w.WriteNumber("changed_fields"u8, e.ChangedFields);
                WriteV3Entity(w, e);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (m.Removed.Count > 0)
            {
                w.WriteStartArray("removed"u8);
                for (int i = 0; i < m.Removed.Count; i++) w.WriteStringValue(m.Removed[i]);
                w.WriteEndArray();
            }

            // Events ride the snapshot in both encodings. A JSON connection never interns,
            // so the encoder filled source_id/target_id and left the handles at zero — see
            // SnapshotDeltaState.AppendEvents.
            if (m.Events.Count > 0)
            {
                w.WriteStartArray("events"u8);
                for (int i = 0; i < m.Events.Count; i++)
                {
                    var ev = m.Events[i];
                    w.WriteStartObject();
                    w.WriteNumber("type"u8, (int)ev.Type);
                    if (ev.SourceId.Length > 0) w.WriteString("source_id"u8, ev.SourceId);
                    if (ev.TargetId.Length > 0) w.WriteString("target_id"u8, ev.TargetId);
                    if (ev.Source != 0) w.WriteNumber("source"u8, ev.Source);
                    if (ev.Target != 0) w.WriteNumber("target"u8, ev.Target);
                    if (ev.Amount != 0) w.WriteNumber("amount"u8, ev.Amount);
                    if (ev.AbilityId != 0) w.WriteNumber("ability_id"u8, ev.AbilityId);
                    if (ev.Flags != 0) w.WriteNumber("flags"u8, ev.Flags);
                    if (ev.EffectId != 0) w.WriteNumber("effect_id"u8, ev.EffectId);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Protocol 3 entity fields (ADR-28..30), names and omit-when-zero exactly as the Go struct
    /// tags (`z,omitempty`, `stats,omitempty`, ...). The encoder leaves all of them at their
    /// defaults for a protocol 2 peer, so nothing here is written for one and its bytes are the
    /// protocol 2 bytes. `owner` is always 0 on JSON (no interning); `owner_id` names the owner.
    /// </summary>
    private static void WriteV3Entity(Utf8JsonWriter w, EntitySnapshot e)
    {
        if (e.Z != 0f) w.WriteNumber("z"u8, e.Z);
        if (e.VelX != 0f) w.WriteNumber("vel_x"u8, e.VelX);
        if (e.VelY != 0f) w.WriteNumber("vel_y"u8, e.VelY);
        if (e.VelZ != 0f) w.WriteNumber("vel_z"u8, e.VelZ);
        if (e.Owner != 0) w.WriteNumber("owner"u8, e.Owner);
        if (e.OwnerId.Length > 0) w.WriteString("owner_id"u8, e.OwnerId);
        if (e.SpawnSeq != 0) w.WriteNumber("spawn_seq"u8, e.SpawnSeq);

        if (e.Stats.Count > 0)
        {
            w.WriteStartArray("stats"u8);
            for (int i = 0; i < e.Stats.Count; i++)
            {
                // Go: `stat_id` and `value` without omitempty - both always written.
                w.WriteStartObject();
                w.WriteNumber("stat_id"u8, e.Stats[i].StatId);
                w.WriteNumber("value"u8, e.Stats[i].Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        if (e.StatsRemoved.Count > 0)
        {
            w.WriteStartArray("stats_removed"u8);
            for (int i = 0; i < e.StatsRemoved.Count; i++) w.WriteNumberValue(e.StatsRemoved[i]);
            w.WriteEndArray();
        }

        if (e.Statuses.Count > 0)
        {
            w.WriteStartArray("statuses"u8);
            for (int i = 0; i < e.Statuses.Count; i++)
            {
                var st = e.Statuses[i];
                // Go: `effect_id` always, the rest omitempty.
                w.WriteStartObject();
                w.WriteNumber("effect_id"u8, st.EffectId);
                if (st.Stacks != 0) w.WriteNumber("stacks"u8, st.Stacks);
                if (st.ExpiresTick != 0) w.WriteNumber("expires_tick"u8, st.ExpiresTick);
                if (st.Source != 0) w.WriteNumber("source"u8, st.Source);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        if (e.StatusesRemoved.Count > 0)
        {
            w.WriteStartArray("statuses_removed"u8);
            for (int i = 0; i < e.StatusesRemoved.Count; i++) w.WriteNumberValue(e.StatusesRemoved[i]);
            w.WriteEndArray();
        }
    }

    // Command channel (MsgType 32-34, ADR-30). Go marshals []byte as padded standard base64
    // and omits an empty one (`payload,omitempty`); `seq`, `opcode` and `ok` are always written.

    internal static byte[] Write(CommandRequest m)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + m.Payload.Length * 2);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq"u8, m.Seq);
            w.WriteNumber("opcode"u8, m.Opcode);
            if (m.Payload.Length > 0) w.WriteBase64String("payload"u8, m.Payload.Span);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(CommandResult m)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + m.Payload.Length * 2);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq"u8, m.Seq);
            w.WriteBoolean("ok"u8, m.Ok);
            if (m.Error.Length > 0) w.WriteString("error"u8, m.Error);
            if (m.Payload.Length > 0) w.WriteBase64String("payload"u8, m.Payload.Span);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] Write(ServerPush m)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + m.Payload.Length * 2);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("opcode"u8, m.Opcode);
            if (m.Payload.Length > 0) w.WriteBase64String("payload"u8, m.Payload.Span);
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
            // Written only when set, matching how proto3 elides a zero and an empty string.
            // The JSON encoding is legacy, and a peer reading it must reach the same
            // InputData as a peer reading the protobuf; emitting an explicit 0 here would
            // still decode the same, but it would put bytes on the wire that the encoding
            // this mirrors does not.
            if (m.AbilityId != 0) w.WriteNumber("ability_id"u8, m.AbilityId);
            if (m.AbilityTargetId.Length > 0) w.WriteString("ability_target_id"u8, m.AbilityTargetId);
            if (m.AimX != 0f) w.WriteNumber("aim_x"u8, m.AimX);
            if (m.AimY != 0f) w.WriteNumber("aim_y"u8, m.AimY);
            // Protocol 3 (ADR-28/29), each omitted when zero like the Go tags.
            if (m.AimZ != 0f) w.WriteNumber("aim_z"u8, m.AimZ);
            if (m.RenderTick != 0) w.WriteNumber("render_tick"u8, m.RenderTick);
            if (m.RenderAlpha != 0f) w.WriteNumber("render_alpha"u8, m.RenderAlpha);
            if (m.Jump) w.WriteBoolean("jump"u8, true);
            if (m.SpawnSeq != 0) w.WriteNumber("spawn_seq"u8, m.SpawnSeq);
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
            if (m.ProtocolVersion > 0) w.WriteNumber("protocol_version"u8, m.ProtocolVersion);
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
            bool protocolVersion = r.ValueTextEquals("protocol_version"u8);
            if (!r.Read()) break;
            if (token) m.Token = r.GetString() ?? "";
            // Absent leaves the Protobuf default 0, which the version check
            // reads as "did not advertise" — the same meaning as an elided
            // proto3 field, so both encodings agree without a second rule.
            else if (protocolVersion) m.ProtocolVersion = r.GetUInt32();
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
            bool protocolVersion = r.ValueTextEquals("protocol_version"u8);
            bool characterId = r.ValueTextEquals("character_id"u8);
            if (!r.Read()) break;
            if (ok) m.Ok = r.TokenType == JsonTokenType.True;
            else if (userId) m.UserId = r.GetString() ?? "";
            else if (error) m.Error = r.GetString() ?? "";
            else if (tickRate) m.TickRate = r.GetUInt32();
            else if (protocolVersion) m.ProtocolVersion = r.GetUInt32();
            else if (characterId) m.CharacterId = r.GetString() ?? "";
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
            bool abilityId = r.ValueTextEquals("ability_id"u8);
            bool abilityTarget = r.ValueTextEquals("ability_target_id"u8);
            bool aimX = r.ValueTextEquals("aim_x"u8);
            bool aimY = r.ValueTextEquals("aim_y"u8);
            bool aimZ = r.ValueTextEquals("aim_z"u8);
            bool renderTick = r.ValueTextEquals("render_tick"u8);
            bool renderAlpha = r.ValueTextEquals("render_alpha"u8);
            bool jump = r.ValueTextEquals("jump"u8);
            bool spawnSeq = r.ValueTextEquals("spawn_seq"u8);
            if (!r.Read()) break;
            if (tick) m.Tick = r.GetUInt64();
            else if (moveX) m.MoveX = r.GetSingle();
            else if (moveY) m.MoveY = r.GetSingle();
            else if (target) m.AttackTargetId = r.TokenType == JsonTokenType.Null ? "" : r.GetString() ?? "";
            else if (abilityId) m.AbilityId = r.GetUInt32();
            else if (abilityTarget) m.AbilityTargetId = r.TokenType == JsonTokenType.Null ? "" : r.GetString() ?? "";
            else if (aimX) m.AimX = r.GetSingle();
            else if (aimY) m.AimY = r.GetSingle();
            // Protocol 3. Absent leaves the zero "not sent" value, as in Protobuf.
            else if (aimZ) m.AimZ = r.GetSingle();
            else if (renderTick) m.RenderTick = r.GetUInt64();
            else if (renderAlpha) m.RenderAlpha = r.GetSingle();
            else if (jump) m.Jump = r.TokenType == JsonTokenType.True;
            else if (spawnSeq) m.SpawnSeq = r.GetUInt32();
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
            bool events = r.ValueTextEquals("events"u8);
            if (!r.Read()) break;

            if (tick) m.Tick = r.GetUInt64();
            else if (ackTick) m.AckTick = r.GetUInt64();
            else if (full) m.Full = r.TokenType == JsonTokenType.True;
            else if (entities) ReadEntities(ref r, m);
            else if (removed) ReadRemoved(ref r, m);
            else if (events) ReadEvents(ref r, m);
            else r.Skip();
        }
        return m;
    }

    private static void ReadEvents(ref Utf8JsonReader r, SnapshotMessage m)
    {
        if (r.TokenType != JsonTokenType.StartArray) return;

        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) continue;

            var ev = new RpgMmo.Wire.V1.GameEvent();
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                bool type = r.ValueTextEquals("type"u8);
                bool sourceId = r.ValueTextEquals("source_id"u8);
                bool targetId = r.ValueTextEquals("target_id"u8);
                bool source = r.ValueTextEquals("source"u8);
                bool target = r.ValueTextEquals("target"u8);
                bool amount = r.ValueTextEquals("amount"u8);
                bool abilityId = r.ValueTextEquals("ability_id"u8);
                bool flags = r.ValueTextEquals("flags"u8);
                bool effectId = r.ValueTextEquals("effect_id"u8);
                if (!r.Read()) break;

                // Absent leaves the protobuf default, which every one of these fields
                // defines as "not sent" — so the two encodings agree without a second rule,
                // exactly as the entity fields do.
                if (type) ev.Type = (RpgMmo.Wire.V1.GameEventType)r.GetInt32();
                else if (sourceId) ev.SourceId = r.GetString() ?? "";
                else if (targetId) ev.TargetId = r.GetString() ?? "";
                else if (source) ev.Source = r.GetUInt32();
                else if (target) ev.Target = r.GetUInt32();
                else if (amount) ev.Amount = r.GetInt32();
                else if (abilityId) ev.AbilityId = r.GetUInt32();
                else if (flags) ev.Flags = r.GetUInt32();
                else if (effectId) ev.EffectId = r.GetUInt32();
                else r.Skip();
            }

            m.Events.Add(ev);
        }
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
                bool facingBrad = r.ValueTextEquals("facing_brad"u8);
                bool action = r.ValueTextEquals("action"u8);
                bool actionSeq = r.ValueTextEquals("action_seq"u8);
                bool changedFields = r.ValueTextEquals("changed_fields"u8);
                bool handle = r.ValueTextEquals("handle"u8);
                bool z = r.ValueTextEquals("z"u8);
                bool velX = r.ValueTextEquals("vel_x"u8);
                bool velY = r.ValueTextEquals("vel_y"u8);
                bool velZ = r.ValueTextEquals("vel_z"u8);
                bool owner = r.ValueTextEquals("owner"u8);
                bool ownerId = r.ValueTextEquals("owner_id"u8);
                bool spawnSeq = r.ValueTextEquals("spawn_seq"u8);
                bool stats = r.ValueTextEquals("stats"u8);
                bool statsRemoved = r.ValueTextEquals("stats_removed"u8);
                bool statuses = r.ValueTextEquals("statuses"u8);
                bool statusesRemoved = r.ValueTextEquals("statuses_removed"u8);
                if (!r.Read()) break;
                if (id) e.Id = r.GetString() ?? "";
                else if (type) EntityTypes.SetType(e, r.GetString());
                else if (x) e.X = r.GetSingle();
                else if (y) e.Y = r.GetSingle();
                else if (hp) e.Hp = r.GetInt32();
                else if (maxHp) e.MaxHp = r.GetInt32();
                else if (speed) e.Speed = r.GetSingle();
                // Absent leaves the Protobuf default 0, which both fields define as
                // "not sent" - so the two encodings agree without a second rule.
                else if (facingBrad) e.FacingBrad = r.GetUInt32();
                else if (action) e.Action = (RpgMmo.Wire.V1.EntityAction)r.GetInt32();
                else if (actionSeq) e.ActionSeq = r.GetUInt32();
                else if (changedFields) e.ChangedFields = r.GetUInt32();
                else if (handle) e.Handle = r.GetUInt32();
                else if (z) e.Z = r.GetSingle();
                else if (velX) e.VelX = r.GetSingle();
                else if (velY) e.VelY = r.GetSingle();
                else if (velZ) e.VelZ = r.GetSingle();
                else if (owner) e.Owner = r.GetUInt32();
                else if (ownerId) e.OwnerId = r.GetString() ?? "";
                else if (spawnSeq) e.SpawnSeq = r.GetUInt32();
                else if (stats) ReadStats(ref r, e);
                else if (statsRemoved) ReadUInts(ref r, e.StatsRemoved);
                else if (statuses) ReadStatuses(ref r, e);
                else if (statusesRemoved) ReadUInts(ref r, e.StatusesRemoved);
                else r.Skip();
            }
            m.Entities.Add(e);
        }
    }

    private static void ReadStats(ref Utf8JsonReader r, EntitySnapshot e)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var v = new StatValue();
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                bool statId = r.ValueTextEquals("stat_id"u8);
                bool value = r.ValueTextEquals("value"u8);
                if (!r.Read()) break;
                if (statId) v.StatId = r.GetUInt32();
                else if (value) v.Value = r.GetInt32();
                else r.Skip();
            }
            e.Stats.Add(v);
        }
    }

    private static void ReadStatuses(ref Utf8JsonReader r, EntitySnapshot e)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
            var st = new StatusEffect();
            while (r.Read() && r.TokenType != JsonTokenType.EndObject)
            {
                bool effectId = r.ValueTextEquals("effect_id"u8);
                bool stacks = r.ValueTextEquals("stacks"u8);
                bool expiresTick = r.ValueTextEquals("expires_tick"u8);
                bool source = r.ValueTextEquals("source"u8);
                if (!r.Read()) break;
                if (effectId) st.EffectId = r.GetUInt32();
                else if (stacks) st.Stacks = r.GetUInt32();
                else if (expiresTick) st.ExpiresTick = r.GetUInt64();
                else if (source) st.Source = r.GetUInt32();
                else r.Skip();
            }
            e.Statuses.Add(st);
        }
    }

    private static void ReadUInts(ref Utf8JsonReader r, Google.Protobuf.Collections.RepeatedField<uint> into)
    {
        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.Number && r.TryGetUInt32(out uint v)) into.Add(v);
            else r.Skip();
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

    internal static CommandRequest ReadCommandRequest(byte[] json)
    {
        var m = new CommandRequest();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool seq = r.ValueTextEquals("seq"u8);
            bool opcode = r.ValueTextEquals("opcode"u8);
            bool payload = r.ValueTextEquals("payload"u8);
            if (!r.Read()) break;
            if (seq) m.Seq = r.GetUInt32();
            else if (opcode) m.Opcode = r.GetUInt32();
            else if (payload) m.Payload = ReadBytes(ref r);
            else r.Skip();
        }
        return m;
    }

    internal static CommandResult ReadCommandResult(byte[] json)
    {
        var m = new CommandResult();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool seq = r.ValueTextEquals("seq"u8);
            bool ok = r.ValueTextEquals("ok"u8);
            bool error = r.ValueTextEquals("error"u8);
            bool payload = r.ValueTextEquals("payload"u8);
            if (!r.Read()) break;
            if (seq) m.Seq = r.GetUInt32();
            else if (ok) m.Ok = r.TokenType == JsonTokenType.True;
            else if (error) m.Error = r.GetString() ?? "";
            else if (payload) m.Payload = ReadBytes(ref r);
            else r.Skip();
        }
        return m;
    }

    internal static ServerPush ReadServerPush(byte[] json)
    {
        var m = new ServerPush();
        var r = new Utf8JsonReader(json);
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            bool opcode = r.ValueTextEquals("opcode"u8);
            bool payload = r.ValueTextEquals("payload"u8);
            if (!r.Read()) break;
            if (opcode) m.Opcode = r.GetUInt32();
            else if (payload) m.Payload = ReadBytes(ref r);
            else r.Skip();
        }
        return m;
    }

    /// <summary>
    /// A Go <c>[]byte</c>: standard padded base64, or <c>null</c> for an empty slice. Anything
    /// else is malformed and throws, which the caller answers as <c>invalid_payload</c>.
    /// </summary>
    private static Google.Protobuf.ByteString ReadBytes(ref Utf8JsonReader r)
    {
        if (r.TokenType == JsonTokenType.Null) return Google.Protobuf.ByteString.Empty;
        return Google.Protobuf.UnsafeByteOperations.UnsafeWrap(r.GetBytesFromBase64());
    }

    private static void Expect(ref Utf8JsonReader r, JsonTokenType type)
    {
        if (!r.Read() || r.TokenType != type)
            throw new IOException($"Malformed JSON: expected {type}");
    }
}
