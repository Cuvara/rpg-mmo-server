using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems;

/// <summary>
/// Client-side reconstruction of authoritative world state from the keyframe/delta
/// snapshot stream. Shared with the Unity client: the client must merge snapshots
/// exactly the way the server diffed them, so the merge rule lives here rather than
/// being reimplemented per consumer.
/// <para>
/// Not thread-safe — drive it from the single thread that consumes the socket.
/// </para>
/// </summary>
public sealed class SnapshotMerger
{
    private readonly Dictionary<string, EntitySnapshotData> _entities = new();

    /// <summary>Server tick of the newest applied snapshot. Never moves backwards.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Newest input acknowledgement seen. Monotonic; a zero ack never lowers it.</summary>
    public ulong AckTick { get; private set; }

    /// <summary>Number of keyframes applied.</summary>
    public int Keyframes { get; private set; }

    /// <summary>Number of deltas applied.</summary>
    public int Deltas { get; private set; }

    /// <summary>Reconstructed AOI set, keyed by entity ID.</summary>
    public IReadOnlyDictionary<string, EntitySnapshotData> Entities => _entities;

    /// <summary>Number of entities currently visible.</summary>
    public int Count => _entities.Count;

    /// <summary>
    /// Merge one snapshot. A keyframe replaces the entity set outright; a delta upserts
    /// the carried entities and deletes the ones listed in <see cref="SnapshotData.Removed"/>.
    /// </summary>
    public void Apply(in SnapshotData snapshot)
    {
        if (snapshot.Full)
        {
            _entities.Clear();
            Keyframes++;
        }
        else
        {
            Deltas++;
        }

        if (snapshot.Entities != null)
        {
            foreach (var e in snapshot.Entities)
            {
                _entities[e.Id] = e;
            }
        }

        if (snapshot.Removed != null)
        {
            foreach (var id in snapshot.Removed)
            {
                _entities.Remove(id);
            }
        }

        if (snapshot.Tick > Tick) Tick = snapshot.Tick;
        if (snapshot.AckTick > AckTick) AckTick = snapshot.AckTick;
    }

    /// <summary>Look up one reconstructed entity.</summary>
    public bool TryGet(string id, out EntitySnapshotData entity) => _entities.TryGetValue(id, out entity);

    /// <summary>Drop all reconstructed state (e.g. after a map transfer).</summary>
    public void Reset()
    {
        _entities.Clear();
        Tick = 0;
        AckTick = 0;
        Keyframes = 0;
        Deltas = 0;
    }
}
