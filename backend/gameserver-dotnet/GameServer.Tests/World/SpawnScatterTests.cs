using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace GameServer.Tests.World;

/// <summary>
/// rpg-mmo-server#469: every new player started on the same point (0, 0). The spawn scatter
/// spreads fresh placements; these pin that it is off at 0, deterministic, distinct, within its
/// radius and the map, and never inside a static box.
/// </summary>
public class SpawnScatterTests
{
    private static GameplayState State(float scatter, MapGeometry? geometry = null)
    {
        var world = new EcsWorld();
        if (geometry != null) world.Gameplay.Configure(geometry: geometry);
        world.Gameplay.SpawnScatter = scatter;
        return world.Gameplay;
    }

    [Fact]
    public void Zero_IsThePointItself()
    {
        Assert.Equal(new Vec2(4f, 5f), State(0f).ScatterSpawn("u1", new Vec2(4f, 5f)));
    }

    [Fact]
    public void PlayersStartApart_DeterministicallyAndWithinTheRing()
    {
        GameplayState g = State(3f);
        var placed = new List<Vec2>();
        for (int i = 0; i < 12; i++)
        {
            string id = Guid.NewGuid().ToString();
            Vec2 p = g.ScatterSpawn(id, Vec2.Zero);
            float d = MathF.Sqrt((p.X * p.X) + (p.Y * p.Y));
            Assert.InRange(d, 0f, 3f + 1e-4f);
            placed.Add(p);
        }

        int coincident = 0;
        for (int i = 0; i < placed.Count; i++)
        for (int j = i + 1; j < placed.Count; j++)
        {
            float dx = placed[i].X - placed[j].X, dy = placed[i].Y - placed[j].Y;
            if ((dx * dx) + (dy * dy) < 0.25f) coincident++;
        }

        Assert.True(coincident == 0, $"{coincident} pairs of 12 players started within 0.5 units");
    }

    [Fact]
    public void ThePlacementSequence_IsDeterministic()
    {
        GameplayState a = State(3f), b = State(3f);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(a.ScatterSpawn("x", Vec2.Zero), b.ScatterSpawn("y", Vec2.Zero));
        }
    }

    [Fact]
    public void ACandidateInsideABox_FallsBackToThePoint()
    {
        // One box covering everything around the spawn except the point's own column is not
        // needed: a box over the whole ring means every candidate lands in it.
        var bounds = MapBounds.FromSize(100, 100);
        var box = new StaticBox(-10f, -10f, 0f, 10f, 10f, 2f);
        var geometry = new MapGeometry(bounds, null, new[] { box }, null, null);
        Assert.Equal(new Vec2(0f, 0f), State(3f, geometry).ScatterSpawn("u1", new Vec2(0f, 0f)));
    }

    [Fact]
    public void ThePlacementStaysInsideTheMap()
    {
        var bounds = new MapBounds(0f, 0f, 1f, 1f);
        var geometry = new MapGeometry(bounds, null, null, null, null);
        Vec2 p = State(3f, geometry).ScatterSpawn("u1", new Vec2(0.5f, 0.5f));
        Assert.True(bounds.Contains(p));
    }
}
