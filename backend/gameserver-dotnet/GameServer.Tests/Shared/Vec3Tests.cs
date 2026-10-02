namespace GameServer.Tests.Shared;

/// <summary>
/// Vec3 (ADR-28): the 3D counterpart of Vec2, same explicit-rounding discipline.
/// </summary>
public class Vec3Tests
{
    [Fact]
    public void Magnitude_And_Distance()
    {
        var v = new Vec3(2f, 3f, 6f); // 4 + 9 + 36 = 49
        Assert.Equal(49f, v.SqrMagnitude);
        Assert.Equal(7f, v.Magnitude);
        Assert.Equal(49f, Vec3.DistanceSq(Vec3.Zero, v));
        Assert.Equal(7f, Vec3.Distance(v, Vec3.Zero));
    }

    [Fact]
    public void Normalized_IsUnit_AndZeroBelowEpsilon()
    {
        var n = new Vec3(3f, -4f, 12f).Normalized;
        Assert.Equal(1f, n.Magnitude, precision: 5);
        Assert.Equal(Vec3.Zero, new Vec3(1e-7f, 0f, 0f).Normalized);
        Assert.Equal(Vec3.Zero, Vec3.Zero.Normalized);
    }

    [Fact]
    public void XY_DropsHeight_AndFromXY_RoundTrips()
    {
        var p = new Vec3(1.5f, -2.5f, 9f);
        Assert.Equal(new Vec2(1.5f, -2.5f), p.XY);
        Assert.Equal(p, Vec3.FromXY(p.XY, 9f));
    }

    [Fact]
    public void Lerp_EndpointsAreExact()
    {
        var a = new Vec3(-12.7f, 40.3f, 1.1f);
        var b = new Vec3(-12.35f, 40.05f, 1.45f);
        Assert.Equal(a, Vec3.Lerp(a, b, 0f));
        Assert.Equal(new Vec3(1f, 2f, 3f), Vec3.Lerp(Vec3.Zero, new Vec3(2f, 4f, 6f), 0.5f));
    }

    [Fact]
    public void AddScaled_MatchesSplitMultiplyAdd()
    {
        var o = new Vec3(1f, 2f, 3f);
        var d = new Vec3(0.1f, 0.2f, 0.3f);
        Vec3 r = Vec3.AddScaled(o, d, 3f);
        Assert.Equal((float)(1f + (float)(0.1f * 3f)), r.X);
        Assert.Equal((float)(3f + (float)(0.3f * 3f)), r.Z);
    }

    [Fact]
    public void Dot_And_Operators()
    {
        var a = new Vec3(1f, 2f, 3f);
        var b = new Vec3(4f, -5f, 6f);
        Assert.Equal(12f, Vec3.Dot(a, b));
        Assert.Equal(new Vec3(5f, -3f, 9f), a + b);
        Assert.Equal(new Vec3(-3f, 7f, -3f), a - b);
        Assert.Equal(new Vec3(2f, 4f, 6f), a * 2f);
        Assert.Equal(new Vec3(2f, 4f, 6f), 2f * a);
        Assert.True(a != b);
    }

    [Fact]
    public void IsFinite_DetectsNaNAndInfinity()
    {
        Assert.True(new Vec3(1f, 2f, 3f).IsFinite);
        Assert.False(new Vec3(float.NaN, 0f, 0f).IsFinite);
        Assert.False(new Vec3(0f, 0f, float.PositiveInfinity).IsFinite);
    }
}
