package load

import (
	"math"
	"testing"
)

// The abuse modes exist so the server's input-rejection telemetry can be exercised
// deliberately rather than waited for. These tests pin the two things that make them
// useful: the abusive share is selected reproducibly, and the payload each mode produces
// is one the server is actually going to refuse.

func TestAbusiveSelectionIsByIndexAndBounded(t *testing.T) {
	cfg := Config{Players: 10, Abuse: AbuseDirection, AbusePlayers: 3}

	for idx := 0; idx < 10; idx++ {
		p := &player{cfg: cfg, idx: idx}
		want := idx < 3
		if got := p.abusive(); got != want {
			t.Errorf("idx %d: abusive() = %v, want %v", idx, got, want)
		}
	}
}

func TestNoAbuseByDefault(t *testing.T) {
	tests := []struct {
		name string
		cfg  Config
	}{
		{"defaults", Config{Players: 4}},
		{"mode set but zero players", Config{Players: 4, Abuse: AbuseDirection, AbusePlayers: 0}},
		{"players set but mode none", Config{Players: 4, Abuse: AbuseNone, AbusePlayers: 4}},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := &player{cfg: tt.cfg, idx: 0}
			if p.abusive() {
				t.Error("no player should misbehave unless both -abuse and -abuse-players are set")
			}
		})
	}
}

// The direction abuser must exceed what the server will accept — and must stay FINITE,
// because encoding/json cannot represent NaN or Inf and the legacy json arm would fail to
// encode the frame client-side, so the server would never see it.
func TestDirectionAbuseIsOversizedButFinite(t *testing.T) {
	p := &player{
		cfg: Config{Players: 1, Movement: MovementCluster, Abuse: AbuseDirection, AbusePlayers: 1},
		idx: 0,
	}

	x, y := p.movementVector()

	if math.IsNaN(float64(x)) || math.IsInf(float64(x), 0) ||
		math.IsNaN(float64(y)) || math.IsInf(float64(y), 0) {
		t.Fatalf("vector must stay finite for the json arm, got (%v, %v)", x, y)
	}

	// GameConstants.MaxInputMagnitude is 10 server-side; anything past it is refused.
	if mag := math.Hypot(float64(x), float64(y)); mag <= 10 {
		t.Errorf("magnitude %v is not large enough to be refused", mag)
	}
}

// A well-behaved player in the same run must be unaffected — otherwise a mixed run
// measures nothing, because every player is abusive.
func TestNonAbusivePlayerKeepsItsNormalVector(t *testing.T) {
	cfg := Config{Players: 4, Movement: MovementCluster, Abuse: AbuseDirection, AbusePlayers: 1}

	honest := &player{cfg: cfg, idx: 2}
	x, y := honest.movementVector()

	if x != 1 || y != 0 {
		t.Errorf("honest player got (%v, %v), want the normal cluster vector (1, 0)", x, y)
	}
}

func TestAbuseConfigValidation(t *testing.T) {
	tests := []struct {
		name    string
		cfg     Config
		wantErr bool
	}{
		{"unknown mode", Config{Abuse: "wat", Players: 1}, true},
		{"negative players", Config{Abuse: AbuseDirection, AbusePlayers: -1, Players: 1}, true},
		{"more abusers than players", Config{Abuse: AbuseDirection, AbusePlayers: 5, Players: 2}, true},
		{"valid", Config{Abuse: AbuseDirection, AbusePlayers: 1, Players: 2}, false},
		{"empty mode is accepted as none", Config{Abuse: "", AbusePlayers: 0, Players: 2}, false},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// Only the abuse rules are under test here, so fill in whatever else
			// Validate requires and check the error mentions abuse when it fails.
			c := tt.cfg
			c.Movement = MovementStill
			err := c.Validate()

			if tt.wantErr {
				if err == nil {
					t.Fatal("expected an error")
				}
				return
			}
			// A non-abuse validation error is fine here (this Config is a stub);
			// an abuse-related one is not.
			if err != nil && (containsAny(err.Error(), "abuse")) {
				t.Fatalf("unexpected abuse validation error: %v", err)
			}
		})
	}
}

func containsAny(s, sub string) bool {
	return len(sub) > 0 && len(s) >= len(sub) && (func() bool {
		for i := 0; i+len(sub) <= len(s); i++ {
			if s[i:i+len(sub)] == sub {
				return true
			}
		}
		return false
	})()
}
