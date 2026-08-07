using System.Text;
using GameServer.Net;

namespace GameServer.Tests.Net;

public class EntityTypeTests
{
    /// <summary>
    /// The C# mapping and Go's <c>entityTypeToPB</c> are hand-mirrored, so they
    /// can drift. Pinning the exact set here means a name added on one side and
    /// not the other fails a test rather than silently degrading that type to the
    /// string fallback on one language only — which would cost bytes and, worse,
    /// look like it was working.
    /// </summary>
    [Fact]
    public void KnownNames_MatchTheSchema()
    {
        var expected = new[] { "player", "mob", "npc", "item", "projectile" };
        Assert.Equal(expected.OrderBy(x => x), EntityTypes.KnownNames.OrderBy(x => x));
    }

    /// <summary>Every enumerated name must map to a distinct, non-zero enum value.</summary>
    [Fact]
    public void KnownNames_MapToDistinctNonDefaultValues()
    {
        var values = EntityTypes.KnownNames.Select(EntityTypes.Parse).ToList();
        Assert.DoesNotContain(EntityType.Unspecified, values);
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Theory]
    [InlineData("player")]
    [InlineData("mob")]
    [InlineData("npc")]
    [InlineData("item")]
    [InlineData("projectile")]
    public void KnownType_UsesTheEnumAndLeavesTheStringEmpty(string name)
    {
        var e = new EntitySnapshot { Id = "e1" };
        EntityTypes.SetType(e, name);

        Assert.NotEqual(EntityType.Unspecified, e.Type);
        // Exactly one field carries it: setting both would make the larger one
        // pure waste, which is the cost this change exists to remove.
        Assert.Equal("", e.TypeName);
        Assert.Equal(name, EntityTypes.NameOf(e));
    }

    [Theory]
    [InlineData("siege_engine")]
    [InlineData("Player")]  // case-sensitive on purpose: the wire is exact
    [InlineData("")]
    public void UnknownType_FallsBackToTheStringAndStillRoundTrips(string name)
    {
        var e = new EntitySnapshot { Id = "e1" };
        EntityTypes.SetType(e, name);

        Assert.Equal(EntityType.Unspecified, e.Type);
        Assert.Equal(name, e.TypeName);
        Assert.Equal(name, EntityTypes.NameOf(e));
    }

    /// <summary>
    /// The JSON encoding is unchanged by the enum: it is the legacy wire format
    /// and a pre-enum client parses <c>"type"</c> as text. Drift here is a silent
    /// compatibility break.
    /// </summary>
    [Theory]
    [InlineData("player")]
    [InlineData("siege_engine")]
    public void JsonStillCarriesTheTypeAsAString(string name)
    {
        var snapshot = new SnapshotMessage { Tick = 1 };
        var e = new EntitySnapshot { Id = "p1", X = 1, Y = 2, Hp = 3, MaxHp = 4 };
        EntityTypes.SetType(e, name);
        snapshot.Entities.Add(e);

        var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, WireEncoding.Json);
        string json = Encoding.UTF8.GetString(env.Payload);

        Assert.Contains($"\"type\":\"{name}\"", json);
        Assert.DoesNotContain("type_name", json);

        // And it must parse back to the same name through the JSON reader.
        var back = WireProtocol.GetPayload<SnapshotMessage>(env);
        Assert.Equal(name, EntityTypes.NameOf(back.Entities[0]));
    }

    /// <summary>The saving, asserted rather than assumed.</summary>
    [Fact]
    public void EnumIsSmallerOnTheWireThanTheStringFallback()
    {
        int Build(string type)
        {
            var m = new SnapshotMessage { Tick = 12345, AckTick = 12344 };
            for (int i = 0; i < 50; i++)
            {
                var e = new EntitySnapshot { Id = "lt-000000000042", X = i, Y = i, Hp = 100, MaxHp = 100 };
                EntityTypes.SetType(e, type);
                m.Entities.Add(e);
            }
            return WireProtocol.EncodeBody(
                WireProtocol.NewEnvelope(MsgType.Snapshot, m, WireEncoding.Proto)).Length;
        }

        int enumBytes = Build("player");
        int stringBytes = Build("siege_engine");

        Assert.True(enumBytes < stringBytes,
            $"enum encoding ({enumBytes}B) should be smaller than the string fallback ({stringBytes}B)");
    }
}
