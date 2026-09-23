using GameServer.Input;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The observer's own entity is never withheld by the replication schedule.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The priority sort has carried the rule since it was
/// written — "their own character cannot [be interpolated]: it is the reconciliation
/// anchor, and a stale one reads as rubber-banding" — and the schedule shipped without it.
/// Under the <c>balanced</c> profile self scores 5 (distance 2 + type 3, with no HP or
/// action edge to add), which is the 133ms tier: a client predicting at 60Hz reconciling
/// against an anchor that arrives at 7.5Hz. A three-client play session measured it as the
/// client's reported <c>lastCorrection</c> going from 0.0041 to 0.3333.</para>
///
/// <para><b>Two arms, because a count on its own says nothing.</b> "Self appeared in every
/// world tick" is equally true of a schedule that defers nothing at all. The
/// <c>Off</c> arm is what gives the number a scale: it shows what "every tick" looks like
/// for EVERY entity, so the tiered arm's non-self figure has something to be lower than.
/// Driven through the real input handler and the real AOI gather for the reason
/// <see cref="ScheduleOnTheLivePathTests"/> gives.</para>
/// </remarks>
public class SelfIsNeverDeferredTests
{
    private readonly ITestOutputHelper _out;

    public SelfIsNeverDeferredTests(ITestOutputHelper output) => _out = output;

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
    private const int WorldEvery = 2;           // 60/30
    private const int Players = 24;
    private const int WorldTicks = 200;
    private const int NoKeyframes = int.MaxValue;

    [Fact]
    public void UnderTheTieredSchedule_SelfAppearsEveryWorldTickAndOthersDoNot()
    {
        Run(ReplicationSchedule.Tiered, out int selfSends, out int medianOtherSends,
            out long deferred);
        Run(ReplicationSchedule.Off, out int offSelfSends, out int offMedianOtherSends,
            out long offDeferred);

        _out.WriteLine($"tiered: self={selfSends}/{WorldTicks} medianOther={medianOtherSends} " +
                       $"deferred={deferred}");
        _out.WriteLine($"off:    self={offSelfSends}/{WorldTicks} medianOther={offMedianOtherSends} " +
                       $"deferred={offDeferred}");

        // The control arm first: without it, every assertion below is also satisfied by a
        // schedule that is silently inert.
        Assert.Equal(0, offDeferred);
        Assert.True(offMedianOtherSends >= WorldTicks - 2,
            $"with the schedule off a walking entity should be sent nearly every world tick, " +
            $"but the median was {offMedianOtherSends}/{WorldTicks} — the harness is not " +
            "measuring what it thinks it is, and the tiered arm below proves nothing");

        Assert.True(deferred > 0,
            "the tiered arm deferred nothing, so it is not exercising the schedule");

        // The fix.
        Assert.True(selfSends >= WorldTicks - 2,
            $"self was sent on only {selfSends} of {WorldTicks} world ticks under the tiered " +
            "schedule; it is the client's reconciliation anchor and must never be withheld");

        // And the saving is still real — self being exempt must not exempt everyone.
        Assert.True(medianOtherSends < selfSends * 3 / 4,
            $"every entity was sent about as often as self (median {medianOtherSends} vs self " +
            $"{selfSends}), so the exemption is not scoped to self and the schedule saves nothing");
    }

    private static void Run(
        ReplicationSchedule schedule,
        out int selfSends,
        out int medianOtherSends,
        out long deferred)
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
            Schedule = schedule,
            TickHz = CriticalHz,          // the BASE rate, as TickLoop passes it
            WorldEvery = WorldEvery,      // and the emission cadence, as TickLoop passes it
            AoiRadius = GameConstants.DefaultAoiRadius,
            SelfId = ids[0],
        };

        var buffer = new EntityView[Players + 8];
        var sends = new Dictionary<string, int>(StringComparer.Ordinal);

        for (ulong baseTick = 1; baseTick <= (ulong)(WorldTicks * WorldEvery); baseTick++)
        {
            foreach (string id in ids)
            {
                handler.ProcessInput(id, new InputData(baseTick, 1f, 0f, null), baseTick);
            }

            if ((baseTick - 1) % WorldEvery != 0) continue;

            int n = 0;
            Vec2 anchor = default;
            world.ReadAll(r =>
            {
                r.TryGetSnapshotAnchor(ids[0], out anchor, out _);
                n = r.GetEntitiesInRange(anchor, GameConstants.DefaultAoiRadius, buffer.AsSpan());
            });

            // intern: false so every entity in the message still carries its id — with
            // interning a second mention is a bare handle and there is nothing to count by.
            // NoKeyframes so what is counted is the delta path, not a periodic full resend
            // that would hide a deferral behind it.
            SnapshotMessage msg = state.Encode(
                baseTick, baseTick, buffer.AsSpan(0, n),
                NoKeyframes, intern: false, observer: anchor);

            foreach (EntitySnapshot e in msg.Entities)
            {
                if (string.IsNullOrEmpty(e.Id)) continue;
                sends[e.Id] = sends.TryGetValue(e.Id, out int c) ? c + 1 : 1;
            }
        }

        selfSends = sends.TryGetValue(ids[0], out int s) ? s : 0;
        deferred = state.EntitiesDeferredByInterval;

        var others = ids.Skip(1)
            .Select(id => sends.TryGetValue(id, out int c) ? c : 0)
            .OrderBy(c => c)
            .ToList();
        medianOtherSends = others.Count == 0 ? 0 : others[others.Count / 2];
    }
}
