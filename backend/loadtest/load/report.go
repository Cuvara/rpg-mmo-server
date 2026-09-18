package load

import (
	"encoding/json"
	"fmt"
	"io"
	"math"
	"strings"
)

// WriteJSON serialises results (one run, or a whole sweep) as indented JSON.
func WriteJSON(w io.Writer, results []*Result) error {
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	if len(results) == 1 {
		return enc.Encode(results[0])
	}
	return enc.Encode(results)
}

// tableCols is the compact run table. Every column is something the ADR-7
// acceptance criteria or the bottleneck analysis actually needs; nothing is
// there for decoration.
var tableCols = []struct {
	head string
	get  func(*Result) string
}{
	{"players", func(r *Result) string { return fmt.Sprint(r.Config.Players) }},
	{"joined", func(r *Result) string { return fmt.Sprint(r.Client.PlayersJoined) }},
	{"move", func(r *Result) string { return r.Config.Movement }},
	{"tick p50", func(r *Result) string { return ms(r.Server.TickP50) }},
	{"tick p99", func(r *Result) string { return ms(r.Server.TickP99) }},
	{"tick mean", func(r *Result) string { return ms(r.Server.TickMeanSec) }},
	{"over budget", func(r *Result) string { return pct(r.Server.TickOverBudgetRatio) }},
	{"ticks/s", func(r *Result) string { return fmt.Sprintf("%.2f", r.Server.TicksPerSec) }},
	{"snap p50", func(r *Result) string { return fmt.Sprintf("%.1fms", r.Client.SnapshotInterval.P50) }},
	{"snap p99", func(r *Result) string { return fmt.Sprintf("%.1fms", r.Client.SnapshotInterval.P99) }},
	{"ack p50", func(r *Result) string { return fmt.Sprintf("%.1fms", r.Client.AckLatency.P50) }},
	{"ack p99", func(r *Result) string { return fmt.Sprintf("%.1fms", r.Client.AckLatency.P99) }},
	{"rx B/s/p", func(r *Result) string { return fmt.Sprintf("%.0f", r.Client.RxBytesPerSecPerPlayer) }},
	{"tx B/s/p", func(r *Result) string { return fmt.Sprintf("%.0f", r.Client.TxBytesPerSecPerPlayer) }},
	{"recv%", func(r *Result) string { return pct(r.Client.SnapshotsReceivedRatio) }},
	{"fail", func(r *Result) string { return fmt.Sprint(r.Client.PlayersFailed) }},
	{"verdict", func(r *Result) string {
		switch {
		case r.Verdict.Invalid:
			return "INVALID"
		case r.Verdict.Degraded:
			return "DEGRADED"
		}
		return "ok"
	}},
}

// WriteTable renders the compact human-readable comparison table.
func WriteTable(w io.Writer, results []*Result) {
	widths := make([]int, len(tableCols))
	rows := make([][]string, 0, len(results))
	for i, c := range tableCols {
		widths[i] = len(c.head)
	}
	for _, r := range results {
		row := make([]string, len(tableCols))
		for i, c := range tableCols {
			row[i] = c.get(r)
			if len(row[i]) > widths[i] {
				widths[i] = len(row[i])
			}
		}
		rows = append(rows, row)
	}

	var sb strings.Builder
	for i, c := range tableCols {
		sb.WriteString(pad(c.head, widths[i]))
		if i < len(tableCols)-1 {
			sb.WriteString("  ")
		}
	}
	fmt.Fprintln(w, sb.String())
	fmt.Fprintln(w, strings.Repeat("-", sb.Len()))
	for _, row := range rows {
		var line strings.Builder
		for i, v := range row {
			line.WriteString(pad(v, widths[i]))
			if i < len(row)-1 {
				line.WriteString("  ")
			}
		}
		fmt.Fprintln(w, line.String())
	}
}

// WriteSummary prints the table plus a per-run verdict line and the headline
// configOf returns the rate configuration these results were measured against,
// filling the pre-multi-rate defaults for a result file that predates the fields
// so an old sweep still prints a coherent header.
func configOf(results []*Result) ResultConfig {
	for _, r := range results {
		if r != nil && r.Config.TickBudgetSec > 0 {
			c := r.Config
			if c.SnapshotPeriodSec <= 0 {
				c.SnapshotPeriodSec = c.TickBudgetSec
			}
			if c.SimCriticalHz <= 0 {
				c.SimCriticalHz = 1 / c.TickBudgetSec
			}
			if c.SimWorldHz <= 0 {
				c.SimWorldHz = 1 / c.SnapshotPeriodSec
			}
			return c
		}
	}
	return ResultConfig{
		TickBudgetSec:     TickBudget.Seconds(),
		SnapshotPeriodSec: TickBudget.Seconds(),
		SimCriticalHz:     DefaultTickRate,
		SimWorldHz:        DefaultTickRate,
	}
}

// finding: the highest player count that met every acceptance criterion.
func WriteSummary(w io.Writer, results []*Result) {
	if len(results) == 0 {
		return
	}
	// The encoding is in the header because the two arms differ ~5x in bytes per
	// client from identical load, and a table that does not say which one it
	// drove has already been read as the other one once.
	// Both rates, both periods, and where they came from. The header used to read
	// "tick budget 66.67ms @ 15Hz" whatever the server was configured to, which
	// at the 60/15 default named a budget four times too generous and a rate the
	// server had not run at since ADR-13. A header that states the wrong
	// configuration confidently is worse than one that states none.
	cfg := configOf(results)
	src := cfg.RatesSource
	if src == "" {
		src = "unrecorded (result predates rate reporting)"
	}
	fmt.Fprintf(w,
		"\n--- loadtest results (tick budget %.2fms @ %gHz critical, snapshot period %.2fms @ %gHz world, rates from %s, encoding=%s) ---\n",
		cfg.TickBudgetSec*1000, cfg.SimCriticalHz,
		cfg.SnapshotPeriodSec*1000, cfg.SimWorldHz,
		src, encodingOf(results))
	WriteTable(w, results)

	fmt.Fprintln(w)
	best, invalid := 0, 0
	for _, r := range results {
		switch {
		case r.Verdict.Invalid:
			// Excluded from the aggregate entirely, not counted as a failing
			// level: an invalid level says nothing about capacity in either
			// direction, so letting it cap `best` would understate the ceiling
			// exactly as letting it pass would overstate it.
			invalid++
			fmt.Fprintf(w, "  players=%-5d %s\n", r.Config.Players, r.Verdict.Reason)
		case r.Verdict.Degraded:
			fmt.Fprintf(w, "  players=%-5d DEGRADED: %s\n", r.Config.Players, r.Verdict.Reason)
		default:
			if r.Config.Players > best {
				best = r.Config.Players
			}
		}
	}
	if best > 0 {
		fmt.Fprintf(w, "\nHIGHEST PASSING LEVEL: %d players on one game server\n", best)
	} else {
		fmt.Fprintln(w, "\nHIGHEST PASSING LEVEL: none — every level degraded")
	}
	if invalid > 0 {
		fmt.Fprintf(w, "%d level(s) were INVALID and excluded — the sweep above is incomplete, rerun them.\n", invalid)
	}
}

func ms(sec float64) string {
	if math.IsNaN(sec) {
		return "n/a"
	}
	return fmt.Sprintf("%.2fms", sec*1000)
}

func pct(ratio float64) string {
	if math.IsNaN(ratio) || ratio == 0 {
		return "0%"
	}
	return fmt.Sprintf("%.2f%%", ratio*100)
}

func pad(s string, w int) string {
	if len(s) >= w {
		return s
	}
	return s + strings.Repeat(" ", w-len(s))
}

// encodingOf names the wire encoding a set of results drove, or "mixed" when a
// sweep deliberately spans both arms (scripts/encoding-sweep.sh).
func encodingOf(results []*Result) string {
	enc := ""
	for _, r := range results {
		switch {
		case enc == "":
			enc = r.Config.Encoding
		case enc != r.Config.Encoding:
			return "mixed"
		}
	}
	if enc == "" {
		return "unknown"
	}
	return enc
}
