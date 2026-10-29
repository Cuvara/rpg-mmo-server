using System.Reflection;
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
/// placement from several threads behind a barrier. The follow-up tests cover review findings
/// on #505: bot insertion between the position copy and the reservation, a reservation leaked
/// by an exception, and the unbounded retry loop.
/// </summary>
public class SpawnJoinRaceTests
{
    private const float Scatter = 3f;

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>The production join sequence: reserve, then add the entity, then release.</summary>
    private static Vec2 Join(EcsWorld world, string id)
    {
        Vec2 at = default;
        GameServerHost.PlaceJoiningPlayer(world, id, fresh: true, Vec2.Zero, placed =>
        {
            at = placed;
            // The window the race lived in: placed, not yet visible in the world.
            Thread.Yield();
            world.AddEntity(new EntityState { Id = id, Type = "player", Position = placed, Hp = 100, MaxHp = 100 });
        });
        return at;
    }

    /// <summary>Reserve a placement and leave it in flight, as a join between reserve and AddEntity.</summary>
    private static Vec2 Reserve(EcsWorld world, string id)
    {
        Vec2[] buffer = new Vec2[8];
        return world.ReserveJoinSpawn(id, Vec2.Zero, ref buffer);
    }

    private static object SpawnLockOf(EcsWorld world) =>
        typeof(GameplayState).GetField("_spawnLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.Gameplay)!;

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

        Vec2 first = Reserve(world, "a");
        Vec2 second = Reserve(world, "b");

        Assert.True(Dist(first, second) >= GameplayState.SpawnClearance);
    }

    [Fact]
    public void ReleasedReservation_StopsBlockingAndIsIdempotent()
    {
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;

        Reserve(world, "a");
        world.Gameplay.ReleaseSpawnReservation("a");
        world.Gameplay.ReleaseSpawnReservation("a");
        world.Gameplay.ReleaseSpawnReservation("never-reserved");

        // 16 slots, one released reservation: the next join still gets a placement.
        Vec2 next = Reserve(world, "b");
        Assert.True(Dist(next, Vec2.Zero) <= Scatter);
    }

    [Fact]
    public void BotInsertion_CannotLandBetweenPositionCopyAndReservation()
    {
        // Review finding 1: a bot inserted after a join copied the positions but before it
        // reserved left the join with a stale copy. Hold the spawn lock so a join parks inside
        // its reserve step, then try to insert a bot: the insertion needs the world write lock,
        // which the parked join's read section keeps out, so it must not complete.
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;
        object spawnLock = SpawnLockOf(world);
        var botAt = new Vec2(0.5f, 0f);

        Vec2 joined = default;
        Thread joiner = new(() => joined = Reserve(world, "p0"));
        Thread bot = new(() => world.Spawn(
            new EntityState { Id = "bot-00000", Type = "player", Position = botAt, Hp = 100, MaxHp = 100 },
            EntityTags.Bot));

        Monitor.Enter(spawnLock);
        try
        {
            joiner.Start();
            Assert.True(SpinWait.SpinUntil(
                () => (joiner.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            bot.Start();
            Assert.False(
                bot.Join(TimeSpan.FromMilliseconds(500)),
                "bot was inserted while a join sat between its position copy and its reservation");
        }
        finally { Monitor.Exit(spawnLock); }

        Assert.True(joiner.Join(TimeSpan.FromSeconds(10)));
        Assert.True(bot.Join(TimeSpan.FromSeconds(10)));

        // Serial order is join, then bot; the next join sees both.
        Vec2 next = Reserve(world, "p1");
        Assert.True(Dist(next, joined) >= GameplayState.SpawnClearance);
        Assert.True(Dist(next, botAt) >= GameplayState.SpawnClearance);
    }

    [Fact]
    public void JoinsRacingBotInsertion_NeverMissABotInsertedBeforeThem()
    {
        const float wide = 8f;
        const int bots = 3;
        const int joiners = 3;
        for (int round = 0; round < 100; round++)
        {
            var world = new EcsWorld();
            world.Gameplay.SpawnScatter = wide;
            using var barrier = new Barrier(joiners + 1);
            long botsDone = 0;
            var botPositions = new Vec2[bots];
            for (int b = 0; b < bots; b++) botPositions[b] = new Vec2(0.4f + (b * 1.1f), 0.2f * b);

            var seen = new long[joiners];
            var placed = new Vec2[joiners];
            var threads = new List<Thread>
            {
                new(() =>
                {
                    barrier.SignalAndWait();
                    for (int b = 0; b < bots; b++)
                    {
                        world.Spawn(
                            new EntityState { Id = "bot" + b, Type = "player", Position = botPositions[b], Hp = 100, MaxHp = 100 },
                            EntityTags.Bot);
                        Interlocked.Increment(ref botsDone);
                    }
                }),
            };
            for (int t = 0; t < joiners; t++)
            {
                int index = t;
                threads.Add(new Thread(() =>
                {
                    barrier.SignalAndWait();
                    // Bots finished before the join starts must all be visible to it.
                    seen[index] = Interlocked.Read(ref botsDone);
                    placed[index] = Join(world, "p" + index);
                }));
            }

            foreach (Thread thread in threads) thread.Start();
            foreach (Thread thread in threads) thread.Join();

            for (int j = 0; j < joiners; j++)
            {
                for (int b = 0; b < seen[j]; b++)
                {
                    Assert.True(
                        Dist(placed[j], botPositions[b]) >= GameplayState.SpawnClearance,
                        $"round {round}: p{j} placed {Dist(placed[j], botPositions[b]):F3} from bot {b} that existed before it started");
                }
            }
        }
    }

    [Fact]
    public void ExceptionAfterReservation_ReleasesThePendingPlacement()
    {
        // Review finding 2: anything that throws between the reservation and AddEntity (a
        // throwing logger, a failed bind) must not leave the placement pending.
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;

        Assert.Throws<InvalidOperationException>(() =>
            GameServerHost.PlaceJoiningPlayer(world, "a", fresh: true, Vec2.Zero, _ =>
            {
                Assert.Equal(1, world.Gameplay.PendingSpawnCount);
                throw new InvalidOperationException("logger threw");
            }));

        Assert.Equal(0, world.Gameplay.PendingSpawnCount);
    }

    [Fact]
    public void RestoredPosition_ReservesNothingAndKeepsThePoint()
    {
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;
        Vec2 seen = default;

        GameServerHost.PlaceJoiningPlayer(world, "a", fresh: false, new Vec2(4f, 5f), p =>
        {
            seen = p;
            Assert.Equal(0, world.Gameplay.PendingSpawnCount);
        });

        Assert.Equal(new Vec2(4f, 5f), seen);
        Assert.Equal(0, world.Gameplay.PendingSpawnCount);
    }

    [Fact]
    public void Join_CompletesWhileOtherPlacementsKeepHammeringTheCursor()
    {
        // Review finding 3: the old join retried until no placement landed in between, so
        // sustained respawns could starve it. The read-lock section has no retry at all; a
        // join finishes in one pass however busy the cursor is.
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;
        using var stop = new CancellationTokenSource();
        Thread hammer = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                world.Gameplay.ScatterSpawn(null, Vec2.Zero, ReadOnlySpan<Vec2>.Empty);
            }
        });
        hammer.Start();

        Task joins = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) Join(world, "p" + i);
        });
        bool finished = joins.Wait(TimeSpan.FromSeconds(30));
        stop.Cancel();
        hammer.Join();

        Assert.True(finished, "joins did not complete while the cursor was contended");
        Assert.Equal(0, world.Gameplay.PendingSpawnCount);
    }

    /// <summary>Records what a join rents and returns, to pin the pool contract.</summary>
    private sealed class RecordingPool : System.Buffers.ArrayPool<Vec2>
    {
        public readonly List<Vec2[]> Rented = new();
        public readonly List<Vec2[]> Returned = new();
        public override Vec2[] Rent(int minimumLength)
        {
            var a = new Vec2[minimumLength];
            Rented.Add(a);
            return a;
        }
        public override void Return(Vec2[] array, bool clearArray = false) => Returned.Add(array);
    }

    [Fact]
    public void JoinWithMorePlayersThanTheRental_ReturnsTheRentedArrayAndPlacesClear()
    {
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;
        // More live players than the 64-slot rental, spread wide so the disc has room.
        for (int i = 0; i < 70; i++)
        {
            world.AddEntity(new EntityState
            {
                Id = "e" + i, Type = "player", Position = new Vec2(100f + (i * 10f), 100f), Hp = 100, MaxHp = 100,
            });
        }

        var pool = new RecordingPool();
        Vec2 at = default;
        GameServerHost.PlaceJoiningPlayer(world, "joiner", fresh: true, Vec2.Zero, placed =>
        {
            at = placed;
            world.AddEntity(new EntityState { Id = "joiner", Type = "player", Position = placed, Hp = 100, MaxHp = 100 });
        }, pool);

        Assert.Single(pool.Rented);
        Assert.Single(pool.Returned);
        Assert.Same(pool.Rented[0], pool.Returned[0]);
        Assert.Equal(64, pool.Rented[0].Length);
        Assert.Equal(0, world.Gameplay.PendingSpawnCount);
        Assert.True(Dist(at, Vec2.Zero) <= Scatter + GameplayState.SpawnClearance);

        // A second join with the crowd close to the origin must still keep clear of all of it.
        var crowd = new EcsWorld();
        crowd.Gameplay.SpawnScatter = Scatter;
        for (int i = 0; i < 70; i++)
        {
            crowd.AddEntity(new EntityState
            {
                Id = "c" + i, Type = "player", Position = new Vec2(-20f + (i % 10) * 4f, -20f + (i / 10) * 4f), Hp = 100, MaxHp = 100,
            });
        }
        var pool2 = new RecordingPool();
        Vec2 near = default;
        GameServerHost.PlaceJoiningPlayer(crowd, "j2", fresh: true, Vec2.Zero, placed => near = placed, pool2);
        Assert.Same(pool2.Rented[0], pool2.Returned[0]);
        for (int i = 0; i < 70; i++)
        {
            var o = new Vec2(-20f + (i % 10) * 4f, -20f + (i / 10) * 4f);
            Assert.True(Dist(near, o) >= GameplayState.SpawnClearance);
        }
        crowd.Gameplay.ReleaseSpawnReservation("j2");
    }
}
