using System.Text.Json;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Golden;

/// <summary>
/// ADR-10 golden vectors for <see cref="SnapshotMerger"/>. Verifies that the
/// keyframe/delta merge algorithm produces identical world state on server and
/// client by replaying committed step sequences.
/// </summary>
public class SnapshotMergerGoldenVectorTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // speed/facingBrad/action/changedFields are optional so every pre-existing case in
    // the fixture keeps parsing unchanged. All defaults are default(T), so it does not
    // matter whether System.Text.Json honours the declared default or substitutes the
    // type's — they agree.
    //
    // `speed` and x/y are hex bit patterns (floats do not round-trip identically through
    // two serializers); facingBrad, action, and changedFields are plain integers.
    //
    // `changedFields` is the wire field 13 mask (protocol version 2+). Zero means "all
    // fields present" — identical to omitting the field. Non-zero means a partial update:
    // only the bits that are set have valid data; the merger keeps its last-known value
    // for every unset bit. See Shared.GameLogic.Systems.SnapshotFieldBits.
    //
    // Protocol version 3 fields (z, velX/velY/velZ as hex, ownerId, spawnSeq, stats,
    // statsRemoved, statuses, statusesRemoved) are optional on the same terms: every case
    // written before them parses unchanged and asserts nothing about them.
    private record SnapEntity(
        string Id, string Type, string X, string Y, int Hp, int MaxHp,
        string? Speed = null, uint FacingBrad = 0, int Action = 0,
        uint ChangedFields = 0,
        string? Z = null, string? VelX = null, string? VelY = null, string? VelZ = null,
        string? OwnerId = null, uint SpawnSeq = 0,
        StatJson[]? Stats = null, uint[]? StatsRemoved = null,
        StatusJson[]? Statuses = null, uint[]? StatusesRemoved = null);

    // An expectation that is null is "not stated" and is not asserted. ownerId "" means
    // "expected absent (null)"; stats/statuses [] mean "expected empty (null)".
    private record ExpectedEntity(
        string Id, int Hp, string X, string Y,
        string? Speed = null, uint? FacingBrad = null, int? Action = null,
        string? Z = null, string? VelX = null, string? VelY = null, string? VelZ = null,
        string? OwnerId = null, uint? SpawnSeq = null,
        StatJson[]? Stats = null, StatusJson[]? Statuses = null);

    private record StatJson(uint StatId, int Value);

    private record StatusJson(uint EffectId, uint Stacks, ulong ExpiresTick, string? SourceId = null);

    private static float Hex(string? hex) => hex == null ? 0f : GoldenVectors.Float(hex);

    private sealed class SnapStep
    {
        public ulong tick { get; set; }
        public ulong ackTick { get; set; }
        public bool full { get; set; }
        public SnapEntity[] entities { get; set; } = Array.Empty<SnapEntity>();
        public string[] removed { get; set; } = Array.Empty<string>();
    }

    private sealed class MergerCase
    {
        public string name { get; set; } = "";
        public SnapStep[] steps { get; set; } = Array.Empty<SnapStep>();
        public bool resetAfterSteps { get; set; }
        public ulong expectedTick { get; set; }
        public ulong expectedAckTick { get; set; }
        public int expectedKeyframes { get; set; }
        public int expectedDeltas { get; set; }
        public string[] expectedEntityIds { get; set; } = Array.Empty<string>();
        public int expectedCount { get; set; }
        public ExpectedEntity? expectedEntityState { get; set; }
    }

    private sealed class MergerFile { public MergerCase[] cases { get; set; } = Array.Empty<MergerCase>(); }

    private static MergerCase[] LoadCases()
    {
        string json = File.ReadAllText(GoldenVectors.PathTo("snapshot_merger.json"));
        return JsonSerializer.Deserialize<MergerFile>(json, JsonOpts)?.cases
               ?? throw new InvalidDataException("snapshot_merger.json deserialized to null");
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadCases()) data.Add(c.name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Merger(string name)
    {
        MergerCase c = Array.Find(LoadCases(), v => v.name == name)!;

        var merger = new SnapshotMerger();

        foreach (var step in c.steps)
        {
            var entities = new EntitySnapshotData[step.entities.Length];
            for (int i = 0; i < step.entities.Length; i++)
            {
                var e = step.entities[i];
                entities[i] = new EntitySnapshotData(
                    e.Id, e.Type,
                    GoldenVectors.Float(e.X), GoldenVectors.Float(e.Y),
                    e.Hp, e.MaxHp,
                    e.Speed == null ? 0f : GoldenVectors.Float(e.Speed),
                    e.FacingBrad,
                    (EntityAction)e.Action,
                    // NAMED, not positional. The ten-argument overload that used to accept
                    // this positionally was removed in Shared.GameLogic 0.6.0 precisely
                    // because a positional uint here binds to whatever the overload set
                    // offers -- which is how a retrigger counter once became a field mask
                    // (Cuvara/Netcode#159). actionSeq is stated rather than defaulted: the
                    // golden corpus does not exercise it, and 0 here is that absence, not a
                    // value under test.
                    actionSeq: 0u,
                    changedFields: e.ChangedFields);

                // Protocol version 3 fields, through the constructor that cannot be confused
                // with the positional protocol 2 overloads.
                entities[i] = new EntitySnapshotData(
                    in entities[i],
                    Hex(e.Z), Hex(e.VelX), Hex(e.VelY), Hex(e.VelZ),
                    e.OwnerId, e.SpawnSeq,
                    e.Stats?.Select(s => new StatValueData(s.StatId, s.Value)).ToArray(),
                    e.StatsRemoved,
                    e.Statuses?.Select(s => new StatusEffectData(s.EffectId, s.Stacks, s.ExpiresTick, s.SourceId)).ToArray(),
                    e.StatusesRemoved);
            }

            var snapshot = new SnapshotData(
                step.tick, step.ackTick, step.full, entities,
                step.removed.Length > 0 ? step.removed : null);

            merger.Apply(snapshot);
        }

        if (c.resetAfterSteps)
        {
            merger.Reset();
        }

        Assert.Equal(c.expectedTick, merger.Tick);
        Assert.Equal(c.expectedAckTick, merger.AckTick);
        Assert.Equal(c.expectedKeyframes, merger.Keyframes);
        Assert.Equal(c.expectedDeltas, merger.Deltas);
        Assert.Equal(c.expectedCount, merger.Count);

        var actualIds = merger.Entities.Keys.OrderBy(k => k).ToArray();
        var expectedIds = c.expectedEntityIds.OrderBy(k => k).ToArray();
        Assert.Equal(expectedIds, actualIds);

        if (c.expectedEntityState != null)
        {
            Assert.True(merger.TryGet(c.expectedEntityState.Id, out var entity),
                $"expected entity {c.expectedEntityState.Id} not found");
            Assert.Equal(c.expectedEntityState.Hp, entity.Hp);
            GoldenVectors.AssertBitEqual(c.expectedEntityState.X, entity.X,
                name + "." + c.expectedEntityState.Id + ".x");
            GoldenVectors.AssertBitEqual(c.expectedEntityState.Y, entity.Y,
                name + "." + c.expectedEntityState.Id + ".y");

            // Only asserted when the fixture states them, so the pre-existing cases —
            // written before these fields existed — stay silent about them rather than
            // being retro-fitted with expectations nobody computed.
            if (c.expectedEntityState.Speed != null)
            {
                GoldenVectors.AssertBitEqual(c.expectedEntityState.Speed, entity.Speed,
                    name + "." + c.expectedEntityState.Id + ".speed");
            }

            if (c.expectedEntityState.FacingBrad != null)
            {
                Assert.Equal(c.expectedEntityState.FacingBrad.Value, entity.FacingBrad);
            }

            if (c.expectedEntityState.Action != null)
            {
                Assert.Equal((EntityAction)c.expectedEntityState.Action.Value, entity.Action);
            }

            AssertVersion3(name, c.expectedEntityState, entity);
        }
    }

    private static void AssertVersion3(string name, ExpectedEntity expected, EntitySnapshotData entity)
    {
        string at = name + "." + expected.Id;
        if (expected.Z != null) GoldenVectors.AssertBitEqual(expected.Z, entity.Z, at + ".z");
        if (expected.VelX != null) GoldenVectors.AssertBitEqual(expected.VelX, entity.VelX, at + ".velX");
        if (expected.VelY != null) GoldenVectors.AssertBitEqual(expected.VelY, entity.VelY, at + ".velY");
        if (expected.VelZ != null) GoldenVectors.AssertBitEqual(expected.VelZ, entity.VelZ, at + ".velZ");

        if (expected.OwnerId != null)
        {
            Assert.Equal(expected.OwnerId.Length == 0 ? null : expected.OwnerId, entity.OwnerId);
        }

        if (expected.SpawnSeq != null) Assert.Equal(expected.SpawnSeq.Value, entity.SpawnSeq);

        // Order is part of the contract (SnapshotMerger.MergeStats), so it is asserted.
        if (expected.Stats != null)
        {
            var want = expected.Stats.Select(s => new StatValueData(s.StatId, s.Value)).ToArray();
            Assert.Equal(want, entity.Stats ?? Array.Empty<StatValueData>());
        }

        if (expected.Statuses != null)
        {
            var want = expected.Statuses.Select(s => new StatusEffectData(s.EffectId, s.Stacks, s.ExpiresTick, s.SourceId)).ToArray();
            Assert.Equal(want, entity.Statuses ?? Array.Empty<StatusEffectData>());
        }

        // The merged state is complete; removal lists are consumed by the merge.
        if (expected.Stats != null || expected.Statuses != null)
        {
            Assert.Null(entity.StatsRemoved);
            Assert.Null(entity.StatusesRemoved);
        }
    }
}
