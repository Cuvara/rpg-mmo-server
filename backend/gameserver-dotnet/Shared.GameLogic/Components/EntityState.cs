namespace Shared.GameLogic.Components;

/// <summary>
/// Pure data component representing the state of a game entity.
/// Ported from Go Entity struct. No behavior — logic lives in Systems/.
/// </summary>
public struct EntityState
{
    /// <summary>Unique entity identifier. Go: ID string.</summary>
    public string Id;

    /// <summary>Entity type: "player", "npc", "mob", "boss". Go: Type string.</summary>
    public string Type;

    /// <summary>World position. Go: X, Y float32.</summary>
    public Vec2 Position;

    /// <summary>Current hit points. Go: HP int.</summary>
    public int Hp;

    /// <summary>Maximum hit points. Go: MaxHP int.</summary>
    public int MaxHp;

    /// <summary>Attack power. Go: Attack int.</summary>
    public int Attack;

    /// <summary>Defense power. Go: Defense int.</summary>
    public int Defense;

    /// <summary>
    /// Movement speed in <b>world units per second</b>. Consumed by
    /// <c>MovementSystem.Integrate</c> as <c>position += direction * Speed * dt</c>.
    /// A non-positive value means the entity cannot move.
    /// </summary>
    public float Speed;

    /// <summary>Whether this entity is dead. Go: Dead bool.</summary>
    public bool Dead;

    /// <summary>
    /// Tick value (DateTime.Ticks) until which attack is on cooldown.
    /// Go: CooldownUntil time.Time — converted to long ticks.
    /// </summary>
    public long CooldownUntilTicks;

    /// <summary>Last processed input tick. Go: LastInputTick uint64.</summary>
    public ulong LastInputTick;
}
