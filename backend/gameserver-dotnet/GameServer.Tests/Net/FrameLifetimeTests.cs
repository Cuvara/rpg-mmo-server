namespace GameServer.Tests.Net;

/// <summary>
/// Pins the lifetime contract that makes the reused <see cref="FrameReadBuffer"/> legal:
/// a decoded <see cref="Envelope"/> must never alias the frame bytes it was parsed
/// from, and its payload must be an array nothing else will ever overwrite. Both
/// matter because an envelope can outlive its read-loop iteration — the transfer
/// handler retains one in a fire-and-forget task — while the scratch is overwritten
/// by the very next frame.
///
/// <para><b>Method: clobber-after-decode.</b> Frames are driven through ONE scratch,
/// and after every decode the scratch's buffers are overwritten with a sentinel — a
/// deterministic version of what production does later, when the next frame arrives.
/// If a decode ever returns a payload pointing into the scratch, the clobber corrupts
/// it and the content assertion fails. <see cref="HarnessDetectsPooledPayloads"/> is
/// the negative control: it runs the same assertions against a deliberately defective
/// decode (payload copied into a reused buffer — the pooling "optimization" this
/// contract forbids) and requires them to FAIL, proving a pass of the positive test
/// is evidence of copying rather than of a toothless harness.</para>
/// </summary>
public sealed class FrameLifetimeTests
{
    private const byte Sentinel = 0xAA;

    private static byte[] InputFrame(ulong tick, string target, WireEncoding encoding)
    {
        var env = WireProtocol.NewEnvelope(MsgType.Input,
            new InputMessage { Tick = tick, MoveX = 0.25f, MoveY = -1f, AttackTargetId = target },
            encoding);
        return WireProtocol.Encode(env);
    }

    private static void Clobber(FrameReadBuffer scratch)
    {
        scratch.Header.AsSpan().Fill(Sentinel);
        scratch.Body.AsSpan().Fill(Sentinel);
    }

    [Theory]
    [InlineData(WireEncoding.Proto)]
    [InlineData(WireEncoding.Json)]
    public async Task DecodedEnvelopesSurviveScratchReuse(WireEncoding encoding)
    {
        const int frames = 64;
        var expected = new List<(ulong Tick, string Target)>();
        var ms = new MemoryStream();
        for (int i = 0; i < frames; i++)
        {
            // Distinct payloads per frame, including sizes that force the scratch's
            // grow path mid-stream, so a decode against a freshly grown buffer is
            // exercised too.
            string target = i % 7 == 0 ? new string('t', 300 + i) : $"target-{i}";
            expected.Add(((ulong)(1000 + i), target));
            ms.Write(InputFrame((ulong)(1000 + i), target, encoding));
        }
        ms.Position = 0;

        var scratch = new FrameReadBuffer();
        var decoded = new List<Envelope>();
        for (int i = 0; i < frames; i++)
        {
            var env = await WireProtocol.DecodeAsync(ms, scratch, CancellationToken.None);
            Assert.NotNull(env);
            decoded.Add(env!);

            // Overwrite the scratch NOW, before the envelope is read — the reuse
            // hazard made deterministic instead of load-dependent.
            Clobber(scratch);
        }

        // Every envelope decoded earlier must still parse to exactly what was sent,
        // even though the scratch it came through has been clobbered 64 times since.
        for (int i = 0; i < frames; i++)
        {
            var msg = WireProtocol.GetPayload<InputMessage>(decoded[i]);
            Assert.Equal(expected[i].Tick, msg.Tick);
            Assert.Equal(expected[i].Target, msg.AttackTargetId);
        }
    }

    /// <summary>
    /// The defective decode the contract forbids: framing and parsing identical to the
    /// real path, but the payload lands in a REUSED buffer instead of a fresh array.
    /// This is what "pool the payload too" would look like.
    /// </summary>
    private static async Task<Envelope?> BuggyPooledPayloadDecode(
        Stream stream, FrameReadBuffer scratch, byte[]?[] poolHolder)
    {
        var env = await WireProtocol.DecodeAsync(stream, scratch, CancellationToken.None);
        if (env is null) return null;

        // Exact-size pool, allocated on first use: a byte[] payload cannot carry a
        // length, so an oversized pool would break parsing on trailing bytes and hide
        // the aliasing defect behind a framing error. The caller arranges equal-length
        // payloads so the pool fits every frame.
        poolHolder[0] ??= new byte[env.Payload.Length];
        Assert.Equal(env.Payload.Length, poolHolder[0]!.Length);
        env.Payload.CopyTo(poolHolder[0].AsSpan());

        // The injected defect: hand back the pooled buffer as the envelope's payload.
        return new Envelope { Type = env.Type, Payload = poolHolder[0]!, Encoding = env.Encoding };
    }

    /// <summary>
    /// Negative control: the positive test's assertions must CATCH a pooled payload.
    /// Two equal-length frames are decoded through the defective path; decoding the
    /// second overwrites the first's payload, and the first's content assertion —
    /// the same one <see cref="DecodedEnvelopesSurviveScratchReuse"/> relies on —
    /// must observe the corruption. If this test ever fails, the harness has gone
    /// toothless and the positive test proves nothing.
    /// </summary>
    [Fact]
    public async Task HarnessDetectsPooledPayloads()
    {
        // Same-length distinct payloads, so the pooled buffer fits both exactly.
        byte[] frameA = InputFrame(1, "AAAA-target", WireEncoding.Proto);
        byte[] frameB = InputFrame(2, "BBBB-target", WireEncoding.Proto);
        Assert.Equal(frameA.Length, frameB.Length);

        var ms = new MemoryStream();
        ms.Write(frameA);
        ms.Write(frameB);
        ms.Position = 0;

        var scratch = new FrameReadBuffer();
        var pool = new byte[]?[1];

        var envA = await BuggyPooledPayloadDecode(ms, scratch, pool);
        Assert.NotNull(envA);
        // Sanity: before reuse, the defective path still reads correctly — the defect
        // is invisible to any single-frame test, which is the whole point.
        Assert.Equal("AAAA-target", WireProtocol.GetPayload<InputMessage>(envA!).AttackTargetId);

        var envB = await BuggyPooledPayloadDecode(ms, scratch, pool);
        Assert.NotNull(envB);

        // envA's payload was overwritten by envB's decode. The content check that the
        // positive test applies to every frame must now fail for envA.
        var reparsedA = WireProtocol.GetPayload<InputMessage>(envA!);
        Assert.NotEqual("AAAA-target", reparsedA.AttackTargetId);
        Assert.Equal("BBBB-target", reparsedA.AttackTargetId);
    }
}
