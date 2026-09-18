using GameServer.Snapshot;
using Shared.GameLogic.Components;
using W = GameServer.Snapshot.ReplicationImportance.Weights;
using I = GameServer.Snapshot.ReplicationImportance.Inputs;

namespace GameServer.Tests.Snapshot;

public class ReplicationImportanceTests
{
    private const float Radius = GameConstants.DefaultAoiRadius;

    private static I At(float distance, bool isPlayer = false,
        EntityAction action = EntityAction.Moving,
        bool hpChanged = false, bool actionChanged = false) =>
        new(distance * distance, Radius, isPlayer, action, hpChanged, actionChanged);

    // ── The shipped default ──────────────────────────────────────────────────────

    /// <summary>
    /// Every weight zero must score zero for every input, or the "wiring this in changes
    /// nothing" claim the byte-identity digests rest on is false for some entity nobody
    /// happened to put in a fixture.
    /// </summary>
    [Fact]
    public void LegacyWeights_ScoreZeroForEveryInput()
    {
        foreach (float d in new[] { 0f, 1f, Radius / 2f, Radius, Radius * 10f })
        foreach (bool player in new[] { true, false })
        foreach (EntityAction a in new[] { EntityAction.Idle, EntityAction.Moving, EntityAction.Attacking, EntityAction.Dead })
        foreach (bool hp in new[] { true, false })
        foreach (bool act in new[] { true, false })
        {
            var inputs = new I(d * d, Radius, player, a, hp, act);
            Assert.Equal(0f, ReplicationImportance.Score(in inputs, W.Legacy));
        }
    }

    [Fact]
    public void AllZero_IsTrueOnlyWhenEveryFactorIsZero()
    {
        Assert.True(W.Legacy.AllZero);
        Assert.False(new W(distance: 1f).AllZero);
        Assert.False(new W(change: 1f).AllZero);
        Assert.False(new W(type: 1f).AllZero);
        Assert.False(new W(combat: 1f).AllZero);
        // A reserved factor still counts: turning one on without an input would otherwise
        // skip the whole score path and look like it did nothing.
        Assert.False(new W(party: 1f).AllZero);
        Assert.False(new W(boss: 1f).AllZero);
    }

    // ── Distance ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Distance_NearerScoresHigher()
    {
        var w = new W(distance: 1f);
        float near = ReplicationImportance.Score(At(1f), w);
        float mid = ReplicationImportance.Score(At(Radius / 2f), w);
        float far = ReplicationImportance.Score(At(Radius), w);

        Assert.True(near > mid, $"{near} !> {mid}");
        Assert.True(mid > far, $"{mid} !> {far}");
    }

    /// <summary>
    /// The distance term is normalised by the radius, so the SAME fraction of the AOI
    /// scores the same at any radius.
    /// </summary>
    /// <remarks>
    /// Without this, every weight is radius-dependent: a deployment that halves
    /// GAMESERVER_AOI_RADIUS would quadruple the distance term's contribution relative to
    /// the others and silently retune a policy nobody edited — a footgun that only fires on
    /// the deployment that touches the radius, which is the one this project just made
    /// configurable.
    /// </remarks>
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void Distance_IsScaleFreeInTheRadius(float fractionOfRadius)
    {
        var w = new W(distance: 1f);
        float small = ReplicationImportance.Score(
            new I(Pow2(20f * fractionOfRadius), 20f, false, EntityAction.Moving, false, false), w);
        float large = ReplicationImportance.Score(
            new I(Pow2(120f * fractionOfRadius), 120f, false, EntityAction.Moving, false, false), w);

        Assert.Equal(small, large, 5);
    }

    private static float Pow2(float v) => v * v;

    /// <summary>A zero or negative radius must not divide by zero or go NaN.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Distance_SurvivesADegenerateRadius(float radius)
    {
        float s = ReplicationImportance.Score(
            new I(100f, radius, false, EntityAction.Moving, false, false), new W(distance: 1f));
        Assert.True(float.IsFinite(s), $"score was {s}");
    }

    // ── The other live factors ───────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Change_FiresOnEitherHpOrAction(bool hp, bool action)
    {
        var w = new W(change: 5f);
        Assert.Equal(5f, ReplicationImportance.Score(At(10f, hpChanged: hp, actionChanged: action), w));
    }

    [Fact]
    public void Change_DoesNotFireWhenNeitherChanged()
    {
        Assert.Equal(0f, ReplicationImportance.Score(At(10f), new W(change: 5f)));
    }

    [Fact]
    public void Type_PlayerOutranksMob()
    {
        var w = new W(type: 3f);
        Assert.Equal(3f, ReplicationImportance.Score(At(10f, isPlayer: true), w));
        Assert.Equal(0f, ReplicationImportance.Score(At(10f, isPlayer: false), w));
    }

    [Theory]
    [InlineData(EntityAction.Attacking, 7f)]
    [InlineData(EntityAction.Moving, 0f)]
    [InlineData(EntityAction.Idle, 0f)]
    [InlineData(EntityAction.Dead, 0f)]
    public void Combat_FiresOnlyWhileAttacking(EntityAction action, float expected)
    {
        Assert.Equal(expected, ReplicationImportance.Score(At(10f, action: action), new W(combat: 7f)));
    }

    [Fact]
    public void Factors_Add()
    {
        var w = new W(distance: 0f, change: 2f, type: 3f, combat: 7f);
        float s = ReplicationImportance.Score(
            At(10f, isPlayer: true, action: EntityAction.Attacking, hpChanged: true), w);
        Assert.Equal(12f, s);
    }

    // ── The reserved slots ───────────────────────────────────────────────────────

    /// <summary>
    /// The seven factors with no data source contribute nothing even when weighted, because
    /// there is no input that could feed them.
    ///
    /// <para>This is a guard, not a feature. It turns "someone gave party a weight and
    /// nothing happened" from a silent non-event into a red test that says the input does
    /// not exist yet — which is the honest answer, and better than a factor that quietly
    /// reads a field nobody populated.</para>
    /// </summary>
    [Fact]
    public void ReservedFactors_ContributeNothingBecauseNothingFeedsThem()
    {
        var w = new W(party: 100f, pvp: 100f, boss: 100f, quest: 100f,
                      visibility: 100f, zone: 100f, interaction: 100f);

        float s = ReplicationImportance.Score(
            At(1f, isPlayer: true, action: EntityAction.Attacking, hpChanged: true, actionChanged: true), w);

        Assert.Equal(0f, s);
    }
}
