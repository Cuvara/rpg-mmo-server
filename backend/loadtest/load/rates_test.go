package load

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestStatusURLDerivedFromMetricsURL(t *testing.T) {
	tests := []struct{ in, want string }{
		{"http://localhost:9101/metrics", "http://localhost:9101/status"},
		{"http://127.0.0.1:9101/metrics", "http://127.0.0.1:9101/status"},
		{"http://host:9101/", "http://host:9101/status"},
		{"http://host:9101", "http://host:9101/status"},
		{"", ""},
	}
	for _, tt := range tests {
		if got := statusURL(tt.in); got != tt.want {
			t.Errorf("statusURL(%q) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

func TestFetchServerRatesReadsBothRates(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/status" {
			t.Errorf("fetched %q, want /status", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"sim_critical_hz":60,"sim_world_hz":15,"tick_rate":60}`))
	}))
	defer srv.Close()

	rates, err := FetchServerRates(context.Background(), srv.Client(), srv.URL+"/metrics")
	if err != nil {
		t.Fatalf("FetchServerRates: %v", err)
	}
	if rates.CriticalHz != 60 || rates.WorldHz != 15 {
		t.Fatalf("rates = %+v, want critical 60 world 15", rates)
	}
	if rates.Source != "server /status" {
		t.Errorf("Source = %q, want the server", rates.Source)
	}

	// The whole point: the two periods are NOT the same number at 60/15, and the
	// harness used one constant for both until this existed.
	if got, want := rates.TickBudget(), time.Second/60; got != want {
		t.Errorf("TickBudget = %s, want %s", got, want)
	}
	if got, want := rates.SnapshotPeriod(), time.Second/15; got != want {
		t.Errorf("SnapshotPeriod = %s, want %s", got, want)
	}
	if rates.TickBudget() == rates.SnapshotPeriod() {
		t.Error("tick budget and snapshot period came out equal at 60/15; " +
			"they must not, or nothing about this change is doing anything")
	}
}

// A failure must be labelled, not silently substituted: a run judged against
// rates nobody confirmed is still a run, but a reader has to be told.
func TestFetchServerRatesLabelsEveryFallback(t *testing.T) {
	unreachable := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNotFound)
	}))
	defer unreachable.Close()

	noRates := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// A server predating the multi-rate fields. Absent and zero are the same
		// bytes here, so zero has to be treated as absent rather than divided by.
		_, _ = w.Write([]byte(`{"map_id":"map_01"}`))
	}))
	defer noRates.Close()

	junk := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`not json`))
	}))
	defer junk.Close()

	cases := []struct{ name, url string }{
		{"http error", unreachable.URL + "/metrics"},
		{"no rates in body", noRates.URL + "/metrics"},
		{"unparseable body", junk.URL + "/metrics"},
		{"no metrics url", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			rates, err := FetchServerRates(context.Background(), http.DefaultClient, c.url)
			if err == nil {
				t.Fatal("want an error")
			}
			if !strings.HasPrefix(rates.Source, "ASSUMED") {
				t.Errorf("Source = %q, want it to announce the assumption", rates.Source)
			}
			if rates.TickBudget() != TickBudget || rates.SnapshotPeriod() != TickBudget {
				t.Errorf("fallback periods = %s/%s, want the pre-multi-rate %s",
					rates.TickBudget(), rates.SnapshotPeriod(), TickBudget)
			}
		})
	}
}

// The regression this change exists for, stated as the two verdicts it gets
// wrong on a 60/15 server when one constant serves both rates.
func TestEvaluateSeparatesTickBudgetFromSnapshotPeriod(t *testing.T) {
	sixtyFifteen := func() *Result {
		return &Result{
			Config: ResultConfig{
				TickBudgetSec:     1.0 / 60.0, // 16.67ms
				SnapshotPeriodSec: 1.0 / 15.0, // 66.67ms
				SimCriticalHz:     60,
				SimWorldHz:        15,
				RatesSource:       "server /status",
			},
			Client: ClientStats{
				PlayersRequested: 10, PlayersJoined: 10,
				SnapshotInterval:       Dist{Count: 100, P99: 70}, // ms — normal at 15Hz
				SnapshotsReceivedRatio: 1.0,
			},
			Server: ServerStats{Scraped: true, TickCount: 100, TickP99: 0.001},
		}
	}

	t.Run("a base tick 2.4x over its own budget must fail", func(t *testing.T) {
		res := sixtyFifteen()
		// 40ms. Comfortably inside the old shared 66.67ms constant, which is
		// exactly how a server 2.4x over budget was reported as passing.
		res.Server.TickP99 = 0.040
		if v := Evaluate(res); v.TickBudgetOK {
			t.Fatalf("tick p99 40ms passed a %.2fms budget: %+v",
				res.Config.TickBudgetSec*1000, v)
		}
	})

	t.Run("a normal 15Hz snapshot interval must not fail", func(t *testing.T) {
		res := sixtyFifteen()
		// 70ms p99 is what a healthy 15Hz cadence looks like. Judged against 2x
		// the BASE period it would be 70 > 33.3 and every level in every sweep
		// would have been condemned.
		if v := Evaluate(res); !v.SnapshotCadenceOK {
			t.Fatalf("a healthy 66.7ms cadence failed: %+v", v)
		}
	})

	t.Run("a genuinely late snapshot still fails", func(t *testing.T) {
		res := sixtyFifteen()
		res.Client.SnapshotInterval.P99 = 200 // > 2 x 66.67ms
		if v := Evaluate(res); v.SnapshotCadenceOK {
			t.Fatalf("a 200ms cadence passed: %+v", v)
		}
	})

	t.Run("an old result with no periods falls back and still evaluates", func(t *testing.T) {
		res := sixtyFifteen()
		res.Config.TickBudgetSec = 0
		res.Config.SnapshotPeriodSec = 0
		res.Server.TickP99 = 0.040 // inside the 66.67ms fallback
		if v := Evaluate(res); !v.TickBudgetOK || !v.SnapshotCadenceOK {
			t.Fatalf("a pre-multi-rate result should evaluate as it always did: %+v", v)
		}
	})
}
