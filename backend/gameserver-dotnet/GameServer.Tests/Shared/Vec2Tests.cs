using Shared.GameLogic.Components;

namespace GameServer.Tests.Shared;

public class Vec2Tests
{
    [Fact]
    public void Constructor_SetsXAndY()
    {
        var v = new Vec2(3f, 4f);
        Assert.Equal(3f, v.X);
        Assert.Equal(4f, v.Y);
    }

    [Fact]
    public void Zero_HasZeroComponents()
    {
        var v = new Vec2(0f, 0f);
        Assert.Equal(0f, v.X);
        Assert.Equal(0f, v.Y);
    }

    // --- Arithmetic operators ---

    [Fact]
    public void Add_ReturnsCorrectSum()
    {
        var a = new Vec2(1f, 2f);
        var b = new Vec2(3f, 4f);
        var result = a + b;
        Assert.Equal(4f, result.X);
        Assert.Equal(6f, result.Y);
    }

    [Fact]
    public void Subtract_ReturnsCorrectDifference()
    {
        var a = new Vec2(5f, 7f);
        var b = new Vec2(2f, 3f);
        var result = a - b;
        Assert.Equal(3f, result.X);
        Assert.Equal(4f, result.Y);
    }

    [Fact]
    public void Multiply_ScalarReturnsCorrectProduct()
    {
        var v = new Vec2(3f, 4f);
        var result = v * 2f;
        Assert.Equal(6f, result.X);
        Assert.Equal(8f, result.Y);
    }

    [Fact]
    public void Multiply_ByZero_ReturnsZeroVector()
    {
        var v = new Vec2(3f, 4f);
        var result = v * 0f;
        Assert.Equal(0f, result.X);
        Assert.Equal(0f, result.Y);
    }

    [Fact]
    public void Multiply_ByNegative_NegatesVector()
    {
        var v = new Vec2(3f, 4f);
        var result = v * -1f;
        Assert.Equal(-3f, result.X);
        Assert.Equal(-4f, result.Y);
    }

    // --- Distance ---

    [Fact]
    public void Distance_ReturnsCorrectValue()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(3f, 4f);
        Assert.Equal(5f, Vec2.Distance(in a, in b), precision: 5);
    }

    [Fact]
    public void Distance_ToSelf_ReturnsZero()
    {
        var v = new Vec2(5f, 10f);
        Assert.Equal(0f, Vec2.Distance(in v, in v));
    }

    [Fact]
    public void Distance_IsCommutative()
    {
        var a = new Vec2(1f, 2f);
        var b = new Vec2(4f, 6f);
        Assert.Equal(Vec2.Distance(in a, in b), Vec2.Distance(in b, in a), precision: 5);
    }

    [Fact]
    public void DistanceSq_ReturnsSquaredDistance()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(3f, 4f);
        Assert.Equal(25f, Vec2.DistanceSq(in a, in b), precision: 5);
    }

    [Fact]
    public void DistanceSq_ToSelf_ReturnsZero()
    {
        var v = new Vec2(3f, 4f);
        Assert.Equal(0f, Vec2.DistanceSq(in v, in v));
    }

    // --- Equality (value equality) ---

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new Vec2(1f, 2f);
        var b = new Vec2(1f, 2f);
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var a = new Vec2(1f, 2f);
        var b = new Vec2(3f, 4f);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void GetHashCode_SameValues_SameHash()
    {
        var a = new Vec2(1f, 2f);
        var b = new Vec2(1f, 2f);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // --- Normalized ---

    [Fact]
    public void Normalized_ReturnsUnitVector()
    {
        var v = new Vec2(3f, 4f);
        var n = v.Normalized;
        var length = MathF.Sqrt(n.X * n.X + n.Y * n.Y);
        Assert.Equal(1f, length, precision: 5);
    }

    [Fact]
    public void Normalized_PreservesDirection()
    {
        var v = new Vec2(3f, 4f);
        var n = v.Normalized;
        Assert.Equal(3f / 5f, n.X, precision: 5);
        Assert.Equal(4f / 5f, n.Y, precision: 5);
    }

    [Fact]
    public void Normalized_ZeroVector_ReturnsZero()
    {
        var v = new Vec2(0f, 0f);
        var n = v.Normalized;
        Assert.Equal(0f, n.X);
        Assert.Equal(0f, n.Y);
    }

    [Fact]
    public void Normalized_UnitVector_ReturnsSelf()
    {
        var v = new Vec2(1f, 0f);
        var n = v.Normalized;
        Assert.Equal(1f, n.X, precision: 5);
        Assert.Equal(0f, n.Y, precision: 5);
    }

    // --- Edge cases ---

    [Fact]
    public void NegativeComponents_WorkCorrectly()
    {
        var v = new Vec2(-3f, -4f);
        var origin = new Vec2(0f, 0f);
        Assert.Equal(5f, Vec2.Distance(in v, in origin), precision: 5);
    }

    [Fact]
    public void LargeValues_WorkCorrectly()
    {
        var v = new Vec2(10000f, 20000f);
        var n = v.Normalized;
        var length = MathF.Sqrt(n.X * n.X + n.Y * n.Y);
        Assert.Equal(1f, length, precision: 4);
    }
}
