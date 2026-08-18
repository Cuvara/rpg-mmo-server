using System.Text.Json;
using GameServer.Observability;
using GameServer.Server;
using Xunit;

namespace GameServer.Tests.Observability;

/// <summary>
/// #144: <c>/status</c> published the legacy <c>--tick-rate</c> scalar, which no deployment
/// sets, so it reported the compiled-in default (15) on a server whose critical rate was 60.
///
/// <para>These tests pin the two properties that made that possible: that the rates on the
/// status object are derived from the resolved <see cref="SimulationRates"/>, and that each
/// published rate names the group it belongs to so no reader has to guess which one
/// <c>tick_rate</c> meant.</para>
/// </summary>
public class ServerStatusRatesTests
{
    [Fact]
    public void ApplyRates_PublishesEachGroupUnderItsOwnField()
    {
        Assert.True(SimulationRates.TryCreate(60, 15, 5, out var rates, out _));

        var status = new ServerStatus();
        status.ApplyRates(rates!);

        Assert.Equal(60, status.CriticalHz);
        Assert.Equal(15, status.WorldHz);
        Assert.Equal(5, status.BackgroundHz);
    }

    /// <summary>
    /// The defect in one assertion: at the standard 60/15/5 configuration the status
    /// endpoint must report 60, not the 15 the legacy default produced. 15 is a real
    /// configured rate here (the world group), which is why this test names the value it
    /// must NOT be — a regression would otherwise look like a plausible number.
    /// </summary>
    [Fact]
    public void TickRate_IsTheCriticalRate_NotTheWorldRate()
    {
        Assert.True(SimulationRates.TryCreate(60, 15, 5, out var rates, out _));

        var status = new ServerStatus();
        status.ApplyRates(rates!);

        Assert.Equal(60, status.TickRate);
        Assert.NotEqual(15, status.TickRate);
    }

    /// <summary>
    /// Contract: <c>/status</c>'s <c>tick_rate</c> and the wire's
    /// <c>join_token_resp.tick_rate</c> are the same number by definition (docs/API.md).
    /// Both read <see cref="SimulationRates.MovementHz"/>; this test fails if either grows
    /// its own source. A client that predicts at the join-response rate and a status panel
    /// that displays this one must never disagree.
    /// </summary>
    [Theory]
    [InlineData(60, 15, 5)]
    [InlineData(30, 15, 5)]
    [InlineData(20, 20, 20)]
    [InlineData(120, 30, 10)]
    public void TickRate_MatchesTheRatePublishedInTheJoinResponse(int critical, int world, int background)
    {
        Assert.True(SimulationRates.TryCreate(critical, world, background, out var rates, out _));

        var status = new ServerStatus();
        status.ApplyRates(rates!);

        Assert.Equal(rates!.MovementHz, status.TickRate);
    }

    /// <summary>
    /// A single-rate configuration is the one case where every group agrees, so it is the
    /// case where an ambiguous field would still look correct. Pinned so the multi-rate
    /// tests above are not the only thing standing between here and a scalar again.
    /// </summary>
    [Fact]
    public void UniformRates_ReportTheSameValueForEveryGroup()
    {
        var status = new ServerStatus();
        status.ApplyRates(SimulationRates.Uniform(20));

        Assert.Equal(20, status.TickRate);
        Assert.Equal(20, status.CriticalHz);
        Assert.Equal(20, status.WorldHz);
        Assert.Equal(20, status.BackgroundHz);
    }

    /// <summary>
    /// The JSON names are the actual contract — the Unity DOTS sample deserializes
    /// <c>tick_rate</c> out of this payload by name. Serialization goes through the
    /// AOT-safe source-generated context, so this also covers the case where a new property
    /// is added but the context is not regenerated.
    /// </summary>
    [Fact]
    public void SerializedPayload_CarriesTheGroupNamedFields()
    {
        Assert.True(SimulationRates.TryCreate(60, 15, 5, out var rates, out _));

        var status = new ServerStatus { Ok = true, Capacity = 100, CurrentTick = 726335 };
        status.ApplyRates(rates!);

        string json = JsonSerializer.Serialize(status);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(60, root.GetProperty("tick_rate").GetInt32());
        Assert.Equal(60, root.GetProperty("sim_critical_hz").GetInt32());
        Assert.Equal(15, root.GetProperty("sim_world_hz").GetInt32());
        Assert.Equal(5, root.GetProperty("sim_background_hz").GetInt32());
        Assert.Equal(100, root.GetProperty("capacity").GetInt32());
        Assert.Equal(726335UL, root.GetProperty("current_tick").GetUInt64());
    }

    [Fact]
    public void ApplyRates_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerStatus().ApplyRates(null!));
    }
}
