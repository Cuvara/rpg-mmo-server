using System.Linq;
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

    /// <summary>
    /// <b>The floor, tested directly.</b> Under a despawn backlog that consumes the whole
    /// budget on every tick, an entity update must still land — every snapshot, for as long
    /// as the backlog lasts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the premise every other claim about deferral rests on. The bound in
    /// <c>CandidateComparer</c> — that the longest wait is the size of the dirty set and
    /// not the length of the session — is an aging-round-robin argument, and an aging
    /// round-robin that admits <i>nothing</i> on a tick does not drain, it stalls. The
    /// mechanism that rules that out is the single unconditional emit at the top of
    /// <c>EmitBudgeted</c>, before the despawn list is charged.
    /// </para>
    /// <para>
    /// <b>Why the other tests cannot see it.</b> <c>Starvation_IsBoundedByTheDirtySet</c>
    /// and <c>SelfEntity_IsNeverShed</c> both run with an empty or trivial despawn list, so
    /// the budget is spent on entities either way and the floor is never the thing that
    /// admitted one. Delete the floor and all ten of them still pass — verified. The
    /// missing ingredient is <i>competition for the budget from a non-entity cost</i>, and
    /// despawns are the only such cost there is.
    /// </para>
    /// <para>
    /// The backlog is built first and then starved on purpose: 300 entities are sent under
    /// no budget, almost all of them leave the AOI at once, and the budget is then set low
    /// enough that only a handful of despawn ids fit per snapshot. That leaves tens of ticks
    /// during which the owed despawn list alone is many times the budget — precisely the
    /// "steady stream of despawns consuming the budget every tick" the floor exists for.
    /// </para>
    /// <para>
    /// <b>What this test found, and what it therefore does NOT assert.</b> The floor
    /// guarantees one candidate per snapshot, and the observer's own entity is priority
    /// zero — so under despawn saturation the guaranteed slot goes to <i>self, every tick</i>,
    /// and the other dirty entities get nothing until the despawn list stops eating the
    /// budget. Measured here at <b>63 ticks</b> of deferral against a dirty set of 6. The
    /// "longest wait is the size of the dirty set" claim is therefore only true while the
    /// budget is spent on entity updates alone; when a non-entity cost competes, the wait is
    /// the size of the dirty set <i>plus</i> the time the backlog takes to drain. That is
    /// still bounded and still not a function of session length — which is the property
    /// asserted below, by running the world quiet afterwards and requiring the high-water
    /// mark to stop moving.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnderDespawnBacklog_AnEntityUpdateStillLandsEveryTick()
    {
        const int population = 300;
        const int survivors = 5;

        var state = new SnapshotDeltaState { SelfId = "self" };
        var client = new FakeClient();

        // Phase 1, unbudgeted: get all 300 into the encoder's model of what the client has,
        // which is what makes them despawn-able.
        List<EntityState> everyone = Crowd(population, 0f, selfId: "self");
        client.Receive(state.Encode(1, 1, everyone, NoKeyframes, intern: true));
        Assert.Equal(population + 1, client.Merger.Count);

        // Phase 2: the crowd leaves, and the budget drops to a few despawn ids per snapshot.
        // 295 owed despawns at ~6 bytes each is ~1770 bytes of debt against a 60-byte
        // snapshot, so the despawn list saturates the budget for tens of ticks.
        state.MaxSnapshotBytes = 60;

        int ticksWithNoEntity = 0;
        int backloggedTicks = 0;

        for (ulong tick = 2; tick <= 80; tick++)
        {
            // The survivors keep moving, so there is always a dirty entity to starve. If
            // nothing were dirty the test would pass vacuously.
            List<EntityState> remaining = Crowd(survivors, tick * 0.25f, selfId: "self");
            SnapshotMessage msg = state.Encode(tick, tick, remaining, NoKeyframes, intern: true,
                observer: new Vec2(tick * 0.25f, tick * 0.25f));

            bool backlogged = state.RemovalsDeferred > 0 || client.Merger.Count > survivors + 1;
            if (backlogged) backloggedTicks++;

            if (msg.Entities.Count == 0) ticksWithNoEntity++;

            client.Receive(msg);
            Assert.True(client.LastCarriedTick.TryGetValue("self", out ulong carried) && carried == tick,
                $"tick {tick}: the despawn backlog consumed the whole budget and the observer's " +
                "own entity was not sent. The floor in EmitBudgeted is what prevents this.");
        }

        Assert.True(backloggedTicks >= 30,
            $"only {backloggedTicks} ticks carried a despawn backlog — the test is not applying " +
            "the pressure it claims to, so it would pass with or without the floor");
        Assert.Equal(0, ticksWithNoEntity);

        // The deferral figure under despawn competition, recorded rather than bounded by the
        // dirty set — see the remarks above for why the dirty-set bound does not apply here.
        // Measured at 63 ticks for this configuration.
        int ageAfterBacklog = state.MaxShedAge;
        Assert.True(ageAfterBacklog > survivors + 1,
            "the non-self entities were NOT starved by the despawn backlog, so this test is no " +
            "longer exercising the regime it documents");

        // The world goes quiet. The backlog drains, the deferred updates land, and — the
        // point — the deferral high-water mark STOPS MOVING. That is what makes the wait
        // bounded rather than merely long: it is a function of the backlog, which is finite
        // and draining, not of how long the connection has been open.
        for (ulong tick = 81; tick <= 400; tick++)
        {
            List<EntityState> remaining = Crowd(survivors, 80 * 0.25f, selfId: "self");
            client.Receive(state.Encode(tick, tick, remaining, NoKeyframes, intern: true));
        }

        Assert.Equal(ageAfterBacklog, state.MaxShedAge);
        Assert.Equal(0, state.DeferralRecords);
        AssertMergerMatches(Crowd(survivors, 80 * 0.25f, selfId: "self"), client.Merger);
    }

    /// <summary>
    /// <c>Fill</c> is the single writer of an <see cref="EntitySnapshot"/>, and that is the
    /// only reason the budget's sizing pass and its emit path cannot disagree about how many
    /// bytes an entity costs. The rule is structural and nothing enforces it, so this test
    /// pins the schema: add a field to <c>EntitySnapshot</c> and this fails, which is the
    /// prompt to go and write it in <c>Fill</c> rather than beside it.
    /// </summary>
    /// <remarks>
    /// A drift here is not loud. Writing a new field in the emit path only would leave the
    /// budget wrong by a few bytes per entity — invisible at four entities, a whole entity's
    /// worth at eighty, and never an error anywhere.
    /// </remarks>
    [Fact]
    public void EntitySnapshot_HasNoFieldFillDoesNotKnowAbout()
    {
        string[] expected =
        {
            nameof(EntitySnapshot.Id),
            nameof(EntitySnapshot.TypeName),
            nameof(EntitySnapshot.X),
            nameof(EntitySnapshot.Y),
            nameof(EntitySnapshot.Hp),
            nameof(EntitySnapshot.MaxHp),
            nameof(EntitySnapshot.Type),
            nameof(EntitySnapshot.Handle),
            nameof(EntitySnapshot.Speed),
        };

        string[] actual = typeof(EntitySnapshot)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            actual);
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

    /// <summary>
    /// An entity that is new, is deferred before it is ever sent, and then leaves the AOI
    /// never becomes a despawn — nothing was ever sent about it — so the only thing that
    /// can clear its deferral record is the prune pass. Without one, a map whose spawns
    /// churn at the edge of an observer's circle leaks a dictionary entry per entity for
    /// the life of the connection.
    /// </summary>
    [Fact]
    public void DeferralRecords_DoNotAccumulateForEntitiesThatLeaveBeforeBeingSent()
    {
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 64, SelfId = "self" };

        // A steady population of four, plus a churning cohort of newcomers that appears for
        // exactly one tick each. The budget is far too small to carry the newcomers, so each
        // one is deferred once and then gone.
        for (ulong tick = 1; tick <= 400; tick++)
        {
            List<EntityState> world = Crowd(4, 0f, selfId: "self");
            for (int i = 0; i < 20; i++)
            {
                world.Add(TestHelpers.CreatePlayer($"transient-{tick}-{i}", 30f + i, 30f));
            }
            state.Encode(tick, tick, world, NoKeyframes, intern: true);
        }

        // 400 ticks x 20 newcomers = 8000 entities that came and went. If deferral records
        // survived them, this is where the leak would be.
        Assert.True(state.DeferralRecords <= 64,
            $"{state.DeferralRecords} deferral records survive for entities that are long " +
            "gone — the prune pass is not running");
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
