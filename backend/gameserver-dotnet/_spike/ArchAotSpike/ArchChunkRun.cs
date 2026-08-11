// Same workload again, but iterating chunks by hand instead of through the
// delegate-based Query overloads.
//
// Why this variant exists: the ergonomic API — world.Query(in desc, (ref A a,
// ref B b) => ...) — allocates a closure per call whenever the lambda captures
// anything, which a tick loop's lambdas invariably do (the tick number, an
// accumulator, a scratch list). Manual chunk iteration is the allocation-free
// shape, and it is what a server tick loop would actually have to be written in.
// It is also a different AOT surface: Span<T> over chunk arrays rather than
// generic delegate instantiation.

using Arch.Core;

namespace ArchAotSpike;

public static class ArchChunkRun
{
    public static RunResult Run(string label)
    {
        int created = 0, destroyed = 0, stuns = 0, unstuns = 0;

        var world = World.Create();
        for (int i = 0; i < Sim.InitialEntities; i++) { Spawn(world, i); created++; }
        int nextId = Sim.InitialEntities;

        var moveQuery = new QueryDescription().WithAll<Position, Velocity>();
        var healthQuery = new QueryDescription().WithAll<Health>();
        var allQuery = new QueryDescription().WithAll<Identity, Position, Health, Kind>();
        var stunQuery = new QueryDescription().WithAll<Identity, Stunned>();

        var toDestroy = new List<Entity>(64);
        var toStun = new List<Entity>(64);
        var toUnstun = new List<Entity>(64);

        for (int tick = 0; tick < Sim.Ticks; tick++)
        {
            // 1. movement
            foreach (ref var chunk in world.Query(in moveQuery).GetChunkIterator())
            {
                var pos = chunk.GetSpan<Position>();
                var vel = chunk.GetSpan<Velocity>();
                for (int i = 0; i < chunk.Count; i++)
                    Sim.Integrate(ref pos[i].X, ref pos[i].Y, ref vel[i].X, ref vel[i].Y);
            }

            // 2. regen
            foreach (ref var chunk in world.Query(in healthQuery).GetChunkIterator())
            {
                var hp = chunk.GetSpan<Health>();
                for (int i = 0; i < chunk.Count; i++)
                    Sim.Regen(ref hp[i].Hp, hp[i].MaxHp);
            }

            // 3. decide
            toDestroy.Clear(); toStun.Clear(); toUnstun.Clear();

            foreach (ref var chunk in world.Query(in allQuery).GetChunkIterator())
            {
                var ids = chunk.GetSpan<Identity>();
                var entities = chunk.Entities;
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (Sim.ShouldDespawn(ids[i].Id, tick)) { toDestroy.Add(entities[i]); continue; }
                    if (Sim.ShouldStun(ids[i].Id, tick)) toStun.Add(entities[i]);
                }
            }

            foreach (ref var chunk in world.Query(in stunQuery).GetChunkIterator())
            {
                var stunned = chunk.GetSpan<Stunned>();
                var entities = chunk.Entities;
                for (int i = 0; i < chunk.Count; i++)
                {
                    stunned[i].RemainingTicks--;
                    if (stunned[i].RemainingTicks <= 0) toUnstun.Add(entities[i]);
                }
            }

            // 4. apply structural changes
            foreach (var e in toDestroy) { world.Destroy(e); destroyed++; }
            foreach (var e in toStun)
            {
                if (world.IsAlive(e) && !world.Has<Stunned>(e)) { world.Add(e, new Stunned(5)); stuns++; }
            }
            foreach (var e in toUnstun)
            {
                if (world.IsAlive(e) && world.Has<Stunned>(e)) { world.Remove<Stunned>(e); unstuns++; }
            }
            for (int s = 0; s < Sim.SpawnCount(tick); s++) { Spawn(world, nextId++); created++; }
        }

        // 5. read back
        ulong sum = 0, xor = 0;
        int count = 0;
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
