package economy

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

type createCall struct {
	id            string
	authoritative bool
	sortOrder     string
	operator      string
}

// mockLbAdmin implements leaderboardAdmin and records the call sequence.
type mockLbAdmin struct {
	existing []*api.Leaderboard
	getErr   error
	creates  []createCall
	deletes  []string
	calls    []string // ordered call log
}

func (m *mockLbAdmin) LeaderboardsGetId(_ context.Context, _ []string) ([]*api.Leaderboard, error) {
	m.calls = append(m.calls, "get")
	return m.existing, m.getErr
}

func (m *mockLbAdmin) LeaderboardCreate(_ context.Context, id string, authoritative bool, sortOrder, operator,
	_ string, _ map[string]interface{}, _ bool) error {
	m.calls = append(m.calls, "create")
	m.creates = append(m.creates, createCall{id, authoritative, sortOrder, operator})
	return nil
}

func (m *mockLbAdmin) LeaderboardDelete(_ context.Context, id string) error {
	m.calls = append(m.calls, "delete")
	m.deletes = append(m.deletes, id)
	return nil
}

func board(auth bool) *api.Leaderboard {
	return &api.Leaderboard{Id: LeaderboardKillsAllTime, Authoritative: auth}
}

func TestSetupLeaderboards(t *testing.T) {
	cases := []struct {
		name        string
		existing    []*api.Leaderboard
		migrate     string
		wantErr     string
		wantCalls   []string
		wantCreated bool
	}{
		{
			name:        "fresh install creates authoritative board",
			wantCalls:   []string{"get", "create"},
			wantCreated: true,
		},
		{
			name:      "existing authoritative board is left alone",
			existing:  []*api.Leaderboard{board(true)},
			wantCalls: []string{"get"},
		},
		{
			name:      "existing non-authoritative board fails init by default",
			existing:  []*api.Leaderboard{board(false)},
			wantErr:   "authoritative=false",
			wantCalls: []string{"get"},
		},
		{
			name:        "existing non-authoritative board is recreated on opt-in",
			existing:    []*api.Leaderboard{board(false)},
			migrate:     "recreate",
			wantCalls:   []string{"get", "delete", "create"},
			wantCreated: true,
		},
		{
			name:      "unknown migrate value does not delete",
			existing:  []*api.Leaderboard{board(false)},
			migrate:   "yes",
			wantErr:   "authoritative=false",
			wantCalls: []string{"get"},
		},
		{
			name:        "other boards in the answer are ignored",
			existing:    []*api.Leaderboard{{Id: "something_else", Authoritative: false}},
			wantCalls:   []string{"get", "create"},
			wantCreated: true,
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			m := &mockLbAdmin{existing: c.existing}
			err := setupLeaderboardsCore(context.Background(), noopLogger{}, m, c.migrate)
			if c.wantErr == "" && err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if c.wantErr != "" && (err == nil || !strings.Contains(err.Error(), c.wantErr)) {
				t.Fatalf("err = %v, want containing %q", err, c.wantErr)
			}
			if strings.Join(m.calls, ",") != strings.Join(c.wantCalls, ",") {
				t.Fatalf("calls = %v, want %v", m.calls, c.wantCalls)
			}
			if c.wantCreated {
				if len(m.creates) != 1 {
					t.Fatalf("creates = %v, want exactly one", m.creates)
				}
				got := m.creates[0]
				want := createCall{LeaderboardKillsAllTime, true, "desc", "incr"}
				if got != want {
					t.Fatalf("create = %+v, want %+v", got, want)
				}
			}
		})
	}
}

func TestSetupLeaderboards_GetFailureIsAnError(t *testing.T) {
	m := &mockLbAdmin{getErr: errors.New("db down")}
	if err := setupLeaderboardsCore(context.Background(), noopLogger{}, m, ""); err == nil {
		t.Fatal("want error")
	}
	if len(m.creates) != 0 || len(m.deletes) != 0 {
		t.Fatalf("must not create/delete when the lookup fails: %v %v", m.creates, m.deletes)
	}
}

func TestMigrateMode_RuntimeEnvWinsOverProcessEnv(t *testing.T) {
	t.Setenv(LeaderboardMigrateEnv, "from-process")
	if got := migrateMode(context.Background()); got != "from-process" {
		t.Fatalf("process env: got %q", got)
	}
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_ENV, map[string]string{LeaderboardMigrateEnv: "recreate"})
	if got := migrateMode(ctx); got != "recreate" {
		t.Fatalf("runtime env: got %q", got)
	}
}
