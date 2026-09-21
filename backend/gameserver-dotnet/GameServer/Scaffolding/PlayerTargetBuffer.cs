using System;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// The live players an enemy might target, gathered once per system run into buffers that
/// are reused for the life of the process.
///
/// <para><b>Why this exists as a type.</b> Three systems need the same answer — "where are
/// the live players" — and the tick loop forbids allocating to get it. Written inline,
/// each of them would grow its own pair of arrays and its own resize rule, and the
/// nearest-player scan would be copied three times. It is deliberately <b>not</b> a cache
/// shared across systems or across ticks: <see cref="Refresh"/> re-reads the world every
/// call. A cross-tick cache would be simulation state living in a class, which ADR-12
/// forbids and <c>SimulationStateArchitectureTests</c> enforces, and the thing it would
/// save is one archetype query per world tick.</para>
///
/// <para><b>Allocation.</b> Steady state allocates nothing: the handle and position arrays
/// are grown with headroom on the rare tick where the player population exceeds them and
/// are never shrunk. The owning system holds this as a
/// <see cref="SimulationScratchAttribute"/> field, and the claim that attribute makes —
/// that resetting it at a tick boundary would change nothing but allocation — is true
/// here by construction, because nothing reads it before a <see cref="Refresh"/>.</para>
/// </summary>
internal sealed class PlayerTargetBuffer
{
    private EntityHandle[] _handles = Array.Empty<EntityHandle>();
    private Vec2[] _positions = Array.Empty<Vec2>();

    /// <summary>
    /// Handles of the LIVE players, compacted to match <see cref="_positions"/>.
    ///
    /// <para>A second array rather than compacting <see cref="_handles"/> in place: that
    /// one is the raw query destination and is overwritten wholesale by the next
    /// <see cref="Refresh"/>, so compacting it would make the query result and the live
    /// set the same storage and the indices would silently stop meaning the same thing.
    /// Two arrays is one extra copy per live player per refresh and no ambiguity.</para>
    /// </summary>
    private EntityHandle[] _live = Array.Empty<EntityHandle>();

    private int _count;

    /// <summary>Live players found by the last <see cref="Refresh"/>.</summary>
    public int Count => _count;

    /// <summary>Position of live player <paramref name="index"/>.</summary>
    public Vec2 this[int index] => _positions[index];

    /// <summary>
    /// Handle of live player <paramref name="index"/>, for a caller that has to write to
    /// the target rather than only measure against it.
    /// </summary>
    public EntityHandle HandleAt(int index) => _live[index];

    /// <summary>
    /// Re-read the world. Returns the number of <b>live</b> players found, which is what
    /// the population and wave sizes scale on: a dead player is not somebody to fight, and
    /// counting one would keep a server that everybody died on spawning at full rate.
    /// </summary>
    public int Refresh(WorldWriter writer)
    {
        int matches = writer.QueryWith<PlayerTag>(_handles);
        if (matches > _handles.Length)
        {
            // Headroom on growth, not exact size: growing to exactly `matches` re-queries
            // again the moment one more player joins (#249).
            _handles = new EntityHandle[matches + (matches >> 2) + 1];
            matches = writer.QueryWith<PlayerTag>(_handles);
        }

        if (_positions.Length < _handles.Length)
        {
            _positions = new Vec2[_handles.Length];
        }

        if (_live.Length < _handles.Length)
        {
            _live = new EntityHandle[_handles.Length];
        }

        int n = Math.Min(matches, _handles.Length);
        _count = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle handle = ref _handles[i];
            if (!writer.IsAlive(in handle)) continue;
            if (writer.HealthOf(in handle).Dead) continue;

            _live[_count] = handle;
            _positions[_count++] = writer.PositionOf(in handle).Value;
        }

        return _count;
    }

    /// <summary>
    /// Nearest live player to <paramref name="from"/>, or false when there is none.
    /// </summary>
    /// <remarks>
    /// A linear scan, and deliberately so. The alternative is the spatial grid, which
    /// would answer it in fewer comparisons and cost a query object per enemy per tick —
    /// an allocation in the tick loop, which is the one thing this loop may not do. The
    /// scan is P comparisons per enemy with P the live player count, so the cost is
    /// enemies x players per world tick: at the configured ceilings that is bounded work
    /// on floats already in cache, and at any population this server has actually run it
    /// is far below the AOI gather it sits beside.
    /// </remarks>
    public bool TryNearest(in Vec2 from, out Vec2 nearest)
    {
        nearest = default;
        if (_count == 0) return false;

        float bestSq = float.MaxValue;
        for (int i = 0; i < _count; i++)
        {
            float dx = _positions[i].X - from.X;
            float dy = _positions[i].Y - from.Y;
            float distSq = (dx * dx) + (dy * dy);
            if (distSq < bestSq)
            {
                bestSq = distSq;
                nearest = _positions[i];
            }
        }

        return true;
    }

    /// <summary>
    /// True when any live player is within <paramref name="radiusSq"/> of
    /// <paramref name="point"/>. The spawn placement's rejection test.
    /// </summary>
    /// <summary>
    /// Index of the nearest live player to <paramref name="from"/>, and the squared
    /// distance to it; false when there is none.
    /// </summary>
    /// <remarks>
    /// The same scan as <see cref="TryNearest"/>, returning the index instead of a copy of
    /// the position. A caller that has to act ON the target — attack it, write to it —
    /// needs the identity, and re-finding it from the position would be a second scan
    /// plus a float equality test on coordinates two players can legitimately share.
    /// <paramref name="distanceSq"/> is handed back because every caller of this overload
    /// immediately range-tests, and recomputing it is the one part of the scan that was
    /// already done.
    /// </remarks>
    public bool TryNearestIndex(in Vec2 from, out int index, out float distanceSq)
    {
        index = -1;
        distanceSq = float.MaxValue;
        if (_count == 0) return false;

        for (int i = 0; i < _count; i++)
        {
            float dx = _positions[i].X - from.X;
            float dy = _positions[i].Y - from.Y;
            float d = (dx * dx) + (dy * dy);
            if (d < distanceSq)
            {
                distanceSq = d;
                index = i;
            }
        }

        return index >= 0;
    }

    public bool AnyWithin(in Vec2 point, float radiusSq)
    {
        for (int i = 0; i < _count; i++)
        {
            float dx = _positions[i].X - point.X;
            float dy = _positions[i].Y - point.Y;
            if ((dx * dx) + (dy * dy) < radiusSq) return true;
        }

        return false;
    }
}
