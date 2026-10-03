using Shared.GameLogic.Components;

namespace Shared.GameLogic.World
{
    /// <summary>
    /// A trigger volume that moves a character to a named spawn on another (or the same)
    /// map (ADR-28 decision 3).
    /// </summary>
    /// <remarks>
    /// The volume is an upright cylinder — <see cref="Radius"/> on the ground plane,
    /// <see cref="Height"/> up from <see cref="Position"/>. A cylinder rather than a sphere
    /// so that jumping inside a doorway does not leave it, matching ADR-28's planar AOI.
    /// Whether a portal may be used (level, quest, party) is not geometry and is decided
    /// by the server, not here.
    /// </remarks>
    public readonly struct Portal
    {
        /// <summary>Name, unique within the map.</summary>
        public readonly string Name;

        /// <summary>Centre of the trigger's base.</summary>
        public readonly Vec3 Position;

        /// <summary>Trigger radius on the ground plane.</summary>
        public readonly float Radius;

        /// <summary>Trigger height above <see cref="Position"/>.</summary>
        public readonly float Height;

        /// <summary>Map the portal leads to. May be this map's own id.</summary>
        public readonly string TargetMapId;

        /// <summary>Name of the <see cref="SpawnPoint"/> on the target map.</summary>
        public readonly string TargetSpawn;

        /// <summary>Create a portal.</summary>
        public Portal(string name, in Vec3 position, float radius, float height, string targetMapId, string targetSpawn)
        {
            Name = name;
            Position = position;
            Radius = radius;
            Height = height;
            TargetMapId = targetMapId;
            TargetSpawn = targetSpawn;
        }

        /// <summary>
        /// True when a character's feet at <paramref name="feet"/> are inside the trigger
        /// (inclusive edges: standing exactly on the rim counts).
        /// </summary>
        public bool Contains(in Vec3 feet)
        {
            if (feet.Z < Position.Z || feet.Z > (float)(Position.Z + Height)) return false;
            float dx = (float)(feet.X - Position.X);
            float dy = (float)(feet.Y - Position.Y);
            float distSq = (float)((float)(dx * dx) + (float)(dy * dy));
            return distSq <= (float)(Radius * Radius);
        }

        /// <inheritdoc />
        public override string ToString() => $"{Name} -> {TargetMapId}/{TargetSpawn}";
    }
}
