namespace GameServer.Scaffolding;

/// <summary>
/// Compiled-in <b>defaults</b> for the enemy AI.
///
/// <para><b>These are no longer the values the systems read.</b> They are the fallbacks
/// <see cref="EnemyAiSettings"/> uses for a knob nobody configured; the systems read the
/// settings object they were constructed with. The indirection is what makes the fight
/// tunable from a deployment instead of from a release — see that type for why every one
/// of them parses strictly.</para>
///
/// <para>Every value below is unchanged from the single-method spawner these were
/// extracted from, and the two <c>PerPlayer</c> additions are zero-valued on an empty
/// server, so a server with nothing configured and nobody online is bit-for-bit the
/// server that shipped. <c>EnemyAiCharacterizationTests</c> is what holds that claim
/// up.</para>
/// </summary>
internal static class EnemyAiTuning
{
    /// <summary>
    /// World-unit distance enemies spawn at, from whatever they are anchored to: a live
    /// player when there is one, the origin when there is not. At the origin it is the
    /// ring the pre-change spawner always used, which is why the number is unchanged.
    /// </summary>
    public const float SpawnRadius = 13.0f;

    /// <summary>
    /// Closest an enemy is placed to <i>any</i> live player. 8 units is far enough that a
    /// player has roughly (13-8)/2.5 ~ 2s of warning in the worst crowding case and the
    /// enemy is outside <c>GameConstants.AttackRange</c> (3.0) on arrival, so nothing ever
    /// materialises already in contact.
    /// </summary>
    public const float MinSpawnDistance = 8.0f;

    /// <summary>
    /// How close a chaser closes before it stops advancing. Inside
    /// <c>GameConstants.AttackRange</c> (3.0) so the player can hit what is on them, and
    /// non-zero so a ring of chasers does not jitter across the target's exact position
    /// every tick.
    /// </summary>
    public const float ContactRange = 1.0f;

    /// <summary>
    /// Whether enemies chase by default. On: the whole point of the change, and it is an
    /// improvement rather than a behaviour break because an enemy with no player to chase
    /// falls back to the pre-change walk-to-origin step, bit for bit.
    /// </summary>
    public const bool ChaseByDefault = true;

    /// <summary>Enemies per wave with nobody online. The pre-change wave.</summary>
    public const int EnemiesPerWave = 2;

    /// <summary>
    /// Extra enemies per wave, per live player. Sized against the cap, not chosen by
    /// taste: the additional 45-per-player population has to be reachable in a time a
    /// player will wait. At 6 per player per 1.5s wave, one player's 75-enemy cap fills in
    /// about 14 seconds; at the pre-change 2 it would take 56.
    /// </summary>
    public const int EnemiesPerWavePerPlayer = 6;

    /// <summary>Seconds between waves.</summary>
    public const float WaveIntervalSec = 1.5f;

    /// <summary>Maximum simultaneous enemies with nobody online. The pre-change cap.</summary>
    public const int MaxEnemies = 30;

    /// <summary>
    /// Extra simultaneous enemies allowed per live player. This is the number the owner's
    /// "threadbare" complaint is actually about: 30 was the whole world's budget however
    /// many players shared it, so every player who joined made the fight thinner. 45 puts
    /// a solo player in a 75-enemy world and a four-player group in a 210-enemy one, which
    /// is the crowd a battle-royale feel needs, while an empty server stays at 30.
    /// </summary>
    public const int MaxEnemiesPerPlayer = 45;

    /// <summary>
    /// Sized so the demo combat loop can actually complete. The arithmetic that ruled the
    /// old value of 30 out, measured live with the /status attack counters (291 accepted
    /// hits in six minutes, zero kills): a radial enemy at <see cref="EnemySpeed"/> 2.5
    /// crosses a player's <c>GameConstants.AttackRange</c> (3.0) window in at most
    /// 2·3.0/2.5 = 2.4s; at the 500ms server attack cooldown that is 3-4 accepted hits,
    /// and at 8 damage per hit (player attack 10 − <see cref="EnemyDefense"/> 2) a pass
    /// deals 24-32 damage. Against 30 HP a kill required a near-perfect radial alignment;
    /// 16 HP dies to two hits, which any pass through range produces.
    /// </summary>
    public const int EnemyHp = 16;
    public const int EnemyAttack = 5;
    public const int EnemyDefense = 2;

    /// <summary>Movement speed toward the current target (world units per second).</summary>
    public const float EnemySpeed = 2.5f;

    /// <summary>
    /// Radius around (0,0) at which a <b>targetless</b> enemy despawns.
    ///
    /// <para>Targetless is the whole condition, and it is what makes the change additive
    /// rather than a replacement. An enemy with a player to chase is never reaped for
    /// where it is standing — reaching a point is not a reason to stop existing when the
    /// point is wherever the fight happens to be, and a player who walked to the origin
    /// would otherwise watch everything attacking them evaporate. An enemy with nothing to
    /// chase walks to the origin and despawns there, exactly as every enemy did before.</para>
    /// </summary>
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

    /// <summary>
    /// Move every living enemy one step toward its target: the nearest live player, or
    /// the origin when there is none.
    /// </summary>
    Move = 1,

    /// <summary>
    /// Reap last, and never before the thing that kills. It removes enemies that are
    /// dead or that have arrived at the centre with nothing to chase, and "arrived" is a
    /// fact produced by <see cref="Move"/> earlier in this same tick. Reaping first would let an enemy be
    /// visible inside the despawn zone for a tick, and would defer every kill by one.
    /// </summary>
    Reap = 2,
}
