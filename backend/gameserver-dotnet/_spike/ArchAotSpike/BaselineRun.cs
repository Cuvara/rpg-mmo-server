// The same workload against a Dictionary<int, State> — the shape GameWorld uses
// today (Dictionary<string, EntityState> + a struct rewritten on every mutation).
// Integer keys are used rather than strings so the comparison isolates the
// container, not the ADR-10 id-handle prerequisite, which lands either way.

namespace ArchAotSpike;

public static class BaselineRun
{
    private struct State
    {
        public int Id;
        public float Px, Py, Vx, Vy, Hp, MaxHp;
        public EntityKind Kind;
        public bool Stunned;
        public int StunTicks;
    }

    public static RunResult Run(string label)
    {
        int created = 0, destroyed = 0, stuns = 0, unstuns = 0;

        var entities = new Dictionary<int, State>(Sim.InitialEntities * 2);
        for (int i = 0; i < Sim.InitialEntities; i++) { entities[i] = Spawn(i); created++; }

        int nextId = Sim.InitialEntities;
        var keys = new List<int>(512);
        var toDestroy = new List<int>(64);
        var toStun = new List<int>(64);
        var toUnstun = new List<int>(64);

        // Pass order mirrors ArchRun exactly: move, regen, decide, decay, apply.
        for (int tick = 0; tick < Sim.Ticks; tick++)
        {
            keys.Clear();
            foreach (var k in entities.Keys) keys.Add(k);

            toDestroy.Clear();
            toStun.Clear();
            toUnstun.Clear();

            // 1 + 2. movement and regen
            foreach (var k in keys)
            {
                var s = entities[k];
                Sim.Integrate(ref s.Px, ref s.Py, ref s.Vx, ref s.Vy);
                Sim.Regen(ref s.Hp, s.MaxHp);
                entities[k] = s;
            }

            // 3. decide despawn / stun
            foreach (var k in keys)
            {
                var s = entities[k];
                if (Sim.ShouldDespawn(s.Id, tick)) { toDestroy.Add(k); continue; }
                if (Sim.ShouldStun(s.Id, tick)) toStun.Add(k);
            }

            // 4. stun decay over entities already stunned before this tick's adds
            foreach (var k in keys)
            {
                var s = entities[k];
                if (!s.Stunned) continue;
                s.StunTicks--;
                if (s.StunTicks <= 0) toUnstun.Add(k);
                entities[k] = s;
            }

            // 5. apply structural changes
            foreach (var k in toDestroy) { entities.Remove(k); destroyed++; }
            foreach (var k in toStun)
            {
                if (!entities.TryGetValue(k, out var s) || s.Stunned) continue;
                s.Stunned = true;
                s.StunTicks = 5;
                entities[k] = s;
                stuns++;
            }
            foreach (var k in toUnstun)
            {
                if (!entities.TryGetValue(k, out var s) || !s.Stunned) continue;
                s.Stunned = false;
                entities[k] = s;
                unstuns++;
            }

            for (int i = 0; i < Sim.SpawnCount(tick); i++)
            {
                int id = nextId++;
                entities[id] = Spawn(id);
                created++;
            }
        }

        ulong sum = 0, xor = 0;
        foreach (var s in entities.Values)
            Checksum.Accumulate(ref sum, ref xor, s.Id, s.Px, s.Py, s.Hp, (byte)s.Kind);

        return new RunResult(label, entities.Count, sum, xor, created, destroyed, stuns, unstuns);
    }

    private static State Spawn(int id)
    {
        var rng = Sim.SeedFor(id);
        float px = rng.NextUnit() * 256.0f;
        float py = rng.NextUnit() * 256.0f;
        float vx = rng.NextUnit() * 8.0f;
        float vy = rng.NextUnit() * 8.0f;
        float maxHp = 100.0f + (id % 8) * 16.0f;
        return new State
        {
            Id = id, Px = px, Py = py, Vx = vx, Vy = vy,
            Hp = maxHp * 0.5f, MaxHp = maxHp, Kind = Sim.KindFor(id),
        };
    }
}
