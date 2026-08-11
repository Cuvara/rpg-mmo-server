// Third configuration: Arch.System + Arch.System.SourceGenerator.
//
// The generator turns [Query]-annotated private methods into generated query
// loops, so this exercises whether the generated code changes the NativeAOT
// picture (it is the one thing the Arch wiki suggests might, and it is what a
// "systems"-shaped tick loop would be written against).

#if USE_ARCH_SYSTEM
using Arch.Core;
using Arch.System;

namespace ArchAotSpike;

public partial class MovementSystem : BaseSystem<World, float>
{
    public MovementSystem(World world) : base(world) { }

    [Query]
    public void Move(ref Position p, ref Velocity v)
        => Sim.Integrate(ref p.X, ref p.Y, ref v.X, ref v.Y);

    [Query]
    public void RegenHealth(ref Health h)
        => Sim.Regen(ref h.Hp, h.MaxHp);
}

public partial class DecisionSystem : BaseSystem<World, int>
{
    public int Tick;
    public readonly List<Entity> ToDestroy = new(64);
    public readonly List<Entity> ToStun = new(64);
    public readonly List<Entity> ToUnstun = new(64);

    public DecisionSystem(World world) : base(world) { }

    // Arch.System 1.1.0's [All] is the non-generic typeof(...) form; the generic
    // [All<T0,T1>] shown in newer READMEs does not exist in this package version.
    // Naming the components as parameters gives the same archetype filter.
    [Query]
    public void Decide(in Entity e, ref Identity id, ref Position p, ref Health h, ref Kind k)
    {
        if (Sim.ShouldDespawn(id.Id, Tick)) { ToDestroy.Add(e); return; }
        if (Sim.ShouldStun(id.Id, Tick)) ToStun.Add(e);
    }

    [Query]
    public void DecayStun(in Entity e, ref Identity id, ref Stunned s)
    {
        s.RemainingTicks--;
        if (s.RemainingTicks <= 0) ToUnstun.Add(e);
    }
}

public static class ArchSystemRun
{
    public static RunResult Run(string label)
    {
        int created = 0, destroyed = 0, stuns = 0, unstuns = 0;

        var world = World.Create();
        for (int i = 0; i < Sim.InitialEntities; i++) { Spawn(world, i); created++; }
        int nextId = Sim.InitialEntities;

        var movement = new MovementSystem(world);
        var decision = new DecisionSystem(world);

        for (int tick = 0; tick < Sim.Ticks; tick++)
        {
            movement.Update(Sim.Dt);

            decision.Tick = tick;
            decision.ToDestroy.Clear();
            decision.ToStun.Clear();
            decision.ToUnstun.Clear();
            decision.Update(tick);

            foreach (var e in decision.ToDestroy) { world.Destroy(e); destroyed++; }
            foreach (var e in decision.ToStun)
            {
                if (world.IsAlive(e) && !world.Has<Stunned>(e)) { world.Add(e, new Stunned(5)); stuns++; }
            }
            foreach (var e in decision.ToUnstun)
            {
                if (world.IsAlive(e) && world.Has<Stunned>(e)) { world.Remove<Stunned>(e); unstuns++; }
            }
            for (int s = 0; s < Sim.SpawnCount(tick); s++) { Spawn(world, nextId++); created++; }
        }

        ulong sum = 0, xor = 0;
        int count = 0;
        var allQuery = new QueryDescription().WithAll<Identity, Position, Health, Kind>();
        foreach (ref var chunk in world.Query(in allQuery).GetChunkIterator())
        {
            var ids = chunk.GetSpan<Identity>();
            var pos = chunk.GetSpan<Position>();
            var hp = chunk.GetSpan<Health>();
            var kind = chunk.GetSpan<Kind>();
            for (int i = 0; i < chunk.Count; i++)
            {
                Checksum.Accumulate(ref sum, ref xor, ids[i].Id, pos[i].X, pos[i].Y, hp[i].Hp, (byte)kind[i].Value);
                count++;
            }
        }

        World.Destroy(world);
        return new RunResult(label, count, sum, xor, created, destroyed, stuns, unstuns);
    }

    private static void Spawn(World world, int id)
    {
        var rng = Sim.SeedFor(id);
        float px = rng.NextUnit() * 256.0f;
        float py = rng.NextUnit() * 256.0f;
        float vx = rng.NextUnit() * 8.0f;
        float vy = rng.NextUnit() * 8.0f;
        float maxHp = 100.0f + (id % 8) * 16.0f;
        world.Create(
            new Identity(id), new Position(px, py), new Velocity(vx, vy),
            new Health(maxHp * 0.5f, maxHp), new Kind(Sim.KindFor(id)));
    }
}
#endif
