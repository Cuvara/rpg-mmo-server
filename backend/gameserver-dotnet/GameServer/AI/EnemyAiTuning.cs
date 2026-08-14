namespace GameServer.AI;

/// <summary>
/// Tuning constants for the enemy AI, shared by the three systems that need them.
/// Values are unchanged from the single-method spawner they were extracted from —
/// stage 2 is a restructuring, and a changed constant here would move every enemy on
/// the wire.
/// </summary>
internal static class EnemyAiTuning
{
    /// <summary>World-unit radius enemies spawn at (from center).</summary>
    public const float SpawnRadius = 13.0f;

    /// <summary>Enemies per wave.</summary>
    public const int EnemiesPerWave = 2;

    /// <summary>Seconds between waves.</summary>
    public const float WaveIntervalSec = 1.5f;

    /// <summary>Maximum simultaneous enemies in the world.</summary>
    public const int MaxEnemies = 30;

    public const int EnemyHp = 30;
    public const int EnemyAttack = 5;
    public const int EnemyDefense = 2;

    /// <summary>Movement speed toward center (world units per second).</summary>
    public const float EnemySpeed = 2.5f;

    /// <summary>Radius around (0,0) at which enemies despawn (reached the center).</summary>
    public const float DespawnRadius = 2.5f;

    public const float DespawnRadiusSq = DespawnRadius * DespawnRadius;
}

/// <summary>
/// Execution order of the enemy AI systems within one tick.
///
/// <para><b>Why an enum and not <c>[UpdateInGroup]</c>.</b> There is no system-group
/// tree on the server. The one in the codebase belongs to the Unity <b>client</b>
/// package and is Unity DOTS' scheduler, which does not exist here; the obvious way to
/// get attribute-driven ordering server-side would be <c>Arch.System</c>'s source
/// generator, and that is banned (ADR-12 decision 4) because the reflection guard in
/// <c>ArchAotHintTests</c> cannot enumerate the query shapes it generates — adopting it
/// would create AOT surface no test can see.</para>
///
/// <para>So ordering is explicit and total: <see cref="EnemyAiSchedule"/> runs the
/// systems in the order of this enum, and the enum is the documentation of why that
/// order is the only correct one.</para>
/// </summary>
internal enum EnemyAiPhase
{
    /// <summary>
    /// Spawn first, so a freshly spawned enemy takes its first step on the tick it
    /// appears — which is what the original did by spawning into the list it was about
    /// to walk, and is observable in the snapshot.
    /// </summary>
    Spawn = 0,

    /// <summary>Move every living enemy one step toward the origin.</summary>
    Move = 1,

    /// <summary>
    /// Reap last, and never before the thing that kills. It removes enemies that are
    /// dead or that have arrived at the centre, and "arrived" is a fact produced by
    /// <see cref="Move"/> earlier in this same tick. Reaping first would let an enemy be
    /// visible inside the despawn zone for a tick, and would defer every kill by one.
    /// </summary>
    Reap = 2,
}
