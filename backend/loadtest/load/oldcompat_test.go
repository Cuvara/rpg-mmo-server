package load

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Real result files written before the rate fields existed must still load,
// summarise and evaluate. A synthetic zero-valued struct is not the same test:
// these are the bytes on disk.
func TestPreMultiRateResultFilesStillSummarise(t *testing.T) {
	matches, err := filepath.Glob("../results/run-*.json")
	if err != nil || len(matches) == 0 {
		t.Fatal("no archived result files found — this test cannot skip itself into passing; fix the glob")
	}
	checked := 0
	for _, m := range matches {
		b, err := os.ReadFile(m)
		if err != nil {
			t.Fatalf("read %s: %v", m, err)
		}
		var res Result
		if err := json.Unmarshal(b, &res); err != nil {
			t.Fatalf("unmarshal %s: %v", m, err)
		}
		if res.Config.SnapshotPeriodSec != 0 || res.Config.RatesSource != "" {
			t.Fatalf("%s already has the new fields; pick an older fixture", m)
		}

		cfg := configOf([]*Result{&res})
		if cfg.TickBudgetSec <= 0 || cfg.SnapshotPeriodSec <= 0 ||
			cfg.SimCriticalHz <= 0 || cfg.SimWorldHz <= 0 {
			t.Fatalf("%s: configOf left a zero: %+v", m, cfg)
		}
		// The pre-ADR-13 server: one rate, both periods equal.
		if cfg.TickBudgetSec != cfg.SnapshotPeriodSec {
			t.Fatalf("%s: an old single-rate result should have equal periods, got %v/%v",
				m, cfg.TickBudgetSec, cfg.SnapshotPeriodSec)
		}

		var sb strings.Builder
		WriteSummary(&sb, []*Result{&res})
		out := sb.String()
		if !strings.Contains(out, "unrecorded") {
			t.Fatalf("%s: header must admit the rates were not recorded:\n%s", m, out)
		}
		checked++
	}
	t.Logf("checked %d archived result files", checked)
}
