using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// <see cref="EnemyAiPhase.Separate"/> — pushes living enemies that stand closer than
/// <see cref="EnemyAiSettings.SeparationRadius"/> apart, so a crowd chasing one player
/// surrounds it instead of stacking on a single point (rpg-mmo-server#469).
/// </summary>
/// <remarks>
/// <para><b>Bounded.</b> Neighbours are found through a spatial hash with a cell the size of
/// the radius, and each enemy examines at most <see cref="MaxNeighbours"/> candidates from its
/// 3x3 cells, so the cost is O(n x K) whatever the density. A crowd of n enemies on one exact
/// point would otherwise be n^2 pairs, and that pile is the state this system exists to undo.</para>
///
/// <para><b>Deterministic.</b> Every push is computed from one snapshot of the positions
/// (a Jacobi step), so the result does not depend on the order enemies are visited in, and two
/// enemies on exactly the same point separate along a direction derived from their indices in
/// the walk, not from a random source. The walk order is the world's chunk order, which is
/// stable for a given spawn history; <c>GAMESERVER_ENEMY_SEED</c> makes that history repeatable.</para>
///
/// <para><b>Never faster than walking.</b> The total push on one enemy in one step is capped at
/// its walking distance for the step (speed x dt), so separation can rearrange a crowd but
/// cannot throw an enemy across the screen; a client never sees an enemy move faster than
/// <c>GAMESERVER_ENEMY_SPEED</c> because of it. Each pair contributes half the overlap, so
/// two enemies meet at the radius rather than overshooting past it.</para>
///
/// <para><b>Off</b> with <c>GAMESERVER_ENEMY_SEPARATION=0</c>, which restores the pre-change
/// behaviour exactly: the system then returns before touching any position.</para>
/// </remarks>
internal sealed class EnemySeparationSystem : IEcsSystem
{
    /// <summary>
    /// Candidates examined per enemy. A real crowd around one target has at most ~6-8 enemies
    /// within one radius of any member (circle packing); 12 leaves room for a dense pile to
    /// find enough of them to spread, and bounds the worst case.
    /// </summary>
    public const int MaxNeighbours = 12;

    private readonly float _dt;
    private readonly EnemyAiSettings _settings;

    [SimulationScratch] private float[] _x = Array.Empty<float>();
    [SimulationScratch] private float[] _y = Array.Empty<float>();
    [SimulationScratch] private float[] _dx = Array.Empty<float>();
    [SimulationScratch] private float[] _dy = Array.Empty<float>();
    [SimulationScratch] private int[] _next = Array.Empty<int>();
    [SimulationScratch] private int[] _heads = Array.Empty<int>();
    [SimulationScratch] private int _count;
    [SimulationScratch] private int _gridCount;
    [SimulationScratch] private int _gridMask = -1;
    [SimulationScratch] private float _gridInv;

    public EnemySeparationSystem(float dt, EnemyAiSettings settings)
    {
        _dt = dt;
        _settings = settings;
    }

    public string Name => "enemy.separate";

    public int Order => (int)EnemyAiPhase.Separate;

    /// <summary>World, with the movement it corrects.</summary>
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        reads: new[] { typeof(Health) },
        writes: new[] { typeof(Position) });

    /// <summary>Enemies the last run pushed; for tests and the bench. Reset at the start of every run.</summary>
    public int LastPushed => _pushed;

    /// <summary>Apply-pass cursor into the snapshot and its push count; reset every run.</summary>
    [SimulationScratch] private int _cursor;
    [SimulationScratch] private int _pushed;

    public void Run(WorldWriter writer, ulong currentTick)
    {
        _pushed = 0;
        _cursor = 0;
        float radius = _settings.SeparationRadius;
        if (!(radius > 0f)) return;

        // Pass 1: snapshot every living enemy's position, in walk order.
        _count = 0;
        var collect = new Collect(this);
        writer.VisitChunks<EnemyAi, Collect>(ref collect);
        int n = _count;
        if (n < 2) return;

        ComputePushes(n, radius, _settings.Speed * _dt);

        // Pass 2: the same walk, applying each enemy's own push.
        var apply = new Apply(this);
        writer.VisitChunks<EnemyAi, Apply>(ref apply);
    }

    /// <summary>
    /// The push for each of the first <paramref name="n"/> snapshot positions. Exposed to the
    /// tests and the bench so the geometry can be checked without a world.
    /// </summary>
    internal void ComputePushes(int n, float radius, float maxStep)
    {
        EnsureCapacity(n);
        float r2 = radius * radius;
        float inv = 1f / radius;

        // Hash table sized to a power of two above 2n: few collisions, no per-tick allocation
        // once the population has been seen.
        int tableSize = 1;
        while (tableSize < n * 2) tableSize <<= 1;
        if (_heads.Length < tableSize) _heads = new int[tableSize];
        int mask = tableSize - 1;
        Array.Fill(_heads, -1, 0, tableSize);
        _gridCount = n;
        _gridMask = mask;
        _gridInv = inv;

        for (int i = 0; i < n; i++)
        {
            int h = Hash(Cell(_x[i], inv), Cell(_y[i], inv)) & mask;
            _next[i] = _heads[h];
            _heads[h] = i;
            _dx[i] = 0f;
            _dy[i] = 0f;
        }

        for (int i = 0; i < n; i++)
        {
            int cx = Cell(_x[i], inv);
            int cy = Cell(_y[i], inv);
            int examined = 0;
            float px = 0f, py = 0f;

            for (int ox = -1; ox <= 1 && examined < MaxNeighbours; ox++)
            {
                for (int oy = -1; oy <= 1 && examined < MaxNeighbours; oy++)
                {
                    for (int j = _heads[Hash(cx + ox, cy + oy) & mask];
                         j >= 0 && examined < MaxNeighbours;
                         j = _next[j])
                    {
                        if (j == i) continue;

                        // Hash collisions put foreign cells in the bucket; the distance test
                        // below rejects them, but they must not use up the neighbour budget.
                        if (Cell(_x[j], inv) != cx + ox || Cell(_y[j], inv) != cy + oy) continue;

                        examined++;
                        float ddx = _x[i] - _x[j];
                        float ddy = _y[i] - _y[j];
                        float d2 = (ddx * ddx) + (ddy * ddy);
                        if (d2 >= r2) continue;

                        float d = MathF.Sqrt(d2);

                        // Deadband: a pair within 2 % of the spacing is spaced. Correcting the last
                        // few hundredths every tick only makes a settled crowd shimmer.
                        if (radius - d < radius * 0.02f) continue;
                        float ux, uy;
                        if (d > 1e-4f)
                        {
                            ux = ddx / d;
                            uy = ddy / d;
                        }
                        else
                        {
                            // Coincident: a direction from the pair's indices, antisymmetric so
                            // the two members move apart rather than together.
                            float a = PairAngle(i < j ? i : j, i < j ? j : i);
                            ux = MathF.Cos(a);
                            uy = MathF.Sin(a);
                            if (i > j)
                            {
                                ux = -ux;
                                uy = -uy;
                            }
                        }

                        float push = (radius - d) * 0.5f;
                        px += ux * push;
                        py += uy * push;
                    }
                }
            }

            float m2 = (px * px) + (py * py);
            if (m2 > maxStep * maxStep)
            {
                float s = maxStep / MathF.Sqrt(m2);
                px *= s;
                py *= s;
            }

            _dx[i] = px;
            _dy[i] = py;
        }
    }

    /// <summary>
    /// Queueing, used by <see cref="EnemyMoveSystem"/> before it steps an enemy: true when another
    /// enemy stands within the spacing plus this step <b>ahead</b> of (<paramref name="x"/>,
    /// <paramref name="y"/>) toward the target and is itself nearer the target. The enemy then
    /// holds this tick instead of walking into the one in front, so a crowd forms a queue around
    /// its target rather than compressing into it and being pushed back out every tick (which
    /// read as jitter). Answered from the previous separation step's grid - one world tick old,
    /// which is the scale on which a queue changes - and bounded by <see cref="MaxNeighbours"/>.
    /// </summary>
    internal bool IsBlockedAhead(float x, float y, float targetX, float targetY, float step) =>
        IsBlockedAhead(x, y, targetX, targetY, step, out _);

    /// <summary>
    /// As the other overload, also reporting on which side of the line to the target the blocker
    /// stands (<paramref name="side"/> &gt; 0: left), so the caller can slide the other way.
    /// </summary>
    internal bool IsBlockedAhead(float x, float y, float targetX, float targetY, float step, out float side)
    {
        side = 0f;
        if (_gridCount < 2 || _gridMask < 0) return false;
        float radius = _settings.SeparationRadius;
        if (!(radius > 0f)) return false;

        // Blocked when a step would bring it well inside the spacing of the enemy ahead (the
        // remaining half-step overlap is within what one separation pass resolves): holding near
        // the spacing is then stable, where a threshold below the spacing let separation push an
        // enemy out, the enemy walk straight back in, and the pair oscillate every tick.
        float block = radius + (0.5f * step);
        float block2 = block * block;
        float tx = targetX - x, ty = targetY - y;
        float myDist2 = (tx * tx) + (ty * ty);
        int cx = Cell(x, _gridInv), cy = Cell(y, _gridInv);
        int examined = 0;
        for (int ox = -1; ox <= 1 && examined < MaxNeighbours; ox++)
        {
            for (int oy = -1; oy <= 1 && examined < MaxNeighbours; oy++)
            {
                for (int j = _heads[Hash(cx + ox, cy + oy) & _gridMask];
                     j >= 0 && examined < MaxNeighbours;
                     j = _next[j])
                {
                    if (j >= _gridCount) continue;
                    float jx = _x[j] + _dx[j], jy = _y[j] + _dy[j];
                    if (Cell(_x[j], _gridInv) != cx + ox || Cell(_y[j], _gridInv) != cy + oy) continue;
                    examined++;
                    float ddx = jx - x, ddy = jy - y;
                    float d2 = (ddx * ddx) + (ddy * ddy);
                    if (d2 <= 1e-6f || d2 >= block2) continue;   // itself, or not in the way
                    // In the way only inside a 60 degree cone toward the target: a neighbour beside
                    // this one on the ring is separation's to resolve, not a reason to stop short.
                    float dot = (ddx * tx) + (ddy * ty);
                    if (dot <= 0f || (dot * dot) < 0.25f * d2 * myDist2) continue;
                    float jtx = targetX - jx, jty = targetY - jy;
                    if ((jtx * jtx) + (jty * jty) < myDist2)
                    {
                        side = (tx * ddy) - (ty * ddx);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when no enemy of the last separation step stands within the spacing of (x, y),
    /// ignoring one at the exact point <paramref name="selfX"/>, <paramref name="selfY"/> (the
    /// asking enemy itself). Bounded like the other queries, and conservative where the bound
    /// bites: a query that reaches <see cref="MaxNeighbours"/> candidates reports the spot taken,
    /// because an unexamined enemy may stand on it.
    /// </summary>
    internal bool IsFree(float x, float y, float selfX, float selfY)
    {
        if (_gridCount < 2 || _gridMask < 0) return true;
        float radius = _settings.SeparationRadius;
        float r2 = radius * radius;
        int cx = Cell(x, _gridInv), cy = Cell(y, _gridInv);
        int examined = 0;
        for (int ox = -1; ox <= 1 && examined < MaxNeighbours; ox++)
        {
            for (int oy = -1; oy <= 1 && examined < MaxNeighbours; oy++)
            {
                for (int j = _heads[Hash(cx + ox, cy + oy) & _gridMask];
                     j >= 0 && examined < MaxNeighbours;
                     j = _next[j])
                {
                    if (j >= _gridCount) continue;
                    if (Cell(_x[j], _gridInv) != cx + ox || Cell(_y[j], _gridInv) != cy + oy) continue;
                    examined++;
                    float jx = _x[j] + _dx[j], jy = _y[j] + _dy[j];
                    float sx = jx - selfX, sy = jy - selfY;
                    if ((sx * sx) + (sy * sy) <= 1e-6f) continue;
                    float ddx = jx - x, ddy = jy - y;
                    if ((ddx * ddx) + (ddy * ddy) < r2) return false;
                }
            }
        }

        return examined < MaxNeighbours;
    }

    internal void SetSnapshot(ReadOnlySpan<Vec2> positions)
    {
        EnsureCapacity(positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            _x[i] = positions[i].X;
            _y[i] = positions[i].Y;
        }

        _count = positions.Length;
    }

    internal Vec2 PushOf(int i) => new(_dx[i], _dy[i]);

    private void EnsureCapacity(int n)
    {
        if (_x.Length >= n) return;
        int size = Math.Max(64, _x.Length);
        while (size < n) size *= 2;
        Array.Resize(ref _x, size);
        Array.Resize(ref _y, size);
        Array.Resize(ref _dx, size);
        Array.Resize(ref _dy, size);
        Array.Resize(ref _next, size);
    }

    private static int Cell(float v, float inv) => (int)MathF.Floor(v * inv);

    private static int Hash(int x, int y) => unchecked((x * 73856093) ^ (y * 19349663));

    private static float PairAngle(int a, int b) =>
        (float)(unchecked((uint)((a * 2654435761u) ^ (b * 40503u))) % 3600u) * (MathF.Tau / 3600f);

    private struct Collect : ISimChunkVisitor
    {
        private readonly EnemySeparationSystem _s;

        public Collect(EnemySeparationSystem s) => _s = s;

        public void Visit(in SimChunk chunk)
        {
            Span<Position> positions = chunk.Positions;
            Span<Health> healths = chunk.Healths;
            for (int i = 0; i < chunk.Count; i++)
            {
                if (healths[i].Dead) continue;
                int k = _s._count++;
                _s.EnsureCapacity(k + 1);
                _s._x[k] = positions[i].Value.X;
                _s._y[k] = positions[i].Value.Y;
            }
        }
    }

    private struct Apply : ISimChunkVisitor
    {
        private readonly EnemySeparationSystem _s;

        public Apply(EnemySeparationSystem s) => _s = s;

        public void Visit(in SimChunk chunk)
        {
            Span<Position> positions = chunk.Positions;
            Span<Health> healths = chunk.Healths;
            for (int i = 0; i < chunk.Count; i++)
            {
                if (healths[i].Dead) continue;
                int k = _s._cursor++;
                float dx = _s._dx[k];
                float dy = _s._dy[k];
                if (dx == 0f && dy == 0f) continue;

                Vec2 p = positions[i].Value;
                positions[i].Value = _s._settings.Bounds.Clamp(new Vec2(p.X + dx, p.Y + dy));
                _s._pushed++;
            }
        }
    }
}
