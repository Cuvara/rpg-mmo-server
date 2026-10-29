using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace GameServer.Tests.World;

/// <summary>
/// Spawn placement keeps clear of live players (S04 20261028T232609Z-S04-r2iw: a respawn
/// 0.462 units from a player walking past the slot, against the 0.5 spawn_overlap policy).
/// The spiral alone is blind to who is standing where; these pin that the occupant-aware
/// overload never places a player closer than <see cref="GameplayState.SpawnClearance"/>
/// while some slot allows it, and stays deterministic.
/// </summary>
public class SpawnMinDistanceTests
{
    private const float Scatter = 3f;

    private static GameplayState State()
    {
        var world = new EcsWorld();
        world.Gameplay.SpawnScatter = Scatter;
        return world.Gameplay;
    }

    private static Vec2 Slot(int k)
    {
        float angle = k * 2.39996323f, r = Scatter * MathF.Sqrt((k + 0.5f) / 16f);
        return new Vec2(MathF.Cos(angle) * r, MathF.Sin(angle) * r);
    }

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    [Fact]
    public void TheMeasuredCase_APlayerBeside_Slot7_IsAvoided()
    {
        GameplayState g = State();
        for (int i = 0; i < 7; i++) g.ScatterSpawn("warmup", Vec2.Zero);

        Vec2 s7 = Slot(7);
        var other = new Vec2(s7.X + 0.462f, s7.Y);

        // Without the occupant, slot 7 is what the spiral gives: the 0.462 situation.
        GameplayState blind = State();
        for (int i = 0; i < 7; i++) blind.ScatterSpawn("warmup", Vec2.Zero);
        Assert.True(Dist(blind.ScatterSpawn("p", Vec2.Zero), other) < 0.5f);

        Vec2 placed = g.ScatterSpawn("p", Vec2.Zero, new[] { other });
        Assert.True(Dist(placed, other) >= GameplayState.SpawnClearance, $"placed {Dist(placed, other):F3} from the occupant");
    }

    [Fact]
    public void NobodyInTheWay_IsExactlyTheSpiralAsBefore()
    {
        GameplayState a = State(), b = State();
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(a.ScatterSpawn("x", Vec2.Zero), b.ScatterSpawn("y", Vec2.Zero, ReadOnlySpan<Vec2>.Empty));
        }
    }

    [Fact]
    public void ADensePopulation_GetsTheSlotFarthestFromItsNearestPlayer()
    {
        GameplayState g = State();

        // A player on every slot and the centre: no slot clears 1.5, so the farthest wins.
        var crowd = new List<Vec2> { Vec2.Zero };
        for (int k = 0; k < 16; k++) crowd.Add(Slot(k));
        Vec2 placed = g.ScatterSpawn("p", Vec2.Zero, crowd.ToArray());

        float nearest = crowd.Min(c => Dist(c, placed));
        foreach (int k in Enumerable.Range(0, 16))
        {
            Assert.True(nearest >= crowd.Min(c => Dist(c, Slot(k))) - 1e-3f, $"slot {k} was farther from everybody");
        }
    }

    /// <summary>
    /// Property over many seeds: random live players (anywhere in and around the disc) and a
    /// random starting cursor. Whenever any slot has clearance the result has it; the result
    /// is also deterministic for a repeat of the same seed.
    /// </summary>
    [Fact]
    public void Property_PlacementHonoursClearanceWheneverASlotAllowsIt()
    {
        int constrained = 0;
        for (int seed = 0; seed < 5000; seed++)
        {
            var rng = new Random(seed);
            int warm = rng.Next(0, 16), players = rng.Next(0, 8);
            var occupied = new Vec2[players];
            for (int i = 0; i < players; i++)
            {
                occupied[i] = new Vec2((float)((rng.NextDouble() * 8) - 4), (float)((rng.NextDouble() * 8) - 4));
            }

            Vec2 Place()
            {
                GameplayState g = State();
                for (int i = 0; i < warm; i++) g.ScatterSpawn("w", Vec2.Zero);
                return g.ScatterSpawn("p", Vec2.Zero, occupied);
            }

            Vec2 placed = Place();
            Assert.Equal(placed, Place());

            float Nearest(Vec2 p) => occupied.Length == 0 ? float.MaxValue : occupied.Min(o => Dist(o, p));
            bool someSlotClears = Enumerable.Range(0, 16).Any(k => Nearest(Slot(k)) >= GameplayState.SpawnClearance + 1e-3f);
            if (someSlotClears)
            {
                Assert.True(Nearest(placed) >= GameplayState.SpawnClearance - 1e-3f,
                    $"seed {seed}: placed {Nearest(placed):F3} from a live player although a slot had clearance");
            }
            else
            {
                constrained++;
                Assert.True(Nearest(placed) >= Enumerable.Range(0, 16).Max(k => Nearest(Slot(k))) - 1e-3f, $"seed {seed}: not the farthest slot");
            }
        }

        Assert.True(constrained < 5000, "the property never exercised the clearance path");
    }
}
