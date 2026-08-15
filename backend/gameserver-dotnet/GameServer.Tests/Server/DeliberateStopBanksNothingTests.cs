using System;
using GameServer.Input;
using GameServer.Net;
using GameServer.Server;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// A player who deliberately stops must not bank movement, and the next input they send
/// must be worth one ordinary step.
///
/// <para><b>Why this is a separate invariant from the cap.</b> The elapsed-time step
/// (#100) exists so that packets <i>lost or coalesced away</i> do not cost a player
/// distance: <c>StepDeltaTime</c> pays back <c>baseTick - LastMoveTick</c>, bounded by
/// <see cref="GameConstants.MaxBankedMovementMs"/>. That is right for a client that went
/// quiet. It is wrong for a client that told us to stop — nothing was lost, because we
/// heard them.</para>
///
/// <para>Until this was fixed, the deadzone branch cleared <c>HeldFromTick</c> but left
/// <c>LastMoveTick</c> behind, so every deliberate pause was indistinguishable from a
/// network stall. Standing still for longer than the cap and then pressing a key
/// discharged the whole capped window in one step: <b>1.36 world units measured against a
/// normal 0.083</b>. Distance was correct and the delivery was not, which reads to a
/// player as a lurch on every restart and has nothing to do with the network.</para>
///
/// <para><b>What makes this catchable at all.</b> Total distance over a run is unchanged
/// by the defect — the player does end up where they should. What changes is the size of
/// a single step, so that is what these assert. A test summing travel would pass
/// throughout.</para>
/// </summary>
public sealed class DeliberateStopBanksNothingTests
{
    private static SimulationRates Rates(int critical, int world, int background)
    {
        Assert.True(SimulationRates.TryCreate(critical, world, background, out var rates, out string? error), error);
        return rates!;
    }

    /// <summary>
    /// Move, stop explicitly, wait longer than the cap, then move again. The step after
    /// the pause must be an ordinary one.
    /// </summary>
    [Theory]
    [InlineData(15, 15, 5)]
    [InlineData(60, 15, 5)]
    public void AnExplicitStopIsNotBankedAsLostTime(int critical, int world, int background)
    {
        SimulationRates rates = Rates(critical, world, background);

        var ecs = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(ecs, NullLogger.Instance, null, rates.MovementHz, null);
        var loop = new TickLoop(ecs, handler, connections, rates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;
        float ordinaryStep = speed / rates.MovementHz;

        // Move for one tick.
        ecs.PushInput("p1", new InputData(1, 1f, 0f, null));
        loop.TickOnce();

        // Explicit stop: a zero vector is MoveResult.None, which is how the server learns
        // the player released the key rather than the packet going missing.
        ecs.PushInput("p1", new InputData(2, 0f, 0f, null));
        loop.TickOnce();

        // Stand still well past the cap, sending nothing — the player is holding no key.
        int idleTicks = (GameConstants.MaxBankedMovementTicks(rates.MovementHz) * 3) + 5;
        for (int i = 0; i < idleTicks; i++) loop.TickOnce();

        float before = ecs.GetEntity("p1")!.Value.Position.X;

        // Press again.
        ecs.PushInput("p1", new InputData(3, 1f, 0f, null));
        loop.TickOnce();

        float step = ecs.GetEntity("p1")!.Value.Position.X - before;

        Assert.True(step <= ordinaryStep * 1.5f,
            $"the step after a deliberate stop was {step:F4}, against an ordinary " +
            $"{ordinaryStep:F4}. Banked time is being paid back for a pause the player " +
            "chose, so every restart lurches. Only silence the server did not hear about " +
            "should bank.");
    }

    /// <summary>
    /// The complement, and the reason this fix is not simply "never bank": a client that
    /// goes <b>silent</b> — sends nothing at all, the packet-loss case #100 is about —
    /// must still be paid back, or the defect returns.
    ///
    /// <para>Without this case the invariant above could be satisfied by removing the
    /// elapsed-time step altogether, which is the change that would silently reintroduce
    /// a 46% travel loss under bursty arrival.</para>
    /// </summary>
    [Fact]
    public void SilenceTheServerNeverHeardAboutIsStillBanked()
    {
        SimulationRates rates = Rates(60, 15, 5);

        var ecs = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(ecs, NullLogger.Instance, null, rates.MovementHz, null);
        var loop = new TickLoop(ecs, handler, connections, rates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;
        float ordinaryStep = speed / rates.MovementHz;

        ecs.PushInput("p1", new InputData(1, 1f, 0f, null));
        loop.TickOnce();

        // No stop, no packets: the hold expires and then nothing arrives. This is a client
        // whose packets were lost, and its time must not be lost with them.
        int silentTicks = rates.WorldEvery + 4;
        for (int i = 0; i < silentTicks; i++) loop.TickOnce();

        float before = ecs.GetEntity("p1")!.Value.Position.X;

        ecs.PushInput("p1", new InputData(2, 1f, 0f, null));
        loop.TickOnce();

        float step = ecs.GetEntity("p1")!.Value.Position.X - before;

        Assert.True(step > ordinaryStep * 1.5f,
            $"a step after unannounced silence was {step:F4}, no larger than an ordinary " +
            $"{ordinaryStep:F4}. The elapsed-time step is not paying back time the client " +
            "lost to the network, which is the #100 defect returning.");
    }
}
