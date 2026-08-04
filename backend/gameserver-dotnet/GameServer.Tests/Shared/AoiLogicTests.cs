using Shared.GameLogic;

namespace GameServer.Tests.Shared;

public class AoiLogicTests
{
    [Fact]
    public void GetNearbyEntities_FiltersOutOfRange()
    {
        var entities = new List<EntityState>
        {
            TestHelpers.CreatePlayer("p1", x: 0, y: 0),
            TestHelpers.CreatePlayer("p2", x: 200, y: 200), // far away
        };

        var center = new Vec2(0f, 0f);
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

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
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void GetNearbyEntities_EmptyWorld()
    {
        var entities = new List<EntityState>();
        var center = new Vec2(0f, 0f);
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

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
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

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
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

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
        var result = AoiLogic.GetNearbyEntities(entities, center, 10f);

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
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

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
        var result = AoiLogic.GetNearbyEntities(entities, center, GameConstants.DefaultAoiRadius);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Id == "p3");
    }
}
