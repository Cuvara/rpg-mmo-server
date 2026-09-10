package smoke

import "testing"

// TestDeployDerivedEnvPairs pins the contract that CD's deploy/.env generator and
// the k8s verify target both rely on: GAMESERVER_SEALED is one input, and the two
// SMOKE_ variables derived from it must be a pair the binary accepts.
//
// WHY THIS LIVES HERE. The derivation itself is shell embedded in
// .github/workflows/cd.yml and in checks_flow.sh -- neither is executed by any
// test, so a wrong pair is discovered by a red deploy. This test cannot check
// that the workflow computes the pair; it checks the half that can be checked,
// which is that each pair the workflow can produce is one this binary accepts or
// rejects as intended.
//
// The failure it guards against has happened once already, in the other
// direction: deriving SMOKE_SEALED alone, without SMOKE_ENCODING=proto, produces
// a client that asks to seal over JSON. That is refused at startup now, but the
// value of pinning it is that the refusal is what CD depends on.
func TestDeployDerivedEnvPairs(t *testing.T) {
	tests := []struct {
		name             string
		gameserverSealed string // the single input an environment sets
		smokeSealed      string // what the generator writes
		smokeEncoding    string
		wantErr          bool
		wantSealed       bool
	}{
		{
			name:             "unsealed environment",
			gameserverSealed: "off",
			smokeSealed:      "0",
			smokeEncoding:    "json",
			wantSealed:       false,
		},
		{
			name:             "sealed environment",
			gameserverSealed: "require",
			smokeSealed:      "1",
			smokeEncoding:    "proto",
			wantSealed:       true,
		},
		{
			// The half-derived pair: what the generator would produce if someone
			// added SMOKE_SEALED and not SMOKE_ENCODING. It must fail at startup
			// rather than at the join, because a startup error names the cause and
			// a join failure looks like a broken stack.
			name:             "half-derived pair is refused",
			gameserverSealed: "require",
			smokeSealed:      "1",
			smokeEncoding:    "json",
			wantErr:          true,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			env := map[string]string{
				"JWT_SECRET":     "s",
				"SMOKE_SEALED":   tt.smokeSealed,
				"SMOKE_ENCODING": tt.smokeEncoding,
			}
			cfg, err := LoadConfig(fakeEnv(env), nil)
			if (err != nil) != tt.wantErr {
				t.Fatalf("LoadConfig() error = %v, wantErr %v (GAMESERVER_SEALED=%s)",
					err, tt.wantErr, tt.gameserverSealed)
			}
			if tt.wantErr {
				return
			}
			if cfg.Sealed != tt.wantSealed {
				t.Errorf("Sealed = %v, want %v", cfg.Sealed, tt.wantSealed)
			}
		})
	}
}
