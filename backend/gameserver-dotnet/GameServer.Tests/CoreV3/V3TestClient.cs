using GameServer.Net;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// A protocol 3 client to the extent the wire contract defines one: it resolves entity,
/// owner and status-source handles against the bindings it was sent (refusing to guess an
/// entity handle), and merges with the shared <see cref="SnapshotMerger"/>, which owns the
/// stat/status merge rules (complete set on keyframe/introduction, changed-only + removed on a
/// delta with bits 0x1000/0x2000).
/// </summary>
internal sealed class V3TestClient
{
    private readonly Dictionary<uint, string> _bindings = new();

    public readonly SnapshotMerger Merger = new();

    /// <summary>Every entity type this client was ever sent.</summary>
    public readonly HashSet<string> TypesSeen = new(StringComparer.Ordinal);

    /// <summary>Union of every changed_fields mask received.</summary>
    public uint MasksSeen { get; private set; }

    public int Received { get; private set; }

    public void Receive(SnapshotMessage msg)
    {
        Received++;
        if (msg.Full) _bindings.Clear();

        // Bindings first: an owner or status source may name an entity introduced anywhere in
        // the same message.
        foreach (EntitySnapshot e in msg.Entities)
        {
            if (e.Handle != 0 && e.Id.Length > 0) _bindings[e.Handle] = e.Id;
        }

        var entities = new EntitySnapshotData[msg.Entities.Count];
        for (int i = 0; i < msg.Entities.Count; i++)
        {
            EntitySnapshot e = msg.Entities[i];
            string id;
            if (e.Handle != 0 && e.Id.Length == 0)
            {
                Assert.True(_bindings.TryGetValue(e.Handle, out string? bound),
                    $"tick {msg.Tick}: handle {e.Handle} arrived with no binding");
                id = bound!;
            }
            else
            {
                id = e.Id;
            }

            MasksSeen |= e.ChangedFields;
            string type = EntityTypes.NameOf(e);
            if (e.ChangedFields == 0 || (e.ChangedFields & SnapshotFieldBits.Type) != 0) TypesSeen.Add(type);

            var core = new EntitySnapshotData(
                id, type, e.X, e.Y, e.Hp, e.MaxHp, e.Speed, e.FacingBrad, (SimAction)e.Action,
                actionSeq: e.ActionSeq, changedFields: e.ChangedFields);

            string? owner = e.OwnerId.Length > 0 ? e.OwnerId
                : e.Owner != 0 && _bindings.TryGetValue(e.Owner, out string? o) ? o : null;

            StatValueData[]? stats = e.Stats.Count == 0 ? null
                : e.Stats.Select(s => new StatValueData(s.StatId, s.Value)).ToArray();
            StatusEffectData[]? statuses = e.Statuses.Count == 0 ? null
                : e.Statuses.Select(s => new StatusEffectData(
                    s.EffectId, s.Stacks, s.ExpiresTick,
                    s.Source != 0 && _bindings.TryGetValue(s.Source, out string? src) ? src : null)).ToArray();

            entities[i] = new EntitySnapshotData(
                in core, e.Z, e.VelX, e.VelY, e.VelZ, owner, e.SpawnSeq,
                stats, e.StatsRemoved.Count == 0 ? null : e.StatsRemoved.ToArray(),
                statuses, e.StatusesRemoved.Count == 0 ? null : e.StatusesRemoved.ToArray());
        }

        Merger.Apply(new SnapshotData(msg.Tick, msg.AckTick, msg.Full, entities, msg.Removed.ToArray()));
    }
}
