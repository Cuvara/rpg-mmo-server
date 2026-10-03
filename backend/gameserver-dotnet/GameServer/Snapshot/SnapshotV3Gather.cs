using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Snapshot;

/// <summary>
/// One active status as the gather captured it: <see cref="StatusEffectData"/> with the
/// applier resolved to its world-stable key, so the encoder can intern it per connection
/// without hashing an id string on the write task.
/// </summary>
public readonly struct GatheredStatus
{
    /// <summary>Builds a gathered status.</summary>
    public GatheredStatus(uint effectId, uint stacks, ulong expiresTick, int sourceKey)
    {
        EffectId = effectId;
        Stacks = stacks;
        ExpiresTick = expiresTick;
        SourceKey = sourceKey;
    }

    /// <summary>Content status id.</summary>
    public uint EffectId { get; }

    /// <summary>Current stacks.</summary>
    public uint Stacks { get; }

    /// <summary>Server tick the effect ends on; 0 = until removed.</summary>
    public ulong ExpiresTick { get; }

    /// <summary>
    /// World-stable key of the applier, or <see cref="PendingGameEvent.NoKey"/> when there is
    /// none or it is no longer in the world.
    /// </summary>
    public int SourceKey { get; }
}

/// <summary>
/// The variable-length protocol 3 part of one gather (ADR-30): each AOI entity's stat block
/// and active statuses, copied out of the world under the gather's read lock so the write
/// task can encode them later without touching the world.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a copy.</b> Snapshot encoding runs on the connection's write task, after the read
/// lock is released (see <c>Connection.GatherSnapshotView</c>). The scalar v3 fields ride in
/// <see cref="EntityView"/>; stats and statuses are variable-length and live in
/// <see cref="GameplayState"/>, so they are copied into flat arrays here, indexed by the
/// entity's position in the gathered AOI buffer. Double-buffered with that buffer.
/// </para>
/// <para>
/// Only filled for connections whose peer advertised protocol 3; a protocol 2 connection never
/// pays for it. Arrays grow with headroom and are never shrunk, so the steady state allocates
/// nothing.
/// </para>
/// </remarks>
public sealed class SnapshotV3Gather
{
    private int[] _statStart = Array.Empty<int>();
    private int[] _statCount = Array.Empty<int>();
    private int[] _statusStart = Array.Empty<int>();
    private int[] _statusCount = Array.Empty<int>();
    private StatValueData[] _stats = new StatValueData[64];
    private GatheredStatus[] _statuses = new GatheredStatus[16];
    private StatusEffectData[] _statusScratch = new StatusEffectData[8];
    private int _statsUsed;
    private int _statusesUsed;
    private int _entities;

    /// <summary>Number of entities this gather describes.</summary>
    public int EntityCount => _entities;

    /// <summary>Forget the previous gather and make room for <paramref name="entityCount"/> entities.</summary>
    public void Reset(int entityCount)
    {
        if (_statStart.Length < entityCount)
        {
            int cap = entityCount + (entityCount >> 2) + 8;
            _statStart = new int[cap];
            _statCount = new int[cap];
            _statusStart = new int[cap];
            _statusCount = new int[cap];
        }

        Array.Clear(_statCount, 0, entityCount);
        Array.Clear(_statusCount, 0, entityCount);
        _entities = entityCount;
        _statsUsed = 0;
        _statusesUsed = 0;
    }

    /// <summary>Record entity <paramref name="index"/>'s stat block and statuses (tests and the gather).</summary>
    public void Set(int index, ReadOnlySpan<StatValueData> stats, ReadOnlySpan<GatheredStatus> statuses)
    {
        EnsureStats(_statsUsed + stats.Length);
        stats.CopyTo(_stats.AsSpan(_statsUsed));
        _statStart[index] = _statsUsed;
        _statCount[index] = stats.Length;
        _statsUsed += stats.Length;

        EnsureStatuses(_statusesUsed + statuses.Length);
        statuses.CopyTo(_statuses.AsSpan(_statusesUsed));
        _statusStart[index] = _statusesUsed;
        _statusCount[index] = statuses.Length;
        _statusesUsed += statuses.Length;
    }

    /// <summary>Stat block of the entity at <paramref name="index"/> in the gathered buffer.</summary>
    public ReadOnlySpan<StatValueData> StatsOf(int index) =>
        (uint)index < (uint)_entities ? _stats.AsSpan(_statStart[index], _statCount[index]) : default;

    /// <summary>Active statuses of the entity at <paramref name="index"/>, in application order.</summary>
    public ReadOnlySpan<GatheredStatus> StatusesOf(int index) =>
        (uint)index < (uint)_entities ? _statuses.AsSpan(_statusStart[index], _statusCount[index]) : default;

    /// <summary>
    /// Copy every gathered entity's stats and statuses out of the world. Runs inside the
    /// gather's read scope; allocates nothing once the arrays have grown to the population.
    /// </summary>
    public void Capture(WorldReader reader, ReadOnlySpan<EntityView> views)
    {
        Reset(views.Length);
        for (int i = 0; i < views.Length; i++)
        {
            int key = views[i].Key;

            int statCount = reader.StatCount(key);
            _statStart[i] = _statsUsed;
            if (statCount > 0)
            {
                EnsureStats(_statsUsed + statCount);
                int written = reader.CopyStats(key, _stats.AsSpan(_statsUsed, statCount));
                if (written > statCount) written = statCount;
                _statCount[i] = written;
                _statsUsed += written;
            }

            int statusCount = reader.StatusCount(key);
            _statusStart[i] = _statusesUsed;
            if (statusCount > 0)
            {
                if (_statusScratch.Length < statusCount) _statusScratch = new StatusEffectData[statusCount + 4];
                int written = reader.CopyStatuses(key, _statusScratch.AsSpan(0, statusCount));
                if (written > statusCount) written = statusCount;
                EnsureStatuses(_statusesUsed + written);
                for (int s = 0; s < written; s++)
                {
                    ref readonly StatusEffectData d = ref _statusScratch[s];
                    int sourceKey = PendingGameEvent.NoKey;
                    // One id lookup per active status: statuses are rare next to entities, and
                    // resolving here (under the lock the gather already holds) is what lets the
                    // write task intern the source without a world access.
                    if (d.SourceId is { Length: > 0 } src && reader.TryGetStableKey(src, out int sk)) sourceKey = sk;
                    _statuses[_statusesUsed + s] = new GatheredStatus(d.EffectId, d.Stacks, d.ExpiresTick, sourceKey);
                }

                _statusCount[i] = written;
                _statusesUsed += written;
            }
        }
    }

    private void EnsureStats(int needed)
    {
        if (_stats.Length >= needed) return;
        var grown = new StatValueData[Math.Max(needed + (needed >> 1), _stats.Length * 2)];
        _stats.AsSpan(0, _statsUsed).CopyTo(grown);
        _stats = grown;
    }

    private void EnsureStatuses(int needed)
    {
        if (_statuses.Length >= needed) return;
        var grown = new GatheredStatus[Math.Max(needed + (needed >> 1), _statuses.Length * 2)];
        _statuses.AsSpan(0, _statusesUsed).CopyTo(grown);
        _statuses = grown;
    }
}
