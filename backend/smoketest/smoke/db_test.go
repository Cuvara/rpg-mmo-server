package smoke

import (
	"strings"
	"testing"
	"time"
)

// Every assertion below exists to prove the corresponding check can FAIL. A DB
// smoke test that passes against an empty database is worse than none, so each
// pure validator is exercised with the exact "database is empty / wrong / stale"
// shapes it is meant to catch.

func TestCheckMigrations(t *testing.T) {
	base := time.Date(2026, 8, 5, 7, 34, 8, 0, time.UTC)
	ok := []MigrationRow{{Version: 1, Name: "001_init", Checksum: "sha256:abc", AppliedAt: base}}

	tests := []struct {
		name    string
		rows    []MigrationRow
		want    int
		wantErr string // substring; "" means the check must pass
	}{
		{
			name: "applied at expected version",
			rows: ok, want: 1,
		},
		{
			// The headline case: an untouched database must never look healthy.
			name: "empty schema_migrations fails",
			rows: nil, want: 1,
			wantErr: "schema_migrations is empty",
		},
		{
			name: "behind expected version",
			rows: ok, want: 2,
			wantErr: "schema is at version 1, want 2",
		},
		{
			name: "ahead of expected version",
			rows: []MigrationRow{
				{Version: 1, Name: "001_init", Checksum: "sha256:a", AppliedAt: base},
				{Version: 2, Name: "002_x", Checksum: "sha256:b", AppliedAt: base},
			},
			want:    1,
			wantErr: "schema is at version 2, want 1",
		},
		{
			name: "gap in the version sequence",
			rows: []MigrationRow{
				{Version: 1, Name: "001_init", Checksum: "sha256:a", AppliedAt: base},
				{Version: 3, Name: "003_x", Checksum: "sha256:c", AppliedAt: base},
			},
			want:    3,
			wantErr: "gap",
		},
		{
			name:    "hand-edited checksum",
			rows:    []MigrationRow{{Version: 1, Name: "001_init", Checksum: "", AppliedAt: base}},
			want:    1,
			wantErr: "not a sha256",
		},
		{
			name:    "missing applied_at",
			rows:    []MigrationRow{{Version: 1, Name: "001_init", Checksum: "sha256:a"}},
			want:    1,
			wantErr: "no applied_at",
		},
		{
			name:    "empty migration name",
			rows:    []MigrationRow{{Version: 1, Checksum: "sha256:a", AppliedAt: base}},
			want:    1,
			wantErr: "empty name",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			err := CheckMigrations(tc.rows, tc.want)
			assertErr(t, err, tc.wantErr)
		})
	}
}

func TestCheckPlayerRow(t *testing.T) {
	const (
		user = "1a2b3c4d-0000-0000-0000-000000000000"
		mapp = "map_01"
		maxX = 10
	)
	start := time.Date(2026, 8, 7, 2, 53, 0, 0, time.UTC)
	good := PlayerRow{
		UserID: user, MapID: mapp, X: 3.333333, Y: 0, HP: 100, MaxHP: 100,
		UpdatedAt: start.Add(35 * time.Second),
	}

	mutate := func(f func(*PlayerRow)) PlayerRow {
		r := good
		f(&r)
		return r
	}

	tests := []struct {
		name    string
		row     PlayerRow
		wantErr string
	}{
		{name: "row written by this run", row: good},
		{
			// The row exists but the player never moved: a server that accepts
			// input and persists nothing would otherwise look healthy.
			name:    "position still at spawn",
			row:     mutate(func(r *PlayerRow) { r.X = 0 }),
			wantErr: "want > 0",
		},
		{
			name:    "position moved a whole unit per input",
			row:     mutate(func(r *PlayerRow) { r.X = 133.33 }),
			wantErr: "must be a direction",
		},
		{
			// A row left behind by a previous run must not satisfy this run.
			name:    "stale row from an earlier run",
			row:     mutate(func(r *PlayerRow) { r.UpdatedAt = start.Add(-time.Hour) }),
			wantErr: "older than this smoke run",
		},
		{
			name:    "wrong map",
			row:     mutate(func(r *PlayerRow) { r.MapID = "map_99" }),
			wantErr: "map_id",
		},
		{
			name:    "wrong user",
			row:     mutate(func(r *PlayerRow) { r.UserID = "someone-else" }),
			wantErr: "row is for",
		},
		{
			name:    "drifted off the X axis",
			row:     mutate(func(r *PlayerRow) { r.Y = 2 }),
			wantErr: "only moved along +X",
		},
		{
			name:    "dead player persisted",
			row:     mutate(func(r *PlayerRow) { r.HP = 0 }),
			wantErr: "hp = 0",
		},
		{
			name:    "hp above max_hp",
			row:     mutate(func(r *PlayerRow) { r.MaxHP = 50 }),
			wantErr: "max_hp",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			assertErr(t, CheckPlayerRow(tc.row, user, mapp, maxX, start), tc.wantErr)
		})
	}
}

func TestCheckReloadedPosition(t *testing.T) {
	const tol = 0.01
	tests := []struct {
		name                     string
		gotX, gotY, wantX, wantY float32
		wantErr                  string
	}{
		{name: "restored exactly", gotX: 3.333333, wantX: 3.333333},
		{name: "restored within float4 tolerance", gotX: 3.3333, wantX: 3.333333},
		{
			// The regression this check exists for: saves work, loads do not.
			name: "respawned at the origin", gotX: 0, gotY: 0, wantX: 3.333333,
			wantErr: "spawned at the origin",
		},
		{
			name: "restored to the wrong position", gotX: 9.5, wantX: 3.333333,
			wantErr: "does not match the persisted one",
		},
		{
			name: "y not restored", gotX: 3.333333, gotY: 0, wantX: 3.333333, wantY: 4,
			wantErr: "does not match the persisted one",
		},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			assertErr(t, CheckReloadedPosition(tc.gotX, tc.gotY, tc.wantX, tc.wantY, tol), tc.wantErr)
		})
	}
}

func TestMaskDSN(t *testing.T) {
	tests := []struct{ in, want string }{
		{"", "(unset)"},
		{"postgres://game:localdev@localhost:5433/gamestate?sslmode=disable",
			"postgres://game:***@localhost:5433/gamestate?sslmode=disable"},
		{"postgres://game@localhost/gamestate", "postgres://game@localhost/gamestate"},
		{"host=localhost dbname=gamestate", "host=localhost dbname=gamestate"},
		// A password containing '@' must not fool the split.
		{"postgres://game:p@ss@localhost/gamestate", "postgres://game:***@localhost/gamestate"},
	}
	for _, tc := range tests {
		if got := MaskDSN(tc.in); got != tc.want {
			t.Errorf("MaskDSN(%q) = %q, want %q", tc.in, got, tc.want)
		}
		if tc.in != "" && strings.Contains(MaskDSN(tc.in), "localdev") {
			t.Errorf("MaskDSN(%q) leaked the password", tc.in)
		}
	}
}

func assertErr(t *testing.T, err error, wantErr string) {
	t.Helper()
	switch {
	case wantErr == "" && err != nil:
		t.Fatalf("want pass, got error: %v", err)
	case wantErr != "" && err == nil:
		t.Fatalf("want error containing %q, got nil — this check CANNOT FAIL", wantErr)
	case wantErr != "" && !strings.Contains(err.Error(), wantErr):
		t.Fatalf("error %q does not contain %q", err, wantErr)
	}
}

// --require-db is a promise that the persistence checks ran. These cases pin
// the configurations that cannot keep that promise.
func TestLoadConfigDBFlags(t *testing.T) {
	env := func(m map[string]string) func(string) string {
		return func(k string) string { return m[k] }
	}
	base := map[string]string{"JWT_SECRET": "s"}

	t.Run("game-state checks default to unconfigured", func(t *testing.T) {
		cfg, err := LoadConfig(env(base), nil)
		if err != nil {
			t.Fatalf("LoadConfig: %v", err)
		}
		if cfg.GameDBURL != "" || cfg.RequireDB || cfg.SkipDB {
			t.Fatalf("unexpected defaults: %+v", cfg)
		}
		if cfg.ExpectMigration != DefaultExpectMigration {
			t.Fatalf("ExpectMigration = %d, want %d", cfg.ExpectMigration, DefaultExpectMigration)
		}
	})

	t.Run("GAME_DB_URL is picked up from the environment", func(t *testing.T) {
		m := map[string]string{"JWT_SECRET": "s", "GAME_DB_URL": "postgres://u:p@h/db"}
		cfg, err := LoadConfig(env(m), nil)
		if err != nil {
			t.Fatalf("LoadConfig: %v", err)
		}
		if cfg.GameDBURL != "postgres://u:p@h/db" {
			t.Fatalf("GameDBURL = %q", cfg.GameDBURL)
		}
	})

	t.Run("require-db without a DSN is rejected", func(t *testing.T) {
		_, err := LoadConfig(env(base), []string{"--require-db"})
		assertErr(t, err, "needs GAME_DB_URL")
	})

	t.Run("require-db with skip-db is rejected", func(t *testing.T) {
		m := map[string]string{"JWT_SECRET": "s", "GAME_DB_URL": "postgres://u:p@h/db"}
		_, err := LoadConfig(env(m), []string{"--require-db", "--skip-db"})
		assertErr(t, err, "mutually exclusive")
	})

	t.Run("bad expect-migration-version is rejected", func(t *testing.T) {
		_, err := LoadConfig(env(base), []string{"--expect-migration-version=0"})
		assertErr(t, err, "expect-migration-version must be > 0")
	})
}

// A SKIP must never be rendered the same way as a PASS, or an unconfigured CD
// run would read as a verified one.
func TestFormatStepSkip(t *testing.T) {
	line := FormatStep(StepResult{Name: "gamestate_player_row", Skipped: true, Detail: "GAME_DB_URL is unset"})
	if !strings.HasPrefix(line, "SKIP") {
		t.Fatalf("skipped step rendered as %q, want a SKIP prefix", line)
	}
	if !strings.Contains(line, "GAME_DB_URL is unset") {
		t.Fatalf("skip line %q does not carry the reason", line)
	}
	pass := FormatStep(StepResult{Name: "gamestate_player_row"})
	if strings.HasPrefix(pass, "SKIP") {
		t.Fatalf("passing step rendered as SKIP: %q", pass)
	}
}
