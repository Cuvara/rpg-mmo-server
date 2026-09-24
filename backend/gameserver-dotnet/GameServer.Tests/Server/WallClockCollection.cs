namespace GameServer.Tests.Server;

/// <summary>
/// Tests whose assertions are about wall-clock behaviour -- distance travelled over real
/// seconds, through a real socket, against a server running its own tick loop -- run alone.
///
/// <para>The rest of the suite running beside them is ambient load, and ambient load moves
/// every such number. <c>NetworkAdversityTests</c> measured it first (travel 5.4% short in a
/// full-suite run against 0.2% isolated) and has its own collection for that reason;
/// <c>SlowClientMovementTests</c> joined this one after measuring 1.67 units against 6.00 on
/// a loaded CI runner (#426). A test whose answer depends on what else is running gets
/// re-run until green and then trusted, which is worse than a slower suite.</para>
///
/// <para>Only for tests that genuinely measure time. A test that merely WAITS for a
/// background thread should wait for the condition instead -- see
/// <c>SnapshotPipelineTests.StalledClient_CoalescesToNewest_AndLosesNoState</c>, whose flake
/// was a fixed sleep and was fixed by polling for convergence, not by serialising it.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockCollection
{
    public const string Name = "wall-clock";
}
