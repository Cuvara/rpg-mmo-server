using System.Text.Json;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Observability;

/// <summary>
/// The <c>/debug/entities</c> body: every entity in the world with its authoritative
/// position, read under the world's read lock. Served only when
/// <see cref="ServerEnv.DebugEntities"/> is on.
/// </summary>
/// <remarks>
/// <para>Written for automated test runs (the rpg-factory testlab), which compare what a
/// client rendered with where the server says an entity was. It runs on the HTTP thread,
/// never on the tick: <see cref="EcsWorld.GetEntitiesInRange(Vec2, float)"/> takes the read
/// lock once, materialises the list, and releases it before any JSON is written.</para>
///
/// <para>Hand-written with <see cref="Utf8JsonWriter"/> rather than a serializer so the
/// NativeAOT build needs no reflection metadata for it (see
/// <c>Aot/JsonReflectionGuardTests</c>).</para>
///
/// <para>No enemy target is reported. The server never stores one: enemy movement re-picks
/// the nearest live player every World tick (<c>PlayerTargetBuffer.TryNearest</c>), so a
/// reader derives the target from the player positions in the same document.</para>
/// </remarks>
internal static class DebugEntitiesDocument
{
    /// <summary>Radius that covers any map this server accepts (sides are validated finite).</summary>
    private const float WholeWorldRadius = 1.0e7f;

    /// <summary>Schema id; bump when a field changes meaning.</summary>
    public const string Schema = "rpg.debug-entities.v1";

    public static byte[] Write(EcsWorld world, ulong tick, double monotonicSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);

        List<EntityState> entities = world.GetEntitiesInRange(new Vec2(0f, 0f), WholeWorldRadius);

        using var stream = new MemoryStream(64 + (entities.Count * 128));
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("schema", Schema);
            json.WriteNumber("tick", tick);
            json.WriteNumber("monotonic_s", monotonicSeconds);
            json.WriteNumber("count", entities.Count);
            json.WriteStartArray("entities");
            foreach (EntityState e in entities)
            {
                json.WriteStartObject();
                json.WriteString("id", e.Id);
                json.WriteString("type", e.Type);
                json.WriteNumber("x", e.Position.X);
                json.WriteNumber("y", e.Position.Y);
                json.WriteNumber("hp", e.Hp);
                json.WriteNumber("max_hp", e.MaxHp);
                json.WriteNumber("speed", e.Speed);
                json.WriteBoolean("dead", e.Dead);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }
}
