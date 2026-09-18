using GameServer.Server;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Server;

/// <summary>
/// The AOI radius is the largest single lever on downstream bandwidth — population inside
/// a circle grows with its square — so every way of getting it wrong has to be a startup
/// failure rather than a silent fall-back to the default.
/// </summary>
public class AoiSettingsTests
{
    private const float Width = GameConstants.DefaultMapWidth;
    private const float Height = GameConstants.DefaultMapHeight;

    private static AoiSettings Ok(string? raw)
    {
        Assert.True(AoiSettings.TryCreate(raw, Width, Height, out AoiSettings? s, out string? err),
            $"expected {raw ?? "<unset>"} to be accepted, got: {err}");
        return s!;
    }

    private static string Refused(string raw)
    {
        Assert.False(AoiSettings.TryCreate(raw, Width, Height, out AoiSettings? s, out string? err),
            $"expected {raw} to be refused");
        Assert.Null(s);
        Assert.False(string.IsNullOrWhiteSpace(err));
        return err!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_IsTheCompiledInDefault(string? raw)
    {
        Assert.Equal(GameConstants.DefaultAoiRadius, Ok(raw).Radius);
    }

    [Theory]
    [InlineData("25", 25f)]
    [InlineData("50", 50f)]
    [InlineData("12.5", 12.5f)]
    [InlineData("0.5", 0.5f)]
    public void ValidValues_Parse(string raw, float expected)
    {
        Assert.Equal(expected, Ok(raw).Radius);
    }

    /// <summary>
    /// A container inherits whatever locale its base image carries. Under a comma-decimal
    /// locale a naive parse turns "12,5" into 125 on some runtimes and fails on others —
    /// a tenfold radius, which is a hundredfold population, discovered from a bandwidth
    /// graph. InvariantCulture means "12,5" is simply not a number.
    /// </summary>
    [Fact]
    public void CommaDecimal_IsRefusedRatherThanReinterpreted()
    {
        string err = Refused("12,5");
        Assert.Contains("not a number", err);
    }

    [Theory]
    [InlineData("5o")]        // the fat finger this whole type exists for
    [InlineData("fifty")]
    [InlineData("50m")]
    public void Unparseable_IsRefused(string raw) => Assert.Contains("not a number", Refused(raw));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.0001")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void NonPositiveOrNonFinite_IsRefused(string raw)
        => Assert.Contains("positive finite", Refused(raw));

    [Fact]
    public void AboveTheCeiling_IsRefused()
    {
        string err = Refused((AoiSettings.MaxRadius * 2f).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("ceiling", err);
    }

    [Fact]
    public void AtTheCeiling_IsAccepted()
    {
        Assert.Equal(AoiSettings.MaxRadius, Ok(
            AoiSettings.MaxRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)).Radius);
    }

    /// <summary>
    /// The whole-map report is against the DIAGONAL, not the width: an observer in one
    /// corner has to reach the opposite one before AOI has stopped filtering anything.
    /// Testing it against the width would call a radius "covering" while it still excluded
    /// the far corners, which is the case where AOI is doing its most useful work.
    /// </summary>
    [Fact]
    public void CoversWholeMap_IsMeasuredAgainstTheDiagonal()
    {
        // 1000x1000 -> diagonal ~1414.2
        Assert.False(Ok("1000").CoversWholeMap);
        Assert.False(Ok("1414").CoversWholeMap);
        Assert.True(Ok("1415").CoversWholeMap);
    }

    [Fact]
    public void CoversWholeMap_IsFalseAtTheDefault()
    {
        Assert.False(Ok(null).CoversWholeMap);
        Assert.False(AoiSettings.Default.CoversWholeMap);
    }

    /// <summary>The startup banner prints this; an operator reads it to confirm a pod.</summary>
    [Fact]
    public void ToString_NamesTheRadiusAndTheWholeMapCase()
    {
        Assert.Contains("25", Ok("25").ToString());
        Assert.DoesNotContain("whole map", Ok("25").ToString());
        Assert.Contains("whole map", Ok("5000").ToString());
    }
}
