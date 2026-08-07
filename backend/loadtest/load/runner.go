package load

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"io"
	"math"
	"net/http"
	"sync"
	"sync/atomic"
	"time"
)

// Runner orchestrates one load run: ramp N players in, hold them for the
// measurement window, then tear down and reconcile client observations against
// server-side counters.
type Runner struct {
	cfg Config
	out io.Writer
	hc  *http.Client
}

// NewRunner builds a Runner for cfg, writing progress to out.
func NewRunner(cfg Config, out io.Writer) *Runner {
	return &Runner{
		cfg: cfg,
		out: out,
		hc:  &http.Client{Timeout: 10 * time.Second},
	}
}

// Run executes the full run and returns its Result. An error is returned only
// when the run could not be executed at all; a run where every player failed to
// connect is a valid Result with PlayersJoined == 0.
func (r *Runner) Run(ctx context.Context) (*Result, error) {
	runID := newRunID()
	started := time.Now()

	res := &Result{
		Schema:  ResultSchema,
		Label:   r.cfg.Label,
		RunID:   runID,
		Started: started,
		Config: ResultConfig{
			Players:       r.cfg.Players,
			RampRate:      r.cfg.RampRate,
			DurationSec:   r.cfg.Duration.Seconds(),
			ClientTickHz:  r.cfg.TickRate,
			AuthMode:      string(r.cfg.AuthMode),
			Movement:      r.cfg.Movement,
			Encoding:      r.cfg.Encoding.String(),
			MapID:         r.cfg.MapID,
			Transport:     r.cfg.Transport,
			HoldGateway:   r.cfg.HoldGateway,
			TickBudgetSec: TickBudget.Seconds(),
		},
	}

	runCtx, cancel := context.WithCancel(ctx)
	defer cancel()

	var (
		measuring atomic.Bool
		wg        sync.WaitGroup
		mu        sync.Mutex
		stats     = make([]*PlayerStats, 0, r.cfg.Players)
	)
	auth := newAuthProvider(r.cfg)
	joined := make(chan struct{}, r.cfg.Players)
	serverWindowSec := 0.0

	r.logf("run %s: ramping %d players at %.1f/s (movement=%s auth=%s)",
		runID, r.cfg.Players, r.cfg.RampRate, r.cfg.Movement, r.cfg.AuthMode)

	rampStart := time.Now()
	gap := time.Duration(0)
	if r.cfg.RampRate > 0 {
		gap = time.Duration(float64(time.Second) / r.cfg.RampRate)
	}
	for i := 0; i < r.cfg.Players; i++ {
		if gap > 0 && i > 0 {
			select {
			case <-runCtx.Done():
				return nil, runCtx.Err()
			case <-time.After(gap):
			}
		}
		wg.Add(1)
		go func(idx int) {
			defer wg.Done()
			p := &player{cfg: r.cfg, idx: idx, runID: runID, auth: auth, measuring: &measuring}
			st := p.Run(runCtx, joined)
			mu.Lock()
			stats = append(stats, st)
			mu.Unlock()
		}(i)
	}
	r.logf("ramp issued in %s; settling for %s", time.Since(rampStart).Round(time.Millisecond), r.cfg.Warmup)

	// Warmup: let joins land and the delta encoders reach steady state before
	// anything is recorded. A keyframe storm during the ramp would otherwise be
	// counted as steady-state cost.
	if err := sleepCtx(runCtx, r.cfg.Warmup); err != nil {
		return nil, err
	}

	// Window ordering is deliberate: the clients start recording BEFORE the first
	// scrape and stop AFTER the last one, so the server-side counter window sits
	// strictly inside the client-side one. Any snapshot the server counts as sent
	// was therefore also inside the clients' listening window, and a
	// snapshots_received_ratio below 1 is real frame loss rather than a scrape
	// skew of a few hundred milliseconds at the edges.
	windowStart := time.Now()
	measuring.Store(true)
	beforeGS, errGS := fetchScrape(runCtx, r.hc, r.cfg.GSMetricsURL)
	beforeGW, errGW := fetchScrape(runCtx, r.hc, r.cfg.GWMetricsURL)

	r.logf("measuring for %s", r.cfg.Duration)
	if err := sleepCtx(runCtx, r.cfg.Duration); err != nil {
		return nil, err
	}

	afterGS, errGS2 := fetchScrape(runCtx, r.hc, r.cfg.GSMetricsURL)
	afterGW, errGW2 := fetchScrape(runCtx, r.hc, r.cfg.GWMetricsURL)
	measuring.Store(false)
	windowSec := time.Since(windowStart).Seconds()

	// Throughput and cadence are per-second rates over the CLIENT window, but the
	// server counters cover the slightly shorter scrape-to-scrape window. Use the
	// scrape interval for the server-derived rates so ticks/s is not diluted.
	if beforeGS != nil && afterGS != nil {
		if d := afterGS.At.Sub(beforeGS.At).Seconds(); d > 0 {
			serverWindowSec = d
		}
	}

	cancel()
	wg.Wait()

	if serverWindowSec <= 0 {
		serverWindowSec = windowSec
	}
	res.Client = aggregateClients(stats, windowSec)
	res.Server = aggregateServer(beforeGS, afterGS, beforeGW, afterGW, serverWindowSec,
		firstErr(errGS, errGS2), firstErr(errGW, errGW2))
	reconcile(&res.Client, &res.Server)
	// Sampled after the window so it describes the run rather than whatever
	// preceded it.
	res.Host = ReadHostStats()
	res.Verdict = Evaluate(res)
	return res, nil
}

func (r *Runner) logf(format string, args ...any) {
	if r.out == nil {
		return
	}
	fmt.Fprintf(r.out, "[loadtest] "+format+"\n", args...)
}

// aggregateClients folds every player's observations into one ClientStats.
func aggregateClients(stats []*PlayerStats, windowSec float64) ClientStats {
	out := ClientStats{
		PlayersRequested: len(stats),
		FailuresByPhase:  map[string]int{},
	}
	snap := NewSamples(0)
	ack := NewSamples(0)
	join := NewSamples(len(stats))

	for _, s := range stats {
		if s.Joined {
			out.PlayersJoined++
			join.AddDuration(s.JoinLatency)
		}
		if s.Err != nil {
			out.PlayersFailed++
			out.FailuresByPhase[s.ErrPhase]++
			if len(out.SampleErrors) < 5 {
				out.SampleErrors = append(out.SampleErrors, s.ErrPhase+": "+s.Err.Error())
			}
		}
		snap.Merge(s.SnapInterval)
		ack.Merge(s.AckLatency)
		out.SnapshotsTotal += s.Snapshots
		out.KeyframesTotal += s.Keyframes
		out.DeltasTotal += s.Deltas
		out.InputsTotal += s.Inputs
		out.RxBytesPerSecTotal += float64(atomic.LoadUint64(&s.BytesRx))
		out.TxBytesPerSecTotal += float64(atomic.LoadUint64(&s.BytesTx))
	}
	if len(out.FailuresByPhase) == 0 {
		out.FailuresByPhase = nil
	}

	out.SnapshotInterval = snap.Describe()
	out.AckLatency = ack.Describe()
	out.JoinLatency = join.Describe()

	if windowSec > 0 {
		out.RxBytesPerSecTotal = round2(out.RxBytesPerSecTotal / windowSec)
		out.TxBytesPerSecTotal = round2(out.TxBytesPerSecTotal / windowSec)
		if out.PlayersJoined > 0 {
			out.RxBytesPerSecPerPlayer = round2(out.RxBytesPerSecTotal / float64(out.PlayersJoined))
			out.TxBytesPerSecPerPlayer = round2(out.TxBytesPerSecTotal / float64(out.PlayersJoined))
		}
	}
	return out
}

// aggregateServer differences the two /metrics scrapes over the window.
func aggregateServer(beforeGS, afterGS, beforeGW, afterGW *Scrape, windowSec float64, errGS, errGW error) ServerStats {
	out := ServerStats{}
	if errGS != nil {
		out.ScrapeErrGameserver = errGS.Error()
	}
	if errGW != nil {
		out.ScrapeErrGateway = errGW.Error()
	}
	if afterGS == nil && afterGW == nil {
		return out
	}
	out.Scraped = true

	if afterGS != nil {
		out.RestartedMidRun = counterWentBackwards(beforeGS, afterGS,
			"gameserver_tick_duration_seconds_count", "gameserver_snapshots_sent_total")
		buckets := BucketDelta(beforeGS, afterGS, "gameserver_tick_duration_seconds")
		out.TickP50 = round6(HistogramQuantile(buckets, 0.50))
		out.TickP95 = round6(HistogramQuantile(buckets, 0.95))
		out.TickP99 = round6(HistogramQuantile(buckets, 0.99))
		ratio, edge := TickBudgetExceededRatio(buckets, TickBudget.Seconds())
		out.TickOverBudgetRatio = round6(ratio)
		out.TickOverBudgetEdge = edge

		sum := delta(beforeGS, afterGS, "gameserver_tick_duration_seconds_sum")
		count := delta(beforeGS, afterGS, "gameserver_tick_duration_seconds_count")
		out.TickCount = count
		if count > 0 {
			out.TickMeanSec = round6(sum / count)
		}
		if windowSec > 0 {
			out.TicksPerSec = round2(count / windowSec)
		}

		out.PlayersOnline = afterGS.Get("gameserver_players_online")
		out.Entities = afterGS.Get("gameserver_entities")
		out.SnapshotsSent = delta(beforeGS, afterGS, "gameserver_snapshots_sent_total")
		out.ProcessedInputs = delta(beforeGS, afterGS, "gameserver_tick_processed_inputs_total")
	}
	if afterGW != nil {
		out.GatewayConnsActive = afterGW.Get("gateway_connections_active")
		out.GatewayAuthOK = delta(beforeGW, afterGW, "gateway_auth_total|result=ok")
		out.GatewayAuthFail = delta(beforeGW, afterGW, "gateway_auth_total|result=fail")
		out.GatewayEnterOK = delta(beforeGW, afterGW, "gateway_enter_world_total|result=ok")
		out.GatewayEnterFail = delta(beforeGW, afterGW, "gateway_enter_world_total|result=fail")
		out.GatewayRateLimited = delta(beforeGW, afterGW, "gateway_rate_limited_total")
	}
	return out
}

// reconcile derives cross-checks that need both sides of the measurement.
func reconcile(c *ClientStats, s *ServerStats) {
	if s.SnapshotsSent > 0 {
		c.SnapshotsReceivedRatio = round4(float64(c.SnapshotsTotal) / s.SnapshotsSent)
	}
}

// Evaluate applies the acceptance criteria. Order matters only for the human
// readable Reason: the first criterion that fails names the run.
func Evaluate(res *Result) Verdict {
	v := Verdict{TickBudgetOK: true, SnapshotCadenceOK: true, NoErrors: true, NoFrameLoss: true}
	budget := TickBudget.Seconds()

	if res.Server.Scraped && res.Server.TickCount > 0 {
		// Prefer the exact over-budget ratio: it needs no bucket interpolation.
		// Allow a 1-in-1000 tail so a single GC pause does not condemn a level.
		if !math.IsNaN(res.Server.TickOverBudgetRatio) && res.Server.TickOverBudgetRatio > 0.01 {
			v.TickBudgetOK = false
		}
		if res.Server.TickP99 > budget {
			v.TickBudgetOK = false
		}
	}
	// Snapshot cadence: p99 must stay inside 2x the tick period.
	if res.Client.SnapshotInterval.Count > 0 &&
		res.Client.SnapshotInterval.P99 > 2*budget*1000 {
		v.SnapshotCadenceOK = false
	}
	if res.Client.PlayersFailed > 0 || res.Client.PlayersJoined < res.Client.PlayersRequested {
		v.NoErrors = false
	}
	// A ratio meaningfully below 1 means the server's bounded per-connection
	// send channel (64, DropOldest) threw snapshots away.
	if res.Client.SnapshotsReceivedRatio > 0 && res.Client.SnapshotsReceivedRatio < 0.95 {
		v.NoFrameLoss = false
	}

	// --- Validity: did this level measure what it claims to have measured? ---
	//
	// Distinct from the criteria above, which describe a server that kept up or
	// did not. These describe a run that is not a measurement at all, so its
	// numbers must be excluded rather than reported as a worse result.
	invalidReason := validityFailure(res)

	switch {
	case invalidReason != "":
		v.Invalid = true
		v.Reason = "INVALID: " + invalidReason + " — rerun this level"
	case res.Server.RestartedMidRun:
		// A restart makes every other signal meaningless. The clients all saw
		// "server closed the connection" and the counter deltas span two process
		// lifetimes.
		v.Invalid = true
		v.Reason = "INVALID: the game server restarted mid-run (counter reset) — rerun this level"
	case !v.TickBudgetOK:
		v.Reason = fmt.Sprintf("tick p99 %.1fms over the %.1fms budget (%.2f%% of ticks above %.0fms)",
			res.Server.TickP99*1000, budget*1000,
			res.Server.TickOverBudgetRatio*100, res.Server.TickOverBudgetEdge*1000)
	case !v.SnapshotCadenceOK:
		v.Reason = fmt.Sprintf("snapshot interval p99 %.1fms over 2x tick period (%.1fms)",
			res.Client.SnapshotInterval.P99, 2*budget*1000)
	case !v.NoErrors:
		v.Reason = fmt.Sprintf("%d/%d players failed",
			res.Client.PlayersFailed, res.Client.PlayersRequested)
	case !v.NoFrameLoss:
		v.Reason = fmt.Sprintf("clients received only %.1f%% of the snapshots the server enqueued",
			res.Client.SnapshotsReceivedRatio*100)
	}
	v.Degraded = v.Reason != ""
	return v
}

// validityFailure reports why a level failed to measure what it claims, or "".
//
// Every check here answers "is this a measurement?", never "is this a good
// result?". They exist because a broken run does not announce itself in the
// headline numbers, and can look BETTER than a healthy one — a level whose
// clients all died early reported a 97% bandwidth saving, because the bytes they
// never received were indistinguishable from bytes the server never sent.
//
// # A deliberate asymmetry: this gate errs pessimistic, not optimistic
//
// It catches runs where the INSTRUMENT broke — clients failed, ratios impossible,
// the server carrying load it was never given. It does NOT catch runs where the
// HOST was stolen: a sweep interrupted by a deploy comes back with
// players_failed=0, a received ratio of 1.005 and sane entity counts, and is
// recorded as "degraded" — i.e. as though the server could not keep up, when in
// truth the machine was busy elsewhere. That understates capacity.
//
// That gap is known and accepted rather than overlooked. The alternative was a
// rule keyed on achieved tick rate, which was implemented, tested against
// saturated runs already in the repo, and rejected: a genuinely overloaded server
// loses ticks exactly like a starved one (10.46 ticks/s at 300 players, 12.51 at
// 400, both real limits). Such a rule fails in the other direction, and in the
// worse way — it would mark real capacity ceilings INVALID, so those levels would
// not be reported wrongly, they would be ABSENT, and an absent level reads as a
// sweep that simply did not go that high.
//
// So the trade is: a pessimistic level a human can see and question, over an
// optimistic one that silently deletes evidence. Contention is handled where it
// can actually be observed — externally, by refusing to sweep during a deploy
// (scripts/encoding-sweep.sh) and by recording host load with every result
// (HostStats).
func validityFailure(res *Result) string {
	c, s := &res.Client, &res.Server

	// 1. Any client failure means the level did not run at the size on its label.
	//    Even one is enough: a "150-player" level where one client never joined
	//    measured 149, and reporting it as 150 misstates the x-axis of every
	//    curve drawn from it.
	if c.PlayersFailed > 0 || c.PlayersJoined < c.PlayersRequested {
		return fmt.Sprintf("%d/%d players failed, so this level did not run at %d players",
			c.PlayersFailed, c.PlayersRequested, c.PlayersRequested)
	}

	// 2. A received ratio far ABOVE 1 is as broken as one far below, and used to
	//    go unchecked: NoFrameLoss only looks downward, because it is asking a
	//    different question (did the send queue drop frames?). A ratio of 1.40
	//    means the client and server windows describe different populations —
	//    reconnects, a dirty server, or overlapping runs — and nothing derived
	//    from either side can be trusted.
	if c.SnapshotsReceivedRatio > 1.05 {
		return fmt.Sprintf("clients recorded %.2fx the snapshots the server sent; the two measurement windows disagree",
			c.SnapshotsReceivedRatio)
	}

	// 3. The server cannot hold more players or entities than were asked of it.
	//    When it reports more, the process was not clean at the start of the
	//    level and is carrying load from somewhere else.
	if s.Scraped {
		if s.PlayersOnline > float64(c.PlayersRequested) {
			return fmt.Sprintf("server reported %.0f players online for a %d-player level; it was not empty when the level started",
				s.PlayersOnline, c.PlayersRequested)
		}
		if s.Entities > float64(c.PlayersRequested) {
			return fmt.Sprintf("server reported %.0f entities for a %d-player level; it was not empty when the level started",
				s.Entities, c.PlayersRequested)
		}
	}
	return ""
}

// ---------------------------------------------------------------- helpers

// counterWentBackwards reports whether any named monotonic counter decreased
// between the two scrapes. A counter can only go down by being reset, and a
// Prometheus counter is only reset by the process restarting.
func counterWentBackwards(before, after *Scrape, names ...string) bool {
	if before == nil || after == nil {
		return false
	}
	for _, n := range names {
		if after.Get(n) < before.Get(n) {
			return true
		}
	}
	return false
}

func delta(before, after *Scrape, name string) float64 {
	if after == nil {
		return 0
	}
	prev := 0.0
	if before != nil {
		prev = before.Get(name)
	}
	d := after.Get(name) - prev
	if d < 0 {
		return 0 // process restarted mid-window; the counter reset
	}
	return d
}

func sleepCtx(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return nil
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

func firstErr(errs ...error) error {
	for _, e := range errs {
		if e != nil {
			return e
		}
	}
	return nil
}

func newRunID() string {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return "00000000"
	}
	return hex.EncodeToString(b)
}

func round4(v float64) float64 { return math.Round(v*10000) / 10000 }
func round6(v float64) float64 {
	if math.IsNaN(v) || math.IsInf(v, 0) {
		return 0
	}
	return math.Round(v*1e6) / 1e6
}
