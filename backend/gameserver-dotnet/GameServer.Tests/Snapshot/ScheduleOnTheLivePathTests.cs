using GameServer.Input;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The schedule driven through the REAL input handler and the REAL AOI gather, rather than
/// by handing the encoder a hand-built list.
///
/// <para><b>Why this exists.</b> A live 200-player run reported
/// <c>snapshot_deferred_by_interval = 0</c> with the schedule on, while an in-process probe
/// that called <c>Encode</c> directly reported nearly twenty thousand deferrals from the
/// same population shape. Two probes of the same mechanism disagreeing means at least one
/// of them is not measuring the mechanism, and the difference between them is the path:
/// the direct probe used the <c>EntityState</c> overload, which cannot carry
/// <c>action_seq</c> and therefore hands the encoder a constant zero.</para>
/// </summary>
public class ScheduleOnTheLivePathTests
{
    private readonly ITestOutputHelper _out;

    public ScheduleOnTheLivePathTests(ITestOutputHelper output) => _out = output;

    private const int CriticalHz = 60;
    // 60/30, not the shipped 60/15, and that is deliberate (#413).
    //
    // The interval ceiling now reserves ReplicationSchedule.LinkSpreadAllowanceMs of the
    // client's cover for the network, which leaves 105ms for deferral. Emission happens on
    // world ticks, so at 60/15 the only waits that exist are 66.7ms and 133.3ms: 105 sits
    // between them and every band collapses to "every tick". The schedule defers NOTHING at
    // the shipped rate, which is asserted on purpose by
    // ScheduleFitsTheClientBudgetTests.TieringBuysNothingAtTheShippedWorldRate_AndNeedsAFasterOne.
    //
    // These cases are about the schedule's MECHANICS — aging, self-exemption, edges, keyframe
    // coverage, convergence after a deferral — which are rate-independent. Running them where
    // the feature is inert would leave every one of them passing vacuously; their own guards
    // ("nothing was deferred, so this proves nothing") caught exactly that when the ceiling
    // changed, which is why they are re-homed rather than relaxed.
    private const int WorldEvery = 2;          // 60/30
    private const int Players = 24;

    [Fact]
    public void DrivenThroughTheRealInputPathAt60Over30_TheScheduleStillDefers()
    {
        using var world = new EcsWorld();
        var handler = new InputHandler(
            world, NullLogger.Instance, onDeath: null, tickRate: CriticalHz,
            bounds: MapBounds.Default, onRejected: null, onAttackAccepted: null,
            oneShotHoldTicks: WorldEvery);

        var ids = new List<string>();
        for (int i = 0; i < Players; i++)
        {
            string id = $"p{i:D2}";
            world.AddEntity(TestHelpers.CreatePlayer(id, (i % 6) * 0.4f, (i / 6) * 0.4f));
            ids.Add(id);
        }

        var state = new SnapshotDeltaState
        {
            MaxSnapshotBytes = SnapshotDeltaState.DefaultMaxSnapshotBytes,
            ImportanceWeights = ImportanceSettings.Balanced.Weights,
            Schedule = ReplicationSchedule.Tiered,
            // The BASE rate, as the host passes it: the encoder is handed a base tick.
            TickHz = CriticalHz,
            WorldEvery = WorldEvery,
            AoiRadius = GameConstants.DefaultAoiRadius,
            SelfId = ids[0],
        };

        var buffer = new EntityView[Players + 8];
        var seqSeen = new Dictionary<string, HashSet<uint>>();
        ulong worldTick = 0;

        // 200 world ticks, each preceded by WorldEvery critical ticks of real input --
        // exactly the shape the loadtest drives.
        for (ulong baseTick = 1; baseTick <= 200 * WorldEvery; baseTick++)
        {
            foreach (string id in ids)
            {
                handler.ProcessInput(id, new InputData(baseTick, 1f, 0f, null), baseTick);
            }

            if ((baseTick - 1) % WorldEvery != 0) continue;
            worldTick++;

            int n = 0;
            Vec2 anchor = default;
            world.ReadAll(r =>
            {
                r.TryGetSnapshotAnchor(ids[0], out anchor, out _);
                n = r.GetEntitiesInRange(anchor, GameConstants.DefaultAoiRadius, buffer.AsSpan());
            });

            for (int i = 0; i < n && i < buffer.Length; i++)
            {
                if (!seqSeen.TryGetValue(buffer[i].Id, out HashSet<uint>? set))
                    seqSeen[buffer[i].Id] = set = new HashSet<uint>();
                set.Add(buffer[i].ActionSeq);
            }

            // The BASE tick, not the world tick -- TickLoop hands the encoder
            // _currentTick, which advances at the critical rate even though snapshots are
            // only built every WorldEvery of them. Feeding a world tick here is what made an
            // earlier version of this test pass against a server that deferred nothing.
            state.Encode(baseTick, baseTick, buffer.AsSpan(0, n),
                GameConstants.DefaultKeyframeInterval, intern: true, observer: anchor);
        }

        int maxDistinctSeq = seqSeen.Values.Count == 0 ? 0 : seqSeen.Values.Max(s => s.Count);
        _out.WriteLine($"live path: worldTicks={worldTick} shed={state.EntitiesShed} " +
                       $"deferredByInterval={state.EntitiesDeferredByInterval} " +
                       $"maxStateAge={state.MaxStateAge} distinctActionSeqPerEntity={maxDistinctSeq}");

        Assert.True(maxDistinctSeq <= 3,
            $"an entity took {maxDistinctSeq} distinct action_seq values while doing nothing " +
            "but walking in a straight line; every one of those reads as an EDGE, so the " +
            "schedule can never defer it");

        Assert.True(state.EntitiesDeferredByInterval > 0,
            "the schedule deferred nothing on the real input path, while deferring thousands " +
            "when handed a synthetic list -- the two probes disagree and this is the one " +
            "that matches production");
    }
}
