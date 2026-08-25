using System.Net;
using System.Net.Sockets;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Persistence;
using GameServer.Server;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Server;

/// <summary>
/// The invariant <c>MovementSystem</c> states and the held-input model exists to uphold:
/// <b>travel distance depends on wall-clock time and the entity's speed, never on how many
/// input packets the client sent.</b> Measured end to end, over a socket.
///
/// <para><b>Why this exists next to unit tests that assert the same property.</b>
/// <c>MultiRateSimulationTests</c> builds the world by hand and pushes inputs straight into
/// the queue, which skips the join handshake, the network thread, the entity's real
/// creation path and the snapshot encoder. When a measurement against a running server
/// disagreed with those tests, the first question asked was whether they exercised the path
/// the live server takes — and nothing could answer it. These drive the real server over
/// TCP and read the position back out of the snapshot stream, which is the same number a
/// client sees.</para>
///
/// <para>The failure mode being guarded is silent by construction: a client and a server
/// that both integrate once per packet agree with each other perfectly while both moving at
/// a fraction of the configured speed. No reconciliation is raised, no counter moves, and
/// the only symptom is a character that feels sluggish.</para>
///
/// <para>These are wall-clock tests — the server runs its own tick loop, the test sends on
/// a timer — so the tolerance is wide enough to absorb a tick or two of scheduling jitter
/// and no wider. The distinction it has to make is 4x.</para>
/// </summary>
public class SlowClientMovementTests
{
    private const string JwtSecret = "slow-client-secret-32-bytes-aaaaa";
    private const string ServerId = "gs-slow-client";
    private const double RunSeconds = 1.2;

    /// <summary>
    /// A 15Hz client against a 60Hz base tick — what the smoke client and the DOTS sample
    /// send at — must still travel at its full speed.
    ///
    /// <para>Without the held-input model the server integrates once per packet, and the
    /// player covers a quarter of the ground: 18 packets x speed/60 instead of
    /// 1.2s x speed. That quarter-speed number is what a running server was measured at,
    /// and it is what this test exists to fail on.</para>
    /// </summary>
    [Fact]
    public async Task AFifteenHertzClient_AgainstASixtyHertzServer_TravelsAtFullSpeed()
    {
        (float travelled, float speed) = await MeasureAsync(Rates(60, 15, 5), sendHz: 15);

        float expected = speed * (float)RunSeconds;
        float packetDriven = speed * 15f * (float)RunSeconds / 60f; // one step per packet

        Assert.True(travelled > (expected + packetDriven) / 2f,
            $"travelled {travelled:F2} in {RunSeconds}s at speed {speed}: expected about " +
            $"{expected:F2}, and {packetDriven:F2} would mean the server integrated once " +
            "per packet rather than holding the direction");

        Assert.InRange(travelled, expected * 0.80f, expected * 1.10f);
    }

    /// <summary>
    /// The same client against a single-rate server — the configuration <c>staging</c>
    /// runs. One packet, one step, unchanged.
    /// </summary>
    [Fact]
    public async Task AFifteenHertzClient_AgainstAFifteenHertzServer_IsUnchanged()
    {
        (float travelled, float speed) = await MeasureAsync(Rates(15, 15, 5), sendHz: 15);

        float expected = speed * (float)RunSeconds;
        Assert.InRange(travelled, expected * 0.80f, expected * 1.10f);
    }

    /// <summary>
    /// A client sending on every base tick — what a predicting client would do — is also
    /// unaffected, because a new input always arrives before the held one is due.
    /// </summary>
    [Fact]
    public async Task ASixtyHertzClient_AgainstASixtyHertzServer_TravelsAtFullSpeed()
    {
        (float travelled, float speed) = await MeasureAsync(Rates(60, 15, 5), sendHz: 60);

        float expected = speed * (float)RunSeconds;
        Assert.InRange(travelled, expected * 0.80f, expected * 1.10f);
    }

    /// <summary>
    /// The same 15 packets a second, delivered in bursts of four rather than evenly — what
    /// TCP batching, a stalled render thread, a client GC pause or a mobile radio waking up
    /// all produce.
    ///
    /// <para>This is the invariant's harder half and it is what #100 was. Per-tick
    /// coalescing turns a burst of four inputs into <b>one</b> movement step, so before the
    /// elapsed-time step the other three took their simulated time with them and a client
    /// whose packets clumped lost most of its travel at an unchanged average send rate:
    /// 2.75 units against 6.00 expected.</para>
    /// </summary>
    /// <remarks>
    /// <para><b><c>burst: 4</c> is load-bearing and must not be lowered.</b> The obvious
    /// reading of this schedule is that it is dangerously tight: a burst of four at 15Hz
    /// idles 264ms, and on the single-rate case below the server's banking cap is
    /// <c>MaxBankedMovementTicks(15) = 4</c> ticks = 266.7ms — 2.7ms of design margin, so
    /// lowering <c>burst</c> looks like free headroom. It is not. What bounds the measured
    /// distance is not the cap but <b>where the last burst lands</b>: <c>MeasureAsync</c>
    /// waits after packet <c>p</c> when <c>p % burst == 0</c>, so the final arrival is at
    /// <c>floor((packets - 1) / burst) * burst * interval</c> — 1056ms at <c>burst: 4</c>
    /// and at <c>burst: 2</c>, but only 990ms at <c>burst: 3</c>, while <c>expected</c> is
    /// a full 1200ms either way. <c>burst: 3</c> therefore shifts the whole distribution
    /// down by one 66.7ms tick and lands <i>under</i> the threshold.</para>
    ///
    /// <para>Measured, 42 runs of each under 8 spinner processes on 12 cores (#175 F1):
    /// <c>burst: 4</c> min 0.930 / median 0.945 (multi-rate) and min 0.888 / median 0.945
    /// (single-rate), <b>0 failures in 42</b>; <c>burst: 3</c> min 0.833 single-rate,
    /// <b>3 failures in 42</b>. See <c>docs/rejected/README.md</c>.</para>
    ///
    /// <para>The 0.85 threshold is not tight either: with banking removed the same two
    /// cases measure 0.278 and 0.388, so the assertion clears the defect it exists for by
    /// 3.1x and 2.2x.</para>
    /// </remarks>
    [Fact]
    public async Task AClientWhosePacketsArriveInBursts_TravelsTheSameDistance()
    {
        (float travelled, float speed) = await MeasureAsync(Rates(60, 15, 5), sendHz: 15, burst: 4);

        float expected = speed * (float)RunSeconds;

        Assert.True(travelled > expected * 0.85f,
            $"bursty client travelled {travelled:F2} against {expected:F2} expected: " +
            "distance is depending on the arrival pattern, not on elapsed time (#100)");
    }

    /// <summary>
    /// The same burst pattern against the single-rate configuration <c>staging</c> runs.
    ///
    /// <para>Worth its own case because this is where the defect was <b>worst</b> — 28% of
    /// the intended distance, against 46% under multi-rate, because the held-input model
    /// happened to cover part of the gap. What closes it is the elapsed-time step, not the
    /// rate configuration.</para>
    ///
    /// <para>This is the thinner of the two cases — the one whose banking cap sits 2.7ms
    /// above the burst gap. <c>burst: 4</c> is still the right value and lowering it makes
    /// this case fail; see the remarks on
    /// <see cref="AClientWhosePacketsArriveInBursts_TravelsTheSameDistance"/>.</para>
    /// </summary>
    [Fact]
    public async Task AClientWhosePacketsArriveInBursts_AgainstASingleRateServer_TravelsTheSameDistance()
    {
        (float travelled, float speed) = await MeasureAsync(Rates(15, 15, 5), sendHz: 15, burst: 4);

        float expected = speed * (float)RunSeconds;

        Assert.True(travelled > expected * 0.85f,
            $"single-rate server: bursty client travelled {travelled:F2} against " +
            $"{expected:F2} expected (#100)");
    }

    /// <summary>
    /// The bound, from the other side: a client that goes quiet and comes back must not be
    /// credited with the whole silence. The held direction survives at most
    /// <see cref="GameConstants.MaxBankedMovementMs"/> of it and then expires.
    ///
    /// <para>What is being bounded changed with the model, and the number did not. Under
    /// banking this asserted that no single step could claim more than the cap; now no step
    /// ever claims more than one tick, so the cap is spent instead on <b>how long a held
    /// direction keeps producing those steps</b>. Either way 1.5s of silence must not turn
    /// into 1.5s of travel, which is what this measures.</para>
    /// </summary>
    [Fact]
    public async Task AClientThatGoesQuietAndReturns_CoastsOnlyForTheSilenceTimeout()
    {
        var rates = Rates(60, 15, 5);
        (float travelled, float speed) =
            await MeasureAsync(rates, sendHz: 15, burst: 1, silentGapMs: 1500);

        float capUnits = speed * global::Shared.GameLogic.Components.GameConstants.MaxBankedMovementMs / 1000f;
        float movingUnits = speed * (float)RunSeconds;

        // The 1.5x absorbs the resume input's own step and a tick of scheduling jitter. The
        // defect it has to separate from is an order of magnitude away: crediting the whole
        // silence would read as movingUnits + 1.5s x speed, i.e. more than twice this bound.
        Assert.True(travelled <= movingUnits + capUnits * 1.5f,
            $"travelled {travelled:F2}: 1.5s of silence appears to have been credited as " +
            $"movement (the coast is bounded at {capUnits:F2} units on top of {movingUnits:F2})");
    }

    /// <summary>
    /// A client whose packets stop arriving without a deadzone input is the genuine
    /// lost-input case, and it is the one the smoothness of the whole model is decided on:
    /// <b>no step is ever larger than a normal one, however long the silence was.</b>
    ///
    /// <para>This assertion is the inverse of the one it replaces, and the inversion is the
    /// point. Banking repaid unheard silence in a single multiplied step — it restored the
    /// right distance and was wrong by every other measure: measured against a live server
    /// it produced a 1.36-unit step where a normal one is 0.083, which a player reads as
    /// the avatar teleporting, and it is what made a client that predicts correctly snap
    /// back on reconciliation. The current model steps once per tick and never repays, so
    /// the largest sampled movement must sit at a normal step and not above it.</para>
    ///
    /// <para>Distance is not abandoned by dropping the repayment; it is defended
    /// structurally instead, by stepping on every tick of the gap rather than by
    /// multiplying one step at the end. <see
    /// cref="AClientWhosePacketsArriveInBursts_TravelsTheSameDistance"/> and its single-rate
    /// twin are what hold that half, and they are what would fail if held movement were
    /// deleted rather than merely unbanked — the concern the old complement assertion
    /// here existed to cover.</para>
    /// </summary>
    /// <remarks>
    /// Parameterised over both configurations for the same reason as the explicit-stop
    /// case: the held-movement pass used to be gated off entirely at a single rate, so a
    /// defect on that path shows at 15/15/5 and hides at 60/15/5.
    /// </remarks>
    [Theory]
    [InlineData(60, 15, 5)]
    [InlineData(15, 15, 5)]
    public async Task AfterUnheardSilence_NoStepIsLargerThanANormalOne(int critical, int world, int background)
    {
        var rates = Rates(critical, world, background);

        (_, float largest, float speed, string diag) = await LargestSingleStepAsync(rates, pauseMs: 800);

        // Positions are read from SNAPSHOTS, which ship at the world rate, so one sample
        // interval already contains WorldEvery base steps. That, not the base step, is the
        // baseline a normal frame is compared against. Samples spanning a lost frame are
        // discarded by SampleStepsAsync rather than rescaled — see there for why.
        float normal = speed / rates.WorldHz;

        // 1.25x, not 1.5x. The tolerance is for one tick of tick-loop lateness, not for a
        // repayment: the defect this replaces measured 4x and up, and anything above a
        // normal step is now a model change rather than jitter.
        Assert.True(largest <= normal * 1.25f,
            $"largest sampled movement after unheard silence was {largest:F4} against a " +
            $"normal {normal:F4} ({largest / normal:F1}x): the gap is being repaid in one " +
            $"step rather than stepped through{diag}");

        // And the player did resume: a server that simply stopped moving a silent client
        // for good would satisfy the bound above trivially.
        Assert.True(largest >= normal * 0.75f,
            $"largest sampled movement after unheard silence was {largest:F4}, well under a " +
            $"normal {normal:F4}: the client never resumed moving at all{diag}");
    }

    /// <summary>
    /// A deliberate stop is not lost input. A player who releases the stick, stands still,
    /// and presses again is owed nothing for the pause — the client told the server it was
    /// not moving.
    ///
    /// <para>Before this, every deliberate stop looked identical to a network stall, so the
    /// first input after it repaid the whole pause. That is the most common thing a player
    /// does, which is why the lurch was constant rather than occasional.</para>
    /// </summary>
    /// <remarks>
    /// Parameterised over both rate configurations deliberately. The held-movement pass is
    /// gated differently when every group runs at one rate — <c>staging</c>'s configuration
    /// — so a fix that lives on that path passes at 60/15/5 and fails at 15/15/5, which is
    /// fixed on develop and broken in production. Anything touching the movement paths must
    /// be checked at both.
    /// </remarks>
    [Theory]
    [InlineData(60, 15, 5)]
    [InlineData(15, 15, 5)]
    public async Task AnExplicitStopIsNotTreatedAsLostTime(int critical, int world, int background)
    {
        var rates = Rates(critical, world, background);

        (_, float largest, float speed, string diag) =
            await LargestSingleStepAsync(rates, pauseMs: 800, stopExplicitly: true);

        // One snapshot interval, as above. A lost frame used to make two consecutive
        // mentions span two steps and land on exactly 2.00x — the same value this rejects —
        // so a busy CI runner reported a repaid pause on a server that had done nothing
        // wrong. Those pairs are now discarded: LargestSingleStepAfterResume keeps only
        // single-interval samples (#154).
        float normal = speed / rates.WorldHz;

        Assert.True(largest < normal * 2f,
            $"largest sampled movement after an explicit stop was {largest:F4} against a " +
            $"normal {normal:F4} ({largest / normal:F1}x): the pause was repaid as travel{diag}");
    }

    // ── Harness ──

    /// <summary>
    /// Move, pause, move again — and return the largest jump between two consecutive
    /// snapshot positions. Deliberately measures the <b>rate</b> rather than the distance:
    /// it is the number a player perceives as smoothness.
    /// </summary>
    /// <param name="stopExplicitly">
    /// When true the client sends a deadzone input before going quiet — it released the
    /// stick, rather than its packets stopping arriving. The server can tell these apart
    /// and must.
    /// </param>
    /// <summary>One recorded movement between two consecutive reports of the player.</summary>
    private readonly record struct StepSample(float Distance, ulong Tick, ulong FrameGap);

    /// <summary>
    /// What the sampler saw, kept so a failure can explain itself.
    ///
    /// <para>These assertions fail on a number without any way to tell a simulation defect
    /// from a measurement artefact, and that cost two CI investigations: a lost frame made
    /// the raw difference land on exactly 2.00x, which is the same value the explicit-stop
    /// threshold rejects. The discard counters are here so the next failure says which it
    /// was instead of leaving it to be re-derived.</para>
    /// </summary>
    private sealed class StepLog
    {
        public readonly List<StepSample> Samples = new();

        /// <summary>Newest base tick seen on any snapshot, for marking the resume.</summary>
        public ulong LatestFrameTick;

        /// <summary>Snapshots whose tick gap showed at least one frame never arrived.</summary>
        public int LostFrames;

        /// <summary>Pairs dropped because they spanned one of those gaps.</summary>
        public int DiscardedPairs;

        /// <summary>
        /// Base tick at which the client started moving again, or 0 before that.
        ///
        /// <para>Samples earlier than this are not evidence about a repaid pause, and
        /// including them is what made this suite flake. Under load the server's own tick
        /// loop runs late, and the elapsed-time step then integrates two ticks of movement
        /// into one — which is #100 working as specified, not a defect. Observed at exactly
        /// 2.00x with <c>lostFrames=0</c>, in the FIRST movement phase, several hundred
        /// milliseconds before the pause the message blamed.</para>
        /// </summary>
        public ulong ResumeTick;

        /// <summary>Largest sampled step at or after <see cref="ResumeTick"/>.</summary>
        public float LargestAfterResume()
        {
            float largest = 0f;
            foreach (var s in Samples)
            {
                if (s.Tick < ResumeTick) continue;
                if (s.Distance > largest) largest = s.Distance;
            }
            return largest;
        }

        /// <summary>
        /// Largest sampled step at or after <see cref="ResumeTick"/>, considering
        /// only single-interval pairs (#154). A pair spanning more than one world
        /// interval is discarded rather than rescaled — it is a scheduling artifact,
        /// not a measurement of a single step, and rescaling hides the repayment
        /// that <c>ASilentClientsRepayment</c> exists to see.
        /// </summary>
        public float LargestSingleStepAfterResume(int worldEvery)
        {
            float largest = 0f;
            foreach (var s in Samples)
            {
                if (s.Tick < ResumeTick) continue;
                if (s.FrameGap > (ulong)worldEvery) continue; // skip multi-interval pairs
                if (s.Distance > largest) largest = s.Distance;
            }
            return largest;
        }

        public string Describe(float largest)
        {
            var top = new List<StepSample>(Samples);
            top.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));

            var sb = new System.Text.StringBuilder();
            int before = 0;
            foreach (var s in Samples) if (s.Tick < ResumeTick) before++;

            sb.Append($" [samples={Samples.Count} beforeResume={before} ")
              .Append($"resumeTick={ResumeTick} lostFrames={LostFrames} ")
              .Append($"discardedPairs={DiscardedPairs} largest={largest:F4} top=");
            for (int i = 0; i < top.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{top[i].Distance:F4}@tick{top[i].Tick}(gap{top[i].FrameGap})");
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    private static async Task<(float largest, float largestSingleStep, float speed, string diag)> LargestSingleStepAsync(
        SimulationRates rates, int pauseMs, bool stopExplicitly = false)
    {
        string userId = $"lurch-{Guid.NewGuid():N}"[..16];
        var options = BuildOptions(rates);

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        try
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();

            await SendAsync(stream, MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) });
            var joinEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(joinEnv);
            Assert.True(WireProtocol.GetPayload<JoinTokenResponse>(joinEnv!).Ok);

            EntitySnapshot spawn = await ReadOwnEntityAsync(stream, userId, cts.Token);
            float speed = spawn.Speed;

            var log = new StepLog();
            var samples = Task.Run(
                () => SampleStepsAsync(stream, userId, log, rates.WorldEvery, cts.Token), cts.Token);

            ulong tick = 0;
            // Move for a while at the world rate.
            for (int i = 0; i < 8; i++)
            {
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = ++tick, MoveX = 1f, MoveY = 0f });
                await Task.Delay(66, cts.Token);
            }

            if (stopExplicitly)
            {
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = ++tick, MoveX = 0f, MoveY = 0f });
            }

            // Stand still, then start again. This is the restart that used to lurch.
            await Task.Delay(pauseMs, cts.Token);

            // Everything sampled from here on is what these tests are about: the restart.
            log.ResumeTick = log.LatestFrameTick;

            for (int i = 0; i < 8; i++)
            {
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = ++tick, MoveX = 1f, MoveY = 0f });
                await Task.Delay(66, cts.Token);
            }

            await Task.Delay(200, cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await samples;

            Assert.NotEmpty(log.Samples);
            float largest = log.LargestAfterResume();
            float largestSingle = log.LargestSingleStepAfterResume(rates.WorldEvery);
            Assert.True(largest > 0f,
                $"no movement was sampled after the restart{log.Describe(largest)}");
            return (largest, largestSingle, speed, log.Describe(largest));
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>
    /// Record the distance between consecutive reported positions, <b>discarding any pair
    /// that spans a snapshot the transport lost.</b>
    ///
    /// <para><b>Why the discard is necessary.</b> The outbound snapshot channel drops the
    /// oldest frame under load. When the dropped frame was one that mentioned the player,
    /// the next mention is two movement steps away from the last one, so the raw difference
    /// reads as a single step of exactly twice the normal size — and "a step several times
    /// the normal size" is precisely what the assertions here treat as the defect. A busy
    /// CI runner therefore produced "the pause was repaid as travel" against a server that
    /// had done nothing wrong. Observed as exactly 2.00x, which is the tell: a timing
    /// artefact would not land on a round multiple.</para>
    ///
    /// <para><b>Why the gap is not simply divided out.</b> That was the first attempt and it
    /// is wrong here. Dividing distance by the tick gap also divides away the thing two of
    /// these tests exist to see: banked repayment puts a whole silent interval's travel into
    /// one tick, and the mention before it is a whole silent interval earlier, so the
    /// quotient comes back at exactly one normal step. It measured the defect as absent.</para>
    ///
    /// <para><b>What distinguishes the two.</b> A drop and a legitimate absence look the
    /// same if only the frames mentioning the player are tracked — but not if <i>every</i>
    /// snapshot's tick is tracked. Snapshots ship at the world rate, so consecutive frames
    /// are <paramref name="worldEvery"/> base ticks apart; a larger jump means a frame never
    /// arrived. That is independent of whether the player was in it, which is what makes it
    /// usable: an entity standing still is absent from deltas while frames keep arriving on
    /// schedule.</para>
    /// </summary>
    /// <param name="worldEvery">
    /// Base ticks between two snapshots, i.e. <c>SimulationRates.WorldEvery</c>. A received
    /// gap wider than this is a lost frame.
    /// </param>
    private static async Task SampleStepsAsync(
        NetworkStream stream, string userId, StepLog log, int worldEvery,
        CancellationToken ct)
    {
        float previous = float.NaN;
        ulong previousTick = 0;
        ulong lastFrameTick = 0;
        bool haveFrame = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var env = await WireProtocol.DecodeAsync(stream, ct);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;

                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);

                // Frame accounting first, and for every snapshot rather than only the ones
                // carrying the player: this is what separates a lost frame from an entity
                // that simply did not move.
                bool lostFrame = haveFrame && msg.Tick > lastFrameTick + (ulong)worldEvery;
                lastFrameTick = msg.Tick;
                log.LatestFrameTick = msg.Tick;
                haveFrame = true;

                if (lostFrame)
                {
                    // Whatever the player did across the missing frames cannot be attributed
                    // to one step, so the next pair starts fresh instead of being measured
                    // against a position from before the gap.
                    log.LostFrames++;
                    if (!float.IsNaN(previous)) log.DiscardedPairs++;
                    previous = float.NaN;
                }

                foreach (var e in msg.Entities)
                {
                    if (e.Id != userId) continue;
                    if (!float.IsNaN(previous))
                    {
                        log.Samples.Add(new StepSample(
                            Math.Abs(e.X - previous), msg.Tick, msg.Tick - previousTick));
                    }
                    previous = e.X;
                    previousTick = msg.Tick;
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (IOException) { /* expected */ }
    }

    private static ServerOptions BuildOptions(SimulationRates rates) => new()
    {
        ServerAddr = ":0",
        ServerId = ServerId,
        MapId = "map_slow_client",
        Mode = "map",
        Transport = TransportKind.Tcp,
        TickRate = rates.CriticalHz,
        SimulationRates = rates,
        Capacity = 4,
        JwtSecret = JwtSecret,
        JoinTokenSecret = JwtSecret,
        SaveInterval = TimeSpan.FromSeconds(30),
        PlayerStore = new MemoryPlayerStore(),
        LoggerFactory = NullLoggerFactory.Instance
    };

    private static SimulationRates Rates(int critical, int world, int background)
    {
        Assert.True(SimulationRates.TryCreate(critical, world, background, out var rates, out string? error), error);
        return rates!;
    }

    /// <summary>
    /// Join, send input at <paramref name="sendHz"/> for <see cref="RunSeconds"/>, and
    /// return how far the server says the player moved along +X, plus its speed.
    /// </summary>
    private static async Task<(float travelled, float speed)> MeasureAsync(
        SimulationRates rates, int sendHz, int burst = 1, int silentGapMs = 0)
    {
        string userId = $"slow-{Guid.NewGuid():N}"[..16];

        var options = new ServerOptions
        {
            ServerAddr = ":0",
            ServerId = ServerId,
            MapId = "map_slow_client",
            Mode = "map",
            Transport = TransportKind.Tcp,
            TickRate = rates.CriticalHz,
            SimulationRates = rates,
            Capacity = 4,
            JwtSecret = JwtSecret,
            JoinTokenSecret = JwtSecret,
            SaveInterval = TimeSpan.FromSeconds(30),
            PlayerStore = new MemoryPlayerStore(),
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        try
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();

            await SendAsync(stream, MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) });

            var joinEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(joinEnv);
            var joinResp = WireProtocol.GetPayload<JoinTokenResponse>(joinEnv!);
            Assert.True(joinResp.Ok, joinResp.Error);
            Assert.Equal((uint)rates.MovementHz, joinResp.TickRate);

            // Spawn state, before any input.
            EntitySnapshot spawn = await ReadOwnEntityAsync(stream, userId, cts.Token);
            float startX = spawn.X;
            float speed = spawn.Speed > 0 ? spawn.Speed : TestHelpers.CreatePlayer("probe", 0, 0).Speed;

            // Send at the requested rate for the measurement window. Snapshots are drained
            // concurrently so the socket's receive buffer cannot fill and stall the writes.
            //
            // Its own cancellation source, so the drain can be stopped while the connection
            // stays usable for the keyframe request that follows.
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var drain = Task.Run(() => DrainAsync(stream, userId, drainCts.Token), drainCts.Token);

            int interval = 1000 / sendHz;
            int packets = (int)(RunSeconds * sendHz);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int p = 1; p <= packets; p++)
            {
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = (ulong)p, MoveX = 1f, MoveY = 0f });

                // In burst mode only every Nth packet is followed by a wait, so N packets
                // land back to back and the rest of the interval is idle. Average rate is
                // identical; arrival pattern is not.
                if (burst > 1 && p % burst != 0) continue;

                int due = p * interval;
                int wait = due - (int)sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, cts.Token);
            }

            if (silentGapMs > 0)
            {
                // Go quiet WITHOUT saying so, then send one more input. This is the
                // lost-packet case: the server cannot tell it apart from a client that
                // stopped, and what it does during the gap is bounded by the silence
                // timeout rather than by anything the client said.
                await Task.Delay(silentGapMs, cts.Token);
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = (ulong)(packets + 1), MoveX = 1f, MoveY = 0f });

                // ...and then say it stopped, so the measurement window closes where the
                // resume input lands instead of staying open for another silence timeout.
                // Without this the trailing wait below is itself coasted through, and the
                // distance carries 200ms of movement that has nothing to do with the gap
                // this case exists to bound.
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = (ulong)(packets + 2), MoveX = 0f, MoveY = 0f });
            }
            else
            {
                // A conforming client says when it stopped. docs/API.md requires the movement
                // vector on every input tick, so releasing the stick produces an explicit
                // zero on the next one, and the server clears the held direction instead of
                // carrying it to the silence timeout.
                //
                // Without this the harness models a client that simply vanishes, and the
                // measurement window then includes however long the server is willing to
                // keep walking someone it has stopped hearing from -- which is a property of
                // the disconnect timeout, not of the send rate this test is about.
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = (ulong)(packets + 1), MoveX = 0f, MoveY = 0f });
            }

            // One world interval past the last packet, so the final held step lands and is
            // included in a snapshot.
            await Task.Delay(200, cts.Token);

            // Stop draining, then ask for a KEYFRAME and read the position out of that.
            //
            // Taking "the newest position seen" is not a measurement of the server: deltas
            // omit unchanged entities and the outbound channel drops the oldest frame under
            // load, so the last frame that happened to arrive can be several ticks stale.
            // The distance then reads short — and short is exactly what these tests fail on,
            // so a dropped tail was indistinguishable from the defect and produced "travelled
            // 3.17 against 6.00 expected" on a server that had moved the full distance.
            //
            // A keyframe carries every entity in the AOI unconditionally, so the position in
            // it is the server's own, not whatever survived the transport.
            drainCts.Cancel();
            await drain;

            await SendResyncAsync(stream);
            EntitySnapshot final = await ReadKeyframeEntityAsync(stream, userId, cts.Token);
            return (final.X - startX, speed);
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>
    /// Read snapshots until cancelled, returning the newest X seen for the player. Deltas
    /// omit unchanged entities, so a frame without our entity is normal and is skipped
    /// rather than treated as an answer.
    /// </summary>
    private static async Task<float> DrainAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        float lastX = float.NaN;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var env = await WireProtocol.DecodeAsync(stream, ct);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;

                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
                foreach (var e in msg.Entities)
                {
                    if (e.Id == userId) lastX = e.X;
                }
            }
        }
        catch (OperationCanceledException) { /* expected: the window closed */ }
        catch (IOException) { /* expected: the socket closed under us */ }

        Assert.False(float.IsNaN(lastX), "the player never appeared in any snapshot");
        return lastX;
    }

    /// <summary>
    /// Read until a <b>keyframe</b> that mentions the player, and return its entity.
    ///
    /// <para>Requires <c>Full</c> rather than accepting any snapshot: a delta arriving
    /// between the resync request and the keyframe answers with whatever changed, which is
    /// the stale reading this exists to avoid.</para>
    /// </summary>
    private static async Task<EntitySnapshot> ReadKeyframeEntityAsync(
        NetworkStream stream, string userId, CancellationToken ct)
    {
        for (int frames = 0; frames < 400; frames++)
        {
            var env = await WireProtocol.DecodeAsync(stream, ct);
            if (env == null) break;
            if ((MsgType)env.Type != MsgType.Snapshot) continue;

            var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
            if (!msg.Full) continue;

            foreach (var e in msg.Entities)
            {
                if (e.Id == userId) return e;
            }
        }
        throw new InvalidOperationException(
            $"player {userId} never appeared in a keyframe after MsgResync");
    }

    private static async Task<EntitySnapshot> ReadOwnEntityAsync(
        NetworkStream stream, string userId, CancellationToken ct)
    {
        for (int frames = 0; frames < 200; frames++)
        {
            var env = await WireProtocol.DecodeAsync(stream, ct);
            if (env == null) break;
            if ((MsgType)env.Type != MsgType.Snapshot) continue;

            var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
            foreach (var e in msg.Entities)
            {
                if (e.Id == userId) return e;
            }
        }
        throw new InvalidOperationException($"player {userId} never appeared in a snapshot");
    }

    private static async Task SendAsync(Stream stream, MsgType type, JoinTokenRequest payload)
        => await WriteFrameAsync(stream, WireProtocol.NewEnvelope(type, payload, WireEncoding.Json));

    private static async Task SendAsync(Stream stream, MsgType type, InputMessage payload)
        => await WriteFrameAsync(stream, WireProtocol.NewEnvelope(type, payload, WireEncoding.Json));

    /// <summary>
    /// Ask for a keyframe. The envelope is built here rather than through
    /// <c>WireProtocol.NewEnvelope</c> because <c>MsgResync</c> has no body the server
    /// reads — <c>GameServer.HandleMessage</c> says so explicitly and only calls
    /// <c>RequestFull()</c> — so there is no payload type to pick, and picking one anyway
    /// would imply the server parses it.
    /// </summary>
    private static async Task SendResyncAsync(Stream stream)
        => await WriteFrameAsync(stream, new GameServer.Net.Envelope
        {
            Type = (byte)MsgType.Resync,
            Payload = Array.Empty<byte>(),
            Encoding = WireEncoding.Json
        });

    private static async Task WriteFrameAsync(Stream stream, GameServer.Net.Envelope env)
    {
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"game server never started listening on :{port}");
    }
}
