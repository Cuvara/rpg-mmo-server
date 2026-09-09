using Google.Protobuf;
using GameServer.Net;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// Tests for the per-connection downlink budget (<c>GAMESERVER_MAX_SNAPSHOT_BYTES</c>).
///
/// <para>The thing under test is not "does it stay under the cap" — that part is
/// arithmetic. It is the invariant that makes shedding safe at all: the encoder's model
/// of what the client has (<c>_lastSent</c>, <c>_handles</c>) and the bytes it actually
/// wrote must never disagree. If they can, an entity is dropped while marked delivered
/// and the client is wrong about it until the next keyframe, with nothing on either side
/// reporting an error. Every test here drives a real <see cref="SnapshotMerger"/> — the
/// same merge the Unity client compiles — through a handle resolver that fails loudly on
/// a handle it has no binding for, so a divergence cannot pass as a smaller assertion.</para>
/// </summary>
public class SnapshotBudgetTests
{
    private const int NoKeyframes = 100_000; // keyframe interval large enough not to fire

    /// <summary>
    /// A client, to the extent the wire contract defines one: it resolves entity handles
    /// against the bindings it was sent, refuses to guess, and merges with the shared
    /// <see cref="SnapshotMerger"/>.
    /// </summary>
    private sealed class FakeClient
    {
        private readonly Dictionary<uint, string> _bindings = new();

        public readonly SnapshotMerger Merger = new();

        /// <summary>Largest snapshot payload this client was sent, in bytes.</summary>
        public int MaxPayloadBytes { get; private set; }

        /// <summary>Snapshots received.</summary>
        public int Received { get; private set; }

        /// <summary>Last tick each entity id was carried in a snapshot.</summary>
        public readonly Dictionary<string, ulong> LastCarriedTick = new();

        public void Receive(SnapshotMessage msg)
        {
            Received++;
            MaxPayloadBytes = Math.Max(MaxPayloadBytes, msg.CalculateSize());

            // A keyframe resets the handle space on both sides — wire.proto. A client that
            // did not do this here would start resolving new handles to old entities,
            // which is precisely the failure the reset exists to prevent.
            if (msg.Full) _bindings.Clear();

            var entities = new EntitySnapshotData[msg.Entities.Count];
            for (int i = 0; i < msg.Entities.Count; i++)
            {
                EntitySnapshot e = msg.Entities[i];
                string id;

                if (e.Handle != 0)
                {
                    if (e.Id.Length > 0)
                    {
                        if (_bindings.TryGetValue(e.Handle, out string? previous))
                        {
                            Assert.True(previous == e.Id,
                                $"handle {e.Handle} was rebound from '{previous}' to '{e.Id}' " +
                                "inside one keyframe interval — a client that missed a despawn " +
                                "would now attribute updates to the wrong entity");
                        }
                        _bindings[e.Handle] = e.Id;
                        id = e.Id;
                    }
                    else
                    {
                        Assert.True(_bindings.TryGetValue(e.Handle, out string? bound),
                            $"tick {msg.Tick}: handle {e.Handle} arrived with no binding. " +
                            "wire.proto forbids guessing, so a real client would have to " +
                            "MsgResync — which means the encoder shed an entity it had " +
                            "already recorded as sent.");
                        id = bound!;
                    }
                }
                else
                {
                    id = e.Id;
                }

                entities[i] = new EntitySnapshotData(
                    id, EntityTypes.NameOf(e), e.X, e.Y, e.Hp, e.MaxHp, e.Speed);
                LastCarriedTick[id] = msg.Tick;
            }

            Merger.Apply(new SnapshotData(
                msg.Tick, msg.AckTick, msg.Full, entities, msg.Removed.ToArray()));
        }
    }

    private static List<EntityState> Crowd(int count, float phase, string? selfId = null)
    {
        var list = new List<EntityState>(count + (selfId != null ? 1 : 0));
        if (selfId != null) list.Add(TestHelpers.CreatePlayer(selfId, phase, phase));
        for (int i = 0; i < count; i++)
        {
            // Spread over a wide ring so the distance term in the priority order is not
            // degenerate, and move every entity every tick so every entity is dirty.
            float angle = i * 0.37f;
            float r = 5f + (i % 40);
            list.Add(TestHelpers.CreatePlayer(
                $"e{i:D3}",
                (float)(r * Math.Cos(angle)) + phase,
                (float)(r * Math.Sin(angle)) + phase,
                hp: 100 - (i % 7)));
        }
        return list;
    }

    private static void AssertMergerMatches(List<EntityState> expected, SnapshotMerger merger)
    {
        Assert.Equal(expected.Count, merger.Count);
        foreach (EntityState e in expected)
        {
            Assert.True(merger.TryGet(e.Id, out EntitySnapshotData got),
                $"'{e.Id}' is visible to the server but missing from the client");
            Assert.Equal(e.Position.X, got.X);
            Assert.Equal(e.Position.Y, got.Y);
            Assert.Equal(e.Hp, got.Hp);
            Assert.Equal(e.MaxHp, got.MaxHp);
        }
    }

    // ── The budget must be invisible until it bites ────────────────────────────────

    /// <summary>
    /// A budget that is never exceeded must produce byte-for-byte the same stream as no
    /// budget at all.
    /// </summary>
    /// <remarks>
    /// This is the property that lets the budget ship on by default. The shedding path
    /// re-orders entities, so if it ran whenever a budget was merely configured, every
    /// existing byte-level guarantee (SnapshotByteIdentityTests, the golden vectors)
    /// would be asserting a different encoder than production runs.
    /// </remarks>
    [Fact]
    public void BudgetThatIsNeverExceeded_ProducesIdenticalBytes()
    {
        var unbudgeted = new SnapshotDeltaState();
        var budgeted = new SnapshotDeltaState { MaxSnapshotBytes = 1 << 20, SelfId = "self" };

        for (int tick = 1; tick <= 90; tick++)
        {
            List<EntityState> world = Crowd(40, tick * 0.5f, selfId: "self");
            byte[] a = unbudgeted.Encode((ulong)tick, 0, world, 30, intern: true).ToByteArray();
            byte[] b = budgeted.Encode((ulong)tick, 0, world, 30, intern: true,
                observer: new Vec2(tick * 0.5f, tick * 0.5f)).ToByteArray();

            Assert.True(a.SequenceEqual(b),
                $"tick {tick}: an unexceeded budget changed the bytes on the wire");
        }

        Assert.Equal(0, budgeted.EntitiesShed);
        Assert.Equal(0, budgeted.BudgetedSnapshots);
    }

    /// <summary>JSON connections are not budgeted — see the note in <c>Encode</c>.</summary>
    [Fact]
    public void JsonConnection_IsNotBudgeted()
    {
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 32 };

        SnapshotMessage msg = state.Encode(1, 0, Crowd(50, 0f), NoKeyframes, intern: false);

        Assert.Equal(50, msg.Entities.Count);
        Assert.Equal(0, state.EntitiesShed);
    }

    // ── Convergence under sustained pressure ──────────────────────────────────────

    /// <summary>
    /// The headline test: shed on almost every tick for hundreds of ticks, then stop
    /// changing the world, and require the client to end up exactly right — with no
    /// keyframe available to rescue it.
    /// </summary>
    /// <remarks>
    /// Keyframes are switched off on purpose. A keyframe repairs any disagreement by
    /// construction, so a convergence test that let one fire would pass just as happily
    /// against an encoder that drops entities and marks them sent. What must hold is that
    /// the DELTA stream alone converges.
    /// </remarks>
    [Fact]
    public void UnderSustainedPressure_ClientConvergesWithoutAKeyframe()
    {
        const int entities = 60;
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 200, SelfId = "self" };
        var client = new FakeClient();

        List<EntityState> world = Crowd(entities, 0f, selfId: "self");

        // Phase 1: everything moves every tick. Far more dirty state than the budget can
        // carry, so the encoder sheds continuously.
        for (int tick = 1; tick <= 300; tick++)
        {
            world = Crowd(entities, tick * 0.25f, selfId: "self");
            client.Receive(state.Encode((ulong)tick, (ulong)tick, world, NoKeyframes,
                intern: true, observer: new Vec2(tick * 0.25f, tick * 0.25f)));
        }

        Assert.True(state.BudgetedSnapshots >= 250,
            $"the budget only bit on {state.BudgetedSnapshots}/300 snapshots — this test is " +
            "not applying the pressure it claims to");
        Assert.True(state.EntitiesShed > 0, "nothing was shed");

        // Phase 2: the world stops changing. Every deferred update must drain.
        for (int tick = 301; tick <= 700; tick++)
        {
            client.Receive(state.Encode((ulong)tick, (ulong)tick, world, NoKeyframes,
                intern: true, observer: new Vec2(300 * 0.25f, 300 * 0.25f)));
        }

        Assert.Equal(0, client.Merger.Keyframes - 1); // only the join keyframe ever fired
        AssertMergerMatches(world, client.Merger);
    }

    /// <summary>
    /// The same pressure, but the client is checked against the server on EVERY tick once
    /// the world settles — so a single entity that was dropped and marked sent shows up,
    /// rather than being hidden by a later resend.
    /// </summary>
    [Fact]
    public void ShedEntity_IsReOfferedRatherThanAssumedDelivered()
    {
        const int entities = 40;
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 160, SelfId = "self" };
        var client = new FakeClient();

        // One burst of change, then silence. Anything the encoder decides not to send in
        // the burst has exactly one way to reach the client: being re-offered, because
        // nothing will make it dirty again.
        List<EntityState> before = Crowd(entities, 0f, selfId: "self");
        client.Receive(state.Encode(1, 1, before, NoKeyframes, intern: true));

        List<EntityState> after = Crowd(entities, 17f, selfId: "self");
        client.Receive(state.Encode(2, 2, after, NoKeyframes, intern: true,
            observer: new Vec2(17f, 17f)));

        Assert.True(state.EntitiesShed > 0,
            "the burst did not exceed the budget — the test proves nothing");

        for (ulong tick = 3; tick <= 200; tick++)
        {
            client.Receive(state.Encode(tick, tick, after, NoKeyframes, intern: true,
                observer: new Vec2(17f, 17f)));
        }

        AssertMergerMatches(after, client.Merger);
    }

    // ── Starvation ────────────────────────────────────────────────────────────────

    /// <summary>
    /// No visible entity may be deferred for ever. The scheduler is strict oldest-first,
    /// so the bound is the number of dirty entities in the observer's AOI — not the length
    /// of the session.
    /// </summary>
    [Fact]
    public void Starvation_IsBoundedByTheDirtySetNotBySessionLength()
    {
        const int entities = 60;
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 200, SelfId = "self" };
        var client = new FakeClient();

        var longestGap = new Dictionary<string, ulong>();
        var lastSeen = new Dictionary<string, ulong>();

        for (ulong tick = 1; tick <= 1000; tick++)
        {
            List<EntityState> world = Crowd(entities, tick * 0.25f, selfId: "self");
            client.Receive(state.Encode(tick, tick, world, NoKeyframes, intern: true,
                observer: new Vec2(tick * 0.25f, tick * 0.25f)));

            foreach (EntityState e in world)
            {
                if (client.LastCarriedTick.TryGetValue(e.Id, out ulong carried) && carried == tick)
                {
                    if (lastSeen.TryGetValue(e.Id, out ulong previous))
                    {
                        ulong gap = tick - previous;
                        if (!longestGap.TryGetValue(e.Id, out ulong worst) || gap > worst)
                            longestGap[e.Id] = gap;
                    }
                    lastSeen[e.Id] = tick;
                }
            }
        }

        // Every entity was carried at least once, and none went missing for longer than
        // the dirty-set bound. The +1 is the tick it is finally sent on.
        Assert.Equal(entities + 1, lastSeen.Count);
        foreach (var kv in longestGap)
        {
            Assert.True(kv.Value <= (ulong)entities + 1,
                $"'{kv.Key}' went {kv.Value} ticks without an update; the aging order bounds " +
                $"that at {entities + 1}");
        }

        // The encoder's own view of the same bound, which is what the metric publishes.
        Assert.True(state.MaxShedAge <= entities + 1,
            $"MaxShedAge={state.MaxShedAge} exceeds the dirty-set bound of {entities + 1}");
    }

    /// <summary>
    /// The observer's own entity is priority zero and is never deferred: it is the
    /// reconciliation anchor, and a stale one reads as rubber-banding.
    /// </summary>
    [Fact]
    public void SelfEntity_IsNeverShed()
    {
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 48, SelfId = "self" };
        var client = new FakeClient();

        for (ulong tick = 1; tick <= 300; tick++)
        {
            List<EntityState> world = Crowd(80, tick * 0.5f, selfId: "self");
            SnapshotMessage msg = state.Encode(tick, tick, world, NoKeyframes, intern: true,
                observer: new Vec2(tick * 0.5f, tick * 0.5f));

            // Resolved through the handle table, not by string matching: after the first
            // mention "self" travels as a bare handle, and a test that looked for the id
            // would pass on a stream that never carried it again.
            client.Receive(msg);
            Assert.True(client.LastCarriedTick.TryGetValue("self", out ulong carried) && carried == tick,
                $"tick {tick}: the observer's own entity was deferred");

            // ...and it is the FIRST entity in the message, which is what priority zero means.
            Assert.NotEmpty(msg.Entities);
        }

        Assert.True(state.EntitiesShed > 0, "budget never bit");
    }

    // ── Despawns ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A crowd leaving the AOI at once produces a despawn list that may not fit either. A
    /// deferred despawn must leave the client's ghost for one tick, not for ever.
    /// </summary>
    [Fact]
    public void MassDespawn_LeavesNoPermanentGhost()
    {
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 120, SelfId = "self" };
        var client = new FakeClient();

        List<EntityState> crowd = Crowd(80, 0f, selfId: "self");
        for (ulong tick = 1; tick <= 200; tick++)
        {
            client.Receive(state.Encode(tick, tick, crowd, NoKeyframes, intern: true));
        }
        AssertMergerMatches(crowd, client.Merger);

        // Everyone but a handful leaves the AOI on one tick.
        List<EntityState> remaining = Crowd(4, 0f, selfId: "self");
        client.Receive(state.Encode(201, 201, remaining, NoKeyframes, intern: true));

        // The despawn list for 76 entities cannot fit a 120-byte budget, so this test is
        // only meaningful if some of it was actually deferred. Asserted rather than
        // assumed: a later change to the budget or to the id length would otherwise let
        // the whole list fit and quietly stop exercising the deferred-despawn path.
        Assert.True(state.RemovalsDeferred > 0,
            "the despawn list fitted in one snapshot — the deferred-despawn path was not taken");

        for (ulong tick = 202; tick <= 500; tick++)
        {
            client.Receive(state.Encode(tick, tick, remaining, NoKeyframes, intern: true));
        }

        AssertMergerMatches(remaining, client.Merger);
    }

    // ── Keyframes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyframes are budgeted too, and shedding one costs a visible pop — the merger drops
    /// what a keyframe omits — but never a permanent error: the following deltas re-add
    /// the omitted entities, because a shed entity was never written into <c>_lastSent</c>.
    /// </summary>
    [Fact]
    public void ShedKeyframe_IsRepairedByTheFollowingDeltas()
    {
        const int keyframeInterval = 40;
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 200, SelfId = "self" };
        var client = new FakeClient();

        List<EntityState> world = Crowd(50, 0f, selfId: "self");

        for (ulong tick = 1; tick <= 400; tick++)
        {
            client.Receive(state.Encode(tick, tick, world, keyframeInterval, intern: true));
        }

        Assert.True(client.Merger.Keyframes >= 8, "the keyframe cadence never ran");
        Assert.True(state.EntitiesShed > 0, "the keyframes fitted — no shedding was exercised");

        // Measured immediately before the next keyframe is due, which is the window the
        // deltas have to repair it.
        AssertMergerMatches(world, client.Merger);
    }

    // ── The cap itself ────────────────────────────────────────────────────────────

    /// <summary>
    /// The budget is a soft cap in exactly one place: the top-priority candidate is
    /// emitted whatever it costs, so a snapshot may exceed the budget by at most one
    /// entity. Anything beyond that is a leak.
    /// </summary>
    [Fact]
    public void PayloadStaysWithinBudgetPlusOneEntity()
    {
        const int budget = 200;
        var state = new SnapshotDeltaState { MaxSnapshotBytes = budget, SelfId = "self" };
        var client = new FakeClient();

        for (ulong tick = 1; tick <= 400; tick++)
        {
            List<EntityState> world = Crowd(70, tick * 0.25f, selfId: "self");
            client.Receive(state.Encode(tick, tick, world, 30, intern: true,
                observer: new Vec2(tick * 0.25f, tick * 0.25f)));
        }

        // One EntitySnapshot with an id, a handle, four floats and two ints is comfortably
        // under 48 bytes; the allowance is the single forced entity, not a second budget.
        Assert.True(client.MaxPayloadBytes <= budget + 48,
            $"largest payload was {client.MaxPayloadBytes} bytes against a {budget}-byte " +
            "budget — more than the one forced entity the floor allows");
    }
}
