using Shared.GameLogic.Components;

namespace Shared.GameLogic.World
{
    /// <summary>
    /// A named place a character enters the map at (ADR-28 decision 3). Portals and the
    /// gateway refer to spawns by name, so a map can move a spawn without anything that
    /// targets it changing.
    /// </summary>
    public readonly struct SpawnPoint
    {
        /// <summary>Name, unique within the map. "default" is where a fresh join lands.</summary>
        public readonly string Name;

        /// <summary>Feet position of a character placed here.</summary>
        public readonly Vec3 Position;

        /// <summary>Create a spawn point.</summary>
        public SpawnPoint(string name, in Vec3 position)
        {
            Name = name;
            Position = position;
        }

        /// <inheritdoc />
        public override string ToString() => $"{Name} @ {Position}";
    }
}
