using System;
using System.Collections.Generic;
using Arch.Core;
using Shared.GameLogic.Components;

namespace GameServer.World;

/// <summary>
/// A uniform spatial hash grid over entity positions, rebuilt once per tick and queried
/// once per viewer.
///
/// <para><b>What it replaces.</b> The AOI scan tested every entity against every viewer:
/// at 200 players that is 40 000 distance tests per tick, O(n²) in players, and the
/// largest single term in the tick (~874–1177 µs, measured). A viewer now examines only
/// the cells its radius covers.</para>
///
/// <para><b>Rebuilt whole, every tick, instead of maintained incrementally.</b> That is the
/// important design decision and it is deliberately the less clever one. Positions are
/// written from several places — the input handler through a <c>ref Position</c>, the enemy
/// move system through chunk spans, spawn and reconnect through <c>Store</c> — and an
/// incremental index has to intercept all of them, forever, including the next one someone
/// adds. Missing a write does not throw; it silently leaves an entity in the wrong bucket,
/// and the symptom is a player who vanishes from someone else's screen. A whole rebuild is
/// O(n) with no distance tests, costs one linear pass against the per-viewer scan it
/// removes, and cannot be stale by construction: it is built from the authoritative
/// component storage immediately before the queries that read it.</para>
///
/// <para><b>Bucketing is a counting sort, not a comparison sort.</b> Two passes — count per
/// cell, then place at a prefix-sum cursor — so entries within a cell keep the order the
/// chunk scan produced. That matters beyond tidiness: the delta encoder interns entity ids
/// in AOI arrival order, so a non-deterministic order would change the bytes on the wire
/// from run to run. <c>Array.Sort</c> would have been the obvious choice and is unstable,
/// which is exactly the bug this avoids.</para>
///
/// <para><b>Unbounded, and deliberately not derived from <c>MapBounds</c>.</b> Cells are
/// hashed rather than indexed into a fixed array, so an entity outside the play area — a
/// test fixture at 10 000, a mob parked beyond the edge — buckets correctly instead of
/// clamping onto an edge cell and being found by queries it is nowhere near. It also keeps
/// the world free of a bounds dependency it does not otherwise have.</para>
/// </summary>
internal sealed class SpatialGrid
{
    /// <summary>One entity's contribution to the index: identity plus the position it had at rebuild.</summary>
    private struct Entry
    {
        public Entity Entity;
        public Vec2 Position;

        /// <summary>
        /// Position of this entity in the rebuild's chunk scan.
        ///
        /// <para>Carried so a query can restore the order the brute-force scan produced.
        /// Bucketing necessarily reorders entities — that is what an index is — but the
        /// delta encoder interns entity ids in AOI arrival order, so emitting them
        /// cell-major would change the bytes on the wire for an identical set. The
        /// ordinal is what lets the index change the <i>search</i> without changing the
        /// <i>answer</i>.</para>
        /// </summary>
        public int ScanOrdinal;
    }

    private readonly float _cellSize;
    private readonly float _invCellSize;

    /// <summary>Cell key -> dense ordinal, rebuilt each tick. Keys are retained across
    /// rebuilds so the dictionary's buckets stop reallocating once the map is warm.</summary>
    private readonly Dictionary<long, int> _cellOrdinals = new();

    private int[] _cellStart = Array.Empty<int>();
    private int[] _cellCount = Array.Empty<int>();
    private int[] _cursor = Array.Empty<int>();
    private long[] _entryKeys = Array.Empty<long>();
    private Entry[] _entries = Array.Empty<Entry>();

    /// <summary>Destination for the counting-sort placement pass, reused across rebuilds
    /// so a rebuild allocates nothing once the population has stabilised.</summary>
    private Entry[] _bucketed = Array.Empty<Entry>();

    private int _entryCount;
    private int _cellsUsed;

    public SpatialGrid(float cellSize)
    {
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize,
                "Cell size must be a positive finite number.");
        }

        _cellSize = cellSize;
        _invCellSize = 1f / cellSize;
    }

    /// <summary>Entities in the index as of the last rebuild.</summary>
    public int Count => _entryCount;

    /// <summary>Distinct occupied cells as of the last rebuild. Diagnostics and tests.</summary>
    public int OccupiedCells => _cellsUsed;

    /// <summary>Cell edge length in world units.</summary>
    public float CellSize => _cellSize;

    // ── Build ────────────────────────────────────────────────────────────────

    /// <summary>Start a rebuild. Callers add every entity, then call <see cref="Finish"/>.</summary>
    public void Begin(int expectedCount)
    {
        if (_entries.Length < expectedCount)
        {
            int capacity = Math.Max(expectedCount, _entries.Length * 2);
            _entries = new Entry[capacity];
            _entryKeys = new long[capacity];
            _bucketed = new Entry[capacity];
        }

        _entryCount = 0;
        _cellsUsed = 0;
        _cellOrdinals.Clear();
    }

    /// <summary>Add one entity at the position it currently holds.</summary>
    public void Add(Entity entity, in Vec2 position)
    {
        if (_entryCount == _entries.Length)
        {
            int capacity = Math.Max(4, _entries.Length * 2);
            Array.Resize(ref _entries, capacity);
            Array.Resize(ref _entryKeys, capacity);
            Array.Resize(ref _bucketed, capacity);
        }

        long key = CellKey(position);

        _entries[_entryCount] = new Entry
        {
            Entity = entity,
            Position = position,
            ScanOrdinal = _entryCount,
        };
        _entryKeys[_entryCount] = key;
        _entryCount++;

        if (!_cellOrdinals.ContainsKey(key))
        {
            _cellOrdinals[key] = _cellsUsed++;
        }
    }

    /// <summary>
    /// Finish the rebuild: bucket the entries by cell, stably.
    /// </summary>
    public void Finish()
    {
        if (_cellStart.Length < _cellsUsed + 1)
        {
            int capacity = Math.Max(_cellsUsed + 1, _cellStart.Length * 2);
            _cellStart = new int[capacity];
            _cellCount = new int[capacity];
            _cursor = new int[capacity];
        }

        Array.Clear(_cellCount, 0, _cellsUsed);

        for (int i = 0; i < _entryCount; i++)
        {
            _cellCount[_cellOrdinals[_entryKeys[i]]]++;
        }

        int running = 0;
        for (int c = 0; c < _cellsUsed; c++)
        {
            _cellStart[c] = running;
            _cursor[c] = running;
            running += _cellCount[c];
        }

        // Place in scan order, so a cell's entries keep the order the chunk iteration
        // produced. This is what makes the query order deterministic.
        for (int i = 0; i < _entryCount; i++)
        {
            int ordinal = _cellOrdinals[_entryKeys[i]];
            _bucketed[_cursor[ordinal]++] = _entries[i];
        }

        Array.Copy(_bucketed, _entries, _entryCount);
    }

    // ── Query ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Visit every entity that could be within <paramref name="radius"/> of
    /// <paramref name="center"/>. Candidates outside the radius are still visited — the
    /// caller applies the exact distance test, so this narrows work without ever deciding
    /// membership itself.
    /// </summary>
    /// <remarks>
    /// <para><b>Falls back to a full sweep when the radius is large relative to the cell
    /// size.</b> A query whose neighbourhood covers more cells than the index holds
    /// entities would spend longer walking empty cells than it would testing everything,
    /// which is exactly what the tests do when they ask for a 10 000-unit radius. The
    /// fallback keeps the result identical and stops the index from being slower than the
    /// scan it replaced.</para>
    /// </remarks>
    public void Visit<TVisitor>(in Vec2 center, float radius, ref TVisitor visitor)
        where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        if (_entryCount == 0) return;

        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(radius))
        {
            // A non-finite query cannot be reasoned about cell-wise. Hand over everything
            // and let the caller's distance test reject it, exactly as the full scan did.
            VisitAll(ref visitor);
            return;
        }

        int minX = CellCoord(center.X - radius);
        int maxX = CellCoord(center.X + radius);
        int minY = CellCoord(center.Y - radius);
        int maxY = CellCoord(center.Y + radius);

        long cellsSpanned = (long)(maxX - minX + 1) * (maxY - minY + 1);
        if (cellsSpanned >= _entryCount)
        {
            VisitAll(ref visitor);
            return;
        }

        for (int cx = minX; cx <= maxX; cx++)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                if (!_cellOrdinals.TryGetValue(Pack(cx, cy), out int ordinal)) continue;

                int start = _cellStart[ordinal];
                int end = start + _cellCount[ordinal];
                for (int i = start; i < end; i++)
                {
                    visitor.Visit(_entries[i].Entity, in _entries[i].Position, _entries[i].ScanOrdinal);
                }
            }
        }
    }

    private void VisitAll<TVisitor>(ref TVisitor visitor) where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        for (int i = 0; i < _entryCount; i++)
        {
            visitor.Visit(_entries[i].Entity, in _entries[i].Position, _entries[i].ScanOrdinal);
        }
    }

    // ── Cell maths ───────────────────────────────────────────────────────────

    private int CellCoord(float v)
    {
        float scaled = v * _invCellSize;
        // Floor, not truncate: truncation folds -0.4 and 0.4 into the same cell and would
        // put an entity a cell away from where a neighbouring query looks for it.
        return (int)MathF.Floor(scaled);
    }

    private long CellKey(in Vec2 position) => Pack(CellCoord(position.X), CellCoord(position.Y));

    private static long Pack(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
}

/// <summary>
/// Receives spatial-index candidates. A <c>struct</c> so the call devirtualises and the
/// query allocates nothing.
/// </summary>
internal interface ISpatialVisitor
{
    /// <summary>
    /// One candidate. The caller still applies the exact distance test.
    /// <paramref name="scanOrdinal"/> is the entity's position in the rebuild scan, so a
    /// caller can restore brute-force order.
    /// </summary>
    void Visit(Entity entity, in Vec2 position, int scanOrdinal);
}
