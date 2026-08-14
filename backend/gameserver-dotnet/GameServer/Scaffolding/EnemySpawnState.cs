using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// The enemy spawner's own simulation state, held on an entity in the world rather than
/// in fields on the system.
///
/// <para><b>Why this exists.</b> The wave accumulator and the next-id counter were private
/// instance fields on <c>EnemySpawnSystem</c>. That is simulation state living in a class
/// — the exact thing ADR-12 says gameplay must not do — and it had two concrete
/// consequences, not just a stylistic one: the state was invisible to anything that
/// inspects the world (so it could never be snapshotted, saved, or reset with the world),
/// and it silently made the system non-reentrant, since two instances would each keep
/// their own idea of when the next wave was due.</para>
///
/// <para>It lives on a <b>singleton entity carrying only this component</b>, which is
/// deliberate: the AOI and player queries require the seven standard components, so an
/// entity holding just this one matches none of them and can never appear in a snapshot
/// or a persistence sweep.</para>
///
/// <para>It is declared here, in <c>Scaffolding</c>, not in <c>World/Components.cs</c>.
/// The AOT hint guard discovers component types by namespace <b>or</b> by
/// <see cref="EcsComponentAttribute"/> across the whole assembly, so the attribute is
/// enough to keep it covered — content-owned components do not have to sit in the core.
/// (<c>EnemyAi</c> stays where it is only because moving it is not this change's job.)</para>
/// </summary>
[EcsComponent]
public struct EnemySpawnState
{
    /// <summary>Seconds accumulated toward the next wave.</summary>
    public float Accumulator;

    /// <summary>Monotonic counter behind enemy ids. Never reused.</summary>
    public int NextEnemyNumber;
}
