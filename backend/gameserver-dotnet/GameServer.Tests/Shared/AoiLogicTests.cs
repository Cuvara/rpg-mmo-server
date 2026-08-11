using Shared.GameLogic;

namespace GameServer.Tests.Shared;

public class AoiLogicTests
{
    /// <summary>
    /// Run the AOI filter with a buffer that is always large enough, and return the
    /// matches as a list. The overflow contract is exercised separately below.
    /// </summary>
    private static List<EntityState> Nearby(List<EntityState> entities, Vec2 center, float radius)
    {
        var buffer = new EntityState[entities.Count];
        int count = AoiLogic.GetNearbyEntities(entities, center, radius, buffer);
        Assert.True(count <= buffer.Length, "buffer sized to the whole world cannot overflow");
        return buffer.Take(count).ToList();
    }

    [Fact]
    public void GetNearbyEntities_FiltersOutOfRange()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 200, y: 200), // far away
        };

        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Single(result);
        Assert.Equal("p1", result[0].Id);
    }

    [Fact]
    public void GetNearbyEntities_IncludesInRange()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 10, y: 10),
            TestHelpers.CreatePlayer("p3", x: 20, y: 20),
        };

        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void GetNearbyEntities_EmptyWorld()
    {
        var entities = new List<EntityState>();
        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Empty(result);
    }

    [Fact]
    public void GetNearbyEntities_ExactBoundary_IsIncluded()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: GameConstants.DefaultAoiRadius, y: 0),
        };

        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Single(result);
    }

    [Fact]
    public void GetNearbyEntities_SlightlyOutsideBoundary_IsExcluded()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: GameConstants.DefaultAoiRadius + 0.1f, y: 0),
        };

        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Empty(result);
    }

    [Fact]
    public void GetNearbyEntities_CustomRadius()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 5, y: 0),
            TestHelpers.CreatePlayer("p2", x: 15, y: 0),
        };

        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, 10f);

        Assert.Single(result);
        Assert.Equal("p1", result[0].Id);
    }

    [Fact]
    public void GetNearbyEntities_IncludesMobsAndPlayers()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreateMob("m1", x: 5, y: 5),
        };

        var center = new Vec2(0f, 0f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetNearbyEntities_CenterNotAtOrigin()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 100, y: 100),
            TestHelpers.CreatePlayer("p2", x: 105, y: 105),
            TestHelpers.CreatePlayer("p3", x: 0, y: 0), // far from center
        };

        var center = new Vec2(100f, 100f);
        var result = Nearby(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Id == "p3");
    }

    // ── Overflow contract (ADR-10): count, do not saturate ──

    [Fact]
    public void GetNearbyEntities_BufferTooSmall_ReturnsNeededCount_AndFillsWhatFits()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 1, y: 0),
            TestHelpers.CreatePlayer("p3", x: 2, y: 0),
        };

        var buffer = new EntityState[2];
        int count = AoiLogic.GetNearbyEntities(entities, new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, buffer);

        // The return value is what the buffer NEEDED, not what it held — that is how
        // the caller distinguishes "exactly full" from "truncated".
        Assert.Equal(3, count);
        Assert.True(count > buffer.Length);

        // The prefix that fitted is written, in source order.
        Assert.Equal("p1", buffer[0].Id);
        Assert.Equal("p2", buffer[1].Id);
    }

    [Fact]
    public void GetNearbyEntities_ExactlyFullBuffer_IsNotReportedAsOverflow()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 1, y: 0),
        };

        var buffer = new EntityState[2];
        int count = AoiLogic.GetNearbyEntities(entities, new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, buffer);

        Assert.Equal(2, count);
        Assert.False(count > buffer.Length);
    }

    [Fact]
    public void GetNearbyEntities_ResizeAndRetry_SucceedsInOneRetry()
    {
        var entities = new List<EntityState>();
        for (int i = 0; i < 10; i++)
        {
            entities.Add(TestHelpers.CreatePlayer($"p{i}", x: i, y: 0));
        }

        var small = new EntityState[1];
        int needed = AoiLogic.GetNearbyEntities(entities, new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, small);

        var right = new EntityState[needed];
        int count = AoiLogic.GetNearbyEntities(entities, new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, right);

        Assert.Equal(needed, count);
        Assert.Equal(10, count);
    }

    [Fact]
    public void GetNearbyEntities_EmptyDestination_StillCounts()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 1, y: 0),
        };

        int count = AoiLogic.GetNearbyEntities(
            entities, new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, Span<EntityState>.Empty);

        Assert.Equal(2, count);
    }

    [Fact]
    public void GetNearbyEntities_SpanSource_MatchesListSource()
    {
        var entities = new[]
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 200, y: 0),
            TestHelpers.CreatePlayer("p3", x: 3, y: 4),
        };

        var fromSpan = new EntityState[3];
        int spanCount = AoiLogic.GetNearbyEntities(
            (ReadOnlySpan<EntityState>)entities, new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, fromSpan);

        var fromList = new EntityState[3];
        int listCount = AoiLogic.GetNearbyEntities(
            entities.ToList(), new Vec2(0f, 0f), GameConstants.DefaultAoiRadius, fromList);

        Assert.Equal(listCount, spanCount);
        Assert.Equal(2, spanCount);
        for (int i = 0; i < spanCount; i++)
        {
            Assert.Equal(fromList[i].Id, fromSpan[i].Id);
        }
    }
}
