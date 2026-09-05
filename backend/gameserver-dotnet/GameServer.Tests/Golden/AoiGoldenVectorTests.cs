using System.Text.Json;
using Shared.GameLogic;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Golden;

/// <summary>
/// ADR-10 golden vectors for <see cref="AoiLogic.GetNearbyEntities"/>.
/// Verifies that the AOI filter produces identical results on server (NativeAOT)
/// and client (IL2CPP) by replaying committed fixtures bit-for-bit.
/// </summary>
public class AoiGoldenVectorTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private record AoiEntity(string Id, string Type, string X, string Y, int Hp, int MaxHp);

    private sealed class AoiCase
    {
        public string name { get; set; } = "";
        public string centerX { get; set; } = "";
        public string centerY { get; set; } = "";
        public string radius { get; set; } = "";
        public AoiEntity[] entities { get; set; } = Array.Empty<AoiEntity>();
        public string[] expectedIds { get; set; } = Array.Empty<string>();
        public int expectedCount { get; set; }
        public int? bufferSize { get; set; }
    }

    private sealed class AoiFile { public AoiCase[] cases { get; set; } = Array.Empty<AoiCase>(); }

    private static AoiCase[] LoadCases()
    {
        string json = File.ReadAllText(GoldenVectors.PathTo("aoi.json"));
        return JsonSerializer.Deserialize<AoiFile>(json, JsonOpts)?.cases
               ?? throw new InvalidDataException("aoi.json deserialized to null");
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadCases()) data.Add(c.name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Aoi(string name)
    {
        AoiCase c = Array.Find(LoadCases(), v => v.name == name)!;

        var center = new Vec2(GoldenVectors.Float(c.centerX), GoldenVectors.Float(c.centerY));
        float radius = GoldenVectors.Float(c.radius);

        var allEntities = new EntityState[c.entities.Length];
        for (int i = 0; i < c.entities.Length; i++)
        {
            var e = c.entities[i];
            allEntities[i] = new EntityState
            {
                Id = e.Id,
                Type = e.Type,
                Position = new Vec2(GoldenVectors.Float(e.X), GoldenVectors.Float(e.Y)),
                Hp = e.Hp,
                MaxHp = e.MaxHp,
            };
        }

        int bufSize = c.bufferSize ?? c.entities.Length;
        var buffer = new EntityState[bufSize];
        int count = AoiLogic.GetNearbyEntities(
            new ReadOnlySpan<EntityState>(allEntities), in center, radius, new Span<EntityState>(buffer));

        Assert.Equal(c.expectedCount, count);

        int written = Math.Min(count, bufSize);
        var actualIds = new string[written];
        for (int i = 0; i < written; i++) actualIds[i] = buffer[i].Id;

        Assert.Equal(c.expectedIds, actualIds);
    }
}
