namespace GameServer.Observability;

/// <summary>
/// The opt-in debug HTTP surface on the metrics port, behind one switch
/// (<see cref="ServerEnv.DebugEntities"/>): <c>GET /debug/entities</c> and
/// <c>POST /debug/kill</c>. Off by default and never set by a fleet manifest.
/// </summary>
/// <remarks>
/// Pulled out of <c>Program.cs</c> so that "off means neither listener exists" is a tested
/// statement rather than a reading of the composition root.
/// </remarks>
internal static class DebugSurface
{
    /// <summary>
    /// Bind the debug endpoints when <paramref name="enabled"/>; do nothing otherwise.
    /// </summary>
    /// <param name="endpoint">The metrics endpoint; null when metrics are disabled.</param>
    /// <param name="enabled">The parsed <see cref="ServerEnv.DebugEntities"/> switch.</param>
    /// <param name="entities">Producer of the <c>/debug/entities</c> document.</param>
    /// <param name="kill">
    /// Queues a kill for the tick thread (<c>Scaffolding.DebugKillPhase.Submit</c>); null when no
    /// kill phase was composed, in which case <c>/debug/kill</c> is not bound.
    /// </param>
    public static void Enable(
        MetricsEndpoint? endpoint, bool enabled, Func<byte[]> entities,
        Func<string, CancellationToken, Task<DebugKillResult>>? kill)
    {
        if (!enabled || endpoint == null) return;

        endpoint.EnableDebugEntities(entities);
        if (kill != null)
        {
            endpoint.EnableDebugKill(kill);
        }
    }
}
