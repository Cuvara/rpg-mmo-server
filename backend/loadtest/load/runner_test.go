package load

import (
	"strings"
	"testing"
	"time"
)

func TestAggregateClients(t *testing.T) {
	good := func(joinMs float64, snaps int) *PlayerStats {
		s := newPlayerStats(0)
		s.Joined = true
		s.JoinLatency = time.Duration(joinMs) * time.Millisecond
		s.Snapshots = snaps
		s.Keyframes = 1
		s.Deltas = snaps - 1
		s.Inputs = 10
		s.BytesRx = 1000
		s.BytesTx = 500
		s.SnapInterval.Add(66)
		s.AckLatency.Add(30)
		return s
	}
	bad := newPlayerStats(0)
	bad.Err = errTest
	bad.ErrPhase = "join"

	out := aggregateClients([]*PlayerStats{good(10, 5), good(20, 5), bad}, 10)

	if out.PlayersRequested != 3 || out.PlayersJoined != 2 || out.PlayersFailed != 1 {
		t.Errorf("got requested=%d joined=%d failed=%d, want 3/2/1",
			out.PlayersRequested, out.PlayersJoined, out.PlayersFailed)
	}
	if out.FailuresByPhase["join"] != 1 {
		t.Errorf("FailuresByPhase = %v, want one join failure", out.FailuresByPhase)
	}
	if len(out.SampleErrors) != 1 {
		t.Errorf("SampleErrors = %v, want 1", out.SampleErrors)
	}
	if out.SnapshotsTotal != 10 || out.InputsTotal != 20 {
		t.Errorf("got snapshots=%d inputs=%d, want 10/20", out.SnapshotsTotal, out.InputsTotal)
	}
	// 2000 bytes over a 10s window = 200 B/s total, 100 B/s per joined player.
	if out.RxBytesPerSecTotal != 200 || out.RxBytesPerSecPerPlayer != 100 {
		t.Errorf("got rx total=%v per-player=%v, want 200/100",
			out.RxBytesPerSecTotal, out.RxBytesPerSecPerPlayer)
	}
	if out.JoinLatency.Count != 2 {
		t.Errorf("JoinLatency.Count = %d, want 2 (failed players have no join)", out.JoinLatency.Count)
	}
}

func TestAggregateClientsAllFailed(t *testing.T) {
	bad := newPlayerStats(0)
	bad.Err = errTest
	bad.ErrPhase = "gateway"
	out := aggregateClients([]*PlayerStats{bad}, 10)
	if out.PlayersJoined != 0 {
		t.Errorf("PlayersJoined = %d, want 0", out.PlayersJoined)
	}
	// No division by zero when nobody joined.
	if out.RxBytesPerSecPerPlayer != 0 {
		t.Errorf("RxBytesPerSecPerPlayer = %v, want 0", out.RxBytesPerSecPerPlayer)
	}
}

func TestAggregateServer(t *testing.T) {
	before := scrapeText(sampleMetrics, time.Now())
	after := scrapeText(`gameserver_players_online{map_id="map_01"} 50
gameserver_entities{} 50
gameserver_snapshots_sent_total{map_id="map_01"} 1600
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.00050000000000000001"} 10
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.050000000000000003"} 190
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.074999999999999997"} 200
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="+Inf"} 200
gameserver_tick_duration_seconds_sum{map_id="map_01"} 6.5
gameserver_tick_duration_seconds_count{map_id="map_01"} 200
`, time.Now())

	got := aggregateServer(before, after, nil, nil, 10, nil, nil)
	if !got.Scraped {
		t.Fatal("Scraped = false")
	}
	if got.PlayersOnline != 50 || got.Entities != 50 {
		t.Errorf("got players=%v entities=%v, want 50/50", got.PlayersOnline, got.Entities)
	}
	// Counters are differenced across the window, never read absolute.
	if got.SnapshotsSent != 600 {
		t.Errorf("SnapshotsSent = %v, want 600 (1600-1000)", got.SnapshotsSent)
	}
	if got.TickCount != 100 {
		t.Errorf("TickCount = %v, want 100 (200-100)", got.TickCount)
	}
	// (6.5-2.5)/100 = 40ms mean, computed exactly from sum/count.
	if got.TickMeanSec != 0.04 {
		t.Errorf("TickMeanSec = %v, want 0.04", got.TickMeanSec)
	}
	if got.TicksPerSec != 10 {
		t.Errorf("TicksPerSec = %v, want 10", got.TicksPerSec)
	}
}

// A restarted process resets its counters; a negative delta must clamp to 0
// rather than produce a nonsense negative rate.
func TestAggregateServerCounterReset(t *testing.T) {
	before := scrapeText(`c_total 1000`, time.Now())
	after := scrapeText(`c_total 5`, time.Now())
	if got := delta(before, after, "c_total"); got != 0 {
		t.Errorf("delta after reset = %v, want 0", got)
	}
}

func TestEvaluate(t *testing.T) {
	budget := TickBudget.Seconds()
	okRes := func() *Result {
		return &Result{
			Client: ClientStats{
				PlayersRequested: 10, PlayersJoined: 10,
				SnapshotInterval:       Dist{Count: 100, P99: 70},
				SnapshotsReceivedRatio: 1.0,
			},
			Server: ServerStats{Scraped: true, TickCount: 100, TickP99: 0.001},
		}
	}
	tests := []struct {
		name         string
		mutate       func(*Result)
		wantDegraded bool
		wantField    func(Verdict) bool
	}{
		{"healthy", func(*Result) {}, false, nil},
		{
			"tick p99 over budget",
			func(r *Result) { r.Server.TickP99 = budget + 0.01 },
			true,
			func(v Verdict) bool { return !v.TickBudgetOK },
		},
		{
			"too many ticks over the edge",
			func(r *Result) { r.Server.TickOverBudgetRatio = 0.05 },
			true,
			func(v Verdict) bool { return !v.TickBudgetOK },
		},
		{
			// A single GC pause must not condemn a level.
			"one tick in a thousand is tolerated",
			func(r *Result) { r.Server.TickOverBudgetRatio = 0.001 },
			false,
			nil,
		},
		{
			"snapshot cadence drift",
			func(r *Result) { r.Client.SnapshotInterval.P99 = 2*budget*1000 + 1 },
			true,
			func(v Verdict) bool { return !v.SnapshotCadenceOK },
		},
		{
			"players failed",
			func(r *Result) { r.Client.PlayersJoined = 9; r.Client.PlayersFailed = 1 },
			true,
			func(v Verdict) bool { return !v.NoErrors },
		},
		{
			"server dropped snapshots",
			func(r *Result) { r.Client.SnapshotsReceivedRatio = 0.5 },
			true,
			func(v Verdict) bool { return !v.NoFrameLoss },
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			r := okRes()
			tt.mutate(r)
			v := Evaluate(r)
			if v.Degraded != tt.wantDegraded {
				t.Errorf("Degraded = %v (reason %q), want %v", v.Degraded, v.Reason, tt.wantDegraded)
			}
			if tt.wantField != nil && !tt.wantField(v) {
				t.Errorf("expected criterion not flagged: %+v", v)
			}
			if v.Degraded && v.Reason == "" {
				t.Error("degraded verdict with no reason")
			}
		})
	}
}

func TestReconcile(t *testing.T) {
	c := ClientStats{SnapshotsTotal: 900}
	s := ServerStats{SnapshotsSent: 1000}
	reconcile(&c, &s)
	if c.SnapshotsReceivedRatio != 0.9 {
		t.Errorf("SnapshotsReceivedRatio = %v, want 0.9", c.SnapshotsReceivedRatio)
	}
	// No server scrape means no ratio, not a divide by zero.
	c2 := ClientStats{SnapshotsTotal: 900}
	reconcile(&c2, &ServerStats{})
	if c2.SnapshotsReceivedRatio != 0 {
		t.Errorf("SnapshotsReceivedRatio = %v, want 0", c2.SnapshotsReceivedRatio)
	}
}

type testErr struct{}

func (testErr) Error() string { return "boom" }

var errTest = testErr{}

func TestCounterWentBackwards(t *testing.T) {
	a := scrapeText("c_total 100\nd_total 5", time.Now())
	b := scrapeText("c_total 200\nd_total 6", time.Now())
	reset := scrapeText("c_total 3\nd_total 1", time.Now())

	if counterWentBackwards(a, b, "c_total", "d_total") {
		t.Error("monotonic counters flagged as a restart")
	}
	if !counterWentBackwards(a, reset, "c_total") {
		t.Error("a counter reset must be detected as a restart")
	}
	// A missing scrape is not evidence of a restart.
	if counterWentBackwards(nil, b, "c_total") || counterWentBackwards(a, nil, "c_total") {
		t.Error("nil scrape flagged as a restart")
	}
}

// A mid-run restart must outrank every other verdict: its counter deltas span
// two process lifetimes, so the latency numbers are meaningless.
func TestEvaluateRestartOutranksOtherFailures(t *testing.T) {
	r := &Result{
		Client: ClientStats{PlayersRequested: 10, PlayersJoined: 0, PlayersFailed: 10},
		Server: ServerStats{Scraped: true, TickCount: 100, TickP99: 9, RestartedMidRun: true},
	}
	v := Evaluate(r)
	if !v.Degraded {
		t.Fatal("restart must degrade the run")
	}
	if !strings.Contains(v.Reason, "INVALID") {
		t.Errorf("Reason = %q, want it to flag the run as INVALID", v.Reason)
	}
}
