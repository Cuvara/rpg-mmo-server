using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Xunit.Abstractions;

namespace GameServer.Tests.Server;

/// <summary>
/// Runs these cases alone. Every assertion here is about wall-clock behaviour — arrival
/// spacing, a coast bounded in milliseconds, distance over a window — and the rest of the
/// suite running beside them is ambient load that moves all of it.
///
/// <para>This is not caution. The first full-suite run of this class measured a
/// <b>12-tick</b> snapshot gap and 18 lost frames where an isolated run measured none, and
/// travel 5.4% short where an isolated run measured 0.2%. Both readings were true; they were
/// readings of a starved tick loop rather than of the link. A test whose answer depends on
/// what else is running is the kind that gets re-run until green and then trusted.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdversityCollection
{
    public const string Name = "network-adversity";
}

/// <summary>
/// The server under a bad network.
///
/// <para><b>Why this exists.</b> Every latency and loss number this project has was taken on
/// loopback on one box, where there is no loss and the physical RTT floor is zero. The
/// behaviours written to survive adversity — held movement's 250ms silence budget, the
/// outbound channel's drop-the-oldest path, the reconnect hold — had therefore never met
/// one. They were all green under conditions that cannot test them.</para>
///
/// <para><b>Where the adversity is injected.</b> Between a real client socket and the real
/// game server listener, by <see cref="AdversityProxy"/>. The server runs its own
/// <c>TcpListener</c> and its real tick loop; the client is a real <see cref="TcpClient"/>
/// speaking the real <c>WireProtocol</c> framing. Nothing is stubbed, so shipping code sits
/// on both sides of the injection point. A hand-written transport returning canned frames
/// would have measured the fixture instead, which is the failure this design exists to
/// avoid.</para>
///
/// <para><b>How a broken result is told from a healthy one.</b> Every case runs against a
/// <b>control arm over the same proxy with a clean link</b>, so the two arms differ in
/// exactly one thing, and every case also carries an <b>absolute</b> bound. The ratio alone
/// is not enough: deleting held movement shortens both arms equally and leaves the ratio at
/// 1.0, so the ratio assertions would pass on a server that had lost three quarters of its
/// movement. Both are asserted, and the mutation table in the pull request records which
/// assertion caught which defect.</para>
///
/// <para><b>Determinism.</b> Jitter is drawn from a seeded generator, the seed is a constant
/// on this class, and it is named in every failure message. All timing is
/// <see cref="Stopwatch"/>-based — this host's wall clock runs 10–17% fast and has been seen
/// stepping backwards, so <c>DateTime.UtcNow</c> is not a usable timing base here.</para>
/// </summary>
[Collection(AdversityCollection.Name)]
public class NetworkAdversityTests
{
    private const string JwtSecret = "adversity-test-secret-32-bytes-aa";
    private const string ServerId = "gs-adversity";

    /// <summary>
    /// The one seed for every jittered arm in this class. Named in failure messages so a
    /// failure is reproducible rather than "it went red once".
    /// </summary>
    private const int Seed = 20260923;

    /// <summary>
    /// A mobile link on a bad day, as one-way numbers: 80ms of base latency with ±60ms of
    /// jitter on top, in both directions. That is a 160ms RTT floor spreading to 40–280ms,
    /// and the upper end deliberately brushes the 250ms held-movement silence budget without
    /// crossing it — the arm that crosses it is a separate, explicitly-failing case.
    /// </summary>
    private const int BaseDelayMs = 80;
    private const int JitterMs = 60;

    private readonly ITestOutputHelper _out;
    public NetworkAdversityTests(ITestOutputHelper o) => _out = o;

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Latency and jitter on the input path
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A player holding one direction travels the same distance over a jittered link as over
    /// a clean one.
    ///
    /// <para><b>The number.</b> Travel distance is defined by the movement model to depend
    /// only on wall-clock time and the entity's speed, "never on how many input packets a
    /// client sends" (<c>MovementSystem</c>), and held movement is the pass that makes that
    /// true when packets clump. A healthy server therefore moves the jittered client within
    /// <see cref="RatioTolerance"/> of the clean one. A server that integrated only on packet
    /// arrival would fall short by whatever fraction of ticks arrived empty — at 15Hz input
    /// against a 60Hz critical group, three ticks in four.</para>
    ///
    /// <para><b>And the absolute bound.</b> The ratio is asserted against a control arm, and
    /// the control arm is asserted against <c>speed × seconds</c>. Without the second
    /// assertion this test passes with held movement deleted, because both arms shrink
    /// together.</para>
    /// </summary>
    [Fact]
    public async Task MovementDistanceSurvivesJitter()
    {
        var clean = await MeasureTravelAsync(AdversityProxy.Profile.Clean(Seed), nameof(MovementDistanceSurvivesJitter) + ":clean");
        var jittered = await MeasureTravelAsync(AdversityProxy.Profile.Symmetric(BaseDelayMs, JitterMs, Seed), nameof(MovementDistanceSurvivesJitter) + ":jittered");

        _out.WriteLine(clean.ToString());
        _out.WriteLine(jittered.ToString());

        // The absolute arm first: a ratio between two equally-broken arms is 1.0.
        double owed = clean.Speed * MoveSeconds;
        Assert.True(clean.Distance >= owed * (1 - AbsoluteTolerance),
            $"the CONTROL arm travelled {clean.Distance:F3} against {owed:F3} owed " +
            $"({clean.Distance / owed:P1} of it) over a clean link. The jittered comparison " +
            "below is meaningless until this holds: two arms that have both lost their " +
            $"movement have a ratio of 1.0. {clean}");

        double ratio = jittered.Distance / clean.Distance;
        Assert.True(Math.Abs(ratio - 1.0) <= RatioTolerance,
            $"a client on a {BaseDelayMs}±{JitterMs}ms link travelled {jittered.Distance:F3} " +
            $"against {clean.Distance:F3} on a clean one ({ratio:P1}), outside ±" +
            $"{RatioTolerance:P0}. Distance must depend on elapsed time and speed, not on " +
            $"when packets landed — held movement is the pass that makes that true. seed={Seed}. " +
            $"clean: {clean} | jittered: {jittered}");
    }

    /// <summary>
    /// The instrument proof, and the boundary of what the server promises.
    ///
    /// <para>A link that goes dark for longer than the 250ms silence budget <b>must</b> cost
    /// the player movement — the hold is bounded on purpose, so that a client the server has
    /// stopped hearing from coasts rather than drifting forever. This case asserts the
    /// shortfall happens, which is what makes <see cref="MovementDistanceSurvivesJitter"/>
    /// evidence: a measurement that cannot detect a shortfall is not a measurement that a
    /// shortfall did not occur.</para>
    ///
    /// <para><b>The number.</b> A blackout of <see cref="BlackoutMs"/> costs roughly
    /// <c>(blackout − 250ms) × speed</c>. The assertion is loose — at least half of that —
    /// because the blackout boundary lands on an arbitrary tick phase; it is a detection
    /// threshold, not a model of the coast.</para>
    /// </summary>
    [Fact]
    public async Task UpstreamBlackoutBeyondTheSilenceBudgetCostsMovement()
    {
        var clean = await MeasureTravelAsync(AdversityProxy.Profile.Clean(Seed), "blackout:clean");
        var dark = await MeasureTravelAsync(
            AdversityProxy.Profile.Clean(Seed), "blackout:dark", upstreamBlackoutMs: BlackoutMs);

        _out.WriteLine(clean.ToString());
        _out.WriteLine(dark.ToString());

        // What the bounded coast is expected to cost: the part of the silence past the budget.
        double owedLoss = clean.Speed * (BlackoutMs - HeldSilenceBudgetMs) / 1000.0;
        double observedLoss = clean.Distance - dark.Distance;

        Assert.True(observedLoss >= owedLoss * 0.5,
            $"a {BlackoutMs}ms upstream blackout cost {observedLoss:F3} units where the " +
            $"{HeldSilenceBudgetMs}ms silence budget leaves {owedLoss:F3} unpaid. Either the " +
            "hold is unbounded — a client whose packets stop is being walked forever — or " +
            $"this measurement cannot see a shortfall, which would make " +
            $"{nameof(MovementDistanceSurvivesJitter)} vacuous. seed={Seed}. " +
            $"clean: {clean} | dark: {dark}");

        // And the coast did happen: a server that froze the moment a packet was late would
        // lose the whole blackout, not the part past the budget.
        double wholeBlackout = clean.Speed * BlackoutMs / 1000.0;
        Assert.True(observedLoss <= wholeBlackout * 0.95,
            $"a {BlackoutMs}ms upstream blackout cost {observedLoss:F3} units, which is " +
            $"essentially the whole blackout ({wholeBlackout:F3}). The held direction is " +
            $"supposed to coast for {HeldSilenceBudgetMs}ms before expiring; this reads like " +
            $"it is not coasting at all. seed={Seed}. clean: {clean} | dark: {dark}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Latency and jitter on the snapshot path
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot cadence under a jittered downstream.
    ///
    /// <para><b>The numbers.</b> Two of them, and they answer different questions.
    /// <list type="bullet">
    /// <item><b>Tick gaps are all 4.</b> Ticks are stamped by the server, so a gap of 8 is a
    /// snapshot that never arrived. Jitter changes <i>when</i> a frame lands, not whether it
    /// does, because the transport is a reliable ordered stream — so a gap other than 4 here
    /// is frame loss, not lateness.</item>
    /// <item><b>p99 inter-arrival stays inside the client's interpolation budget.</b> The
    /// Unity client renders from a 100ms target delay plus at most 50ms of extrapolation, so
    /// 150ms is the longest arrival gap it can cover without the entity freezing. That bound
    /// lives in the other repository and neither build can see the other, which is exactly
    /// why it is asserted here.</item>
    /// </list></para>
    ///
    /// <para>The clean arm is run and reported alongside, so a jittered p99 has something to
    /// be a number <i>about</i>.</para>
    /// </summary>
    [Fact]
    public async Task SnapshotCadenceSurvivesDownstreamJitter()
    {
        var clean = await MeasureCadenceAsync(AdversityProxy.Profile.Clean(Seed), "cadence:clean");
        var jittered = await MeasureCadenceAsync(
            new AdversityProxy.Profile(AdversityProxy.Link.Clean, new AdversityProxy.Link(BaseDelayMs, JitterMs), Seed),
            "cadence:jittered");

        _out.WriteLine(clean.ToString());
        _out.WriteLine(jittered.ToString());

        foreach (var (name, r) in new[] { ("clean", clean), ("jittered", jittered) })
        {
            Assert.True(r.Snapshots > 0, $"the {name} arm decoded no snapshots at all: {r}");

            // Not "every gap is 4". A full-suite run measured `4x88 8x1` — one frame lost to
            // a starved tick loop, on a link with no loss in it — and an all-or-nothing
            // assertion on that is an assertion about this box's spare capacity. The bound is
            // a rate: one stray gap is tolerated, a pattern is not. The mutation that halves
            // the cadence produces 44 bad gaps out of 44 and is still killed outright.
            int allowed = Math.Max(1, (int)(r.TotalGaps * 0.02));
            Assert.True(r.NonFourGaps <= allowed,
                $"the {name} arm saw {r.NonFourGaps} snapshot tick gaps that were not 4, out of " +
                $"{r.TotalGaps} ({r.Gaps}), above the {allowed} tolerated. The server stamps the " +
                "tick, so a gap of 8 is a snapshot that never reached the socket. A reliable " +
                "ordered transport does not lose one to jitter, so a pattern of them is frame " +
                $"loss on the server's outbound path. seed={Seed}. {r}");

            Assert.InRange(r.Rate, 15.0 - 1.0 / WindowSeconds, 15.0 + 1.0 / WindowSeconds);

            // Jitter spreads arrivals around the send period; it must not move the period
            // itself. A median that has shifted is a rate change wearing jitter's clothes.
            double nominal = 1000.0 / 15.0;
            Assert.True(Math.Abs(r.MedianInterArrivalMs - nominal) <= nominal * 0.15,
                $"the {name} arm's median snapshot spacing was {r.MedianInterArrivalMs:F1}ms " +
                $"against a {nominal:F1}ms send period. Jitter spreads arrivals around the " +
                $"period; it does not move the period. seed={Seed}. {r}");
        }

        // The server's own contribution to the spread, isolated from the link's.
        //
        // The naive assertion here — "p99 stays inside the client's interpolation budget" —
        // is a test of the jitter CONSTANT, not of the server: at ±60ms one-way, arrivals
        // can legitimately be 66.7 + 120 = 187ms apart whatever the server does, and the
        // first run of this test duly failed at 165.3ms having found nothing. What the
        // server owes is that it adds no spread of its own on top of the link's.
        double linkBudget = 2 * JitterMs;
        double excess = jittered.P99InterArrivalMs - clean.P99InterArrivalMs;
        Assert.True(excess <= linkBudget + SpreadSlackMs,
            $"p99 snapshot spacing went from {clean.P99InterArrivalMs:F1}ms on a clean link to " +
            $"{jittered.P99InterArrivalMs:F1}ms on a {BaseDelayMs}±{JitterMs}ms one, an excess of " +
            $"{excess:F1}ms against the {linkBudget:F0}ms the link itself can account for " +
            $"(+{SpreadSlackMs:F0}ms slack). The server is adding spread of its own — batching, " +
            $"a stalled write or a coalesced send — on top of the network's. seed={Seed}. " +
            $"clean: {clean} | jittered: {jittered}");

        // And the server on its own fits inside what the client can render through. This is
        // the absolute arm: the excess bound above is satisfied by two equally-bad arms.
        Assert.True(clean.P99InterArrivalMs <= ClientInterpolationBudgetMs,
            $"p99 snapshot spacing on a CLEAN link was {clean.P99InterArrivalMs:F1}ms, past the " +
            $"{ClientInterpolationBudgetMs}ms the client can cover (100ms target delay + 50ms " +
            "max extrapolation). With no network adversity at all the server is already " +
            $"outside the budget a remote entity is interpolated through. {clean}");

        // Reported, not asserted: how much one-way jitter the client's budget actually
        // tolerates. 150ms of cover against a 66.7ms send period leaves ~83ms of extra
        // spread, so roughly ±41ms one way — and the ±60ms arm above measured 165.3ms, past
        // it. That is a statement about the link a deployment may run over, not a defect in
        // the server, and it is recorded in backend/docs/MEASUREMENT.md rather than asserted
        // here, where it would fail on the jitter constant instead of on the code.
        _out.WriteLine(
            $"[budget] clientBudget={ClientInterpolationBudgetMs}ms sendPeriod={1000.0 / 15:F1}ms " +
            $"=> tolerable one-way jitter ~±{(ClientInterpolationBudgetMs - 1000.0 / 15) / 2:F0}ms; " +
            $"measured p99 at ±{JitterMs}ms was {jittered.P99InterArrivalMs:F1}ms");
    }

    /// <summary>
    /// A downstream that goes dark for seconds and comes back. Does the server keep
    /// simulating a player whose snapshots it cannot deliver, and does the client get the
    /// world back afterwards?
    ///
    /// <para><b>The measurement is taken across the blackout, inside one arm.</b> The obvious
    /// design — compare the stalled run's total distance against a clean run's — was tried
    /// and is not sound here: ambient load moves the two runs by different amounts. An
    /// isolated run put them 0.2% apart and a full-suite run put them 5.4% apart, on the same
    /// binary. So the assertion is on <see cref="Travel.GainAcrossStall"/>: the player's
    /// position sampled the instant the link goes dark, against the same reading once the
    /// buffered burst has drained. Nothing outside the blackout enters it.</para>
    ///
    /// <para><b>The numbers, in order.</b>
    /// <list type="number">
    /// <item><b>The client really did see a gap of at least 80% of the blackout</b>, measured
    /// at the socket with a <see cref="Stopwatch"/>, with a clean arm that must not. Asserted
    /// first: without it everything below is asserted about a run in which nothing happened,
    /// which is the empty result that reads as good news.</item>
    /// <item><b>The player advanced by the blackout's worth of movement, and no more.</b>
    /// Lower bound <c>speed × blackout × 0.8</c> — a server that froze a client it could not
    /// write to scores zero. Upper bound <c>speed × (blackout + settle) × 1.2</c> — a server
    /// that repaid the gap in one catch-up step would overshoot, which is the #100 lurch the
    /// movement model was rebuilt to remove.</item>
    /// </list></para>
    ///
    /// <para><b>What is deliberately NOT asserted: how many frames were lost.</b> It depends
    /// on the load. Idle, a 4s blackout drops nothing at all — every tick gap stays 4, because
    /// loopback send buffers absorb the backlog and <c>Connection._sendChannel</c>'s 64-frame
    /// drop-the-oldest path is never reached (a mutation shrinking that channel to <b>4</b>
    /// frames still dropped nothing). Under a full-suite run the same code dropped 18 frames
    /// and opened a 12-tick gap. Both readings are true, and an assertion on either is an
    /// assertion about this box's spare capacity. The counts are printed instead.</para>
    /// </summary>
    [Fact]
    public async Task DownstreamBlackoutDoesNotStopTheSimulation()
    {
        // A NARROW link, not merely a slow one, so the claim is not an artefact of generous
        // buffers. Both arms run over the same link.
        var link = AdversityProxy.Profile.Clean(Seed, socketBufferBytes: NarrowLinkBytes);
        var clean = await MeasureTravelAsync(link, "stall:clean", moveSeconds: StallRunSeconds);
        var stalled = await MeasureTravelAsync(
            link, "stall:stalled", downstreamBlackoutMs: LongBlackoutMs, moveSeconds: StallRunSeconds);

        _out.WriteLine(clean.ToString());
        _out.WriteLine(stalled.ToString());

        // 1. The adversity reached the client. Measured at the socket, not asked of the
        //    proxy: a relay that believed it was blacking out while bytes flowed anyway would
        //    report a blackout and change nothing.
        Assert.True(stalled.MaxArrivalGapMs >= LongBlackoutMs * 0.8,
            $"a {LongBlackoutMs}ms downstream blackout produced a largest arrival gap of only " +
            $"{stalled.MaxArrivalGapMs:F0}ms at the client socket, so the link did not actually " +
            "go dark. Everything below would then be asserted about a run containing no " +
            $"adversity. The proxy held {stalled.BlackoutMsHeld:F0}ms. seed={Seed}. {stalled}");

        Assert.True(clean.MaxArrivalGapMs < LongBlackoutMs * 0.5,
            $"the CONTROL arm saw a {clean.MaxArrivalGapMs:F0}ms arrival gap on a link with no " +
            $"blackout in it. The two arms are then not differing in one thing. {clean}");

        Assert.False(float.IsNaN(stalled.GainAcrossStall),
            $"the player was never seen in a snapshot on one side of the blackout, so there is " +
            $"no gain to measure. {stalled}");

        // 2. The simulation ran throughout, and did not repay the gap in one step.
        double owed = stalled.Speed * LongBlackoutMs / 1000.0;
        double ceiling = stalled.Speed * (LongBlackoutMs + StallSettleMs) / 1000.0;

        Assert.True(stalled.GainAcrossStall >= owed * 0.8,
            $"across a {LongBlackoutMs}ms downstream blackout the server moved the player " +
            $"{stalled.GainAcrossStall:F3} units against {owed:F3} owed. Input flowed the whole " +
            "time and only the snapshots were dark, so a short reading means the server stopped " +
            $"simulating a player whose frames it could not deliver. seed={Seed}. {stalled}");

        Assert.True(stalled.GainAcrossStall <= ceiling * 1.2,
            $"across a {LongBlackoutMs}ms downstream blackout the player advanced " +
            $"{stalled.GainAcrossStall:F3} units where {ceiling:F3} covers the blackout and the " +
            $"{StallSettleMs}ms settle. An overshoot is the gap being repaid in one catch-up " +
            $"step — the #100 lurch, which a predicting client is snapped back by. seed={Seed}. {stalled}");

        _out.WriteLine(
            $"[loss] clean maxTickGap={clean.MaxTickGap} snapshots={clean.Snapshots} | " +
            $"stalled maxTickGap={stalled.MaxTickGap} snapshots={stalled.Snapshots} " +
            $"(frame loss under a stall is load-dependent and is reported, not asserted)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Blackout, reset and reconnect
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A link that goes dark and then resets is not a disconnect the player asked for. The
    /// server holds the entity for <c>HoldTtl</c>; reconnecting inside that window must find
    /// the same entity, still in the world, where it left it.
    ///
    /// <para><b>What is measured, and why position alone is not enough.</b> The first version
    /// of this test asserted that a player rejoining <i>after</i> the hold window expired
    /// would be back at spawn, and it failed: they came back at 8.500, the position they had
    /// left. That reading was correct and the assertion was wrong — the player store persists
    /// position on disconnect and restores it on rejoin, so <b>a re-created entity and a held
    /// one land in the same place</b>. Position cannot tell them apart, which is the exact
    /// shape of a test that passes on a broken implementation.</para>
    ///
    /// <para>The discriminator is therefore <c>EntityCount</c>, polled across the gap:
    /// <list type="bullet">
    /// <item><b>Inside the hold window</b> the count must <b>never reach zero</b> — the entity
    /// survived a dark link and an RST — and the rejoining client must be handed it back at
    /// the position it had.</item>
    /// <item><b>Past the hold window</b> the count must <b>reach zero</b>, and the player must
    /// still come back where they were, via the store. This arm is what makes the other one
    /// evidence: a server that never removes anything passes the first arm trivially, and
    /// #401 is precisely that leak.</item>
    /// </list></para>
    ///
    /// <para>Existing coverage (<c>EntityLifecycleTests.ReconnectWithinHold_KeepsTheEntity</c>)
    /// closes the socket politely and asserts the hold count. This is the case a polite close
    /// cannot reach: the link goes dark first, so nothing is said and the server does not
    /// learn of the disconnect until TCP itself gives up.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlackoutThenResetThenReconnect(bool insideHoldWindow)
    {
        var hold = TimeSpan.FromMilliseconds(1200);
        using var metrics = new GameMetrics("map_adversity", $"test.{Guid.NewGuid():N}");
        var (server, runTask, port, cts) = await StartServerAsync(hold, metrics);

        string userId = $"adv{Guid.NewGuid():N}"[..14];
        float spawnX, atCut, speed;

        try
        {
            await using (var proxy = AdversityProxy.Start(port, AdversityProxy.Profile.Clean(Seed)))
            {
                using var client = new TcpClient { NoDelay = true };
                await ConnectWithRetryAsync(client, proxy.Port);
                var stream = client.GetStream();
                await JoinAsync(stream, userId, cts.Token);

                var spawn = await ReadOwnEntityAsync(stream, userId, cts.Token);
                spawnX = spawn.X;
                speed = spawn.Speed;

                // Walk away from spawn, so "where they were" is a position and not the
                // origin, then stop EXPLICITLY. Without the stop the held direction coasts
                // for its 250ms budget after the last packet and the position at the cut is
                // whatever the coast reached — the first run measured 6.333 at the keyframe
                // and 8.500 on return, a gap that is the coast rather than a defect.
                // Drained CONCURRENTLY while walking. Without this the receive buffer fills
                // with 1.5s of snapshots and the first keyframe read afterwards comes off the
                // FRONT of that backlog: the first run of this test read 3.333 for a player
                // the server had already walked to 7.5, and the 4.25-unit gap looked exactly
                // like an entity being rebuilt at the wrong position.
                using var walkDrain = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                var walkDrained = Task.Run(() => DrainTickGapsAsync(stream, walkDrain.Token), walkDrain.Token);

                var walk = Stopwatch.StartNew();
                ulong t = 0;
                while (walk.ElapsedMilliseconds < 1500)
                {
                    await SendInputAsync(stream, new InputMessage { Tick = ++t, MoveX = 1f, MoveY = 0f }, cts.Token);
                    await Task.Delay(66, cts.Token);
                }
                await SendInputAsync(stream, new InputMessage { Tick = ++t, MoveX = 0f, MoveY = 0f }, cts.Token);
                await Task.Delay(300, cts.Token);

                walkDrain.Cancel();
                await walkDrained;

                await SendResyncAsync(stream, cts.Token);
                atCut = (await ReadKeyframeEntityAsync(stream, userId, cts.Token)).X;

                Assert.True(atCut - spawnX > 1.0f,
                    $"the player only moved {atCut - spawnX:F3} units before the cut, so a " +
                    "restored position is indistinguishable from a spawn. The position " +
                    "assertions below would then hold whatever the server did.");

                // Dark first, then the reset. The order is the point: a link that stopped
                // carrying packets and only later told anyone is what a polite close cannot
                // model, and the server cannot tell it from a client that went quiet.
                proxy.Blackout(true);
                await Task.Delay(200, cts.Token);
                proxy.Cut();
            }

            // Poll across the gap. Whether the count ever reached zero is the whole
            // measurement — a single reading taken at rejoin time cannot distinguish a held
            // entity from one removed and reloaded a moment earlier.
            int waitMs = insideHoldWindow ? 300 : (int)hold.TotalMilliseconds + 2500;
            bool sawZero = false;
            var gap = Stopwatch.StartNew();
            while (gap.ElapsedMilliseconds < waitMs)
            {
                if (server.EntityCount == 0) sawZero = true;
                await Task.Delay(20, cts.Token);
            }

            using var rejoin = new TcpClient { NoDelay = true };
            await ConnectWithRetryAsync(rejoin, port);
            var stream2 = rejoin.GetStream();
            await JoinAsync(stream2, userId, cts.Token);

            await SendResyncAsync(stream2, cts.Token);
            var recovered = await ReadKeyframeEntityAsync(stream2, userId, cts.Token);

            _out.WriteLine(
                $"[reconnect insideHold={insideHoldWindow}] spawnX={spawnX:F3} atCut={atCut:F3} " +
                $"recovered={recovered.X:F3} sawEntityCountZero={sawZero} " +
                $"hold={hold.TotalMilliseconds}ms holds={server.PendingHolds} entities={server.EntityCount}");

            if (insideHoldWindow)
            {
                Assert.False(sawZero,
                    $"the entity was removed from the world during a {waitMs}ms gap inside a " +
                    $"{hold.TotalMilliseconds}ms hold window. A dark link and an RST are not a " +
                    "player leaving, and the hold exists so that they are not treated as one. " +
                    "The position check below cannot see this: the store restores the same " +
                    "coordinates either way.");
            }
            else
            {
                Assert.True(sawZero,
                    $"the entity was still in the world {waitMs}ms after the cut, past a " +
                    $"{hold.TotalMilliseconds}ms hold TTL (holds={server.PendingHolds}, " +
                    $"entities={server.EntityCount}). Nothing else removes a player entity, so " +
                    "one that outlives its hold is leaked for the life of the process and is " +
                    "still AOI-scanned and delta-encoded every tick (#401). This arm is also " +
                    "what makes the held arm evidence rather than a tautology.");
            }

            // One world interval of sampling slack, either side. The player was stopped
            // before the cut, so nothing should have moved at all.
            float slack = (float)(speed * (1000.0 / 15.0) / 1000.0) + 0.05f;
            Assert.True(Math.Abs(recovered.X - atCut) <= slack,
                $"the player left at {atCut:F3} and came back at {recovered.X:F3} " +
                $"(spawn {spawnX:F3}, slack {slack:F3}). Whether the entity was held or " +
                "restored from the store, a client that reconnects must be given the world " +
                "back as it was.");
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Constants the assertions are written against
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Seconds of held input in a travel measurement.</summary>
    private const double MoveSeconds = 6.0;

    /// <summary>Seconds of snapshot arrivals measured, after the warmup.</summary>
    private const double WindowSeconds = 6.0;

    /// <summary>Seconds discarded before a cadence window: the join keyframe and phase-in.</summary>
    private const double WarmupSeconds = 2.0;

    /// <summary>
    /// How far two arms may differ. Jitter lands on the first and last packet of the window
    /// as well as the middle ones, so up to <c>2 × JitterMs</c> of the window is genuinely
    /// unbounded by anything the server does — 120ms against 6000ms, or 2%. 5% leaves room
    /// for the tick phase on top, and is still far inside the 75% a lost held-movement pass
    /// costs a 15Hz client on a 60Hz group.
    /// </summary>
    private const double RatioTolerance = 0.05;

    /// <summary>How far the control arm may fall short of <c>speed × seconds</c>.</summary>
    private const double AbsoluteTolerance = 0.10;

    /// <summary>
    /// <c>GameConstants.MaxBankedMovementMs</c>: how long the server keeps moving a player on
    /// information it no longer has. Duplicated as a literal deliberately — if the constant
    /// moves, these assertions must be re-derived rather than silently following it.
    /// </summary>
    private const double HeldSilenceBudgetMs = 250;

    /// <summary>
    /// The client's rendering budget for a gap in arrivals: 100ms target delay plus 50ms of
    /// maximum extrapolation, both from the netcode package in the client repository. Past
    /// this a remote entity freezes rather than interpolating through.
    /// </summary>
    private const double ClientInterpolationBudgetMs = 150;

    /// <summary>How long a blackout lasts in the cases that use one.</summary>
    private const int BlackoutMs = 1200;

    /// <summary>
    /// The downstream blackout. Long enough that the gap it opens at the client is
    /// unmistakable — 60 snapshot intervals — and short enough that the case does not cost
    /// the suite half a minute per arm.
    /// </summary>
    private const int LongBlackoutMs = 4000;

    /// <summary>Run length for the stall case: long enough to bracket the blackout.</summary>
    private const double StallRunSeconds = 9.0;

    /// <summary>
    /// How long the far-side sample waits after the link comes back, for the buffered burst
    /// to drain. It is counted into the upper bound on the gain across the stall.
    /// </summary>
    private const int StallSettleMs = 600;

    /// <summary>
    /// Socket buffer size that makes the link narrow rather than merely slow. It bounds what
    /// the relay's own sockets hold; it cannot bound the server's send buffer, which loopback
    /// sizes in megabytes and which is why a stall here is absorbed rather than lost.
    /// </summary>
    private const int NarrowLinkBytes = 4096;

    /// <summary>
    /// Slack on the spread comparison, for the tick phase and for scheduler lateness on a
    /// loaded box. Small against the 120ms the link itself accounts for, so the assertion
    /// still fails on a server that batches.
    /// </summary>
    private const double SpreadSlackMs = 40;

    // ─────────────────────────────────────────────────────────────────────────
    // Measurement
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>What one travel measurement saw.</summary>
    private readonly record struct Travel(
        string Arm, float Distance, float Speed, ulong MaxTickGap, long Snapshots,
        double MaxArrivalGapMs, double BlackoutMsHeld, float XBeforeStall, float XAfterStall, string Link)
    {
        /// <summary>How far the server moved the player across the blackout itself.</summary>
        internal float GainAcrossStall => XAfterStall - XBeforeStall;

        public override string ToString() =>
            $"[{Arm}] distance={Distance:F3} speed={Speed:F3} maxTickGap={MaxTickGap} " +
            $"snapshots={Snapshots} maxArrivalGap={MaxArrivalGapMs:F0}ms " +
            $"proxyHeld={BlackoutMsHeld:F0}ms acrossStall={XBeforeStall:F3}->{XAfterStall:F3} " +
            $"link({Link})";
    }

    /// <summary>
    /// Hold one direction for <see cref="MoveSeconds"/> through the proxy and report how far
    /// the server says the player got.
    ///
    /// <para>The distance is read out of a <b>keyframe</b> requested after the window closes,
    /// never out of the newest delta that happened to arrive. Deltas omit unchanged entities
    /// and the outbound channel drops the oldest frame under load, so the last surviving
    /// frame can be several ticks stale — and short is exactly what these cases fail on, which
    /// would make a dropped tail indistinguishable from the defect.</para>
    /// </summary>
    private async Task<Travel> MeasureTravelAsync(
        AdversityProxy.Profile profile, string arm, int upstreamBlackoutMs = 0, int downstreamBlackoutMs = 0,
        double? moveSeconds = null)
    {
        using var metrics = new GameMetrics("map_adversity", $"test.{Guid.NewGuid():N}");
        var (server, runTask, port, cts) = await StartServerAsync(TimeSpan.FromSeconds(30), metrics);
        await using var proxy = AdversityProxy.Start(port, profile);

        string userId = $"adv{Guid.NewGuid():N}"[..14];
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await ConnectWithRetryAsync(client, proxy.Port);
            var stream = client.GetStream();
            await JoinAsync(stream, userId, cts.Token);

            var spawn = await ReadOwnEntityAsync(stream, userId, cts.Token);
            float startX = spawn.X;
            float speed = spawn.Speed;

            // Snapshots are drained concurrently, so the receive buffer cannot fill and stall
            // the writes — except when a downstream blackout is deliberately doing that.
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var probe = new SnapshotProbe();
            var drain = Task.Run(() => DrainTickGapsAsync(stream, drainCts.Token, userId, probe), drainCts.Token);

            double seconds = moveSeconds ?? MoveSeconds;
            int packets = (int)(seconds * 15);
            var sw = Stopwatch.StartNew();
            int blackoutAt = packets / 3;     // a third of the way in, so movement brackets it
            bool blackoutDone = false;
            float xBeforeStall = float.NaN, xAfterStall = float.NaN;
            Task? blackoutTask = null;

            for (int p = 1; p <= packets; p++)
            {
                await SendInputAsync(stream, new InputMessage { Tick = (ulong)p, MoveX = 1f, MoveY = 0f }, cts.Token);

                if (!blackoutDone && p == blackoutAt && (upstreamBlackoutMs > 0 || downstreamBlackoutMs > 0))
                {
                    blackoutDone = true;

                    // Sampled on both sides of the blackout, so "did the server keep
                    // simulating a player it could not write to" is answered WITHIN this arm
                    // and not by comparison with another run. Ambient load moves both arms of
                    // a cross-run comparison by different amounts — measured, a full-suite run
                    // put the two 5.4% apart where an isolated run put them 0.2% apart — and
                    // a gain measured across the stall itself is immune to that.
                    xBeforeStall = probe.LastX;
                    proxy.Blackout(toServer: upstreamBlackoutMs > 0, toClient: downstreamBlackoutMs > 0);
                    blackoutTask = Task.Run(async () =>
                    {
                        await Task.Delay(Math.Max(upstreamBlackoutMs, downstreamBlackoutMs));
                        proxy.Blackout(false, false);
                        // Let the buffered burst drain before reading the far side.
                        await Task.Delay(StallSettleMs);
                        xAfterStall = probe.LastX;
                    });
                }

                int due = p * (1000 / 15);
                int wait = due - (int)sw.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, cts.Token);
            }

            // A conforming client says when it stopped: docs/API.md requires the vector on
            // every input tick, so releasing produces an explicit zero and the server clears
            // the held direction instead of coasting to the silence timeout. Without it the
            // window would include the coast, which is a property of the timeout rather than
            // of the link under test.
            if (blackoutTask != null) await blackoutTask;

            await SendInputAsync(stream, new InputMessage { Tick = (ulong)(packets + 1), MoveX = 0f, MoveY = 0f }, cts.Token);

            // Long enough for the stop to arrive over the worst link here and for the final
            // held step to land in a snapshot.
            await Task.Delay(BaseDelayMs + JitterMs + 400, cts.Token);

            drainCts.Cancel();
            var (maxGap, snaps, maxArrivalGapMs) = await drain;

            await SendResyncAsync(stream, cts.Token);
            var final = await ReadKeyframeEntityAsync(stream, userId, cts.Token);

            return new Travel(
                arm, final.X - startX, speed, maxGap, snaps, maxArrivalGapMs,
                proxy.Stats.BlackoutMs, xBeforeStall, xAfterStall, proxy.Config.ToString());
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    /// <summary>What one cadence measurement saw at the socket.</summary>
    private readonly record struct Cadence(
        string Arm, double Window, long Snapshots, double Rate, double TickHz,
        double MedianInterArrivalMs, double P99InterArrivalMs, string Gaps, int NonFourGaps, int TotalGaps, string Link)
    {
        public override string ToString() =>
            $"[{Arm}] window={Window:F2}s snapshots={Snapshots} rate={Rate:F3}/s tickHz={TickHz:F2} " +
            $"medIA={MedianInterArrivalMs:F1}ms p99IA={P99InterArrivalMs:F1}ms gaps=[{Gaps}] " +
            $"nonFour={NonFourGaps}/{TotalGaps} link({Link})";
    }

    /// <summary>
    /// Read snapshots through the proxy for <see cref="WindowSeconds"/> after a warmup and
    /// report the cadence, the tick gaps and the arrival spread.
    /// </summary>
    private async Task<Cadence> MeasureCadenceAsync(AdversityProxy.Profile profile, string arm)
    {
        using var metrics = new GameMetrics("map_adversity", $"test.{Guid.NewGuid():N}");
        var (server, runTask, port, cts) = await StartServerAsync(TimeSpan.FromSeconds(30), metrics);
        await using var proxy = AdversityProxy.Start(port, profile);

        string userId = $"cad{Guid.NewGuid():N}"[..14];
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await ConnectWithRetryAsync(client, proxy.Port);
            var stream = client.GetStream();
            await JoinAsync(stream, userId, cts.Token);

            var sw = Stopwatch.StartNew();
            var lastInput = Stopwatch.StartNew();
            ulong inputTick = 0;
            long snapsInWindow = 0;
            double windowStart = -1, windowEnd = 0, prevArr = -1;
            ulong firstTick = 0, lastTick = 0, prevTick = 0;
            var gaps = new Dictionary<ulong, int>();
            var interArrival = new List<double>();

            while (!cts.Token.IsCancellationRequested)
            {
                if (lastInput.ElapsedMilliseconds >= 66)
                {
                    lastInput.Restart();
                    await SendInputAsync(stream, new InputMessage { Tick = ++inputTick, MoveX = 1f, MoveY = 0f }, cts.Token);
                }

                var env = await WireProtocol.DecodeAsync(stream, cts.Token);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;
                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);

                double now = sw.Elapsed.TotalSeconds;
                if (now < WarmupSeconds) { prevTick = msg.Tick; prevArr = now; continue; }
                if (windowStart < 0) { windowStart = now; firstTick = msg.Tick; prevTick = msg.Tick; prevArr = now; continue; }

                snapsInWindow++;
                windowEnd = now;
                lastTick = msg.Tick;
                if (now - windowStart >= WindowSeconds) break;

                ulong g = msg.Tick - prevTick;
                gaps[g] = gaps.TryGetValue(g, out int n) ? n + 1 : 1;
                interArrival.Add(now - prevArr);
                prevTick = msg.Tick;
                prevArr = now;
            }

            double win = windowEnd - windowStart;
            Assert.True(win > 0, $"[{arm}] the probe never completed a measurement window");
            interArrival.Sort();
            double med = interArrival.Count > 0 ? interArrival[interArrival.Count / 2] : 0;
            double p99 = interArrival.Count > 0 ? interArrival[Math.Min(interArrival.Count - 1, (int)(interArrival.Count * 0.99))] : 0;

            return new Cadence(
                arm, win, snapsInWindow, snapsInWindow / win, (lastTick - firstTick) / win,
                med * 1000, p99 * 1000,
                string.Join(" ", gaps.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}")),
                gaps.Where(k => k.Key != 4).Sum(k => k.Value), gaps.Sum(k => k.Value),
                proxy.Config.ToString());
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Drain snapshots, reporting the largest server-stamped tick gap seen and how many
    /// snapshots arrived. The gap is the evidence that a frame was lost: the client's own
    /// clock cannot tell a late frame from a missing one, the server's tick can.
    /// </summary>
    /// <summary>
    /// Live readings from the drain task, sampled by the measurement while it runs.
    /// <para>Volatile rather than locked: each field is written by the one drain task and
    /// read by the one measuring task, and a sample that is one snapshot stale is inside
    /// every tolerance here. What it must not do is tear or be hoisted, which is what a plain
    /// field under a tight read loop risks.</para>
    /// </summary>
    private sealed class SnapshotProbe
    {
        private volatile int _lastXBits = BitConverter.SingleToInt32Bits(float.NaN);
        internal float LastX => BitConverter.Int32BitsToSingle(_lastXBits);
        internal void SetLastX(float x) => _lastXBits = BitConverter.SingleToInt32Bits(x);
    }

    private static async Task<(ulong MaxGap, long Snapshots, double MaxArrivalGapMs)> DrainTickGapsAsync(
        NetworkStream stream, CancellationToken ct, string? userId = null, SnapshotProbe? probe = null)
    {
        ulong prev = 0, maxGap = 0;
        long snaps = 0;
        double maxArrivalGapMs = 0, prevArrivalMs = -1;
        var clock = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var env = await WireProtocol.DecodeAsync(stream, ct);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;
                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
                snaps++;

                double nowMs = clock.Elapsed.TotalMilliseconds;
                if (prevArrivalMs >= 0 && nowMs - prevArrivalMs > maxArrivalGapMs)
                    maxArrivalGapMs = nowMs - prevArrivalMs;
                prevArrivalMs = nowMs;

                if (probe != null && userId != null)
                {
                    // Deltas omit unchanged entities, so a frame without the player is normal
                    // and leaves the previous reading standing rather than clearing it.
                    foreach (var e in msg.Entities)
                        if (e.Id == userId) probe.SetLastX(e.X);
                }

                if (prev != 0 && msg.Tick > prev)
                {
                    ulong g = msg.Tick - prev;
                    if (g > maxGap) maxGap = g;
                }
                prev = msg.Tick;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return (maxGap, snaps, maxArrivalGapMs);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plumbing
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<(GameServerHost Server, Task RunTask, int Port, CancellationTokenSource Cts)>
        StartServerAsync(TimeSpan hold, GameMetrics metrics)
    {
        Assert.True(SimulationRates.TryCreate(60, 15, 5, out var rates, out string? err), err);
        var options = new ServerOptions
        {
            ServerAddr = ":0",
            ServerId = ServerId,
            MapId = "map_adversity",
            Mode = "map",
            Transport = TransportKind.Tcp,
            TickRate = rates!.CriticalHz,
            SimulationRates = rates,
            Capacity = 8,
            JwtSecret = JwtSecret,
            JoinTokenSecret = JwtSecret,
            HoldTtl = hold,
            SaveInterval = TimeSpan.FromHours(1),
            PlayerStore = new MemoryPlayerStore(),
            Metrics = metrics,
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);
        return (server, runTask, port, cts);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !condition()) await Task.Delay(25);
    }

    private static async Task JoinAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        await WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
            WireEncoding.Json), ct);
        var env = await WireProtocol.DecodeAsync(stream, ct);
        Assert.NotNull(env);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(env!);
        Assert.True(resp.Ok, resp.Error);
    }

    private static Task SendInputAsync(NetworkStream stream, InputMessage input, CancellationToken ct)
        => WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.Input, input, WireEncoding.Json), ct);

    /// <summary>
    /// Ask for a keyframe. The envelope is built by hand because <c>MsgResync</c> has no body
    /// the server reads, so there is no payload type to pick and picking one would imply the
    /// server parses it.
    /// </summary>
    private static Task SendResyncAsync(NetworkStream stream, CancellationToken ct)
        => WriteFrameAsync(stream, new GameServer.Net.Envelope
        {
            Type = (byte)MsgType.Resync,
            Payload = Array.Empty<byte>(),
            Encoding = WireEncoding.Json
        }, ct);

    private static async Task WriteFrameAsync(NetworkStream stream, GameServer.Net.Envelope env, CancellationToken ct)
    {
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<EntitySnapshot> ReadOwnEntityAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        for (int frames = 0; frames < 400; frames++)
        {
            var env = await WireProtocol.DecodeAsync(stream, ct);
            if (env == null) break;
            if ((MsgType)env.Type != MsgType.Snapshot) continue;
            foreach (var e in WireProtocol.GetPayload<SnapshotMessage>(env).Entities)
                if (e.Id == userId) return e;
        }
        throw new InvalidOperationException($"player {userId} never appeared in a snapshot");
    }

    /// <summary>
    /// Read until a <b>keyframe</b> mentioning the player. Requires <c>Full</c> rather than
    /// accepting any snapshot: a delta arriving between the resync and the keyframe answers
    /// with whatever changed, which is the stale reading this exists to avoid.
    /// </summary>
    private static async Task<EntitySnapshot> ReadKeyframeEntityAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        for (int frames = 0; frames < 600; frames++)
        {
            var env = await WireProtocol.DecodeAsync(stream, ct);
            if (env == null) break;
            if ((MsgType)env.Type != MsgType.Snapshot) continue;
            var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
            if (!msg.Full) continue;
            foreach (var e in msg.Entities)
                if (e.Id == userId) return e;
        }
        throw new InvalidOperationException($"player {userId} never appeared in a keyframe after MsgResync");
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { await client.ConnectAsync(IPAddress.Loopback, port); return; }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new TimeoutException($"nothing was listening on :{port}");
    }
}
