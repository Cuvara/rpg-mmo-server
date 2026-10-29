using GameServer.Server;
using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace GameServer.Tests.World;

/// <summary>
/// S01 20261029T091525Z-S01-3r4s: three players joining together all landed on spiral slot 0
/// (nearest 0.0). Join handlers run concurrently, a joining player is invisible to the
/// position copy until its entity is added, and the spiral cursor was an unsynchronised int,
/// so every in-flight join saw an empty world and the same slot. These drive the real join
/// placement from several threads behind a barrier.
/// </summary>
public class SpawnJoinRaceTests
{
    private const float Scatter = 3f;

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static Vec2 Join(EcsWorld world, string id)
    {
        Vec2 at = GameServerHost.ScatterJoinSpawn(world, id, Vec2.Zero);
        // The window the race lived in: placed, not yet visible in the world.
        Thread.Yield();
        world.AddEntity(new EntityState { Id = id, Type = "player", Position = at, Hp = 100, MaxHp = 100 });
        world.Gameplay.ReleaseSpawnReservation(id);
        return at;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void ConcurrentJoins_AreAtLeastClearanceApart(int joiners)
    {
        for (int round = 0; round < 200; round++)
        {
            var world = new EcsWorld();
            world.Gameplay.SpawnScatter = Scatter;
            using var barrier = new Barrier(joiners);
            var placed = new Vec2[joiners];
            var threads = new Thread[joiners];
            for (int t = 0; t < joiners; t++)
            {
                int index = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    placed[index] = Join(world, "p" + index);
                });
                threads[t].Start();
            }

            foreach (Thread thread in threads) thread.Join();

            for (int a = 0; a < joiners; a++)
            {
                for (int b = a + 1; b < joiners; b++)
                {
                    Assert.True(
                        Dist(placed[a], placed[b]) >= GameplayState.SpawnClearance,
                        $"round {round}: p{a} and p{b} placed {Dist(placed[a], placed[b]):F3} apart");
                }
            }
        }
    }

    [Fact]
    public void ReservedButNotYetSpawned_CountsAsOccupied()
    {
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;

        Vec2 first = GameServerHost.ScatterJoinSpawn(world, "a", Vec2.Zero);
        Vec2 second = GameServerHost.ScatterJoinSpawn(world, "b", Vec2.Zero);

        Assert.True(Dist(first, second) >= GameplayState.SpawnClearance);
    }

    [Fact]
    public void ReleasedReservation_StopsBlockingAndIsIdempotent()
    {
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;

        GameServerHost.ScatterJoinSpawn(world, "a", Vec2.Zero);
        world.Gameplay.ReleaseSpawnReservation("a");
        world.Gameplay.ReleaseSpawnReservation("a");
        world.Gameplay.ReleaseSpawnReservation("never-reserved");

        // 16 slots, one released reservation: the next join still gets a placement.
        Vec2 next = GameServerHost.ScatterJoinSpawn(world, "b", Vec2.Zero);
        Assert.True(Dist(next, Vec2.Zero) <= Scatter);
    }
}
