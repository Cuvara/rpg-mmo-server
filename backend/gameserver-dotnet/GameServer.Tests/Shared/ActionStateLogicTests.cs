using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Xunit;

namespace GameServer.Tests.Shared;

/// <summary>
/// The retrigger rule. Every writer of an entity's action goes through
/// <see cref="ActionStateLogic.Advance"/>, so these are the whole contract for when a
/// client is told to play an animation again.
/// </summary>
public class ActionStateLogicTests
{
    [Fact]
    public void FirstAction_AdvancesFromZero()
    {
        var action = EntityAction.Unspecified;
        uint seq = 0;

        Assert.True(ActionStateLogic.Advance(ref action, ref seq, EntityAction.Idle));

        Assert.Equal(EntityAction.Idle, action);
        // 1, not 0: zero is reserved on the wire for "this sender does not send a counter",
        // so an entity whose first action left the counter at zero would be indistinguishable
        // from one served by a server that predates the field.
        Assert.Equal(1u, seq);
    }

    [Fact]
    public void ChangingAction_Advances()
    {
        var action = EntityAction.Idle;
        uint seq = 1;

        Assert.True(ActionStateLogic.Advance(ref action, ref seq, EntityAction.Moving));

        Assert.Equal(EntityAction.Moving, action);
        Assert.Equal(2u, seq);
    }

    /// <summary>
    /// The reason this whole mechanism exists. Two attacks in a row are two occurrences and
    /// must each retrigger, even though the action field is identical on both ticks.
    /// </summary>
    [Fact]
    public void RepeatedAttack_Advances()
    {
        var action = EntityAction.Attacking;
        uint seq = 7;

        Assert.True(ActionStateLogic.Advance(ref action, ref seq, EntityAction.Attacking));

        Assert.Equal(EntityAction.Attacking, action);
        Assert.Equal(8u, seq);
    }

    /// <summary>
    /// The mirror-image failure, and the one a naive "increment whenever a writer runs"
    /// implementation would cause: a walking entity is written every tick, and advancing
    /// there would retrigger the walk animation at the tick rate.
    /// </summary>
    [Theory]
    [InlineData(EntityAction.Idle)]
    [InlineData(EntityAction.Moving)]
    [InlineData(EntityAction.Dead)]
    public void ContinuousAction_RepeatedDoesNotAdvance(EntityAction continuous)
    {
        var action = continuous;
        uint seq = 4;

        Assert.False(ActionStateLogic.Advance(ref action, ref seq, continuous));

        Assert.Equal(continuous, action);
        Assert.Equal(4u, seq);
    }

    [Fact]
    public void ContinuousActions_AreNotRetriggerable_AttackingIs()
    {
        Assert.True(ActionStateLogic.IsRetriggerable(EntityAction.Attacking));
        Assert.False(ActionStateLogic.IsRetriggerable(EntityAction.Idle));
        Assert.False(ActionStateLogic.IsRetriggerable(EntityAction.Moving));
        Assert.False(ActionStateLogic.IsRetriggerable(EntityAction.Dead));
        // Not an action at all — the absence of one.
        Assert.False(ActionStateLogic.IsRetriggerable(EntityAction.Unspecified));
    }

    /// <summary>
    /// Wrapping must skip zero. A counter that wrapped to it would tell every client to stop
    /// retriggering that entity until it wrapped again — four billion actions later — and
    /// nothing anywhere would report an error.
    /// </summary>
    [Fact]
    public void Wrap_SkipsZero()
    {
        var action = EntityAction.Attacking;
        uint seq = uint.MaxValue;

        Assert.True(ActionStateLogic.Advance(ref action, ref seq, EntityAction.Attacking));

        Assert.Equal(1u, seq);
        Assert.NotEqual(0u, seq);
    }

    /// <summary>
    /// A consumer is told to retrigger on INEQUALITY, not on increase. This pins the
    /// property that makes that rule necessary: the sequence is not monotonic.
    /// </summary>
    [Fact]
    public void Sequence_IsNotMonotonicAcrossWrap()
    {
        var action = EntityAction.Attacking;
        uint seq = uint.MaxValue;
        uint before = seq;

        ActionStateLogic.Advance(ref action, ref seq, EntityAction.Attacking);

        Assert.True(seq < before);
        Assert.NotEqual(before, seq);
    }
}
