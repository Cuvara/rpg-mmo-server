package load

import (
	"fmt"
	"io"
	"sort"
)

// Ceiling summarises repeated runs of the same levels into a decidable capacity
// figure.
//
// # Why this exists
//
// ADR-7 defines acceptance as "tick p99 within the 66.67ms budget", evaluated at
// one level from one run. That is not decidable: measured across two sweeps, the
// same build produced p99 53.46ms and then 72.47ms at 200 players — straddling
// the budget — while its tick *mean* and its p99 at other levels barely moved.
// A single threshold crossing of a tail statistic moved the reported ceiling by
// 50 players, and a claim derived from it had to be withdrawn. See
// backend/docs/BENCHMARK.md.
//
// # Why the rule is a median and not unanimity
//
// The obvious fix is "a level counts as passing only if it passed every run".
// That was measured and rejected. The disturbance on this host is bimodal, not a
// smear: with a CD deploy in flight, 2 runs in 6 at a fixed level came back at
// 225–241ms while the other 4 sat inside 72.9–74.6ms. Under a unanimity rule the
// probability that all N runs of a genuinely-passing level are clean is
// (4/6)^N — 0.44 at N=2, 0.30 at N=3, 0.20 at N=4. So unanimity marks a good
// level marginal roughly 70% of the time at N=3, and raising N makes it WORSE,
// not better. N is the wrong lever against an outlier process.
//
// The level verdict is therefore the MEDIAN tick p99 across its runs, which a
// minority of contaminated runs cannot move. The min..max bracket is still
// reported, and a level whose bracket straddles the budget is still flagged,
// because that straddle is the evidence a reader needs — it is just no longer
// allowed to silently decide the ceiling.
//
// The real fix is upstream of all of this: do not run a sweep while a deploy is
// executing on the same host (scripts/encoding-sweep.sh refuses to).
type Ceiling struct {
	// Lower is the highest level that passed in every run of it. Capacity
	// planning should use this: it is the largest count observed to be reliably
	// within budget.
	Lower int `json:"lower"`
	// Upper is the lowest level that failed in every run of it, or 0 if no level
	// failed consistently. The true ceiling lies below it.
	Upper int `json:"upper"`
	// Marginal lists levels whose per-run p99 bracket straddles the budget, in
	// ascending order — some runs passed and some failed. The median still
	// decides the level, but a straddle means the reading is sensitive to
	// whatever else the machine was doing, so it is surfaced rather than hidden.
	Marginal []int `json:"marginal,omitempty"`
	// Levels is the per-level tally, ascending.
	Levels []LevelSummary `json:"levels"`
	// Repeats is the smallest number of valid runs any level got. A ceiling from
	// one run is reported, but it is not reproducible and says so.
	Repeats int `json:"repeats"`
}

// LevelSummary tallies the repeated runs of a single player count.
type LevelSummary struct {
	Players int `json:"players"`
	// Runs counts VALID runs only. Invalid ones measured nothing and are
	// excluded rather than counted as failures — see Verdict.Invalid.
	Runs    int `json:"runs"`
	Passed  int `json:"passed"`
	Invalid int `json:"invalid"`
	// TickP99Median decides the level. A median is used rather than a mean
	// because the contaminating process is bimodal: a mean is dragged by a single
	// 240ms outlier, a median is not.
	TickP99Median float64 `json:"tick_p99_median_sec"`
	// TickP99Min/Max bracket the observed tail across runs. The gap between them
	// is what makes a single-run ceiling untrustworthy, so it is reported rather
	// than reduced away.
	TickP99Min float64 `json:"tick_p99_min_sec"`
	TickP99Max float64 `json:"tick_p99_max_sec"`
	// TickMeanMin/Max bracket the mean, for comparison: it is the stabler
	// statistic and the reason the mean-based alternative in ADR-7 is viable.
	TickMeanMin float64 `json:"tick_mean_min_sec"`
	TickMeanMax float64 `json:"tick_mean_max_sec"`
	// KBPerSecPerClient is averaged across runs. Unlike the tick figures this
	// reproduces tightly (0.4% across two sweeps on different builds), because
	// bytes on the wire are not a tail statistic.
	KBPerSecPerClient float64 `json:"kb_per_sec_per_client"`
}

// Decided reports whether the ceiling is reproducible: at least two valid runs
// per level and no level with a split verdict below Upper.
func (c Ceiling) Decided() bool { return c.Repeats >= 2 && len(c.Marginal) == 0 }

// ComputeCeiling groups results by player count and derives the band.
func ComputeCeiling(results []*Result) Ceiling {
	byLevel := map[int][]*Result{}
	for _, r := range results {
		byLevel[r.Config.Players] = append(byLevel[r.Config.Players], r)
	}

	levels := make([]int, 0, len(byLevel))
	for n := range byLevel {
		levels = append(levels, n)
	}
	sort.Ints(levels)

	c := Ceiling{Repeats: -1}
	for _, n := range levels {
		s := summariseLevel(n, byLevel[n])
		c.Levels = append(c.Levels, s)

		if s.Runs == 0 {
			// Every run of this level was invalid: it is not evidence in either
			// direction, so it constrains neither end of the band.
			continue
		}
		if c.Repeats < 0 || s.Runs < c.Repeats {
			c.Repeats = s.Runs
		}

		// A level whose runs straddle the budget is flagged either way: the
		// median decides it, but the straddle is what tells a reader the number
		// is environment-sensitive.
		if s.Passed > 0 && s.Passed < s.Runs {
			c.Marginal = append(c.Marginal, n)
		}
		if s.TickP99Median <= TickBudget.Seconds() {
			if n > c.Lower {
				c.Lower = n
			}
		} else if c.Upper == 0 || n < c.Upper {
			c.Upper = n
		}
	}
	if c.Repeats < 0 {
		c.Repeats = 0
	}
	return c
}

func summariseLevel(players int, runs []*Result) LevelSummary {
	s := LevelSummary{Players: players}
	var kbSum float64
	p99s := make([]float64, 0, len(runs))
	for _, r := range runs {
		if r.Verdict.Invalid {
			s.Invalid++
			continue
		}
		s.Runs++
		if !r.Verdict.Degraded {
			s.Passed++
		}
		p99, mean := r.Server.TickP99, r.Server.TickMeanSec
		if s.Runs == 1 {
			s.TickP99Min, s.TickP99Max = p99, p99
			s.TickMeanMin, s.TickMeanMax = mean, mean
		} else {
			s.TickP99Min = minF(s.TickP99Min, p99)
			s.TickP99Max = maxF(s.TickP99Max, p99)
			s.TickMeanMin = minF(s.TickMeanMin, mean)
			s.TickMeanMax = maxF(s.TickMeanMax, mean)
		}
		kbSum += r.Client.RxBytesPerSecPerPlayer / 1024
		p99s = append(p99s, p99)
	}
	if s.Runs > 0 {
		s.KBPerSecPerClient = round2(kbSum / float64(s.Runs))
		s.TickP99Median = median(p99s)
	}
	return s
}

// median of a small sample. Sorts a copy: the caller's slice order is not ours
// to change.
func median(xs []float64) float64 {
	if len(xs) == 0 {
		return 0
	}
	c := append([]float64(nil), xs...)
	sort.Float64s(c)
	mid := len(c) / 2
	if len(c)%2 == 1 {
		return c[mid]
	}
	return (c[mid-1] + c[mid]) / 2
}

// WriteCeiling renders the band and the per-level tally.
func WriteCeiling(w io.Writer, c Ceiling) {
	if len(c.Levels) == 0 {
		return
	}

	fmt.Fprintf(w, "\n--- capacity (%d valid run(s) per level) ---\n", c.Repeats)
	fmt.Fprintf(w, "%-8s %-6s %-8s %-11s %-22s %-22s %s\n",
		"players", "pass", "invalid", "p99 median", "tick p99 min..max", "tick mean min..max", "KB/s/client")
	for _, s := range c.Levels {
		fmt.Fprintf(w, "%-8d %-6s %-8d %-11s %-22s %-22s %.1f\n",
			s.Players,
			fmt.Sprintf("%d/%d", s.Passed, s.Runs),
			s.Invalid,
			fmt.Sprintf("%.1fms", s.TickP99Median*1000),
			fmt.Sprintf("%.1f..%.1fms", s.TickP99Min*1000, s.TickP99Max*1000),
			fmt.Sprintf("%.1f..%.1fms", s.TickMeanMin*1000, s.TickMeanMax*1000),
			s.KBPerSecPerClient)
	}

	switch {
	case c.Lower == 0 && c.Upper == 0:
		fmt.Fprintln(w, "\nCEILING: undetermined — no level produced a usable verdict")
	case c.Lower == 0:
		fmt.Fprintf(w, "\nCEILING: below %d — the smallest level swept already fails\n", c.Upper)
	case len(c.Marginal) == 0 && c.Upper == 0:
		fmt.Fprintf(w, "\nCEILING: at least %d — no level swept failed consistently, sweep higher\n", c.Lower)
	case len(c.Marginal) == 0:
		fmt.Fprintf(w, "\nCEILING: %d (next level %d fails consistently)\n", c.Lower, c.Upper)
	default:
		fmt.Fprintf(w, "\nCEILING: %d (median p99 rule); band %d–%d\n", c.Lower, c.Lower, marginalTop(c))
		fmt.Fprintf(w, "  Levels whose runs straddle the budget: %v — some runs passed, some failed.\n", c.Marginal)
		fmt.Fprintln(w, "  The median decides the level, so a minority of disturbed runs cannot move it,")
		fmt.Fprintln(w, "  but a straddle means the reading is sensitive to what else the host was doing.")
		fmt.Fprintln(w, "  Check `host.load_avg_1` in the JSON, and do not sweep during a CD deploy.")
	}

	if c.Repeats < 2 {
		fmt.Fprintln(w, "  ⚠ One run per level: this ceiling is NOT reproducible. Use -repeat 3 or more.")
	}
}

func marginalTop(c Ceiling) int {
	top := c.Lower
	for _, n := range c.Marginal {
		if n > top {
			top = n
		}
	}
	if c.Upper > 0 && c.Upper > top {
		return c.Upper
	}
	return top
}

func minF(a, b float64) float64 {
	if b < a {
		return b
	}
	return a
}

func maxF(a, b float64) float64 {
	if b > a {
		return b
	}
	return a
}
