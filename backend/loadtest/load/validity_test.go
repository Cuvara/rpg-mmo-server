package load

import (
	"strings"
	"testing"
)

// The cases below are the real numbers from the sweep level that reported a 97%
// bandwidth saving. They are kept verbatim rather than reduced to minimal
// fixtures, because the point is that these exact figures once passed straight
// through into a benchmark document as a result.
func TestEvaluateRejectsRunsThatDidNotMeasureAnything(t *testing.T) {
	tests := []struct {
		name       string
		mutate     func(*Result)
		wantSubstr string
	}{
		{
			// The 200-player level: every client failed mid-run, so they received
			// 7.3 KB/s instead of ~109. Reported as a 97% saving.
			name: "every client failed mid-run",
			mutate: func(r *Result) {
				r.Client.PlayersRequested = 200
				r.Client.PlayersJoined = 200
				r.Client.PlayersFailed = 200
				r.Client.SnapshotsReceivedRatio = 0.20
			},
			wantSubstr: "200/200 players failed",
		},
		{
			// The 150-player level: clients recorded 1.4x the snapshots the server
			// sent. Nothing derived from either side is trustworthy, and the old
			// NoFrameLoss check could not see it because it only looks downward.
			name: "received ratio above 1",
			mutate: func(r *Result) {
				r.Client.SnapshotsReceivedRatio = 1.40
			},
			wantSubstr: "measurement windows disagree",
		},
		{
			// Same level: a supposedly fresh server reporting 200 entities for 150
			// players. It was carrying load from somewhere else.
			name: "server not empty at level start",
			mutate: func(r *Result) {
				r.Server.Entities = 200
			},
			wantSubstr: "not empty when the level started",
		},
		{
			name: "more players online than requested",
			mutate: func(r *Result) {
				r.Server.PlayersOnline = 200
			},
			wantSubstr: "not empty when the level started",
		},
		{
			// One client short still misstates the level's size: a "150-player"
			// level that ran 149 puts the wrong x on every curve drawn from it.
			name: "a single client short",
			mutate: func(r *Result) {
				r.Client.PlayersJoined = 149
				r.Client.PlayersFailed = 1
			},
			wantSubstr: "did not run at 150 players",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			res := healthyResult()
			tt.mutate(res)

			v := Evaluate(res)
			if !v.Invalid {
				t.Fatalf("Evaluate marked this run valid; verdict = %+v", v)
			}
			if !strings.HasPrefix(v.Reason, "INVALID: ") {
				t.Errorf("Reason = %q, want it to lead with INVALID", v.Reason)
			}
			if !strings.Contains(v.Reason, tt.wantSubstr) {
				t.Errorf("Reason = %q, want it to mention %q", v.Reason, tt.wantSubstr)
			}
		})
	}
}

// A server that populates its own map (the enemy spawner, 6 on a stock map
// server) reports more entities than players on every level, and the strict
// gate marks all of them INVALID — which is exactly what happened to the first
// sweep against the dev stack. A declared baseline is tolerated; anything past
// it is still a dirty server.
func TestBaselineEntitiesLoosensOnlyTheEntityCheck(t *testing.T) {
	res := healthyResult()
	res.Config.BaselineEntities = 6
	res.Server.Entities = float64(res.Client.PlayersRequested + 6)
	if v := Evaluate(res); v.Invalid {
		t.Errorf("entities == players + baseline must be valid; got %q", v.Reason)
	}

	res.Server.Entities = float64(res.Client.PlayersRequested + 7)
	v := Evaluate(res)
	if !v.Invalid {
		t.Fatal("one entity past the baseline must still be INVALID")
	}
	if !strings.Contains(v.Reason, "6-entity baseline") {
		t.Errorf("Reason = %q, want it to state the baseline it was judged against", v.Reason)
	}

	// The baseline is about entities the server puts there itself. Players are
	// never part of it: one extra player online is a client from somewhere else.
	res.Server.Entities = float64(res.Client.PlayersRequested)
	res.Server.PlayersOnline = float64(res.Client.PlayersRequested + 1)
	if v := Evaluate(res); !v.Invalid {
		t.Error("a baseline must not excuse an extra player online")
	}
}

// Without a baseline the strict message must point at the knob, because the
// symptom (every level INVALID against a healthy server) does not.
func TestStrictEntityCheckNamesTheBaselineFlag(t *testing.T) {
	res := healthyResult()
	res.Server.Entities = float64(res.Client.PlayersRequested + 6)
	v := Evaluate(res)
	if !v.Invalid || !strings.Contains(v.Reason, "-baseline-entities") {
		t.Errorf("Reason = %q, want INVALID naming -baseline-entities", v.Reason)
	}
}

// A healthy run must not be swept up by the validity checks — otherwise the gate
// would simply reject everything and look like it was working.
func TestEvaluateAcceptsAHealthyRun(t *testing.T) {
	v := Evaluate(healthyResult())
	if v.Invalid {
		t.Errorf("healthy run marked INVALID: %s", v.Reason)
	}
	if v.Degraded {
		t.Errorf("healthy run marked DEGRADED: %s", v.Reason)
	}
}

// A server that genuinely cannot keep up is DEGRADED, not INVALID: the level
// measured something real and belongs in the results. Collapsing the two would
// lose the distinction the gate exists to draw.
func TestOverBudgetRunIsDegradedNotInvalid(t *testing.T) {
	res := healthyResult()
	res.Server.TickP99 = 0.240 // 240ms, far over the 66.67ms budget

	v := Evaluate(res)
	if v.Invalid {
		t.Errorf("an over-budget run must be DEGRADED, not INVALID; got %q", v.Reason)
	}
	if !v.Degraded {
		t.Error("an over-budget run must be DEGRADED")
	}
}

// An invalid level must not cap the reported ceiling. Letting it count as a
// failing level understates capacity exactly as letting it pass would overstate
// it — it is not evidence in either direction.
func TestInvalidLevelIsExcludedFromTheCeiling(t *testing.T) {
	ok := healthyResult()
	ok.Config.Players = 200
	ok.Verdict = Evaluate(ok)

	broken := healthyResult()
	broken.Config.Players = 250
	broken.Client.PlayersFailed = 250
	broken.Verdict = Evaluate(broken)

	var sb strings.Builder
	WriteSummary(&sb, []*Result{ok, broken})
	out := sb.String()

	if !strings.Contains(out, "HIGHEST PASSING LEVEL: 200") {
		t.Errorf("ceiling should be the highest VALID level (200); got:\n%s", out)
	}
	if !strings.Contains(out, "INVALID") {
		t.Errorf("the invalid level should still be reported; got:\n%s", out)
	}
	if !strings.Contains(out, "rerun") {
		t.Errorf("an incomplete sweep must say so; got:\n%s", out)
	}
}

// An INVALID level must also count as Degraded, so `-fail-on-degraded` exits
// non-zero on it. An unmeasurable level is not a passing level, and a CI job
// gating on that flag must not go green because the run was too broken to judge.
func TestInvalidAlsoTripsTheFailOnDegradedExitCode(t *testing.T) {
	r := healthyResult()
	r.Client.PlayersFailed = 1

	v := Evaluate(r)
	if !v.Invalid {
		t.Fatal("expected INVALID")
	}
	if !v.Degraded {
		t.Error("INVALID must also set Degraded, or -fail-on-degraded would exit 0 on a run that measured nothing")
	}
}

// healthyResult is a run with nothing wrong with it, at 150 players.
func healthyResult() *Result {
	r := &Result{}
	r.Config.Players = 150
	r.Client.PlayersRequested = 150
	r.Client.PlayersJoined = 150
	r.Client.PlayersFailed = 0
	r.Client.SnapshotsReceivedRatio = 1.0
	r.Client.SnapshotInterval = Dist{Count: 1000, P99: 80}
	r.Server.Scraped = true
	r.Server.TickCount = 500
	r.Server.TickP99 = 0.025
	r.Server.TickOverBudgetRatio = 0
	r.Server.PlayersOnline = 150
	r.Server.Entities = 150
	return r
}
