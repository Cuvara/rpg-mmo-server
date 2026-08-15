using System.Collections.Generic;
using GameServer.Server;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// The rate model: what configurations are accepted, what the integer timeline reduces to,
/// and what a rejected configuration says to the operator.
/// </summary>
public class SimulationRatesTests
{
    [Fact]
    public void DefaultConfiguration_IsSixtyFifteenFive()
    {
        SimulationRates rates = SimulationRates.Default;

        Assert.Equal(60, rates.CriticalHz);
        Assert.Equal(15, rates.WorldHz);
        Assert.Equal(5, rates.BackgroundHz);
    }

    [Fact]
    public void BaseRateIsTheCriticalRate_AndTheOthersAreDivisorsOfIt()
    {
        SimulationRates rates = SimulationRates.Default;

        Assert.Equal(60, rates.BaseHz);
        Assert.Equal(1, rates.CriticalEvery);
        Assert.Equal(4, rates.WorldEvery);   // 60 / 15
        Assert.Equal(12, rates.BackgroundEvery); // 60 / 5
    }

    /// <summary>
    /// The dt rule, stated as a test: a group's timestep is the reciprocal of its own
    /// frequency, never of the base rate. This is the single property that keeps
    /// "units per second" meaning the same thing in every group.
    /// </summary>
    [Fact]
    public void EachGroupsDeltaTime_IsTheReciprocalOfItsOwnRate_NotOfTheBaseRate()
    {
        SimulationRates rates = SimulationRates.Default;

        Assert.Equal(1f / 60f, rates.DeltaTimeFor(SimulationGroup.Critical));
        Assert.Equal(1f / 15f, rates.DeltaTimeFor(SimulationGroup.World));
        Assert.Equal(1f / 5f, rates.DeltaTimeFor(SimulationGroup.Background));

        // The bug this forbids: the world group is stepped every 4th base tick, so handing
        // it the base dt would make everything it integrates run at a quarter speed.
        Assert.NotEqual(rates.DeltaTimeFor(SimulationGroup.World),
                        rates.DeltaTimeFor(SimulationGroup.Critical));
    }

    /// <summary>
    /// The headline ratio from the spec: 60 base ticks is 60 critical runs, 15 world runs
    /// and 5 background runs. Counted by walking the timeline, not by arithmetic on the
    /// divisors, so an off-by-one in the boundary test would show up here.
    /// </summary>
    [Fact]
    public void OverSixtyBaseTicks_GroupsRunSixtyFifteenAndFiveTimes()
    {
        SimulationRates rates = SimulationRates.Default;
        var runs = new Dictionary<SimulationGroup, int>
        {
            [SimulationGroup.Critical] = 0,
            [SimulationGroup.World] = 0,
            [SimulationGroup.Background] = 0,
        };

        for (ulong tick = 1; tick <= 60; tick++)
        {
            foreach (SimulationGroup group in SimulationSchedule.Groups)
            {
                if (rates.RunsOn(group, tick)) runs[group]++;
            }
        }

        Assert.Equal(60, runs[SimulationGroup.Critical]);
        Assert.Equal(15, runs[SimulationGroup.World]);
        Assert.Equal(5, runs[SimulationGroup.Background]);
    }

    /// <summary>The spec's second ratio: 12 base ticks is 12 / 3 / 1.</summary>
    [Fact]
    public void OverTwelveBaseTicks_GroupsRunTwelveThreeAndOnce()
    {
        SimulationRates rates = SimulationRates.Default;
        int critical = 0, world = 0, background = 0;

        for (ulong tick = 1; tick <= 12; tick++)
        {
            if (rates.RunsOn(SimulationGroup.Critical, tick)) critical++;
            if (rates.RunsOn(SimulationGroup.World, tick)) world++;
            if (rates.RunsOn(SimulationGroup.Background, tick)) background++;
        }

        Assert.Equal(12, critical);
        Assert.Equal(3, world);
        Assert.Equal(1, background);
    }

    /// <summary>
    /// All three groups align on the first tick, and the world/background boundaries fall
    /// where the spec's diagram says: 1, 5, 9, 13 for world; 1, 13 for background.
    /// </summary>
    [Fact]
    public void GroupBoundariesLandOnTheTicksTheTimelineDiagramShows()
    {
        SimulationRates rates = SimulationRates.Default;

        Assert.True(rates.RunsOn(SimulationGroup.World, 1));
        Assert.False(rates.RunsOn(SimulationGroup.World, 2));
        Assert.False(rates.RunsOn(SimulationGroup.World, 4));
        Assert.True(rates.RunsOn(SimulationGroup.World, 5));
        Assert.True(rates.RunsOn(SimulationGroup.World, 13));

        Assert.True(rates.RunsOn(SimulationGroup.Background, 1));
        Assert.False(rates.RunsOn(SimulationGroup.Background, 12));
        Assert.True(rates.RunsOn(SimulationGroup.Background, 13));
    }

    /// <summary>
    /// A uniform configuration collapses to one timeline — every group on every tick. This
    /// is what reproduces the pre-multi-rate server, and it is why the existing
    /// tick-sensitive tests did not have to be rewritten.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    public void UniformConfiguration_RunsEveryGroupOnEveryTick(int hz)
    {
        SimulationRates rates = SimulationRates.Uniform(hz);

        Assert.Equal(hz, rates.BaseHz);
        Assert.Equal(1, rates.WorldEvery);
        Assert.Equal(1, rates.BackgroundEvery);

        for (ulong tick = 1; tick <= 10; tick++)
        {
            Assert.True(rates.RunsOn(SimulationGroup.World, tick));
            Assert.True(rates.RunsOn(SimulationGroup.Background, tick));
        }
    }

    [Theory]
    [InlineData(60, 15, 5)]
    [InlineData(60, 20, 10)]
    [InlineData(30, 15, 5)]
    [InlineData(15, 15, 5)]
    [InlineData(15, 15, 15)]
    public void ValidConfigurationsAreAccepted(int critical, int world, int background)
    {
        Assert.True(SimulationRates.TryCreate(critical, world, background, out SimulationRates? rates, out string? error));
        Assert.NotNull(rates);
        Assert.Null(error);
    }

    /// <summary>
    /// Every rejection is a startup failure, not a fallback. A server that quietly
    /// substitutes a working rate for an unusable one is a server whose behaviour does not
    /// match its configuration, and nothing downstream can detect that.
    /// </summary>
    [Theory]
    [InlineData(0, 15, 5, "SIM_CRITICAL_HZ")]
    [InlineData(-1, 15, 5, "SIM_CRITICAL_HZ")]
    [InlineData(60, 0, 5, "SIM_WORLD_HZ")]
    [InlineData(60, 15, 0, "SIM_BACKGROUND_HZ")]
    [InlineData(6000, 15, 5, "SIM_CRITICAL_HZ")]
    public void OutOfRangeRatesAreRejected_NamingTheVariable(
        int critical, int world, int background, string expectedVariable)
    {
        Assert.False(SimulationRates.TryCreate(critical, world, background, out SimulationRates? rates, out string? error));
        Assert.Null(rates);
        Assert.NotNull(error);
        Assert.Contains(expectedVariable, error);
    }

    /// <summary>
    /// The configuration with no integer timeline. 25 does not divide 60; the true common
    /// base would be 300Hz, which would make the server run five times faster than anyone
    /// asked for. Rejected, with the usable values named.
    /// </summary>
    [Fact]
    public void ANonDivisorRateIsRejected_AndTheErrorListsUsableValues()
    {
        Assert.False(SimulationRates.TryCreate(60, 25, 5, out _, out string? error));

        Assert.NotNull(error);
        Assert.Contains("SIM_WORLD_HZ=25", error);
        Assert.Contains("does not divide", error);
        // The message must be actionable, not merely correct.
        Assert.Contains("20", error); // 20 is a divisor of 60 and the nearest usable value
    }

    [Fact]
    public void AGroupFasterThanTheCriticalGroupIsRejected()
    {
        Assert.False(SimulationRates.TryCreate(15, 60, 5, out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("faster than", error);

        Assert.False(SimulationRates.TryCreate(60, 5, 15, out _, out string? backgroundError));
        Assert.NotNull(backgroundError);
        Assert.Contains("SIM_BACKGROUND_HZ", backgroundError);
    }
}
