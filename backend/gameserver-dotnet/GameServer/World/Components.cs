using System;
using Shared.GameLogic.Components;

namespace GameServer.World.Components;

/// <summary>
/// Marks a struct as an Arch ECS component stored in <see cref="EcsWorld"/>.
/// <para>
/// This attribute is the input to the AOT hint guard (ADR-11): every type carrying
/// it, and every struct declared in this namespace, must have its <c>T[]</c> array
/// type statically constructed by <see cref="ArchAotHints"/>. A component that is
/// unhinted publishes without a warning and then throws
/// <c>NotSupportedException: 'T[]' is missing native code or metadata</c> the first
/// time the native binary creates an archetype containing it.
/// </para>
/// <para>
/// <c>GameServer.Tests.World.ArchAotHintTests</c> enforces this. Do not hand-edit
/// the hint list without running that test.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class EcsComponentAttribute : Attribute
{
}

/// <summary>
/// Durable identity of an entity inside the simulation.
/// <para>
/// This is still a <see cref="string"/> and is still equal to the user id, exactly as
/// <c>GameWorld</c>'s dictionary key was. ADR-10 calls for an integer simulation
/// handle here; that is a separate migration (it reaches persistence and the
/// reconnect/hold bookkeeping) and is deliberately NOT part of the Arch migration.
/// Until it happens this component puts a managed reference in every chunk.
/// </para>
/// </summary>
[EcsComponent]
public struct EntityIdRef
{
    /// <summary>Entity identifier; for players this is the user id.</summary>
    public string Value;

    public EntityIdRef(string value) => Value = value;
}

/// <summary>
/// Entity category: <c>"player"</c>, <c>"npc"</c>, <c>"mob"</c>, <c>"boss"</c>.
/// Mirrors <see cref="EntityState.Type"/> verbatim so the wire encoder keeps
/// producing the same values. <see cref="PlayerTag"/> is the archetype-level
/// mirror of <c>Value == "player"</c> and is maintained alongside it.
/// </summary>
[EcsComponent]
public struct EntityKind
{
    /// <summary>Entity type string.</summary>
    public string Value;

    public EntityKind(string value) => Value = value;
}

/// <summary>World-space position.</summary>
[EcsComponent]
public struct Position
{
    /// <summary>Position in world units.</summary>
    public Vec2 Value;

    public Position(Vec2 value) => Value = value;
}

/// <summary>Hit points and liveness.</summary>
[EcsComponent]
public struct Health
{
    /// <summary>Current hit points.</summary>
    public int Hp;

    /// <summary>Maximum hit points.</summary>
    public int MaxHp;

    /// <summary>Whether the entity is dead.</summary>
    public bool Dead;
}

/// <summary>Offensive/defensive stats and the tick-based attack cooldown.</summary>
[EcsComponent]
public struct Combat
{
    /// <summary>Attack power.</summary>
    public int Attack;

    /// <summary>Defense power.</summary>
    public int Defense;

    /// <summary>
    /// Simulation tick at which the attack cooldown expires. A SIMULATION tick,
    /// never wall-clock — see <see cref="EntityState.CooldownUntilTick"/>.
    /// </summary>
    public ulong CooldownUntilTick;
}

/// <summary>Movement capability.</summary>
[EcsComponent]
public struct Locomotion
{
    /// <summary>Movement speed in world units per second.</summary>
    public float Speed;

    public Locomotion(float speed) => Speed = speed;
}

/// <summary>
/// Newest input tick this entity has accepted. This is the value a client
/// reconciles its prediction against (<c>ack_tick</c> on the wire).
/// </summary>
[EcsComponent]
public struct InputCursor
{
    /// <summary>Last processed input tick (monotonic).</summary>
    public ulong LastInputTick;

    /// <summary>X of the most recently accepted movement direction.</summary>
    /// <remarks>
    /// Held so the critical group can keep integrating between input packets. A client
    /// sends at its own rate — the smoke client sends 10 per second — and without this the
    /// server would step that player only 10 times a second while simulating 60, which
    /// makes movement speed a function of the client's send rate. The wire field is a
    /// direction with no expiry of its own, so the expiry is the server's:
    /// <see cref="HeldFromTick"/> plus one world interval. See
    /// <c>InputHandler.ApplyHeldMovement</c>.
    /// <para>Server-side only. It is not part of <c>EntityState</c> and never reaches the
    /// wire.</para>
    /// </remarks>
    public float HeldMoveX;

    /// <summary>Y of the most recently accepted movement direction.</summary>
    public float HeldMoveY;

    /// <summary>
    /// Base tick on which the held direction was accepted. Zero means "nothing held".
    /// </summary>
    public ulong HeldFromTick;

    /// <summary>Base tick on which this entity's last input packet was accepted.</summary>
    /// <remarks>
    /// Paired with <see cref="LastInputTick"/> so the two clocks can be related: the client
    /// tick says WHICH step an input is, this says WHEN it landed. The ratio between the two
    /// deltas is how long one of that client's steps lasts in base ticks, which nothing else
    /// on the server knows -- the input rate is the client's choice and is never sent.
    /// </remarks>
    public ulong LastInputBaseTick;

    /// <summary>
    /// Measured length of one of this client's input steps, in base ticks. Zero means "not
    /// measured yet".
    /// </summary>
    public float TicksPerInput;

    /// <summary>
    /// Movement time this entity is owed, in base ticks, not yet paid out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accrued from the CLIENT's own input ticks, not from arrival timing. A gap in
    /// <see cref="LastInputTick"/> states exactly how many of the client's steps were
    /// coalesced away or lost; arrival timing cannot see them at all, because the packets
    /// that carried them turned up in the same base tick as the one that survived.
    /// </para>
    /// <para>
    /// Paid down at a bounded rate rather than discharged. Repaying in one step restores the
    /// right distance and is wrong by every other measure: measured on a live server it
    /// produced a 1.36-unit jump where a normal step is 0.083, which a player reads as the
    /// avatar jumping around rather than lagging (#104).
    /// </para>
    /// </remarks>
    public float OwedTicks;

    /// <summary>
    /// Base tick on which this entity's position was last advanced, by either path — a
    /// packet or a held step. Zero means "never moved".
    /// </summary>
    /// <remarks>
    /// This is what makes a movement step cover the time it actually represents. The tick
    /// loop coalesces to at most one step per player per tick, so a burst of four inputs
    /// arriving together used to become one step of one tick's worth and the other three
    /// were discarded along with the simulated time they stood for. Scaling the step by
    /// <c>baseTick - LastMoveTick</c> gives that time back without weakening the rule the
    /// coalescing exists to enforce: a client that spams packets always has
    /// <c>LastMoveTick == baseTick - 1</c>, so it earns exactly one tick per tick, and the
    /// cap bounds the pathological case. See #100.
    /// </remarks>
    public ulong LastMoveTick;

    public InputCursor(ulong lastInputTick) => LastInputTick = lastInputTick;
}

/// <summary>
/// Archetype tag for player entities, kept in sync with
/// <c>EntityKind.Value == "player"</c>. It exists so the persistence sweep is an
/// archetype query rather than a full scan with a string comparison per entity.
/// <para>
/// Carries a byte because a zero-size component would make the chunk's element
/// stride zero, which is not a shape worth relying on in a pre-1.0 library.
/// </para>
/// </summary>
[EcsComponent]
public struct PlayerTag
{
    /// <summary>Unused; present only to give the tag a non-zero size.</summary>
    public byte Reserved;
}


/// <summary>
/// Archetype tag for entities driven by the enemy AI systems
/// (<c>GameServer.Scaffolding</c>): they walk toward the origin each tick and are reaped when
/// they reach the centre zone or die.
///
/// <para><b>This is not "is a mob".</b> Enemy-ness is ownership, not type. Plenty of
/// entities carry <c>EntityKind.Value == "mob"</c> without being AI-driven — the test
/// suite creates them constantly, and a mob placed by anything other than the spawner
/// must sit where it was put. Deriving the tag from the type string would silently put
/// every such mob on a march to the origin, so the tag is applied <b>only</b> at spawn
/// by <see cref="EcsWorld.Spawn"/> and is preserved, never re-derived, when an existing
/// entity is updated.</para>
///
/// <para>Carries a byte for the same reason <see cref="PlayerTag"/> does: a zero-size
/// component would make the chunk's element stride zero, which is not a shape worth
/// relying on in a pre-1.0 library.</para>
/// </summary>
[EcsComponent]
public struct EnemyAi
{
    /// <summary>Unused; present only to give the tag a non-zero size.</summary>
    public byte Reserved;
}
