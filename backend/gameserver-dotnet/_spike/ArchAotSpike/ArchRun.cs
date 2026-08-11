// The Arch implementation of the spike workload.

using Arch.Buffer;
using Arch.Core;

namespace ArchAotSpike;

public static class ArchRun
{
    public static RunResult Run(string label, bool useCommandBuffer)
    {
        int created = 0, destroyed = 0, stuns = 0, unstuns = 0;

        var world = World.Create();

        // --- spawn the initial population -----------------------------------
        for (int i = 0; i < Sim.InitialEntities; i++)
        {
            SpawnInto(world, i);
            created++;
        }

        int nextId = Sim.InitialEntities;

        var moveQuery = new QueryDescription().WithAll<Position, Velocity>();
        var healthQuery = new QueryDescription().WithAll<Health>();
        var allQuery = new QueryDescription().WithAll<Identity, Position, Health, Kind>();
        var stunQuery = new QueryDescription().WithAll<Identity, Stunned>();

        // Scratch lists reused across ticks so the loop itself does not allocate.
        var toDestroy = new List<Entity>(64);
        var toStun = new List<Entity>(64);
        var toUnstun = new List<Entity>(64);

        var buffer = useCommandBuffer ? new CommandBuffer() : null;

        for (int tick = 0; tick < Sim.Ticks; tick++)
        {
            // 1. movement — mutate components by ref through a query
            world.Query(in moveQuery, (ref Position p, ref Velocity v) =>
            {
                Sim.Integrate(ref p.X, ref p.Y, ref v.X, ref v.Y);
            });

            // 2. health regen
            world.Query(in healthQuery, (ref Health h) =>
            {
                Sim.Regen(ref h.Hp, h.MaxHp);
            });

            // 3. decide structural changes (collect during iteration, apply after)
            toDestroy.Clear();
            toStun.Clear();
            toUnstun.Clear();

            int t = tick;
            world.Query(in allQuery, (Entity e, ref Identity id) =>
            {
                if (Sim.ShouldDespawn(id.Id, t)) { toDestroy.Add(e); return; }
                if (Sim.ShouldStun(id.Id, t)) toStun.Add(e);
            });

            world.Query(in stunQuery, (Entity e, ref Identity id, ref Stunned s) =>
            {
                s.RemainingTicks--;
                if (s.RemainingTicks <= 0) toUnstun.Add(e);
            });

            // 4. apply structural changes — archetype moves
            if (buffer is not null)
            {
                // The alive/has guards mirror the direct path exactly so the two
                // configurations' counters are comparable. An entity can be in
                // both toDestroy and toUnstun in the same tick.
                foreach (var e in toDestroy) { buffer.Destroy(e); destroyed++; }
                foreach (var e in toStun)
                {
                    if (!toDestroy.Contains(e) && !world.Has<Stunned>(e))
                    {
                        buffer.Add(e, new Stunned(5));
                        stuns++;
                    }
                }
                foreach (var e in toUnstun)
                {
                    if (!toDestroy.Contains(e))
                    {
                        buffer.Remove<Stunned>(e);
                        unstuns++;
                    }
                }
                for (int s = 0; s < Sim.SpawnCount(tick); s++)
                {
                    // CommandBuffer.Create takes a raw signature rather than typed
                    // components; creating directly keeps the two paths identical.
                    SpawnInto(world, nextId++);
                    created++;
                }
                try
                {
                    buffer.Playback(world);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [cmdbuffer] Playback threw on tick {tick}: " +
                        $"{ex.GetType().Name} (destroy={toDestroy.Count} stun={toStun.Count} unstun={toUnstun.Count})");
                    Console.WriteLine($"    entityCount={world.CountEntities(in allQuery)}");
                    foreach (var e in toStun)
                        Console.WriteLine($"    stun   e={e.Id}v{e.Version} alive={world.IsAlive(e)} hasStunned={world.Has<Stunned>(e)}");
                    foreach (var e in toUnstun)
                        Console.WriteLine($"    unstun e={e.Id}v{e.Version} alive={world.IsAlive(e)} hasStunned={world.Has<Stunned>(e)}");
                    throw;
                }
            }
            else
            {
                foreach (var e in toDestroy) { world.Destroy(e); destroyed++; }
                foreach (var e in toStun)
                {
                    if (world.IsAlive(e) && !world.Has<Stunned>(e))
                    {
                        world.Add(e, new Stunned(5));   // archetype move (add)
                        stuns++;
                    }
                }
                foreach (var e in toUnstun)
                {
                    if (world.IsAlive(e) && world.Has<Stunned>(e))
                    {
                        world.Remove<Stunned>(e);        // archetype move (remove)
                        unstuns++;
                    }
                }
                for (int s = 0; s < Sim.SpawnCount(tick); s++)
                {
                    SpawnInto(world, nextId++);
                    created++;
                }
            }
        }

        // --- read the final state back --------------------------------------
        ulong sum = 0, xor = 0;
        int count = 0;
        world.Query(in allQuery, (ref Identity id, ref Position p, ref Health h, ref Kind k) =>
        {
            Checksum.Accumulate(ref sum, ref xor, id.Id, p.X, p.Y, h.Hp, (byte)k.Value);
            count++;
        });

        buffer?.Dispose();
        World.Destroy(world);

        return new RunResult(label, count, sum, xor, created, destroyed, stuns, unstuns);
    }

    private static void SpawnInto(World world, int id)
    {
        var rng = Sim.SeedFor(id);
        float px = rng.NextUnit() * 256.0f;
        float py = rng.NextUnit() * 256.0f;
        float vx = rng.NextUnit() * 8.0f;
        float vy = rng.NextUnit() * 8.0f;
        float maxHp = 100.0f + (id % 8) * 16.0f;

        world.Create(
            new Identity(id),
            new Position(px, py),
            new Velocity(vx, vy),
            new Health(maxHp * 0.5f, maxHp),
            new Kind(Sim.KindFor(id)));
    }
}
