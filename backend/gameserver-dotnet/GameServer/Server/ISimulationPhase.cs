namespace GameServer.Server;

/// <summary>
/// A unit of per-tick simulation the host runs but knows nothing about.
///
/// <para>The server core — transport, codec, snapshot encoding, AOI, the ECS world, this
/// tick loop — is content-agnostic: it moves entities' state to clients and does not care
/// what created those entities or why they move. Everything that decides <i>what the game
/// is</i> lives behind this interface, in <c>GameServer/Scaffolding</c>.</para>
///
/// <para>The point is deletability, stated precisely: <b>the core</b> never names
/// <c>Scaffolding</c>. Delete the directory, drop the two references in <c>Program.cs</c>
/// — the composition root is allowed to know what the game is — and the server still
/// builds, accepts connections, ticks, and streams snapshots of whatever entities exist.
/// That is the test of whether the seam is real, and it is worth keeping true as actual
/// gameplay arrives.</para>
///
/// <para>What is deliberately <b>not</b> behind this seam: four components stay in
/// <c>World/Components.cs</c> because they are protocol, not content, however much they
/// read like gameplay. <c>Health</c> (<c>hp</c>, <c>max_hp</c> — <c>wire.proto</c> fields
/// 5 and 6), <c>Combat</c> (<c>Attack</c>, <c>Defense</c>, <c>CooldownUntilTick</c> — all
/// fields of the <c>EntityState</c> the Unity client compiles as source), <c>Locomotion</c>
/// (<c>Speed</c>, likewise an <c>EntityState</c> field) and <c>InputCursor</c>
/// (<c>LastInputTick</c> is <c>ack_tick</c> on the wire, the client's reconciliation
/// anchor). Moving any of them changes what the client compiles against.</para>
/// </summary>
public interface ISimulationPhase
{
    /// <summary>
    /// Advances this phase by one tick, called after input has been applied and before
    /// snapshots are built, so anything it changes is visible in the same tick's snapshot
    /// rather than the next one.
    ///
    /// <para>The tick loop's own write scope is <b>closed</b> by then: an implementation
    /// takes its own, via <c>EcsWorld.UpdateComponents</c> or <c>ReadAll</c>. That is
    /// deliberate — a phase that spans several systems wants them under one scope so the
    /// deferred structural drain lands once, at the end of the phase and still before
    /// snapshots, rather than between systems.</para>
    ///
    /// <para>Consequence worth knowing: the phase is responsible for its own locking, and
    /// two phases would take the write lock twice per tick. If that ever matters, the fix
    /// is to hand phases a writer rather than to widen this contract quietly.</para>
    /// </summary>
    void Tick(ulong currentTick);

    /// <summary>
    /// How many entities this phase currently owns. Reported through the status endpoint
    /// for operators; the core attaches no meaning to the number beyond passing it on.
    /// </summary>
    int TrackedEntityCount { get; }
}
