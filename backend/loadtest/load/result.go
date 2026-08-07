package load

import "time"

// Result is the machine-readable outcome of one load run. It is the unit of
// comparison across a sweep: two runs at different player counts differ only in
// the fields below, so a sweep is just a list of these.
type Result struct {
	Schema  string    `json:"schema"`
	Label   string    `json:"label,omitempty"`
	RunID   string    `json:"run_id"`
	Started time.Time `json:"started"`

	Config ResultConfig `json:"config"`
	Client ClientStats  `json:"client"`
	Server ServerStats  `json:"server"`
	// Host records what else the machine was doing. Evidence for a human, never
	// an input to the verdict — see HostStats for why that distinction is load
	// bearing.
	Host HostStats `json:"host"`

	// Verdict summarises the acceptance checks; see Evaluate.
	Verdict Verdict `json:"verdict"`
}

// ResultSchema versions the JSON so a later sweep can be compared safely.
const ResultSchema = "rpg-mmo.loadtest/v1"

// ResultConfig is the subset of Config that changes a measurement.
type ResultConfig struct {
	Players      int     `json:"players"`
	RampRate     float64 `json:"ramp_players_per_sec"`
	DurationSec  float64 `json:"duration_sec"`
	ClientTickHz int     `json:"client_tick_hz"`
	AuthMode     string  `json:"auth_mode"`
	Movement     string  `json:"movement"`
	// Encoding is the wire encoding the virtual players spoke ("json" or
	// "proto"). Recorded so a result file is self-describing: the two encodings
	// produce very different bandwidth numbers from identical load parameters.
	Encoding      string  `json:"encoding"`
	MapID         string  `json:"map_id"`
	Transport     string  `json:"transport"`
	HoldGateway   bool    `json:"hold_gateway"`
	TickBudgetSec float64 `json:"tick_budget_sec"`
}

// ClientStats is everything the virtual clients observed themselves.
type ClientStats struct {
	PlayersRequested int `json:"players_requested"`
	PlayersJoined    int `json:"players_joined"`
	PlayersFailed    int `json:"players_failed"`
	// FailuresByPhase counts connection failures by lifecycle phase.
	FailuresByPhase map[string]int `json:"failures_by_phase,omitempty"`
	// SampleErrors keeps a few representative error strings for the write-up.
	SampleErrors []string `json:"sample_errors,omitempty"`

	SnapshotInterval Dist `json:"snapshot_interval"`
	AckLatency       Dist `json:"ack_latency"`
	JoinLatency      Dist `json:"join_latency"`

	// Resyncs counts keyframes requested because a snapshot referenced an entity
	// handle the client had no binding for. Non-zero means client and server
	// disagreed about interning state; a run that resyncs constantly is
	// reconstructing far less than its snapshot count suggests.
	Resyncs        int `json:"resyncs,omitempty"`
	SnapshotsTotal int `json:"snapshots_total"`
	KeyframesTotal int `json:"keyframes_total"`
	DeltasTotal    int `json:"deltas_total"`
	InputsTotal    int `json:"inputs_total"`

	// Throughput, measured over the window and reported per connected player.
	RxBytesPerSecPerPlayer float64 `json:"rx_bytes_per_sec_per_player"`
	TxBytesPerSecPerPlayer float64 `json:"tx_bytes_per_sec_per_player"`
	RxBytesPerSecTotal     float64 `json:"rx_bytes_per_sec_total"`
	TxBytesPerSecTotal     float64 `json:"tx_bytes_per_sec_total"`

	// SnapshotsReceivedRatio is snapshots actually received divided by the
	// snapshots the server says it enqueued for this window. Below 1.0 means the
	// server dropped frames: Connection.cs uses a bounded channel of 64 with
	// DropOldest, so a client the writer cannot keep up with loses snapshots
	// silently rather than blocking the tick.
	SnapshotsReceivedRatio float64 `json:"snapshots_received_ratio,omitempty"`
}

// ServerStats is what /metrics reported, differenced across the window.
type ServerStats struct {
	Scraped bool `json:"scraped"`

	// TickP50/P95/P99 are interpolated from the tick histogram delta, seconds.
	TickP50 float64 `json:"tick_p50_sec"`
	TickP95 float64 `json:"tick_p95_sec"`
	TickP99 float64 `json:"tick_p99_sec"`
	// TickMeanSec is exact: sum delta / count delta, no bucket interpolation.
	TickMeanSec float64 `json:"tick_mean_sec"`
	TickCount   float64 `json:"tick_count"`
	// TickOverBudgetRatio is the exact fraction of ticks above the largest
	// histogram edge that is still <= the budget (see TickBudgetExceededRatio).
	TickOverBudgetRatio float64 `json:"tick_over_budget_ratio"`
	TickOverBudgetEdge  float64 `json:"tick_over_budget_edge_sec"`
	// TicksPerSec is the achieved simulation rate. Below the configured tick
	// rate the server is not merely slow per tick, it is losing whole ticks.
	TicksPerSec float64 `json:"ticks_per_sec"`

	PlayersOnline       float64 `json:"players_online"`
	Entities            float64 `json:"entities"`
	SnapshotsSent       float64 `json:"snapshots_sent_window"`
	ProcessedInputs     float64 `json:"processed_inputs_window"`
	GatewayConnsActive  float64 `json:"gateway_connections_active"`
	GatewayAuthOK       float64 `json:"gateway_auth_ok_window"`
	GatewayAuthFail     float64 `json:"gateway_auth_fail_window"`
	GatewayEnterOK      float64 `json:"gateway_enter_world_ok_window"`
	GatewayEnterFail    float64 `json:"gateway_enter_world_fail_window"`
	GatewayRateLimited  float64 `json:"gateway_rate_limited_window"`
	ScrapeErrGameserver string  `json:"scrape_error_gameserver,omitempty"`
	ScrapeErrGateway    string  `json:"scrape_error_gateway,omitempty"`

	// RestartedMidRun is set when a monotonic counter went backwards between the
	// two scrapes, which can only mean the process restarted. On a shared dev box
	// a concurrent redeploy will otherwise masquerade as a load-induced failure:
	// every client sees "server closed the connection" and the run looks like it
	// found a breaking point when it found a deployment.
	RestartedMidRun bool `json:"restarted_mid_run,omitempty"`
}

// Verdict records which acceptance criteria held. A run is Degraded when any
// one of them fails; the sweep stops climbing at the first degraded level.
type Verdict struct {
	// TickBudgetOK: gameserver tick p99 <= the 15Hz period (66.67ms). ADR-7's
	// stated acceptance threshold.
	TickBudgetOK bool `json:"tick_budget_ok"`
	// SnapshotCadenceOK: client-observed snapshot interval p99 <= 2x the tick
	// period. A server that ticks on time but cannot drain its send queues shows
	// up here and nowhere else.
	SnapshotCadenceOK bool `json:"snapshot_cadence_ok"`
	// NoErrors: every requested player joined and none dropped mid-run.
	NoErrors bool `json:"no_errors"`
	// NoFrameLoss: the server did not silently drop queued snapshots.
	NoFrameLoss bool `json:"no_frame_loss"`

	// Invalid marks a level that did not actually measure what it claims, as
	// opposed to one that measured a server failing to keep up. An INVALID level
	// must be excluded from every aggregate and rerun — its numbers are not a
	// worse result, they are not a result.
	//
	// This exists because the distinction is not visible in the headline figures
	// and the failure can point in the flattering direction. A sweep level where
	// every client died mid-run reported a 97% bandwidth saving: the clients
	// received almost nothing, and bytes-not-received read as bytes-not-sent.
	// See backend/docs/BENCHMARK.md.
	Invalid bool `json:"invalid"`

	Degraded bool   `json:"degraded"`
	Reason   string `json:"reason,omitempty"`
}
