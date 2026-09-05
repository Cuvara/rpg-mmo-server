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

    private record SnapEntity(string Id, string Type, string X, string Y, int Hp, int MaxHp);
    private record ExpectedEntity(string Id, int Hp, string X, string Y);

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
                    e.Hp, e.MaxHp);
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
        }
    }
}
