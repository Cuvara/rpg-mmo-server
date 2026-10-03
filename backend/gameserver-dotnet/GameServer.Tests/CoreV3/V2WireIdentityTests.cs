using System.Security.Cryptography;
using System.Text;
using GameServer.Net;
using GameServer.Snapshot;
using GameServer.World;
using RpgMmo.Wire.V1;
using GameEventType = Shared.GameLogic.Components.GameEventType;
using Shared.GameLogic.Components;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// Protocol 2 peers must receive, from a protocol 3 world, exactly the bytes the encoder
/// produced before protocol 3 existed (ADR-30 decision 5: v3 data goes only to v3 peers).
/// </summary>
/// <remarks>
/// <para>
/// <b>How the digests were made, because that is the whole value of this test.</b> They were
/// generated with the encoder as it was BEFORE the protocol 3 snapshot work (commit
/// 3f87b56, which still ignored every v3 field), fed this scenario with the protocol 3 GameEvent
/// types (7-9) filtered out in the harness - those types did not exist for a protocol 2 peer.
/// The scenario is deliberately rich in v3-only state: every actor carries a stat block, players
/// jump (z and vertical velocity move), statuses are applied and expire, damage over time ticks.
/// It has no projectile and no dropped item, the two v3-only ENTITY kinds (the version 2 shape
/// simply omits those; <see cref="SnapshotV3EncoderTests"/> covers that).
/// </para>
/// <para>
/// After the change the harness passes the UNFILTERED events and sets the peer version to 2: the
/// encoder itself must drop the v3 event types, leave z/velocity/owner/stats/statuses out of the
/// bytes and out of its change detection, and reproduce every digest exactly.
/// </para>
/// </remarks>
public class V2WireIdentityTests
{
    private const int Ticks = 150;
    private const int KeyframeInterval = 30;

    internal static (string digest, string head, int statusEvents) RunScenario(
        WireEncoding encoding, int maxSnapshotBytes, uint peerVersion, bool filterV3EventsInHarness)
    {
        using var f = new SimFixture(withLoot: false);
        string[] players = { "p0", "p1", "p2", "p3" };
        for (int i = 0; i < players.Length; i++) f.AddPlayer(players[i], -6f + i * 4f, 0f, hp: 100);
        f.AddMob("m0", 0f, 3f, hp: 5000);
        f.AddMob("m1", 4f, -3f, hp: 5000);
        f.AddMob("m2", 30f, 0f, hp: 5000); // outside some AOIs, enters as players walk

        var states = new SnapshotDeltaState[players.Length];
        for (int i = 0; i < players.Length; i++)
        {
            states[i] = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor(players[i]))
            {
                SelfId = players[i],
                MaxSnapshotBytes = maxSnapshotBytes,
                FieldDelta = true,
                PeerProtocolVersion = peerVersion,
            };
        }

        var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new StringBuilder();
        var buffer = new EntityView[64];
        var events = new List<PendingGameEvent>();
        int statusEvents = 0;

        for (int t = 1; t <= Ticks; t++)
        {
            ulong tick = (ulong)t;
            for (int i = 0; i < players.Length; i++)
            {
                float dir = ((t / (9 + i)) % 2 == 0) ? 1f : -1f;
                bool jump = (t + i * 7) % 23 == 0;
                if (t % 17 == i)
                {
                    // Ground snare on the mobs: damage + root status on everything hostile there.
                    f.Input(players[i], SimFixture.Cast(tick, SimFixture.Snare, aimX: 0f, aimY: 3f),
                        new InputExtras(jump, 0f, 0, 0f, 0));
                }
                else if (t % 29 == i)
                {
                    f.Input(players[i], SimFixture.Cast(tick, SimFixture.Mend), new InputExtras(jump, 0f, 0, 0f, 0));
                }
                else
                {
                    f.Input(players[i], SimFixture.Move(tick, dir, dir * 0.25f), new InputExtras(jump, 0f, 0, 0f, 0));
                }
            }

            if (t == 5) f.ApplyStatus("m1", SimFixture.Burning, source: "p0");
            if (t == 40) f.ApplyStatus("p2", SimFixture.Chilled, source: "m0");

            f.Events.Clear();
            f.Step();

            events.Clear();
            foreach (var ev in f.Events.Events)
            {
                if (ev.Data.Type is GameEventType.StatusApplied or GameEventType.StatusRemoved
                    or GameEventType.ProjectileHit)
                {
                    statusEvents++;
                    if (filterV3EventsInHarness) continue;
                }
                events.Add(ev);
            }

            for (int i = 0; i < players.Length; i++)
            {
                Vec2 anchor = default;
                ulong ack = 0;
                int count = 0;
                int observerKey = PendingGameEvent.NoKey;
                string id = players[i];
                f.World.ReadAll(r =>
                {
                    r.TryGetSnapshotAnchor(id, out anchor, out ack);
                    count = r.GetEntitiesInRange(anchor, 9f, buffer);
                    if (r.TryGetStableKey(id, out int k)) observerKey = k;
                });

                SnapshotMessage msg = states[i].Encode(
                    tick, ack, buffer.AsSpan(0, count), KeyframeInterval,
                    intern: encoding == WireEncoding.Proto, observer: anchor,
                    events: events.ToArray(), observerKey: observerKey);

                byte[] frame = WireProtocol.Encode(WireProtocol.NewEnvelope(MsgType.Snapshot, msg, encoding));
                sha.AppendData(frame);
                if (head.Length < 600)
                {
                    head.Append($"t{t} {id} full={msg.Full} n={msg.Entities.Count} rm={msg.Removed.Count} " +
                                $"ev={msg.Events.Count} bytes={frame.Length}\n");
                }
            }
        }

        return (Convert.ToHexString(sha.GetHashAndReset()), head.ToString(), statusEvents);
    }

    // Generated against commit 3f87b56's encoder (see the class remarks). Do not regenerate to
    // make a failure pass: a protocol 2 peer's bytes are not supposed to move.
    private const string ProtoBudgetDefault = "591F0A3B32011B3A42EF71B6157F2EFDD043CBC47DF4AC8B3BFE524CF1804328";
    private const string ProtoBudgetTight = "457EA7F069CFF77DEA6109BD5FF79629865AB8E1541E408BA4EE73A2FEEC86EB";
    private const string Json = "5DC61A00E56415975650F0E13D1189BE5B685FF4A9182E1A4C37731482BC2296";

    [Theory]
    [InlineData(WireEncoding.Proto, SnapshotDeltaState.DefaultMaxSnapshotBytes, ProtoBudgetDefault)]
    [InlineData(WireEncoding.Proto, 120, ProtoBudgetTight)]
    [InlineData(WireEncoding.Json, SnapshotDeltaState.DefaultMaxSnapshotBytes, Json)]
    public void ProtocolTwoPeer_GetsThePreV3Bytes_FromAV3World(WireEncoding encoding, int budget, string expected)
    {
        (string digest, string head, int statusEvents) =
            RunScenario(encoding, budget, peerVersion: 2, filterV3EventsInHarness: false);

        Assert.True(statusEvents > 0, "the scenario no longer produces status events; it proves less than it claims");
        Assert.True(expected == digest,
            $"protocol 2 snapshot stream changed ({encoding}, budget {budget}).\nexpected {expected}\n" +
            $"actual   {digest}\nfirst snapshots:\n{head}");
    }

    [Fact]
    public void TheScenarioExercisesKeyframesDeltasEventsAndShedding()
    {
        (_, string head, _) = RunScenario(WireEncoding.Proto, 120, peerVersion: 2, filterV3EventsInHarness: false);
        Assert.Contains("full=True", head);
        Assert.Contains("full=False", head);
    }
}
