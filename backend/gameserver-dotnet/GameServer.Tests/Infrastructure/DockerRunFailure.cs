namespace GameServer.Tests.Infrastructure;

/// <summary>
/// Classifies a failed <c>docker run</c> by its stderr, to the only question the call site
/// has to answer: is this worth attempting again, and if so, why.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a named function rather than two <c>Contains</c> calls inline.</b> The
/// registry fixture already draws a distinction #175 established and that must not be
/// blurred — <b>docker absent → skip</b>, <b>container unusable → fail</b>. Underneath both
/// there is a third case: <b>the docker CLI invocation itself failed before the daemon was
/// reached</b>, which is neither a missing dependency nor a broken container. Deciding that
/// is a judgement about a string, and a judgement about a string is a thing that can be
/// tested directly — which matters more than usual here, because the fault it exists for is
/// environmental and intermittent and cannot be summoned on demand. See
/// <c>DockerRunFailureTests</c>.
/// </para>
/// <para>
/// <b>Recognised signatures only, and never the inverse.</b> Everything not positively
/// recognised is <see cref="Kind.Fatal"/>. It is deliberately not written as "retry unless
/// it looks like a real fault": a broad retry would also silently re-run genuine container
/// faults, which is the one outcome worse than the flake being absorbed here (#214). Adding
/// a signature to this file should mean a failure was actually observed and its stderr
/// captured verbatim — not that one was imagined.
/// </para>
/// </remarks>
internal static class DockerRunFailure
{
    /// <summary>What kind of failure a <c>docker run</c> stderr describes.</summary>
    internal enum Kind
    {
        /// <summary>
        /// A real fault, or one nothing here recognises. Fails on the first attempt: a bad
        /// image, a name conflict or a missing network will fail identically however many
        /// times it is tried, and retrying only delays the report.
        /// </summary>
        Fatal,

        /// <summary>
        /// The published port was taken between the lease being released and docker binding
        /// it. A different free port is available on the next attempt.
        /// </summary>
        PortTaken,

        /// <summary>
        /// The host's docker transport failed <i>before the daemon answered</i>. Nothing was
        /// created, nothing is wrong with the request, and the next attempt commonly works.
        /// </summary>
        HostTransport,
    }

    /// <summary>
    /// The WSL2 ↔ Docker Desktop vsock bridge timing out, seen as
    /// <c>&lt;3&gt;WSL (90584 - ) ERROR: UtilAcceptVsock:271: accept4 failed 110.</c> —
    /// <c>110</c> being <c>ETIMEDOUT</c> (#214).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both tokens are required, and that is what makes this safe.</b> <c>accept4</c> is a
    /// raw Linux syscall name emitted by the WSL utility process itself, and <c>vsock</c>
    /// names the transport it was accepting on. Neither word appears in anything the docker
    /// daemon says, because when the daemon has an opinion it says so in its own format —
    /// every genuine failure captured from docker 29.1.3 on this box is prefixed
    /// <c>docker: Error response from daemon:</c> and then names a cause:
    /// </para>
    /// <list type="bullet">
    /// <item><c>pull access denied for ..., repository does not exist</c> — bad image</item>
    /// <item><c>Conflict. The container name "/x" is already in use</c> — name clash</item>
    /// <item><c>Bind for 127.0.0.1:6379 failed: port is already allocated</c> — port in use</item>
    /// <item><c>failed to set up container networking: network x not found</c> — bad network</item>
    /// </list>
    /// <para>
    /// The vsock failure is the shape of a message from <i>below</i> the daemon: the relay
    /// timed out, so there was never a daemon response to format. That is exactly the
    /// distinction being matched on, and the negative cases in <c>DockerRunFailureTests</c>
    /// pin it — including a stderr carrying <c>accept4 failed</c> without <c>vsock</c> and one
    /// carrying <c>vsock</c> without <c>accept4</c>, both of which must stay
    /// <see cref="Kind.Fatal"/>.
    /// </para>
    /// <para>
    /// The container's own output cannot reach here to confuse it either: the fixture runs
    /// <c>docker run -d</c>, so the captured stderr belongs to the CLI, not to redis.
    /// </para>
    /// </remarks>
    private const string VsockAcceptToken = "accept4 failed";

    /// <summary>Transport name, required alongside <see cref="VsockAcceptToken"/>.</summary>
    private const string VsockTransportToken = "vsock";

    /// <summary>Classify a failed run. Unrecognised is always <see cref="Kind.Fatal"/>.</summary>
    /// <param name="stderr">Standard error from the failed <c>docker run</c>.</param>
    internal static Kind Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            // A failure that said nothing is not a recognised transient. Retrying it would be
            // retrying on the absence of evidence.
            return Kind.Fatal;
        }

        if (Has(stderr, VsockAcceptToken) && Has(stderr, VsockTransportToken))
        {
            return Kind.HostTransport;
        }

        if (Has(stderr, "address already in use") || Has(stderr, "port is already allocated"))
        {
            return Kind.PortTaken;
        }

        return Kind.Fatal;
    }

    /// <summary>One line naming what a <see cref="Kind"/> means, for the retry log.</summary>
    internal static string Describe(Kind kind) => kind switch
    {
        Kind.PortTaken =>
            "the published port was taken between the lease and the bind",
        Kind.HostTransport =>
            "the host's WSL2 <-> Docker Desktop vsock bridge timed out before the daemon "
            + "answered (accept4/ETIMEDOUT, issue #214) — the container was never created, "
            + "and this is the box's docker transport rather than anything under test",
        _ => "an unrecognised failure",
    };

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
