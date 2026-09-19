using GameServer.Snapshot;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit;
using SimAction = Shared.GameLogic.Components.EntityAction;
using WireEventType = RpgMmo.Wire.V1.GameEventType;
using SimEventType = Shared.GameLogic.Components.GameEventType;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The delta encoder's treatment of the retrigger counter and of the event channel.
/// </summary>
/// <remarks>
/// These two features fail in the same silent way if they are wrong: the server behaves
/// correctly, every existing assertion passes, and the client simply never sees the thing.
/// So each test below asserts what reached the wire, not what the server computed.
/// </remarks>
public class ActionSeqAndEventTests
{
    private static EntityView View(
        int key, float x = 0f, SimAction action = SimAction.Idle, uint actionSeq = 1,
        int hp = 100) =>
        new(key, $"e{key}", "player", new Vec2(x, 0f), hp, 100, 4f, facingBrad: 1, action, actionSeq);

    private static SnapshotMessage Encode(
        SnapshotDeltaState state, ulong tick, EntityView[] views,
        PendingGameEvent[]? events = null, int observerKey = PendingGameEvent.NoKey,
        bool intern = true) =>
        state.Encode(tick, ackTick: 0, views.AsSpan(), keyframeInterval: 1000, intern: intern,
                     observer: default,
                     events: (events ?? System.Array.Empty<PendingGameEvent>()).AsSpan(),
                     observerKey: observerKey);

    // ── The retrigger counter through the delta encoder ──────────────────────────

    /// <summary>
    /// THE test for the whole action_seq feature. An attacker swinging twice from a standstill
    /// has identical position, HP, facing and action on both ticks. If the counter is not part
    /// of the delta encoder's equality, the second swing compares equal to the first, the
    /// delta suppresses the entity entirely, and the retrigger never reaches the client — with
    /// the server having done everything right and nothing anywhere failing.
    /// </summary>
    [Fact]
    public void RepeatedAttack_IsNotSuppressedByTheDelta()
    {
        var state = new SnapshotDeltaState(0);

        Encode(state, 1, new[] { View(1, action: SimAction.Attacking, actionSeq: 5) });

        SnapshotMessage second = Encode(
            state, 2, new[] { View(1, action: SimAction.Attacking, actionSeq: 6) });

        Assert.False(second.Full);
        var entity = Assert.Single(second.Entities);
        Assert.Equal(6u, entity.ActionSeq);
        Assert.Equal(RpgMmo.Wire.V1.EntityAction.Attacking, entity.Action);
    }

    /// <summary>
    /// The other half of the same contract: an entity that genuinely did not change is still
    /// suppressed. Without this, the test above could be satisfied by an encoder that simply
    /// stopped doing deltas.
    /// </summary>
    [Fact]
    public void UnchangedEntity_IncludingItsCounter_IsStillSuppressed()
    {
        var state = new SnapshotDeltaState(0);

        Encode(state, 1, new[] { View(1, action: SimAction.Attacking, actionSeq: 5) });

        SnapshotMessage second = Encode(
            state, 2, new[] { View(1, action: SimAction.Attacking, actionSeq: 5) });

        Assert.False(second.Full);
        Assert.Empty(second.Entities);
    }

    [Fact]
    public void ActionSeq_IsWrittenOnHandleOnlyMentions()
    {
        var state = new SnapshotDeltaState(0);

        // First mention introduces the handle and the id.
        Encode(state, 1, new[] { View(1, x: 0f, actionSeq: 3) });

        // Second mention moves, so it is resent — by handle alone, with no id.
        SnapshotMessage second = Encode(state, 2, new[] { View(1, x: 5f, actionSeq: 3) });

        var entity = Assert.Single(second.Entities);
        Assert.Equal("", entity.Id);
        Assert.NotEqual(0u, entity.Handle);
        // Complete state, not just what changed: a receiver resolving a handle expects it.
        Assert.Equal(3u, entity.ActionSeq);
    }

    // ── The event channel ────────────────────────────────────────────────────────

    private static PendingGameEvent Damage(int sourceKey, int targetKey, int amount = 12) =>
        new(GameEventData.Damage($"e{sourceKey}", $"e{targetKey}", amount), sourceKey, targetKey);

    [Fact]
    public void Event_BetweenTwoVisibleEntities_CarriesBothHandles()
    {
        var state = new SnapshotDeltaState(0);

        SnapshotMessage msg = Encode(
            state, 1, new[] { View(1), View(2) }, new[] { Damage(1, 2) });

        var ev = Assert.Single(msg.Events);
        Assert.Equal(WireEventType.Damage, ev.Type);
        Assert.Equal(12, ev.Amount);

        uint h1 = msg.Entities.Single(e => e.Id == "e1").Handle;
        uint h2 = msg.Entities.Single(e => e.Id == "e2").Handle;
        Assert.Equal(h1, ev.Source);
        Assert.Equal(h2, ev.Target);
        // Handles, not ids, on an interning connection.
        Assert.Equal("", ev.SourceId);
        Assert.Equal("", ev.TargetId);
    }

    /// <summary>
    /// A player who can see the victim but not the attacker still needs the number: the
    /// alternative is a health bar that drops with no explanation.
    /// </summary>
    [Fact]
    public void Event_WithOnlyTheTargetVisible_IsDeliveredWithNoSource()
    {
        var state = new SnapshotDeltaState(0);

        SnapshotMessage msg = Encode(
            state, 1, new[] { View(2) }, new[] { Damage(sourceKey: 99, targetKey: 2) });

        var ev = Assert.Single(msg.Events);
        Assert.Equal(0u, ev.Source);
        Assert.NotEqual(0u, ev.Target);
        // The unknown participant is omitted, never named: sending its id would disclose an
        // entity the AOI has deliberately not shown this connection.
        Assert.Equal("", ev.SourceId);
    }

    [Fact]
    public void Event_WithNeitherParticipantVisible_IsDropped()
    {
        var state = new SnapshotDeltaState(0);

        SnapshotMessage msg = Encode(
            state, 1, new[] { View(1) }, new[] { Damage(sourceKey: 98, targetKey: 99) });

        Assert.Empty(msg.Events);
    }

    /// <summary>
    /// Progression is addressed to its subject. An event channel that broadcast another
    /// player's experience would be an information disclosure shipped as a feature.
    /// </summary>
    [Fact]
    public void PrivateEvent_ReachesOnlyItsSubject()
    {
        var xp = new PendingGameEvent(
            new GameEventData(SimEventType.XpGain, null, "e2", 50, 0, GameEventFlags.None),
            PendingGameEvent.NoKey, 2);

        var subject = new SnapshotDeltaState(0);
        SnapshotMessage toSubject = Encode(
            subject, 1, new[] { View(1), View(2) }, new[] { xp }, observerKey: 2);
        var delivered = Assert.Single(toSubject.Events);
        Assert.Equal(WireEventType.XpGain, delivered.Type);
        Assert.Equal(50, delivered.Amount);

        // Same tick, same visible entities, a different observer: nothing.
        var bystander = new SnapshotDeltaState(0);
        SnapshotMessage toBystander = Encode(
            bystander, 1, new[] { View(1), View(2) }, new[] { xp }, observerKey: 1);
        Assert.Empty(toBystander.Events);
    }

    [Fact]
    public void PrivateEvent_WithNoObserverIdentity_IsDropped()
    {
        var xp = new PendingGameEvent(
            new GameEventData(SimEventType.XpGain, null, "e2", 50, 0, GameEventFlags.None),
            PendingGameEvent.NoKey, 2);

        var state = new SnapshotDeltaState(0);
        SnapshotMessage msg = Encode(state, 1, new[] { View(2) }, new[] { xp });

        Assert.Empty(msg.Events);
    }

    /// <summary>
    /// A keyframe restates the world's STATE because a client may have missed a delta. It
    /// does not restate its HISTORY: an event already shown must not be shown twice.
    /// </summary>
    [Fact]
    public void Keyframe_DoesNotReplayEarlierEvents()
    {
        var state = new SnapshotDeltaState(0);
        var views = new[] { View(1), View(2) };

        SnapshotMessage first = state.Encode(
            1, 0, views.AsSpan(), keyframeInterval: 1, intern: true, observer: default,
            events: new[] { Damage(1, 2) }.AsSpan(), observerKey: 1);
        Assert.Single(first.Events);

        // Drive on with no further events until the next keyframe actually lands. At
        // interval 1 the encoder alternates full/delta rather than sending a keyframe every
        // tick, so "the next encode" is not the next keyframe — asserting that was a wrong
        // assumption about the encoder, not a defect in it.
        SnapshotMessage next;
        int guard = 0;
        do
        {
            next = state.Encode(
                (ulong)(2 + guard), 0, views.AsSpan(), keyframeInterval: 1, intern: true,
                observer: default,
                events: System.ReadOnlySpan<PendingGameEvent>.Empty, observerKey: 1);
            guard++;
        }
        while (!next.Full && guard < 8);

        Assert.True(next.Full, "no keyframe arrived within 8 encodes");
        Assert.Empty(next.Events);
    }

    [Fact]
    public void Events_AreClearedBetweenEncodes()
    {
        var state = new SnapshotDeltaState(0);
        var views = new[] { View(1), View(2) };

        Encode(state, 1, views, new[] { Damage(1, 2) });
        SnapshotMessage second = Encode(state, 2, views);

        Assert.Empty(second.Events);
    }

    /// <summary>
    /// A JSON connection never interns, so ids are the only names available. This is the
    /// asymmetry that let action_seq ship to Protobuf alone and stay green.
    /// </summary>
    [Fact]
    public void JsonConnection_AddressesEventsByIdRatherThanHandle()
    {
        var state = new SnapshotDeltaState(0);

        SnapshotMessage msg = Encode(
            state, 1, new[] { View(1), View(2) }, new[] { Damage(1, 2) }, intern: false);

        var ev = Assert.Single(msg.Events);
        Assert.Equal("e1", ev.SourceId);
        Assert.Equal("e2", ev.TargetId);
        Assert.Equal(0u, ev.Source);
        Assert.Equal(0u, ev.Target);
    }

    [Fact]
    public void EventFlagsAndAbilityId_SurviveTheEncoder()
    {
        var state = new SnapshotDeltaState(0);
        var crit = new PendingGameEvent(
            GameEventData.Damage("e1", "e2", 99, abilityId: 7, flags: GameEventFlags.Critical),
            1, 2);

        SnapshotMessage msg = Encode(state, 1, new[] { View(1), View(2) }, new[] { crit });

        var ev = Assert.Single(msg.Events);
        Assert.Equal(7u, ev.AbilityId);
        Assert.Equal((uint)GameEventFlags.Critical, ev.Flags);
        Assert.Equal(99, ev.Amount);
    }

    [Fact]
    public void NoEvents_LeavesTheFieldEmptySoProto3ElidesIt()
    {
        var state = new SnapshotDeltaState(0);

        SnapshotMessage msg = Encode(state, 1, new[] { View(1) });

        Assert.Empty(msg.Events);
    }
}
