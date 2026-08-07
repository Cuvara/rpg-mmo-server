package load

import (
	"strings"
	"testing"
)

// lvl builds one run's Result at a player count, with a given tick p99.
func lvl(players int, p99 float64) *Result {
	r := &Result{}
	r.Config.Players = players
	r.Client.PlayersRequested = players
	r.Client.PlayersJoined = players
	r.Client.SnapshotsReceivedRatio = 1.0
	r.Client.SnapshotInterval = Dist{Count: 100, P99: 80}
	r.Client.RxBytesPerSecPerPlayer = 1024 * float64(players)
	r.Server.Scraped = true
	r.Server.TickCount = 500
	r.Server.TickP99 = p99
	r.Server.TickMeanSec = p99 / 2
	r.Server.PlayersOnline = float64(players)
	r.Server.Entities = float64(players)
	r.Verdict = Evaluate(r)
	return r
}

// The property the median rule exists for: a MINORITY of disturbed runs must not
// be able to move a level's verdict.
//
// The numbers are the real ones measured at 200 players while a CD deploy shared
// the host — four runs inside 72.9..74.6ms and two at 224.7/240.6ms. Under a
// unanimity rule this level is "marginal" purely because the machine was busy
// twice; under the median rule the two outliers cannot outvote the four.
func TestMinorityOfDisturbedRunsCannotMoveTheVerdict(t *testing.T) {
	// A level that genuinely passes, disturbed twice.
	clean := []float64{0.060, 0.061, 0.059, 0.062}
	disturbed := []float64{0.2247, 0.2406}

	var runs []*Result
	for _, p99 := range append(append([]float64{}, clean...), disturbed...) {
		runs = append(runs, lvl(150, p99))
	}

	c := ComputeCeiling(runs)
	if c.Lower != 150 {
		t.Errorf("Lower = %d, want 150 — 4 of 6 runs passed, the median passes", c.Lower)
	}
	if len(c.Marginal) != 1 || c.Marginal[0] != 150 {
		t.Errorf("Marginal = %v, want [150] — the straddle must still be surfaced", c.Marginal)
	}

	var sb strings.Builder
	WriteCeiling(&sb, c)
	out := sb.String()
	if !strings.Contains(out, "straddle the budget") {
		t.Errorf("want the straddle explained, got:\n%s", out)
	}
	if !strings.Contains(out, "CD deploy") {
		t.Errorf("want the reader pointed at the likely cause, got:\n%s", out)
	}
}

// The converse: a level that genuinely fails must not be rescued by the median.
// These are the real quiet-box readings at 200 players, which sit just above the
// 66.67ms budget — the level fails, and it should say so.
func TestGenuinelyFailingLevelIsNotRescuedByTheMedian(t *testing.T) {
	c := ComputeCeiling([]*Result{
		lvl(200, 0.0729), lvl(200, 0.0735), lvl(200, 0.0746), lvl(200, 0.0736),
	})
	if c.Lower != 0 {
		t.Errorf("Lower = %d, want 0 — every run was over budget", c.Lower)
	}
	if c.Upper != 200 {
		t.Errorf("Upper = %d, want 200", c.Upper)
	}
	if len(c.Marginal) != 0 {
		t.Errorf("Marginal = %v, want none — the runs agree", c.Marginal)
	}
}

func TestUnanimousLevelsGiveADecidedCeiling(t *testing.T) {
	c := ComputeCeiling([]*Result{
		lvl(100, 0.020), lvl(100, 0.022), lvl(100, 0.021),
		lvl(200, 0.090), lvl(200, 0.095), lvl(200, 0.088), // fails every time
	})

	if c.Lower != 100 || c.Upper != 200 {
		t.Errorf("band = %d..%d, want 100..200", c.Lower, c.Upper)
	}
	if len(c.Marginal) != 0 {
		t.Errorf("Marginal = %v, want none", c.Marginal)
	}
	if !c.Decided() {
		t.Error("unanimous levels with 3 runs each should be decided")
	}
}

// A single run per level is still reported — it is the default and it is useful
// — but it must announce that it is not reproducible, because that is exactly
// the claim that had to be withdrawn.
func TestSingleRunIsReportedButFlaggedNotReproducible(t *testing.T) {
	c := ComputeCeiling([]*Result{lvl(100, 0.020), lvl(200, 0.090)})
	if c.Decided() {
		t.Error("one run per level cannot be decided")
	}

	var sb strings.Builder
	WriteCeiling(&sb, c)
	if out := sb.String(); !strings.Contains(out, "NOT reproducible") {
		t.Errorf("a one-run ceiling must say so, got:\n%s", out)
	}
}

// An invalid run measured nothing, so it must not count as a failure and drag
// the ceiling down — the same asymmetry the INVALID verdict exists to enforce.
func TestInvalidRunsDoNotCountAsFailures(t *testing.T) {
	broken := lvl(200, 0.020)
	broken.Client.PlayersFailed = 200
	broken.Verdict = Evaluate(broken)
	if !broken.Verdict.Invalid {
		t.Fatal("fixture should be INVALID")
	}

	c := ComputeCeiling([]*Result{
		lvl(200, 0.020), lvl(200, 0.021), broken,
	})

	if c.Lower != 200 {
		t.Errorf("Lower = %d, want 200 — the two valid runs both passed", c.Lower)
	}
	if len(c.Levels) != 1 || c.Levels[0].Invalid != 1 || c.Levels[0].Runs != 2 {
		t.Errorf("level tally = %+v, want 2 valid runs and 1 invalid", c.Levels[0])
	}
	if c.Repeats != 2 {
		t.Errorf("Repeats = %d, want 2 (invalid runs are not repeats)", c.Repeats)
	}
}

// A level where every run was invalid constrains neither end: it is not evidence
// that capacity is higher OR lower.
func TestAllInvalidLevelConstrainsNothing(t *testing.T) {
	broken := lvl(200, 0.020)
	broken.Client.PlayersFailed = 200
	broken.Verdict = Evaluate(broken)

	c := ComputeCeiling([]*Result{lvl(100, 0.020), broken})
	if c.Lower != 100 {
		t.Errorf("Lower = %d, want 100", c.Lower)
	}
	if c.Upper != 0 {
		t.Errorf("Upper = %d, want 0 — an all-invalid level is not a failure", c.Upper)
	}
}

// The p99/mean brackets are the evidence for the criterion recommendation, so
// they must actually reflect the spread rather than the last run seen.
func TestBracketsCaptureTheSpread(t *testing.T) {
	c := ComputeCeiling([]*Result{lvl(200, 0.053), lvl(200, 0.072), lvl(200, 0.060)})
	s := c.Levels[0]

	if s.TickP99Min > 0.0531 || s.TickP99Max < 0.0719 {
		t.Errorf("p99 bracket = %.4f..%.4f, want to span 0.053..0.072", s.TickP99Min, s.TickP99Max)
	}
	if s.TickMeanMax <= s.TickMeanMin {
		t.Error("mean bracket should span a range too")
	}
}
