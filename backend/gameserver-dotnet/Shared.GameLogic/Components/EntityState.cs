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
    /// Simulation tick at which the attack cooldown expires: an attack is allowed when
    /// <c>currentTick &gt;= CooldownUntilTick</c>.
    /// <para>
    /// This is a SIMULATION tick, not <c>DateTime.Ticks</c> — cooldowns must be
    /// deterministic and replayable, so no wall-clock is involved anywhere in combat.
    /// </para>
    /// </summary>
    public ulong CooldownUntilTick;

    /// <summary>Last processed input tick. Go: LastInputTick uint64.</summary>
    public ulong LastInputTick;
}
