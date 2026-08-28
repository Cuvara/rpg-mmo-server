namespace GameServer.Tests.Infrastructure;

/// <summary>
/// <see cref="DockerRunFailure.Classify"/> — the decision that says whether a failed
/// <c>docker run</c> is worth attempting again.
///
/// <para><b>This fixture exists because the fault it guards cannot be summoned.</b> The
/// failure in #214 is the host's WSL2 ↔ Docker Desktop vsock bridge timing out, at roughly
/// one full-suite run in seven and only while the box is loaded. A retry proved by "the
/// suite went green a few times" is not proved at all — that is indistinguishable from the
/// flake simply not firing. What <i>can</i> be pinned exactly is the judgement the retry
/// rests on: given this stderr, does it try again? So that judgement was made a function,
/// and this is the test of it.</para>
///
/// <para><b>Every negative case below is a verbatim capture</b> from docker 29.1.3 on the
/// box that reports #214, produced by actually causing each fault — a pull of an image that
/// does not exist, a duplicate container name, a second bind of a held port, a reference to
/// a network that is not there. They are not paraphrases of what docker might say. The one
/// positive case is the stderr quoted in #214 itself.</para>
///
/// <para>No docker required, nothing to schedule, nothing to flake: these are string
/// comparisons.</para>
/// </summary>
public class DockerRunFailureTests
{
    /// <summary>The stderr #214 was filed with, verbatim.</summary>
    private const string VsockStderr =
        "<3>WSL (90584 - ) ERROR: UtilAcceptVsock:271: accept4 failed 110.";

    // ── the transient the retry exists for ──

    /// <summary>The captured failure is recognised as the host's transport, not as a fault.</summary>
    [Fact]
    public void TheCapturedVsockTimeout_IsAHostTransportTransient()
    {
        Assert.Equal(DockerRunFailure.Kind.HostTransport, DockerRunFailure.Classify(VsockStderr));
    }

    /// <summary>
    /// The volatile parts of that line are the WSL pid, the source line number and the errno,
    /// none of which the match may depend on. 110 is <c>ETIMEDOUT</c> and 11 is
    /// <c>EAGAIN</c>; both are the same bridge failing to hand over a connection.
    /// </summary>
    [Theory]
    [InlineData("<3>WSL (90584 - ) ERROR: UtilAcceptVsock:271: accept4 failed 110.")]
    [InlineData("<3>WSL (12 - ) ERROR: UtilAcceptVsock:271: accept4 failed 110.")]
    [InlineData("<3>WSL (90584 - ) ERROR: UtilAcceptVsock:998: accept4 failed 11.")]
    [InlineData("<3>WSL (7 - foo) ERROR: UtilAcceptVsock:1: accept4 failed 110.")]
    public void TheVsockSignature_DoesNotDependOnThePidLineOrErrno(string stderr)
    {
        Assert.Equal(DockerRunFailure.Kind.HostTransport, DockerRunFailure.Classify(stderr));
    }

    /// <summary>
    /// Docker prints the bridge error and then whatever else it has to say. The signature has
    /// to survive being one line among several, because that is how it arrives.
    /// </summary>
    [Fact]
    public void TheVsockSignature_IsFoundAmongOtherOutput()
    {
        string stderr =
            "some unrelated preamble\n" + VsockStderr + "\ndocker: error during connect\n";

        Assert.Equal(DockerRunFailure.Kind.HostTransport, DockerRunFailure.Classify(stderr));
    }

    // ── genuine faults, captured verbatim from docker 29.1.3, must NOT retry ──

    /// <summary>
    /// <b>The case that matters most.</b> A container that fails for a real reason must fail
    /// on the first attempt: retrying a bad image or a missing network cannot succeed, it just
    /// delays the report and burns a five-minute docker timeout doing it. Each of these is a
    /// real fault this fixture could actually hit — a typo in <c>Image</c>, a leaked container
    /// from a killed run, a genuinely occupied port, a compose network that is not up.
    /// </summary>
    [Theory]
    // `docker run -d --name x nosuchimage-cuvara:nope`
    [InlineData(
        "docker: Error response from daemon: pull access denied for nosuchimage-cuvara, "
        + "repository does not exist or may require 'docker login'")]
    // The line docker prints above it, on its own.
    [InlineData("Unable to find image 'nosuchimage-cuvara:nope' locally")]
    // Two `docker run -d --name rpg-probe-dup` in a row.
    [InlineData(
        "docker: Error response from daemon: Conflict. The container name \"/rpg-probe-dup\" "
        + "is already in use by container "
        + "\"17340839175cfe837c6ebcdab7d658f6b07ab41b832111b39b418c4fbc22662c\". You have to "
        + "remove (or rename) that container to be able to reuse that name.")]
    // `docker run -d --network nosuchnet-cuvara`
    [InlineData(
        "docker: Error response from daemon: failed to set up container networking: "
        + "network nosuchnet-cuvara not found")]
    public void AGenuineContainerFault_IsFatalAndIsNotRetried(string stderr)
    {
        Assert.Equal(DockerRunFailure.Kind.Fatal, DockerRunFailure.Classify(stderr));
    }

    /// <summary>
    /// An unrecognised failure is fatal, not transient. The classifier says yes to signatures
    /// it knows and no to everything else; written the other way round — retry unless it
    /// looks like a real fault — every new docker error on this box would silently become a
    /// three-attempt loop.
    /// </summary>
    [Theory]
    [InlineData("docker: Error response from daemon: something nobody has seen before")]
    [InlineData("permission denied while trying to connect to the Docker daemon socket")]
    [InlineData("timed out")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingUnrecognised_IsFatal(string? stderr)
    {
        Assert.Equal(DockerRunFailure.Kind.Fatal, DockerRunFailure.Classify(stderr));
    }

    // ── the signature needs BOTH tokens ──

    /// <summary>
    /// Half the signature is not the signature. This is what stops the match widening by
    /// accident: <c>accept4</c> could plausibly appear in some future daemon error about a
    /// socket, and <c>vsock</c> appears in Docker Desktop's own configuration vocabulary, so
    /// either alone would be a broader match than #214 justifies.
    /// </summary>
    [Theory]
    [InlineData("<3>WSL (90584 - ) ERROR: UtilAcceptSomething:271: accept4 failed 110.")]
    [InlineData("docker: Error response from daemon: accept4 failed on the listener")]
    [InlineData("docker: Error response from daemon: vsock transport is not configured")]
    [InlineData("<3>WSL (90584 - ) ERROR: UtilAcceptVsock:271: connect failed 110.")]
    public void EitherTokenAlone_IsNotTheSignature(string stderr)
    {
        Assert.Equal(DockerRunFailure.Kind.Fatal, DockerRunFailure.Classify(stderr));
    }

    // ── the pre-existing port-collision retry is untouched ──

    /// <summary>
    /// The lease-to-bind race #175's port handling already retried keeps its own kind, so
    /// adding the transport case did not quietly reclassify it. The second string is the
    /// verbatim capture of two containers binding 127.0.0.1:6379.
    /// </summary>
    [Theory]
    [InlineData("docker: Error response from daemon: address already in use")]
    [InlineData(
        "docker: Error response from daemon: failed to set up container networking: driver "
        + "failed programming external connectivity on endpoint rpg-probe-port2 "
        + "(f192a5a8dc19cb75b007b48f094aa4700e0ab4744ba63247c3de1db954fbfef4): "
        + "Bind for 127.0.0.1:6379 failed: port is already allocated")]
    public void APortCollision_KeepsItsOwnKind(string stderr)
    {
        Assert.Equal(DockerRunFailure.Kind.PortTaken, DockerRunFailure.Classify(stderr));
    }

    /// <summary>
    /// The three kinds are distinct, which is the whole point of the enum: the call site
    /// pauses before a transport retry and does not before a port retry, and fails outright
    /// on the third.
    /// </summary>
    [Fact]
    public void TheThreeKinds_AreDistinguished()
    {
        var kinds = new[]
        {
            DockerRunFailure.Classify(VsockStderr),
            DockerRunFailure.Classify("Bind for 127.0.0.1:6379 failed: port is already allocated"),
            DockerRunFailure.Classify("docker: Error response from daemon: no such image"),
        };

        Assert.Equal(3, kinds.Distinct().Count());
    }

    /// <summary>
    /// Each kind describes itself for the retry log. A retry that printed a bare enum name
    /// would tell the next reader which branch ran and nothing about why it was forgiven.
    /// </summary>
    [Fact]
    public void EveryKind_DescribesItself()
    {
        Assert.Contains("vsock", DockerRunFailure.Describe(DockerRunFailure.Kind.HostTransport),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#214", DockerRunFailure.Describe(DockerRunFailure.Kind.HostTransport));
        Assert.Contains("port", DockerRunFailure.Describe(DockerRunFailure.Kind.PortTaken),
            StringComparison.OrdinalIgnoreCase);
    }
}
