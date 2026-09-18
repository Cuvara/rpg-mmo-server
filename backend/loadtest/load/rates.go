package load

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"time"
)

// ServerRates is the server's own report of the two simulation rates that the
// acceptance criteria are defined against.
//
// # Why these are fetched rather than assumed
//
// Before the multi-rate scheduler (ADR-13) the server ran one 15Hz loop, so the
// tick period and the snapshot period were the same 66.67ms and one constant
// served both. They are no longer the same number and have not been since the
// default became 60/15:
//
//   - gameserver_tick_duration_seconds measures a BASE tick, which runs at
//     SIM_CRITICAL_HZ. At the 60Hz default its budget is 16.67ms, and comparing
//     it against 66.67ms is comparing against a budget four times too generous —
//     a server taking 60ms per base tick, which is 3.6x over, would be reported
//     as comfortably passing. BENCHMARK.md Part VI recorded this and it was never
//     fixed.
//   - Snapshot interval is governed by SIM_WORLD_HZ, because replication is gated
//     to the world group (ADR-13 decision 7). At the default that is still
//     66.67ms. So the fix is NOT to narrow one number: it is to stop using one
//     number for two independent rates. Narrowing the shared constant to 1/60
//     would have failed every level on snapshot cadence, correctly reporting a
//     66.7ms interval as breaching a 33.3ms bound that no server was ever meant
//     to meet.
//
// Neither rate is on /metrics, so this reads /status, which publishes both.
type ServerRates struct {
	// CriticalHz is SIM_CRITICAL_HZ: the base timeline, and what
	// gameserver_tick_duration_seconds times.
	CriticalHz float64
	// WorldHz is SIM_WORLD_HZ: AI, spawning, and the snapshot broadcast cadence.
	WorldHz float64
	// Source names where the numbers came from, for the report header. A run
	// judged against assumed rates must say so: the alternative is a table that
	// looks identical whether or not it knew what it was measuring.
	Source string
}

// TickBudget is the per-base-tick budget these rates imply.
func (r ServerRates) TickBudget() time.Duration {
	if r.CriticalHz <= 0 {
		return TickBudget
	}
	return time.Duration(float64(time.Second) / r.CriticalHz)
}

// SnapshotPeriod is the expected interval between snapshots these rates imply.
func (r ServerRates) SnapshotPeriod() time.Duration {
	if r.WorldHz <= 0 {
		return TickBudget
	}
	return time.Duration(float64(time.Second) / r.WorldHz)
}

// AssumedRates is the fallback when the server cannot be asked: the pre-ADR-13
// single-rate server, which is what the harness assumed unconditionally before
// this existed.
func AssumedRates(reason string) ServerRates {
	return ServerRates{
		CriticalHz: DefaultTickRate,
		WorldHz:    DefaultTickRate,
		Source:     "ASSUMED " + reason,
	}
}

// statusURL turns a /metrics URL into the /status URL on the same listener.
// Both are served by the game server's metrics endpoint.
func statusURL(metricsURL string) string {
	if metricsURL == "" {
		return ""
	}
	if i := strings.LastIndex(metricsURL, "/metrics"); i >= 0 {
		return metricsURL[:i] + "/status"
	}
	return strings.TrimRight(metricsURL, "/") + "/status"
}

// FetchServerRates reads SIM_CRITICAL_HZ and SIM_WORLD_HZ off the server's
// /status endpoint.
//
// A failure is not fatal and not silent: the caller falls back to AssumedRates,
// which labels itself so the report header can say the run was judged against
// rates nobody confirmed.
func FetchServerRates(ctx context.Context, hc *http.Client, metricsURL string) (ServerRates, error) {
	u := statusURL(metricsURL)
	if u == "" {
		return AssumedRates("(no metrics URL configured)"), fmt.Errorf("no metrics URL")
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return AssumedRates("(bad status URL)"), err
	}
	resp, err := hc.Do(req)
	if err != nil {
		return AssumedRates("(/status unreachable)"), err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return AssumedRates(fmt.Sprintf("(/status HTTP %d)", resp.StatusCode)),
			fmt.Errorf("status %d", resp.StatusCode)
	}

	var body struct {
		CriticalHz float64 `json:"sim_critical_hz"`
		WorldHz    float64 `json:"sim_world_hz"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return AssumedRates("(/status unparseable)"), err
	}

	// A server predating the multi-rate fields sends neither, and proto3-style
	// omission is indistinguishable from zero here. Zero is not a rate, so it is
	// treated as absent rather than as a divide-by-zero waiting to happen.
	if body.CriticalHz <= 0 || body.WorldHz <= 0 {
		return AssumedRates("(/status reports no rates)"),
			fmt.Errorf("status reported critical=%v world=%v", body.CriticalHz, body.WorldHz)
	}

	return ServerRates{
		CriticalHz: body.CriticalHz,
		WorldHz:    body.WorldHz,
		Source:     "server /status",
	}, nil
}
