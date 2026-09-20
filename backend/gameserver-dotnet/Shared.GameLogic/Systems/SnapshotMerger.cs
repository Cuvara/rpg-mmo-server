using System.Collections.Generic;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    // SnapshotMerger and SnapshotFieldBits are siblings in this namespace: the bit
    // constants are defined once and read by both the client merger and the server
    // encoder, which is why they live in Shared.GameLogic rather than in GameServer.
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

        /// <summary>
        /// The same map as <see cref="Entities"/>, as its concrete type, so a per-frame
        /// caller can enumerate it without boxing the struct enumerator.
        /// </summary>
        /// <remarks>
        /// A <c>foreach</c> over the interface boxes <c>Dictionary&lt;K,V&gt;.Enumerator</c>
        /// — one 88-byte heap object per enumeration, measured. The Unity client's view
        /// binder enumerates this map once per rendered frame, which at 300–1000 fps made it
        /// the only per-frame allocation left in that path (~44 KB/s at 500 fps, into a
        /// stop-the-world GC). Read-only by contract: only the merger writes it, and a
        /// caller that mutates it desynchronises every consumer of <see cref="Entities"/>.
        /// </remarks>
        public Dictionary<string, EntitySnapshotData> EntityMap => _entities;

        /// <summary>Number of entities currently visible.</summary>
        public int Count => _entities.Count;

        /// <summary>
        /// Merge one snapshot. A keyframe replaces the entity set outright; a delta upserts
        /// the carried entities and deletes the ones listed in <see cref="SnapshotData.Removed"/>.
        /// <para>
        /// <b>Field-level delta (protocol version 2+).</b> When an entity in a delta carries
        /// a non-zero <see cref="EntitySnapshotData.ChangedFields"/> mask, only the bits that
        /// are set have valid data. Every unset bit must be kept from the receiver's last-known
        /// state rather than zeroed. This is the critical rule the issue warned about: a merger
        /// that zeros unset fields produces entities at the origin with 0 HP, but only for
        /// fields that happen not to change — so a test built on moving entities passes while
        /// the feature is broken.
        /// </para>
        /// <para>
        /// A full entity (<c>ChangedFields == 0</c>) is applied unconditionally, matching the
        /// pre-version-2 behaviour. A first introduction (entity not yet in the set) is also
        /// applied unconditionally: the server guarantees that new entities arrive with all
        /// fields present.
        /// </para>
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
                    if (e.ChangedFields != 0 && _entities.TryGetValue(e.Id, out var existing))
                    {
                        // Partial update: keep every field whose bit is absent in the mask.
                        _entities[e.Id] = MergeFieldDelta(in existing, in e);
                    }
                    else
                    {
                        // Full update (ChangedFields == 0) or first introduction of this entity.
                        _entities[e.Id] = e;
                    }
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

        /// <summary>
        /// Produce a complete entity state by combining a partial delta update with the
        /// receiver's last-known state. Only the fields flagged in
        /// <see cref="EntitySnapshotData.ChangedFields"/> are taken from
        /// <paramref name="delta"/>; all others come from <paramref name="existing"/>.
        /// </summary>
        private static EntitySnapshotData MergeFieldDelta(
            in EntitySnapshotData existing, in EntitySnapshotData delta)
        {
            uint mask = delta.ChangedFields;
            return new EntitySnapshotData(
                delta.Id,  // Id is always the entity key — it is how we found existing.
                (mask & SnapshotFieldBits.Type)       != 0 ? delta.Type       : existing.Type,
                (mask & SnapshotFieldBits.X)          != 0 ? delta.X          : existing.X,
                (mask & SnapshotFieldBits.Y)          != 0 ? delta.Y          : existing.Y,
                (mask & SnapshotFieldBits.Hp)         != 0 ? delta.Hp         : existing.Hp,
                (mask & SnapshotFieldBits.MaxHp)      != 0 ? delta.MaxHp      : existing.MaxHp,
                (mask & SnapshotFieldBits.Speed)      != 0 ? delta.Speed      : existing.Speed,
                (mask & SnapshotFieldBits.FacingBrad) != 0 ? delta.FacingBrad : existing.FacingBrad,
                (mask & SnapshotFieldBits.Action)     != 0 ? delta.Action     : existing.Action,
                actionSeq:     (mask & SnapshotFieldBits.ActionSeq) != 0 ? delta.ActionSeq : existing.ActionSeq,
                changedFields: 0  // merged result is complete state; mask is no longer meaningful
            );
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
}
