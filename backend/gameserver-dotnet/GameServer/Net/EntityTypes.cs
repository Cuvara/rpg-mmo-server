using RpgMmo.Wire.V1;

namespace GameServer.Net;

/// <summary>
/// Maps between the simulation's string entity types and the wire enum.
/// </summary>
/// <remarks>
/// <para>
/// The enum exists only on the Protobuf wire. <c>EntityState.Type</c>, the JSON
/// encoding and the Unity-facing mirror all keep speaking strings, so this is the
/// single place the two representations meet on the C# side. Its counterpart is
/// <c>entityTypeToPB</c> in Go's <c>shared/messages/proto.go</c>, and the two
/// must agree — <c>EntityTypeMappingTests</c> pins that against the shared list.
/// </para>
/// <para>
/// Why it exists: a string type costs 8 bytes on the wire ("player" = tag +
/// length + 6 characters) and the enum costs 2. On a 50-entity keyframe that is
/// ~19% of the entire payload.
/// </para>
/// <para>
/// An unrecognised name is not an error. It travels in
/// <c>EntitySnapshot.TypeName</c> instead, so a simulation that grows a new
/// entity kind before the schema does degrades to the old cost rather than
/// dropping the type or failing to encode.
/// </para>
/// </remarks>
public static class EntityTypes
{
    /// <summary>Wire name for <see cref="EntityType.Player"/>.</summary>
    public const string Player = "player";

    /// <summary>Wire name for <see cref="EntityType.Mob"/>.</summary>
    public const string Mob = "mob";

    private static readonly Dictionary<string, EntityType> ToEnum = new(StringComparer.Ordinal)
    {
        ["player"] = EntityType.Player,
        ["mob"] = EntityType.Mob,
        ["npc"] = EntityType.Npc,
        ["item"] = EntityType.Item,
        ["projectile"] = EntityType.Projectile,
    };

    private static readonly Dictionary<EntityType, string> ToName = BuildReverse();

    private static Dictionary<EntityType, string> BuildReverse()
    {
        var m = new Dictionary<EntityType, string>();
        foreach (var (name, value) in ToEnum) m[value] = name;
        return m;
    }

    /// <summary>Every type name this build can encode as an enum.</summary>
    public static IReadOnlyCollection<string> KnownNames => ToEnum.Keys;

    /// <summary>
    /// Populate an entity's type fields from a simulation type string, setting
    /// exactly one of <c>Type</c> or <c>TypeName</c>.
    /// </summary>
    /// <remarks>
    /// Never both: the reader prefers the enum, so also writing the string would
    /// make the larger field pure waste — which is the whole cost this saves.
    /// </remarks>
    public static void SetType(EntitySnapshot entity, string? typeName)
    {
        if (typeName is not null && ToEnum.TryGetValue(typeName, out var value))
        {
            entity.Type = value;
            return;
        }
        entity.TypeName = typeName ?? "";
    }

    /// <summary>
    /// The type name an entity carries, whichever field it arrived in.
    /// </summary>
    public static string NameOf(EntitySnapshot entity) =>
        ToName.TryGetValue(entity.Type, out var name) ? name : entity.TypeName;

    /// <summary>Resolve a name to its enum value, or Unspecified if unknown.</summary>
    public static EntityType Parse(string? typeName) =>
        typeName is not null && ToEnum.TryGetValue(typeName, out var v) ? v : EntityType.Unspecified;
}
