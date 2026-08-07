package load

import (
	"math"
	"testing"
	"time"
)

// Trimmed from a real gameserver + gateway /metrics response.
const sampleMetrics = `# HELP gameserver_entities Entities currently present in the world.
gameserver_entities{} 42
# HELP gameserver_players_online Players currently connected to this server.
gameserver_players_online{map_id="map_01"} 42
# HELP gameserver_snapshots_sent_total Snapshots enqueued to player connections.
gameserver_snapshots_sent_total{map_id="map_01"} 1000
# HELP gameserver_tick_duration_seconds Wall-clock duration of a single simulation tick.
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.00050000000000000001"} 10
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.050000000000000003"} 90
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.074999999999999997"} 100
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="+Inf"} 100
gameserver_tick_duration_seconds_sum{map_id="map_01"} 2.5
gameserver_tick_duration_seconds_count{map_id="map_01"} 100
# HELP gateway_auth_total Client authentication attempts by result.
gateway_auth_total{result="fail"} 3
gateway_auth_total{result="ok"} 7
# HELP gateway_connections_active Client connections currently held by the gateway.
gateway_connections_active 5
`

func TestScrapeText(t *testing.T) {
	s := scrapeText(sampleMetrics, time.Now())

	tests := []struct {
		name string
		want float64
	}{
		{"gameserver_entities", 42},
		{"gameserver_players_online", 42},
		{"gameserver_snapshots_sent_total", 1000},
		{"gameserver_tick_duration_seconds_sum", 2.5},
		{"gameserver_tick_duration_seconds_count", 100},
		{"gateway_connections_active", 5},
		// The family total sums both outcomes...
		{"gateway_auth_total", 10},
		// ...and each outcome is also kept separately, which is what the report
		// needs: summing ok+fail would report every failure as a success.
		{"gateway_auth_total|result=ok", 7},
		{"gateway_auth_total|result=fail", 3},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := s.Get(tt.name); got != tt.want {
				t.Errorf("Get(%q) = %v, want %v", tt.name, got, tt.want)
			}
		})
	}

	buckets := s.Buckets["gameserver_tick_duration_seconds"]
	if len(buckets) != 4 {
		t.Fatalf("got %d buckets, want 4", len(buckets))
	}
	if buckets[0.05] != 90 {
		t.Errorf("0.05 bucket = %v, want 90", buckets[0.05])
	}
	if !math.IsInf(inf(buckets), 1) {
		t.Error("missing +Inf bucket")
	}
}

func inf(buckets map[float64]float64) float64 {
	for b := range buckets {
		if math.IsInf(b, 1) {
			return b
		}
	}
	return 0
}

func TestScrapeTextIgnoresGarbage(t *testing.T) {
	s := scrapeText("# comment only\n\nnot_a_metric\nbad_value abc\ngood_metric 1\n", time.Now())
	if got := s.Get("good_metric"); got != 1 {
		t.Errorf("good_metric = %v, want 1", got)
	}
	if got := s.Get("bad_value"); got != 0 {
		t.Errorf("bad_value = %v, want 0 (unparseable)", got)
	}
}

func TestScrapeNilGet(t *testing.T) {
	var s *Scrape
	if got := s.Get("anything"); got != 0 {
		t.Errorf("nil Scrape Get = %v, want 0", got)
	}
}

// BucketDelta must isolate the measurement window: a histogram read once would
// average in every tick since process start.
func TestBucketDelta(t *testing.T) {
	before := scrapeText(sampleMetrics, time.Now())
	after := scrapeText(`gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.00050000000000000001"} 10
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.050000000000000003"} 140
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="0.074999999999999997"} 200
gameserver_tick_duration_seconds_bucket{map_id="map_01",le="+Inf"} 200
`, time.Now())

	d := BucketDelta(before, after, "gameserver_tick_duration_seconds")
	// A zero-delta bucket must be KEPT, not dropped: the histogram is cumulative,
	// so the edge itself carries meaning even when nothing landed in it.
	if _, ok := d[0.0005]; !ok {
		t.Error("zero-delta bucket was dropped; the 0.0005 edge must survive")
	}
	if d[0.0005] != 0 {
		t.Errorf("0.0005 delta = %v, want 0", d[0.0005])
	}
	if d[0.05] != 50 {
		t.Errorf("0.05 delta = %v, want 50", d[0.05])
	}
	if d[0.075] != 100 {
		t.Errorf("0.075 delta = %v, want 100", d[0.075])
	}
	// Over the window, 50 of the 100 new ticks were above the 0.05 edge.
	ratio, edge := TickBudgetExceededRatio(d, TickBudget.Seconds())
	if edge != 0.05 {
		t.Fatalf("edge = %v, want 0.05", edge)
	}
	if math.Abs(ratio-0.5) > 1e-9 {
		t.Errorf("over-budget ratio = %v, want 0.5", ratio)
	}
}

func TestBucketDeltaNilAfter(t *testing.T) {
	if got := BucketDelta(nil, nil, "x"); got != nil {
		t.Errorf("BucketDelta(nil, nil) = %v, want nil", got)
	}
}

func TestLabelValue(t *testing.T) {
	tests := []struct {
		labels, want string
		key          string
		ok           bool
	}{
		{`map_id="map_01",le="0.5"`, "0.5", "le", true},
		{`map_id="map_01"`, "map_01", "map_id", true},
		{`map_id="map_01"`, "", "le", false},
		{``, "", "le", false},
	}
	for _, tt := range tests {
		got, ok := labelValue(tt.labels, tt.key)
		if ok != tt.ok || got != tt.want {
			t.Errorf("labelValue(%q, %q) = (%q, %v), want (%q, %v)",
				tt.labels, tt.key, got, ok, tt.want, tt.ok)
		}
	}
}

// Regression: under heavy load no tick lands in the sub-millisecond buckets, so
// their deltas are zero. If those edges are dropped, no edge at or below the
// budget survives and the over-budget ratio silently reports 0% for a server
// that is missing its deadline on every single tick.
func TestBucketDeltaKeepsEdgesUnderHeavyLoad(t *testing.T) {
	before := scrapeText(`x_bucket{le="0.0005"} 100
x_bucket{le="0.05"} 100
x_bucket{le="0.075"} 100
x_bucket{le="0.5"} 100
x_bucket{le="+Inf"} 100
`, time.Now())
	// 50 new ticks, every one of them slower than 75ms.
	after := scrapeText(`x_bucket{le="0.0005"} 100
x_bucket{le="0.05"} 100
x_bucket{le="0.075"} 100
x_bucket{le="0.5"} 150
x_bucket{le="+Inf"} 150
`, time.Now())

	d := BucketDelta(before, after, "x")
	ratio, edge := TickBudgetExceededRatio(d, TickBudget.Seconds())
	if edge != 0.05 {
		t.Fatalf("edge = %v, want the 0.05 edge to survive", edge)
	}
	if ratio != 1 {
		t.Errorf("over-budget ratio = %v, want 1 (every tick blew the budget)", ratio)
	}
}
