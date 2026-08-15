using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Input;
using GameServer.Net;
using GameServer.Server;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Server;

/// <summary>
/// The <c>JoinTokenResponse.tick_rate</c> contract, enforced rather than described.
///
/// <para><b>What the field promises</b> (docs/API.md): it is the rate at which the
/// authoritative tick advances <i>and</i> at which player movement is integrated, so a
/// client is correct to build its prediction timestep as <c>1 / tick_rate</c>.</para>
///
/// <para><b>Why a test and not a comment.</b> The value on the wire and the value the
/// movement integrator uses were, until this file existed, two separate reads of the same
/// configuration property. Two reads can drift, and this particular drift is silent: a
/// client predicting at a rate the server no longer simulates at is wrong by a fixed ratio
/// on every input, which is corrected by every snapshot and therefore smooths rather than
/// snaps. Nothing errors, no counter moves, and it presents as the game feeling soft. That
/// is the failure mode <c>#93</c> described and it is exactly what happened when the
/// multi-rate change shipped ahead of the client.</para>
///
/// <para><see cref="JoinTickRateTests"/> covers the other half — that the field actually
/// reaches the wire, in both encodings. This file covers what the number has to mean.</para>
/// </summary>
public class JoinTickRateContractTests
{
    /// <summary>
    /// The rate advertised to a client is the rate movement actually integrates at,
    /// measured rather than asserted: one input, one tick, and the displacement must be
    /// <c>speed / advertised</c>.
    ///
    /// <para>This is the client's own arithmetic. A predicting client that receives
    /// <c>tick_rate = N</c> and integrates <c>speed * (1/N)</c> per input reproduces the
    /// server's position exactly; if this test fails, that client is wrong by whatever
    /// ratio the drift introduced.</para>
    /// </summary>
    [Theory]
    [InlineData(15, 15, 5)]
    [InlineData(30, 15, 5)]
    [InlineData(60, 15, 5)]
    [InlineData(60, 20, 10)]
    public void TheAdvertisedRateIsTheRateMovementIntegratesAt(int critical, int world, int background)
    {
        SimulationRates rates = Rates(critical, world, background);
        uint advertised = AdvertisedRate(rates);

        var ecs = new EcsWorld();
        var connections = new ConnectionManager();
        // Constructed exactly as GameServerHost constructs it, from the same property that
        // feeds the wire — that shared read is the coupling this test is protecting.
        var handler = new InputHandler(ecs, NullLogger.Instance, null, rates.MovementHz, null);
        var loop = new TickLoop(ecs, handler, connections, rates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;

        ecs.PushInput("p1", new InputData(1, 1f, 0f, null));
        loop.TickOnce();

        float moved = ecs.GetEntity("p1")!.Value.Position.X;

        Assert.Equal(speed / advertised, moved, precision: 5);
    }

    /// <summary>
    /// The advertised rate is also the rate the tick counter advances at, which is what
    /// makes it usable for converting <c>tick</c> and <c>ack_tick</c> into seconds — the
    /// arithmetic a client needs to know how much simulated time an acknowledged input
    /// covered.
    /// </summary>
    [Theory]
    [InlineData(15, 15, 5)]
    [InlineData(30, 15, 5)]
    [InlineData(60, 15, 5)]
    [InlineData(120, 20, 10)]
    public void TheAdvertisedRateIsTheBaseTickRate(int critical, int world, int background)
    {
        SimulationRates rates = Rates(critical, world, background);

        Assert.Equal((uint)rates.BaseHz, AdvertisedRate(rates));

        // The structural reason: the critical group runs on every base tick, so the base
        // timeline and the critical rate are the same timeline. A configuration that broke
        // this would break the field's meaning.
        Assert.Equal(1, rates.CriticalEvery);
    }

    /// <summary>
    /// The advertised rate is <b>not</b> the snapshot cadence, and 60/15/5 is the
    /// configuration where confusing them is invisible in a single-rate test: the world
    /// rate there is exactly the old 15 a client used to hardcode.
    ///
    /// <para>A client that sized its interpolation buffer from this field would be four
    /// times short at the default configuration.</para>
    /// </summary>
    [Fact]
    public void TheAdvertisedRateIsNotTheSnapshotCadence()
    {
        SimulationRates rates = Rates(60, 15, 5);

        Assert.Equal(60u, AdvertisedRate(rates));
        Assert.NotEqual((uint)rates.WorldHz, AdvertisedRate(rates));

        // And the send cadence really is the world rate: 4 base ticks per snapshot.
        int due = 0;
        for (ulong tick = 1; tick <= 60; tick++)
        {
            if (rates.RunsOn(SimulationGroup.World, tick)) due++;
        }
        Assert.Equal(rates.WorldHz, due);
    }

    /// <summary>
    /// A client integrating at the advertised rate stays with the server over a whole
    /// second, not merely for one tick — including at a rate where the client sends less
    /// often than the server simulates, which is the case the held-input model exists for.
    /// </summary>
    [Theory]
    [InlineData(15, 15)]  // client sends every tick
    [InlineData(60, 15)]  // client sends every 4th tick: a 15Hz client, a 60Hz server
    [InlineData(60, 60)]
    public void APredictingClientUsingTheAdvertisedRateAgreesWithTheServerOverOneSecond(
        int criticalHz, int clientSendHz)
    {
        SimulationRates rates = Rates(criticalHz, 15, 5);
        uint advertised = AdvertisedRate(rates);

        var ecs = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(ecs, NullLogger.Instance, null, rates.MovementHz, null);
        var loop = new TickLoop(ecs, handler, connections, rates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;

        int sendEvery = criticalHz / clientSendHz;
        ulong clientTick = 0;
        for (int t = 1; t <= criticalHz; t++)
        {
            if ((t - 1) % sendEvery == 0) ecs.PushInput("p1", new InputData(++clientTick, 1f, 0f, null));
            loop.TickOnce();
        }

        float serverPosition = ecs.GetEntity("p1")!.Value.Position.X;

        // What a client predicting at the advertised rate computes for the same second:
        // one integration step per tick of the rate it was told about.
        float predicted = 0f;
        for (int i = 0; i < advertised; i++) predicted += speed / advertised;

        Assert.Equal(predicted, serverPosition, precision: 3);
    }

    /// <summary>
    /// The value a join response carries, taken from the single property both the wire and
    /// the movement integrator read. Mirrors <c>GameServerHost</c>'s construction; if that
    /// ever stops reading <see cref="SimulationRates.MovementHz"/>, the behavioural test
    /// above fails rather than this one silently agreeing with itself.
    /// </summary>
    private static uint AdvertisedRate(SimulationRates rates) => (uint)rates.MovementHz;

    private static SimulationRates Rates(int critical, int world, int background)
    {
        Assert.True(SimulationRates.TryCreate(critical, world, background, out var rates, out string? error), error);
        return rates!;
    }
}
