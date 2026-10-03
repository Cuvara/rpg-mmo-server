using GameServer.Gameplay;
using GameServer.Net;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using GameEventType = Shared.GameLogic.Components.GameEventType;
using SimAction = Shared.GameLogic.Components.EntityAction;
using Envelope = GameServer.Net.Envelope;
using GameEvent = RpgMmo.Wire.V1.GameEvent;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// The protocol 3 snapshot shape (ADR-28..30): z, velocity, owner + owner-only spawn_seq,
/// stat blocks and statuses (complete on keyframe/introduction, changed-only with removals on a
/// delta), v3-only entity kinds, projectiles always due, exact budget sizing, and an
/// allocation-free steady state.
/// </summary>
public class SnapshotV3EncoderTests
{
    // Wide enough that a caster still sees its own projectile after its first 10-unit step.
    private const float Radius = 16f;

    /// <summary>Everything one viewer should hold for one entity, read from the world.</summary>
    private sealed record Truth(
        string Id, string Type, float X, float Y, float Z, int Hp, int MaxHp,
        float VelX, float VelY, float VelZ, string? OwnerId, uint SpawnSeq,
        StatValueData[] Stats, StatusEffectData[] Statuses);

    private static List<Truth> ReadTruth(SimFixture f, string viewer, out Vec2 anchor, out ulong ack,
        out int observerKey, EntityView[] buffer, SnapshotV3Gather gather, out int count)
    {
        var truths = new List<Truth>();
        Vec2 a = default;
        ulong k = 0;
        int ok = PendingGameEvent.NoKey;
        int n = 0;
        f.World.ReadAll(r =>
        {
            r.TryGetSnapshotAnchor(viewer, out a, out k);
            n = r.GetEntitiesInRange(a, Radius, buffer);
            if (r.TryGetStableKey(viewer, out int key)) ok = key;
            gather.Capture(r, buffer.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                EntityView v = buffer[i];
                var stats = new StatValueData[r.StatCount(v.Key)];
                r.CopyStats(v.Key, stats);
                var statuses = new StatusEffectData[r.StatusCount(v.Key)];
                r.CopyStatuses(v.Key, statuses);
                bool ballistic = v.Type == CombatResolver.ProjectileType || v.VelZ != 0f;
                truths.Add(new Truth(v.Id, v.Type, v.Position.X, v.Position.Y, v.Z, v.Hp, v.MaxHp,
                    ballistic ? v.VelX : 0f, ballistic ? v.VelY : 0f, ballistic ? v.VelZ : 0f,
                    v.OwnerId, v.SpawnSeq, stats, statuses));
            }
        });
        anchor = a;
        ack = k;
        observerKey = ok;
        count = n;
        return truths;
    }

    private static void AssertMatches(string viewer, List<Truth> truths, V3TestClient client, ulong tick)
    {
        Assert.Equal(truths.Count, client.Merger.Count);
        foreach (Truth t in truths)
        {
            Assert.True(client.Merger.TryGet(t.Id, out EntitySnapshotData got), $"t{tick} {viewer}: missing {t.Id}");
            string where = $"t{tick} {viewer} sees {t.Id}";
            Assert.True(t.X == got.X && t.Y == got.Y && t.Z == got.Z, $"{where}: position");
            Assert.True(t.Hp == got.Hp && t.MaxHp == got.MaxHp, $"{where}: hp");
            Assert.True(t.VelX == got.VelX && t.VelY == got.VelY && t.VelZ == got.VelZ, $"{where}: velocity");

            // spawn_seq reaches the owner's connection only (ADR-29.2).
            uint expectedSeq = t.OwnerId == viewer ? t.SpawnSeq : 0u;
            Assert.True(expectedSeq == got.SpawnSeq, $"{where}: spawn_seq {got.SpawnSeq}, want {expectedSeq}");
            if (t.OwnerId != null && got.OwnerId != null) Assert.Equal(t.OwnerId, got.OwnerId);

            Assert.True(t.Stats.SequenceEqual(got.Stats ?? Array.Empty<StatValueData>()),
                $"{where}: stats [{string.Join(",", t.Stats)}] vs [{string.Join(",", got.Stats ?? Array.Empty<StatValueData>())}]");

            StatusEffectData[] gs = got.Statuses ?? Array.Empty<StatusEffectData>();
            Assert.True(t.Statuses.Length == gs.Length, $"{where}: {t.Statuses.Length} statuses vs {gs.Length}");
            foreach (StatusEffectData s in t.Statuses)
            {
                StatusEffectData match = gs.SingleOrDefault(x => x.EffectId == s.EffectId);
                Assert.True(match.EffectId == s.EffectId && match.Stacks == s.Stacks && match.ExpiresTick == s.ExpiresTick,
                    $"{where}: status {s}");
                if (match.SourceId != null) Assert.Equal(s.SourceId, match.SourceId);
            }
        }
    }

    /// <summary>
    /// Drive a protocol 3 world (statuses, DoTs, jumps, projectiles with spawn_seq, loot drops)
    /// through the real encoder and a merging client, and require the client's reconstruction
    /// to equal the world every snapshot - keyframes, deltas, introductions, despawns.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Proto, true)]
    [InlineData(WireEncoding.Proto, false)]
    [InlineData(WireEncoding.Json, false)]
    public void V3Client_ReconstructsTheWorldExactly_EverySnapshot(WireEncoding encoding, bool fieldDelta) =>
        RunReconstruction(encoding, fieldDelta, budget: 0, quietTicks: 0);

    /// <summary>
    /// Under a byte budget tight enough to shed every snapshot, a protocol 3 client never meets an
    /// unbound handle, and once the world goes quiet it converges on the exact world: a shed stat
    /// or status update is deferred, never lost.
    /// </summary>
    [Fact]
    public void V3Client_UnderATightBudget_ConvergesExactly() =>
        RunReconstruction(WireEncoding.Proto, fieldDelta: true, budget: 140, quietTicks: 120);

    private static void RunReconstruction(WireEncoding encoding, bool fieldDelta, int budget, int quietTicks)
    {
        using var f = new SimFixture(withLoot: true);
        string[] players = { "p0", "p1", "p2" };
        for (int i = 0; i < players.Length; i++) f.AddPlayer(players[i], -4f + i * 4f, 0f);
        f.AddMob("m0", 0f, 3f, hp: 30);    // dies to the snares, drops loot
        f.AddMob("m1", 4f, -3f, hp: 5000);

        var states = players.Select(p => new SnapshotDeltaState(SnapshotDeltaState.PhaseFor(p))
        {
            SelfId = p, FieldDelta = fieldDelta, PeerProtocolVersion = 3, MaxSnapshotBytes = budget,
        }).ToArray();
        var clients = players.Select(_ => new V3TestClient()).ToArray();
        var buffer = new EntityView[64];
        var gather = new SnapshotV3Gather();
        bool sawProjectile = false, sawItem = false, sawStatus = false, sawSeqToOwner = false;

        const int Active = 160;
        for (int t = 1; t <= Active + quietTicks; t++)
        {
            ulong tick = (ulong)t;
            bool quiet = t > Active;
            for (int i = 0; i < players.Length && !quiet; i++)
            {
                float dir = ((t / (11 + i)) % 2 == 0) ? 1f : -1f;
                var extras = new InputExtras((t + i * 5) % 19 == 0, 0f, 0, 0f, 0);
                if (t % 13 == i) f.Input(players[i], SimFixture.Cast(tick, SimFixture.Snare, aimX: 0f, aimY: 3f), extras);
                else if (t % 17 == i)
                    f.Input(players[i], SimFixture.Cast(tick, SimFixture.Bolt, aimX: 4f, aimY: -3f),
                        new InputExtras(false, 0f, 0, 0f, (uint)(1000 + t)));
                else if (t % 23 == i) f.Input(players[i], SimFixture.Cast(tick, SimFixture.Mend), extras);
                else f.Input(players[i], SimFixture.Move(tick, dir * 0.5f, 0f), extras);
            }
            if (t == 7) f.ApplyStatus("m1", SimFixture.Burning, source: "p1");

            f.Events.Clear();
            f.Step();
            PendingGameEvent[] events = f.Events.Events.ToArray();

            for (int i = 0; i < players.Length; i++)
            {
                List<Truth> truth = ReadTruth(f, players[i], out Vec2 anchor, out ulong ack, out int observerKey,
                    buffer, gather, out int count);
                SnapshotMessage msg = states[i].Encode(tick, ack, buffer.AsSpan(0, count), 30,
                    intern: encoding == WireEncoding.Proto, observer: anchor, events: events,
                    observerKey: observerKey, v3: gather);

                // Through the real wire codec, so JSON is proven too.
                Envelope env = WireProtocol.DecodeBody(WireProtocol.EncodeBody(
                    WireProtocol.NewEnvelope(MsgType.Snapshot, msg, encoding)));
                clients[i].Receive(WireProtocol.GetPayload<SnapshotMessage>(env));
                if (budget == 0 || t == Active + quietTicks) AssertMatches(players[i], truth, clients[i], tick);

                sawProjectile |= truth.Any(x => x.Type == CombatResolver.ProjectileType);
                sawItem |= truth.Any(x => x.Type == CombatResolver.ItemType);
                sawStatus |= truth.Any(x => x.Statuses.Length > 0);
                sawSeqToOwner |= truth.Any(x => x.OwnerId == players[i] && x.SpawnSeq != 0);
            }
        }

        if (budget > 0)
            Assert.True(states.Any(st => st.EntitiesShed > 0), "the budget never shed anything; the test proves nothing");
        Assert.True(sawProjectile && sawItem && sawStatus && sawSeqToOwner,
            $"scenario coverage: projectile={sawProjectile} item={sawItem} status={sawStatus} seq={sawSeqToOwner}");
        if (fieldDelta && encoding == WireEncoding.Proto)
        {
            uint union = clients.Aggregate(0u, (m, c) => m | c.MasksSeen);
            // Stats never change in this content (no mana costs yet); VersionBumpWithoutChange covers 0x1000.
            foreach (uint bit in new[] { SnapshotFieldBits.Z, SnapshotFieldBits.Statuses, SnapshotFieldBits.Velocity })
                Assert.True((union & bit) != 0, $"mask bit 0x{bit:X4} never exercised");
        }
    }

    /// <summary>
    /// The same world through a version 2 encoder: no projectile or item entity, no v3 field, no
    /// v3 event type, no v3 mask bit - ever.
    /// </summary>
    [Fact]
    public void V2Peer_NeverSeesV3EntitiesFieldsOrEvents()
    {
        using var f = new SimFixture(withLoot: true);
        f.AddPlayer("p0", 0f, 0f);
        f.AddMob("m0", 0f, 3f, hp: 12);
        f.AddMob("m1", 4f, -3f, hp: 5000);
        var state = new SnapshotDeltaState { SelfId = "p0", FieldDelta = true, PeerProtocolVersion = 2 };
        var buffer = new EntityView[64];
        var gather = new SnapshotV3Gather();
        bool projectileExisted = false, itemExisted = false, v3EventExisted = false;

        for (int t = 1; t <= 80; t++)
        {
            ulong tick = (ulong)t;
            if (t % 9 == 0) f.Input("p0", SimFixture.Cast(tick, SimFixture.Bolt, aimX: 4f, aimY: -3f), new InputExtras(false, 0f, 0, 0f, 77));
            else if (t % 5 == 0) f.Input("p0", SimFixture.Cast(tick, SimFixture.Snare, aimX: 0f, aimY: 3f));
            else f.Input("p0", SimFixture.Move(tick, 0f, 0f), new InputExtras(t % 11 == 0, 0f, 0, 0f, 0));
            f.Events.Clear();
            f.Step();
            v3EventExisted |= f.Events.Events.Any(e => e.Data.Type is GameEventType.StatusApplied or GameEventType.ProjectileHit);

            List<Truth> truth = ReadTruth(f, "p0", out Vec2 anchor, out ulong ack, out int key, buffer, gather, out int n);
            projectileExisted |= truth.Any(x => x.Type == CombatResolver.ProjectileType);
            itemExisted |= truth.Any(x => x.Type == CombatResolver.ItemType);

            SnapshotMessage msg = state.Encode(tick, ack, buffer.AsSpan(0, n), 30, intern: true, observer: anchor,
                events: f.Events.Events.ToArray(), observerKey: key, v3: gather);
            foreach (EntitySnapshot e in msg.Entities)
            {
                Assert.NotEqual(EntityType.Projectile, e.Type);
                Assert.NotEqual(EntityType.Item, e.Type);
                Assert.Equal(0f, e.Z);
                Assert.True(e.VelX == 0f && e.VelY == 0f && e.VelZ == 0f && e.Owner == 0 && e.SpawnSeq == 0);
                Assert.Empty(e.Stats);
                Assert.Empty(e.Statuses);
                Assert.Equal(0u, e.ChangedFields & ~SnapshotFieldBits.AllVersion2);
            }
            foreach (GameEvent ev in msg.Events)
            {
                Assert.True((int)ev.Type <= 6, $"v3 event type {ev.Type} reached a v2 peer");
                Assert.Equal(0u, ev.EffectId);
            }
        }

        Assert.True(projectileExisted && itemExisted && v3EventExisted,
            $"scenario coverage: projectile={projectileExisted} item={itemExisted} v3 events={v3EventExisted}");
    }

    private static EntityView View(int key, string id, string type, float x, float y, float z = 0f,
        float vx = 0f, float vy = 0f, float vz = 0f, string? owner = null, int ownerKey = 0, uint seq = 0,
        uint statsVersion = 1, uint statusesVersion = 1, int hp = 100) =>
        new(key, id, type, new Vec2(x, y), hp, 100, 5f, 1u, SimAction.Idle, 0u,
            z, vx, vy, vz, owner, ownerKey, seq, statsVersion, statusesVersion);

    /// <summary>
    /// ADR-29.1: a projectile is due every world tick even when the replication schedule would
    /// defer it, while the schedule still defers an ordinary distant mover.
    /// </summary>
    [Fact]
    public void Projectile_IsDueEveryTick_WhileTheScheduleDefersOthers()
    {
        var state = new SnapshotDeltaState
        {
            SelfId = "self",
            PeerProtocolVersion = 3,
            FieldDelta = true,
            Schedule = ReplicationSchedule.Tiered,
            ImportanceWeights = new ReplicationImportance.Weights(distance: 2f, change: 10f, type: 3f, combat: 6f),
            TickHz = 60,
            WorldEvery = 1,
        };

        int projectileSends = 0, mobSends = 0;
        const int Ticks = 40;
        for (int t = 1; t <= Ticks; t++)
        {
            var views = new[]
            {
                View(1, "self", "player", 0f, 0f),
                View(2, "mob-far", "mob", 40f + t * 0.1f, 0f),
                View(3, "proj-1", CombatResolver.ProjectileType, 1f + t * 0.1f, 0f, 1f, vx: 6f, owner: "self", ownerKey: 1, seq: 9),
            };
            SnapshotMessage msg = state.Encode((ulong)t, 0, views, 1000, intern: true, observerKey: 1);
            if (t == 1) continue; // keyframe
            projectileSends += msg.Entities.Count(e => e.Handle == 3);
            mobSends += msg.Entities.Count(e => e.Handle == 2);
        }

        Assert.Equal(Ticks - 1, projectileSends);
        Assert.True(mobSends < Ticks - 1, $"the schedule never deferred the distant mob ({mobSends} sends); the test proves nothing");
    }

    /// <summary>
    /// Owner handle, owner_id (JSON) and spawn_seq: the owner's connection gets the seq, any other
    /// connection gets 0, and a JSON connection names the owner by id.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Owner_IsInterned_AndSpawnSeqGoesToTheOwnerOnly(bool intern)
    {
        EntityView[] views =
        {
            View(1, "caster", "player", 0f, 0f),
            View(2, "other", "player", 2f, 0f),
            View(3, "proj-1", CombatResolver.ProjectileType, 1f, 0f, 1f, vx: 6f, owner: "caster", ownerKey: 1, seq: 42),
        };

        var casterState = new SnapshotDeltaState { SelfId = "caster", PeerProtocolVersion = 3 };
        var otherState = new SnapshotDeltaState { SelfId = "other", PeerProtocolVersion = 3 };
        SnapshotMessage toCaster = casterState.Encode(1, 0, views, 30, intern: intern, observerKey: 1);
        EntitySnapshot p1 = toCaster.Entities.Single(e => EntityTypes.NameOf(e) == CombatResolver.ProjectileType);
        Assert.Equal(42u, p1.SpawnSeq);
        Assert.Equal(6f, p1.VelX);
        Assert.Equal(1f, p1.Z);
        if (intern)
        {
            Assert.Equal(toCaster.Entities.Single(e => e.Id == "caster").Handle, p1.Owner);
            Assert.Equal("", p1.OwnerId);
        }
        else
        {
            Assert.Equal(0u, p1.Owner);
            Assert.Equal("caster", p1.OwnerId);
        }

        SnapshotMessage toOther = otherState.Encode(1, 0, views, 30, intern: intern, observerKey: 2);
        Assert.Equal(0u, toOther.Entities.Single(e => EntityTypes.NameOf(e) == CombatResolver.ProjectileType).SpawnSeq);
    }

    /// <summary>
    /// An owner the connection has not been told about is not named (0 / ""), the same
    /// disclosure rule events follow - and it is named on the delta after it becomes known.
    /// </summary>
    [Fact]
    public void UnknownOwner_IsNotNamed_UntilItIsKnown()
    {
        var state = new SnapshotDeltaState { SelfId = "viewer", PeerProtocolVersion = 3, FieldDelta = true };
        EntityView viewer = View(1, "viewer", "player", 0f, 0f);
        EntityView proj = View(3, "proj-1", CombatResolver.ProjectileType, 1f, 0f, 1f, vx: 6f, owner: "caster", ownerKey: 2, seq: 5);
        EntityView caster = View(2, "caster", "player", 30f, 0f);

        SnapshotMessage first = state.Encode(1, 0, new[] { viewer, proj }, 1000, intern: true, observerKey: 1);
        Assert.Equal(0u, first.Entities.Single(e => e.Id == "proj-1").Owner);
        // Read before the next Encode: the message is the encoder's, reused per call.
        uint projHandle = first.Entities.Single(x => x.Id == "proj-1").Handle;

        // The caster enters the view AFTER the projectile in AOI order: an owner is resolved
        // against handles bound before the projectile is written, so this message still says 0
        // (never a handle the receiver could not resolve)...
        SnapshotMessage second = state.Encode(2, 0, new[] { viewer, proj, caster }, 1000, intern: true, observerKey: 1);
        uint casterHandle = second.Entities.Single(e => e.Id == "caster").Handle;
        Assert.DoesNotContain(second.Entities, e => e.Handle == projHandle);

        // ...and the next delta names it, under the Owner bit alone.
        SnapshotMessage third = state.Encode(3, 0, new[] { viewer, proj, caster }, 1000, intern: true, observerKey: 1);
        EntitySnapshot p = third.Entities.Single(e => e.Handle == projHandle);
        Assert.Equal(casterHandle, p.Owner);
        Assert.Equal(SnapshotFieldBits.Owner, p.ChangedFields);
    }

    /// <summary>
    /// The budget measures an entity by the same Fill that writes it; with stats and statuses
    /// the measured payload must equal the serialized payload exactly, keyframe and delta.
    /// </summary>
    [Fact]
    public void BudgetSizing_IsExact_WithStatsAndStatuses()
    {
        var state = new SnapshotDeltaState
        {
            SelfId = "p", PeerProtocolVersion = 3, FieldDelta = true, MaxSnapshotBytes = 64 * 1024,
        };
        var gather = new SnapshotV3Gather();

        for (int t = 1; t <= 12; t++)
        {
            var views = new[]
            {
                View(1, "p", "player", t, 0f, z: t % 3, statsVersion: (uint)t, statusesVersion: (uint)(t / 2)),
                View(2, "m", "mob", 3f, t, statsVersion: 1, statusesVersion: (uint)(t / 3)),
                View(3, "proj-1", CombatResolver.ProjectileType, t, 1f, 1f, vx: 3f, owner: "p", ownerKey: 1, seq: 4),
            };
            gather.Reset(views.Length);
            gather.Set(0, new[] { new StatValueData(1, 1), new StatValueData(2, 100 - t) },
                t % 4 == 0 ? Array.Empty<GatheredStatus>() : new[] { new GatheredStatus(1, (uint)(t % 3 + 1), 50, 2) });
            gather.Set(1, new[] { new StatValueData(1, 3) },
                new[] { new GatheredStatus(2, 1, (ulong)(t / 3 * 10), 1), new GatheredStatus(4, 1, 0, PendingGameEvent.NoKey) });
            gather.Set(2, ReadOnlySpan<StatValueData>.Empty, ReadOnlySpan<GatheredStatus>.Empty);

            SnapshotMessage msg = state.Encode((ulong)t, 0, views, 5, intern: true, observerKey: 1, v3: gather);
            Assert.Equal(msg.CalculateSize(), state.LastPayloadBytes);
        }
    }

    /// <summary>
    /// After warm-up, encoding a protocol 3 stream whose stats and statuses keep changing
    /// allocates nothing: wire sub-messages, last-sent records and removal lists are pooled.
    /// </summary>
    [Fact]
    public void SteadyState_AllocatesNothing()
    {
        var state = new SnapshotDeltaState
        {
            SelfId = "p", PeerProtocolVersion = 3, FieldDelta = true,
            MaxSnapshotBytes = SnapshotDeltaState.DefaultMaxSnapshotBytes,
        };
        var gathers = new[] { new SnapshotV3Gather(), new SnapshotV3Gather() };
        var viewSets = new EntityView[2][];
        for (int g = 0; g < 2; g++)
        {
            viewSets[g] = new[]
            {
                View(1, "p", "player", g, 0f, z: g, statsVersion: (uint)(10 + g), statusesVersion: (uint)(20 + g)),
                View(2, "m", "mob", 3f, g, statsVersion: (uint)(30 + g), statusesVersion: (uint)(40 + g)),
                View(3, "proj-1", CombatResolver.ProjectileType, g, 1f, 1f, vx: 3f + g, owner: "p", ownerKey: 1, seq: 4),
            };
            gathers[g].Reset(3);
            gathers[g].Set(0, new[] { new StatValueData(1, 1 + g), new StatValueData(2, 100 - g) },
                g == 0 ? new[] { new GatheredStatus(1, 1, 50, 2) } : Array.Empty<GatheredStatus>());
            gathers[g].Set(1, g == 0 ? new[] { new StatValueData(1, 3) } : Array.Empty<StatValueData>(),
                new[] { new GatheredStatus(2, (uint)(1 + g), 10, 1) });
            gathers[g].Set(2, ReadOnlySpan<StatValueData>.Empty, ReadOnlySpan<GatheredStatus>.Empty);
        }

        void Run(int from, int to)
        {
            for (int t = from; t < to; t++)
            {
                int g = t & 1;
                state.Encode((ulong)t, 0, viewSets[g], 7, intern: true, observerKey: 1, v3: gathers[g]);
            }
        }

        Run(1, 200); // warm every pool, both keyframe and delta paths
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(200, 1200);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"steady-state v3 encode allocated {allocated} B over 1000 snapshots");
    }

    /// <summary>
    /// A version change with no visible change is absorbed (nothing is sent), and the version 2
    /// shape of the same entities is unaffected by stats entirely.
    /// </summary>
    [Fact]
    public void VersionBumpWithoutChange_SendsNothing()
    {
        var state = new SnapshotDeltaState { SelfId = "p", PeerProtocolVersion = 3, FieldDelta = true };
        var gather = new SnapshotV3Gather();
        gather.Reset(1);
        gather.Set(0, new[] { new StatValueData(1, 5) }, ReadOnlySpan<GatheredStatus>.Empty);

        state.Encode(1, 0, new[] { View(1, "p", "player", 0f, 0f, statsVersion: 1) }, 1000, intern: true, v3: gather);
        SnapshotMessage same = state.Encode(2, 0, new[] { View(1, "p", "player", 0f, 0f, statsVersion: 2) }, 1000, intern: true, v3: gather);
        Assert.Empty(same.Entities);

        gather.Reset(1);
        gather.Set(0, new[] { new StatValueData(1, 6), new StatValueData(2, 1) }, ReadOnlySpan<GatheredStatus>.Empty);
        SnapshotMessage changed = state.Encode(3, 0, new[] { View(1, "p", "player", 0f, 0f, statsVersion: 3) }, 1000, intern: true, v3: gather);
        EntitySnapshot e = Assert.Single(changed.Entities);
        Assert.Equal(SnapshotFieldBits.Stats, e.ChangedFields);
        Assert.Equal(2, e.Stats.Count);

        gather.Reset(1);
        gather.Set(0, new[] { new StatValueData(2, 1) }, ReadOnlySpan<GatheredStatus>.Empty);
        SnapshotMessage removed = state.Encode(4, 0, new[] { View(1, "p", "player", 0f, 0f, statsVersion: 4) }, 1000, intern: true, v3: gather);
        e = Assert.Single(removed.Entities);
        Assert.Empty(e.Stats);
        Assert.Equal(new uint[] { 1 }, e.StatsRemoved.ToArray());
    }
}
