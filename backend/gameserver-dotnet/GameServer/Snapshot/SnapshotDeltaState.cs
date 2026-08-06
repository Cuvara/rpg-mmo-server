using GameServer.Net;
using Shared.GameLogic.Components;

namespace GameServer.Snapshot;

/// <summary>
/// Per-connection delta encoder state. Remembers the visible state last sent to one
/// client so the tick loop can transmit only what changed.
/// <para>
/// Keyframe policy: a full snapshot is sent on join, whenever the client asks for one
/// (<see cref="RequestFull"/>, wire message <c>MsgResync</c>), and every
/// <c>keyframeInterval</c> snapshots thereafter. Everything in between is a delta.
/// </para>
/// <para>
/// Correctness rests on the transport being ordered and reliable (TCP today): the
/// server treats "last sent" as "last received". The periodic keyframe is the recovery
/// path if that ever stops holding (KCP in unreliable mode, a client that joined late,
/// a client that lost its local state).
/// </para>
/// <para>
/// Threading: <see cref="RequestFull"/> is called from the connection read loop while
/// <see cref="Encode"/> runs on the tick thread — the request flag is interlocked. All
/// other state is touched only by the tick thread.
/// </para>
/// </summary>
public sealed class SnapshotDeltaState
{
    /// <summary>Visible fields of one entity as last sent to this client.</summary>
    private readonly struct SentView : IEquatable<SentView>
    {
        public readonly string Type;
        public readonly float X;
        public readonly float Y;
        public readonly int Hp;
        public readonly int MaxHp;

        public SentView(in EntityState e)
        {
            Type = e.Type;
            X = e.Position.X;
            Y = e.Position.Y;
            Hp = e.Hp;
            MaxHp = e.MaxHp;
        }

        public bool Equals(SentView other) =>
            Hp == other.Hp &&
            MaxHp == other.MaxHp &&
            // Bit comparison, not tolerance: the client mirrors the server's floats
            // exactly, so any change at all must be transmitted. A tolerance here
            // would let slow drift accumulate silently.
            X.Equals(other.X) &&
            Y.Equals(other.Y) &&
            string.Equals(Type, other.Type, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SentView v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Type, X, Y, Hp, MaxHp);
    }

    /// <summary>Shared empty entity list for deltas where nothing changed (never mutated).</summary>
    private static readonly List<EntitySnapshotMsg> EmptyEntities = new(0);

    private readonly Dictionary<string, SentView> _lastSent = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _sinceKeyframe;
    private int _forceFull = 1; // first snapshot on a connection is always a keyframe

    /// <summary>Number of snapshots sent since the last keyframe. Diagnostics/tests.</summary>
    public int SinceKeyframe => _sinceKeyframe;

    /// <summary>Ask for the next snapshot to be a full keyframe. Thread-safe.</summary>
    public void RequestFull() => Interlocked.Exchange(ref _forceFull, 1);

    /// <summary>
    /// Build the snapshot to send to this client for the current tick.
    /// </summary>
    /// <param name="tick">Current simulation tick.</param>
    /// <param name="ackTick">Highest input tick accepted for this client's own entity.</param>
    /// <param name="nearby">Entities inside the client's AOI this tick.</param>
    /// <param name="keyframeInterval">
    /// Snapshots between keyframes. &lt;= 0 disables delta encoding entirely (every
    /// snapshot is a keyframe) — the escape hatch if a client cannot merge deltas.
    /// </param>
    public SnapshotMessage Encode(ulong tick, ulong ackTick, List<EntityState> nearby, int keyframeInterval)
    {
        bool full = Interlocked.Exchange(ref _forceFull, 0) != 0
                    || keyframeInterval <= 0
                    || _sinceKeyframe >= keyframeInterval;

        if (full)
        {
            _sinceKeyframe = 0;
            return EncodeFull(tick, ackTick, nearby);
        }

        _sinceKeyframe++;
        return EncodeDelta(tick, ackTick, nearby);
    }

    private SnapshotMessage EncodeFull(ulong tick, ulong ackTick, List<EntityState> nearby)
    {
        var entities = new List<EntitySnapshotMsg>(nearby.Count);
        _lastSent.Clear();

        // Indexed for-loop, no LINQ, no enumerator boxing: this runs once per client per tick.
        for (int i = 0; i < nearby.Count; i++)
        {
            var e = nearby[i];
            entities.Add(ToMsg(in e));
            _lastSent[e.Id] = new SentView(in e);
        }

        return new SnapshotMessage
        {
            Tick = tick,
            AckTick = ackTick,
            Full = true,
            Entities = entities,
            Removed = null
        };
    }

    private SnapshotMessage EncodeDelta(ulong tick, ulong ackTick, List<EntityState> nearby)
    {
        _seen.Clear();
        List<EntitySnapshotMsg>? changed = null;

        for (int i = 0; i < nearby.Count; i++)
        {
            var e = nearby[i];
            _seen.Add(e.Id);

            var view = new SentView(in e);
            if (_lastSent.TryGetValue(e.Id, out var prev) && prev.Equals(view))
                continue; // unchanged since last send -> omit

            changed ??= new List<EntitySnapshotMsg>(4);
            changed.Add(ToMsg(in e));
            _lastSent[e.Id] = view;
        }

        // Anything previously sent but no longer in AOI is an explicit despawn.
        List<string>? removed = null;
        if (_lastSent.Count != _seen.Count)
        {
            foreach (var id in _lastSent.Keys)
            {
                if (!_seen.Contains(id))
                {
                    removed ??= new List<string>(2);
                    removed.Add(id);
                }
            }
            if (removed != null)
            {
                for (int i = 0; i < removed.Count; i++) _lastSent.Remove(removed[i]);
            }
        }

        return new SnapshotMessage
        {
            Tick = tick,
            AckTick = ackTick,
            Full = false,
            Entities = changed ?? EmptyEntities,
            Removed = removed
        };
    }

    private static EntitySnapshotMsg ToMsg(in EntityState e) => new()
    {
        Id = e.Id,
        Type = e.Type,
        X = e.Position.X,
        Y = e.Position.Y,
        Hp = e.Hp,
        MaxHp = e.MaxHp
    };
}
