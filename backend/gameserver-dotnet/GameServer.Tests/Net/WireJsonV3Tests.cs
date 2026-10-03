using System.Text;
using Google.Protobuf;
using GameServer.Net;
using RpgMmo.Wire.V1;
using Envelope = GameServer.Net.Envelope;
using GameEvent = RpgMmo.Wire.V1.GameEvent;
using GameEventType = RpgMmo.Wire.V1.GameEventType;

namespace GameServer.Tests.Net;

/// <summary>
/// Legacy JSON for every protocol 3 field and message (ADR-28..31), with the names of
/// <c>wire.proto</c> / the Go struct tags in <c>shared/messages/messages.go</c>, Go's
/// <c>omitempty</c> presence rules, and Go's padded standard base64 for <c>[]byte</c>.
/// </summary>
public class WireJsonV3Tests
{
    private static T RoundTrip<T>(MsgType type, Envelope env) where T : class
    {
        Envelope back = WireProtocol.DecodeBody(WireProtocol.EncodeBody(env));
        Assert.Equal(WireEncoding.Json, back.Encoding);
        Assert.Equal((uint)type, back.Type);
        return WireProtocol.GetPayload<T>(back);
    }

    private static string Json(Envelope env) => Encoding.UTF8.GetString(env.Payload);

    [Fact]
    public void JoinTokenResponse_CharacterId_RoundTrips_AndIsOmittedWhenEmpty()
    {
        var env = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
            new JoinTokenResponse { Ok = true, UserId = "u1", TickRate = 60, ProtocolVersion = 3, CharacterId = "c-9" },
            WireEncoding.Json);
        Assert.Contains("\"character_id\":\"c-9\"", Json(env));
        Assert.Equal("c-9", RoundTrip<JoinTokenResponse>(MsgType.JoinTokenResp, env).CharacterId);

        var none = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
            new JoinTokenResponse { Ok = true, UserId = "u1" }, WireEncoding.Json);
        Assert.DoesNotContain("character_id", Json(none));
    }

    [Fact]
    public void InputMessage_V3Extras_RoundTrip_AndAreOmittedWhenZero()
    {
        var msg = new InputMessage
        {
            Tick = 5, MoveX = 1f, AimX = 2f, AimY = 3f, AimZ = 1.5f, RenderTick = 1234567890123UL,
            RenderAlpha = 0.25f, Jump = true, SpawnSeq = 77, AbilityId = 1,
        };
        var env = WireProtocol.NewEnvelope(MsgType.Input, msg, WireEncoding.Json);
        string json = Json(env);
        foreach (string name in new[] { "\"aim_z\":", "\"render_tick\":1234567890123", "\"render_alpha\":", "\"jump\":true", "\"spawn_seq\":77" })
            Assert.Contains(name, json);
        Assert.Equal(msg, RoundTrip<InputMessage>(MsgType.Input, env));

        var plain = WireProtocol.NewEnvelope(MsgType.Input, new InputMessage { Tick = 1 }, WireEncoding.Json);
        foreach (string name in new[] { "aim_z", "render_tick", "render_alpha", "jump", "spawn_seq" })
            Assert.DoesNotContain(name, Json(plain));
    }

    /// <summary>A Go client's bytes (encoding/json over shared/messages.InputMessage) decode to the same input.</summary>
    [Fact]
    public void InputMessage_FromGoJson_Decodes()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "{\"type\":7,\"payload\":{\"tick\":9,\"move_x\":0,\"move_y\":1,\"aim_z\":2.5,\"render_tick\":40," +
            "\"jump\":true,\"spawn_seq\":3,\"render_alpha\":0.5}}");
        InputMessage m = WireProtocol.GetPayload<InputMessage>(WireProtocol.DecodeBody(body));
        Assert.Equal(2.5f, m.AimZ);
        Assert.Equal(40UL, m.RenderTick);
        Assert.True(m.Jump);
        Assert.Equal(3u, m.SpawnSeq);
        Assert.Equal(0.5f, m.RenderAlpha);
    }

    [Fact]
    public void CommandMessages_RoundTrip_WithBase64Payloads()
    {
        byte[] payload = { 0x0A, 0x05, (byte)'i', (byte)'t', (byte)'e', (byte)'m', (byte)'1' };

        var req = new CommandRequest { Seq = 3, Opcode = 2, Payload = ByteString.CopyFrom(payload) };
        var reqEnv = WireProtocol.NewEnvelope(MsgType.Command, req, WireEncoding.Json);
        Assert.Equal("{\"seq\":3,\"opcode\":2,\"payload\":\"" + Convert.ToBase64String(payload) + "\"}", Json(reqEnv));
        Assert.Equal(req, RoundTrip<CommandRequest>(MsgType.Command, reqEnv));

        var res = new CommandResult { Seq = 3, Ok = false, Error = "not_found" };
        var resEnv = WireProtocol.NewEnvelope(MsgType.CommandResult, res, WireEncoding.Json);
        Assert.Equal("{\"seq\":3,\"ok\":false,\"error\":\"not_found\"}", Json(resEnv));
        Assert.Equal(res, RoundTrip<CommandResult>(MsgType.CommandResult, resEnv));

        var ok = new CommandResult { Seq = 4, Ok = true, Payload = ByteString.CopyFrom(payload) };
        Assert.Equal(ok, RoundTrip<CommandResult>(MsgType.CommandResult,
            WireProtocol.NewEnvelope(MsgType.CommandResult, ok, WireEncoding.Json)));

        var push = new ServerPush { Opcode = 100, Payload = ByteString.CopyFrom(payload) };
        var pushEnv = WireProtocol.NewEnvelope(MsgType.ServerPush, push, WireEncoding.Json);
        Assert.Equal("{\"opcode\":100,\"payload\":\"" + Convert.ToBase64String(payload) + "\"}", Json(pushEnv));
        Assert.Equal(push, RoundTrip<ServerPush>(MsgType.ServerPush, pushEnv));

        // Empty payload: omitted (Go `payload,omitempty`), and Go's null decodes as empty.
        Assert.Equal("{\"seq\":1,\"opcode\":1}",
            Json(WireProtocol.NewEnvelope(MsgType.Command, new CommandRequest { Seq = 1, Opcode = 1 }, WireEncoding.Json)));
        CommandRequest fromGo = WireProtocol.GetPayload<CommandRequest>(WireProtocol.DecodeBody(
            Encoding.UTF8.GetBytes("{\"type\":32,\"payload\":{\"seq\":1,\"opcode\":1,\"payload\":null}}")));
        Assert.Equal(0, fromGo.Payload.Length);
    }

    [Fact]
    public void Snapshot_V3EntityFieldsAndEventEffectId_RoundTrip()
    {
        var e = new EntitySnapshot
        {
            Id = "proj-1", Type = EntityType.Projectile, X = 1f, Y = 2f, Hp = 0, MaxHp = 0, Speed = 6f,
            Z = 1.25f, VelX = 6f, VelY = -1f, VelZ = 0.5f, OwnerId = "p1", SpawnSeq = 9, ChangedFields = 0,
        };
        e.Stats.Add(new StatValue { StatId = 2, Value = -5 });
        e.StatsRemoved.Add(3);
        e.Statuses.Add(new StatusEffect { EffectId = 1, Stacks = 2, ExpiresTick = 900, Source = 0 });
        e.StatusesRemoved.Add(4);
        var snap = new SnapshotMessage { Tick = 10, Full = true };
        snap.Entities.Add(e);
        snap.Events.Add(new GameEvent { Type = GameEventType.StatusApplied, TargetId = "p1", Amount = 2, EffectId = 1 });

        var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snap, WireEncoding.Json);
        string json = Json(env);
        foreach (string name in new[]
                 {
                     "\"z\":1.25", "\"vel_x\":6", "\"vel_y\":-1", "\"vel_z\":0.5", "\"owner_id\":\"p1\"", "\"spawn_seq\":9",
                     "\"stats\":[{\"stat_id\":2,\"value\":-5}]", "\"stats_removed\":[3]",
                     "\"statuses\":[{\"effect_id\":1,\"stacks\":2,\"expires_tick\":900}]", "\"statuses_removed\":[4]",
                     "\"effect_id\":1",
                 })
            Assert.Contains(name, json);
        Assert.DoesNotContain("\"owner\":", json); // JSON never interns: owner stays 0, omitted

        SnapshotMessage back = RoundTrip<SnapshotMessage>(MsgType.Snapshot, env);
        Assert.Equal(snap, back);
    }

    /// <summary>A protocol 2 entity (no v3 field set) writes none of the v3 names: protocol 2 JSON bytes are unchanged.</summary>
    [Fact]
    public void Snapshot_V2Entity_WritesNoV3Names()
    {
        var snap = new SnapshotMessage { Tick = 1 };
        snap.Entities.Add(new EntitySnapshot { Id = "p1", Type = EntityType.Player, X = 1f, Y = 2f, Hp = 5, MaxHp = 10, Speed = 4f });
        string json = Json(WireProtocol.NewEnvelope(MsgType.Snapshot, snap, WireEncoding.Json));
        Assert.Equal(
            "{\"tick\":1,\"entities\":[{\"id\":\"p1\",\"type\":\"player\",\"x\":1,\"y\":2,\"hp\":5,\"max_hp\":10,\"speed\":4}]}",
            json);
    }
}
