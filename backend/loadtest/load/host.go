package load

import (
	"os"
	"runtime"
	"strconv"
	"strings"
)

// HostStats records what else the machine was doing during a run.
//
// # Why this is recorded and not judged
//
// The load generator shares a host with the self-hosted CD runner, and a deploy
// executing mid-run visibly moves the numbers: at a fixed 200-player level, tick
// p99 measured 72.9ms with the box quiet and 240.6ms with a CD build in flight,
// a 3.3x swing, while the tick mean moved 1.7x.
//
// It is tempting to turn that into a validity check — "achieved tick rate below
// the configured rate means the process was starved, so the run is INVALID". That
// was tried and it is WRONG, because a genuinely saturated server loses ticks the
// same way: measured 10.46 ticks/s at 300 players and 12.51 at 400, both real
// capacity limits, against 12.87–13.48 for a quiet box disturbed by a deploy. The
// two are indistinguishable from the server's own metrics, so a rule keyed on
// them would classify real saturation as an environment fault and hide the
// capacity ceiling — an error in the optimistic direction, which is the worse one.
//
// So the host state is recorded as evidence for a human, never as a verdict. A
// reader comparing two sweeps can see whether the machine was comparably loaded;
// the tool does not pretend it can tell why.
type HostStats struct {
	// LoadAvg1/5/15 are the standard Unix load averages at the END of the
	// measurement window, so they describe the window rather than whatever
	// preceded it. Zero when unavailable (non-Linux).
	LoadAvg1  float64 `json:"load_avg_1,omitempty"`
	LoadAvg5  float64 `json:"load_avg_5,omitempty"`
	LoadAvg15 float64 `json:"load_avg_15,omitempty"`
	// CPUs is the logical core count, so a load average can be read as a ratio
	// rather than an absolute that means different things on different machines.
	CPUs int `json:"cpus,omitempty"`
}

// LoadPerCPU is the 1-minute load average divided by core count. Roughly 1.0
// means the machine was saturated; well above means the run was competing for
// CPU with something else and its absolute timings should not be compared
// against a run taken on a quieter box.
func (h HostStats) LoadPerCPU() float64 {
	if h.CPUs == 0 {
		return 0
	}
	return h.LoadAvg1 / float64(h.CPUs)
}

// ReadHostStats samples the host's load. Never fails: an unavailable reading is
// reported as zero rather than as an error, because this is context for a human
// and a missing sample must not fail a run that was otherwise fine.
func ReadHostStats() HostStats {
	h := HostStats{CPUs: runtime.NumCPU()}

	b, err := os.ReadFile("/proc/loadavg")
	if err != nil {
		return h
	}
	fields := strings.Fields(string(b))
	if len(fields) < 3 {
		return h
	}
	h.LoadAvg1, _ = strconv.ParseFloat(fields[0], 64)
	h.LoadAvg5, _ = strconv.ParseFloat(fields[1], 64)
	h.LoadAvg15, _ = strconv.ParseFloat(fields[2], 64)
	return h
}
