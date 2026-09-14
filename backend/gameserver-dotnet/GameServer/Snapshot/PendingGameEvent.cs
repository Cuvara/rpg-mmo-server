using Shared.GameLogic.Components;

namespace GameServer.Snapshot;

/// <summary>
/// A <see cref="GameEventData"/> with its participants' world-stable keys already
/// resolved, ready for per-connection handle mapping.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the keys are resolved once here rather than per connection.</b> The simulation
/// produces events naming entities by id string, because an id is the only name for an
/// entity that every layer shares. The delta encoder keys its per-connection maps on the
/// world-stable integer instead, precisely so it does not hash an id string per entity per
/// viewer per tick (issue #237). An event that carried only strings would reintroduce that
/// hashing on the event path and multiply it by the number of observers; resolving once at
/// emit time and copying an int per observer does not.
/// </para>
/// <para>
/// <b>Why the id strings are kept anyway.</b> A key is meaningless to anything outside this
/// process, and the event path has two consumers that are outside it: the JSON encoding,
/// which has no interning and addresses entities by id, and diagnostics. Keeping both costs
/// one reference per event on a path that runs at combat frequency, not tick frequency.
/// </para>
/// <para>
/// <see cref="NoKey"/> rather than 0 for "no participant", because 0 is a legal stable key.
/// </para>
/// </remarks>
public readonly struct PendingGameEvent
{
    /// <summary>
    /// Stands for "this event has no such participant". Distinct from any real key —
    /// <see cref="Components.EntityIdRef.Stable"/> allocates from 1 for entities and the
    /// delta encoder's legacy path allocates negatives, so <see cref="int.MinValue"/> is the
    /// one value neither can produce.
    /// </summary>
    public const int NoKey = int.MinValue;

    public PendingGameEvent(in GameEventData data, int sourceKey, int targetKey)
    {
        Data = data;
        SourceKey = sourceKey;
        TargetKey = targetKey;
    }

    public GameEventData Data { get; }

    /// <summary>Stable key of <see cref="GameEventData.SourceId"/>, or <see cref="NoKey"/>.</summary>
    public int SourceKey { get; }

    /// <summary>Stable key of <see cref="GameEventData.TargetId"/>, or <see cref="NoKey"/>.</summary>
    public int TargetKey { get; }

    public bool HasSource => SourceKey != NoKey;

    public bool HasTarget => TargetKey != NoKey;
}
