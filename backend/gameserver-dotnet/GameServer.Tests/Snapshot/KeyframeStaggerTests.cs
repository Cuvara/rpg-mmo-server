using GameServer.Snapshot;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// Keyframes must not align across a cohort.
///
/// <para>Every connection starts its keyframe counter at zero, so clients that joined on
/// the same tick used to reach the interval on the same tick too — and stay in lockstep
/// forever. Full state is then serialized for every one of them simultaneously, which is
/// a latency spike precisely while a server is filling up.</para>
/// </summary>
public class KeyframeStaggerTests
{
    private const int Interval = 30;

    /// <summary>Tick indices (relative to join) on which this client sends a keyframe.</summary>
    private static List<int> KeyframeTicks(SnapshotDeltaState state, int ticks)
    {
        var nearby = new List<EntityState> { TestHelpers.CreatePlayer("e1", x: 1, y: 1) };
        var result = new List<int>();
        for (int t = 0; t < ticks; t++)
        {
            var msg = state.Encode((ulong)t, 0, nearby, Interval);
            if (msg.Full) result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// The regression: N clients joining together must not share a keyframe schedule.
    /// Without staggering every one of them keyframes on ticks 0, 30, 60, … together.
    /// </summary>
    [Fact]
    public void SimultaneousJoins_DoNotShareAKeyframeSchedule()
    {
        const int cohort = 40;
        const int ticks = 240;

        // Count how many of the cohort keyframe on each tick.
        var perTick = new int[ticks];
        for (int i = 0; i < cohort; i++)
        {
            var state = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor($"user-{i}"));
            foreach (int t in KeyframeTicks(state, ticks))
            {
                perTick[t]++;
            }
        }

        // Tick 0 is the join keyframe: unavoidable, every client needs full state once.
        // What must not happen is the cohort staying aligned afterwards.
        Assert.Equal(cohort, perTick[0]);

        int worstAfterJoin = perTick.Skip(1).Max();
        Assert.True(worstAfterJoin < cohort,
            $"all {cohort} clients still keyframe on the same tick after joining " +
            $"(worst tick carries {worstAfterJoin}) — the stagger is not applied");

        // Spread should be roughly cohort/interval per tick; allow generous slack for
        // hash clustering, but a genuine stampede (everyone on one tick) fails loudly.
        int budget = Math.Max(4, (int)Math.Ceiling(cohort / (double)Interval) * 4);
        Assert.True(worstAfterJoin <= budget,
            $"keyframes are still clustered: worst tick carries {worstAfterJoin} of " +
            $"{cohort} clients, budget {budget}");
    }

    /// <summary>
    /// The offset must come from the user id, not a counter or the clock: the same
    /// player produces the same schedule in every run, so a replay is reproducible.
    /// This mirrors why cooldowns are tick-based rather than wall-clock (DESIGN.md).
    /// </summary>
    [Fact]
    public void PhaseIsDeterministicForAGivenUser()
    {
        Assert.Equal(SnapshotDeltaState.PhaseFor("player-alpha"), SnapshotDeltaState.PhaseFor("player-alpha"));
        Assert.NotEqual(SnapshotDeltaState.PhaseFor("player-alpha"), SnapshotDeltaState.PhaseFor("player-beta"));

        // Same id ⇒ identical keyframe schedule, run after run.
        var a = KeyframeTicks(new SnapshotDeltaState(SnapshotDeltaState.PhaseFor("player-alpha")), 200);
        var b = KeyframeTicks(new SnapshotDeltaState(SnapshotDeltaState.PhaseFor("player-alpha")), 200);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PhaseIsNeverNegative()
    {
        // A negative seed would be clamped to zero and silently unstagger those users.
        foreach (var id in new[] { "", "a", "player-9999999", "ÿÿÿÿ", new string('z', 512) })
        {
            Assert.True(SnapshotDeltaState.PhaseFor(id) >= 0, $"negative phase for {id.Length} char id");
        }
    }

    /// <summary>
    /// The unstaggered period, measured rather than assumed.
    ///
    /// <para>It is <c>Interval + 1</c>, not <c>Interval</c>: the counter is compared
    /// before being incremented, so a keyframe lands on every (interval+1)-th snapshot.
    /// That off-by-one predates the stagger and is left alone — changing it would shift
    /// every client's keyframe cadence for no benefit. Deriving it here rather than
    /// hard-coding a number keeps these tests honest about what the code actually does.</para>
    /// </summary>
    private static int MeasuredPeriod()
    {
        var ticks = KeyframeTicks(new SnapshotDeltaState(), 200);
        return ticks[2] - ticks[1];
    }

    /// <summary>
    /// Staggering shifts the first cycle only. A permanent offset would shorten this
    /// client's cycle forever, handing it more keyframes — and more bandwidth — than
    /// everyone else.
    /// </summary>
    [Fact]
    public void SteadyStateIntervalIsUnchangedByTheStagger()
    {
        int period = MeasuredPeriod();
        var ticks = KeyframeTicks(new SnapshotDeltaState(phaseSeed: 7), 300);

        Assert.True(ticks.Count >= 4, $"expected several keyframes, got {ticks.Count}");

        // Skip the join keyframe and the one shortened cycle after it.
        for (int i = 2; i < ticks.Count; i++)
        {
            Assert.Equal(period, ticks[i] - ticks[i - 1]);
        }
    }

    [Fact]
    public void StaggerShortensExactlyOneCycle()
    {
        int period = MeasuredPeriod();
        const int phase = 7;

        var ticks = KeyframeTicks(new SnapshotDeltaState(phaseSeed: phase), 200);

        Assert.Equal(0, ticks[0]);                          // join keyframe
        Assert.Equal(period - phase, ticks[1] - ticks[0]);  // the one shortened cycle
        Assert.Equal(period, ticks[2] - ticks[1]);          // steady state thereafter
    }

    [Fact]
    public void UnstaggeredStateKeepsExactlyTheOldSchedule()
    {
        // The parameterless constructor must behave as before, so existing callers and
        // tests that assume the old cadence are unaffected.
        int period = MeasuredPeriod();
        var ticks = KeyframeTicks(new SnapshotDeltaState(), 100);

        Assert.Equal(new[] { 0, period, period * 2, period * 3 }, ticks);
    }

    [Fact]
    public void KeyframeIntervalOfOneOrLessIsNotStaggered()
    {
        // interval <= 0 disables delta encoding entirely; 1 means every other snapshot.
        // Neither leaves room for a phase, and a modulo by them would be meaningless.
        var state = new SnapshotDeltaState(phaseSeed: 12345);
        var nearby = new List<EntityState> { TestHelpers.CreatePlayer("e1") };

        for (int t = 0; t < 5; t++)
        {
            Assert.True(state.Encode((ulong)t, 0, nearby, keyframeInterval: 0).Full);
        }
    }
}
