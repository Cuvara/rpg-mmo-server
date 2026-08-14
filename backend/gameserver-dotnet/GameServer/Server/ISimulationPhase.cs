namespace GameServer.Server;

/// <summary>
/// A unit of per-tick simulation the host runs but knows nothing about.
///
/// <para>The server core — transport, codec, snapshot encoding, AOI, the ECS world, this
/// tick loop — is content-agnostic: it moves entities' state to clients and does not care
/// what created those entities or why they move. Everything that decides <i>what the game
/// is</i> lives behind this interface, in <c>GameServer/Scaffolding</c>.</para>
///
/// <para>The point is deletability. Remove the <c>Scaffolding</c> directory and the server
/// still builds, still accepts connections, still ticks, and still streams snapshots of
/// whatever entities exist — it simply has nothing of its own to simulate. That is the test
/// of whether the seam is real, and it is worth keeping true as actual gameplay arrives:
/// gameplay implements this, the core never names it.</para>
///
/// <para>What is deliberately <b>not</b> behind this seam: <c>Health</c> and <c>Combat</c>
/// stay in <c>World/Components.cs</c> because <c>hp</c> and <c>max_hp</c> are first-class
/// fields in <c>wire.proto</c> and in the <c>EntityState</c> the Unity client compiles as
/// source. They are protocol, not content, however much they read like gameplay.</para>
/// </summary>
public interface ISimulationPhase
{
    /// <summary>
    /// Advances this phase by one tick. Called inside the tick loop's world write scope,
    /// after input has been applied and before snapshots are built, so anything it changes
    /// is visible in the same tick's snapshot rather than the next one.
    /// </summary>
    void Tick(ulong currentTick);

    /// <summary>
    /// How many entities this phase currently owns. Reported through the status endpoint
    /// for operators; the core attaches no meaning to the number beyond passing it on.
    /// </summary>
    int TrackedEntityCount { get; }
}
