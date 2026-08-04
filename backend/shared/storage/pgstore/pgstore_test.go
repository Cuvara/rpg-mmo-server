package pgstore

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

const pgImage = "postgres:16.4-alpine"

// dockerCLI returns the docker binary usable on this host, or "" when none
// works. Plain WSL has only the Windows interop binary (docker.exe); CI
// (ubuntu-latest) has the native one.
func dockerCLI() string {
	for _, bin := range []string{"docker", "docker.exe"} {
		path, err := exec.LookPath(bin)
		if err != nil {
			continue
		}
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		err = exec.CommandContext(ctx, path, "info", "--format", "{{.ServerVersion}}").Run()
		cancel()
		if err == nil {
			return path
		}
	}
	return ""
}

// startPostgres launches a throwaway postgres container on a random host port
// and returns its DSN. The container is removed via t.Cleanup. The test is
// skipped when no working docker CLI is available.
func startPostgres(t *testing.T) string {
	t.Helper()

	docker := dockerCLI()
	if docker == "" {
		t.Skip("no working docker CLI (docker/docker.exe) — skipping real-postgres tests")
	}

	run := func(args ...string) (string, error) {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		out, err := exec.CommandContext(ctx, docker, args...).CombinedOutput()
		return strings.TrimSpace(string(out)), err
	}

	out, err := run("run", "--rm", "-d",
		"-e", "POSTGRES_DB=gamestate",
		"-e", "POSTGRES_USER=game",
		"-e", "POSTGRES_PASSWORD=localdev",
		"-p", "0:5432", // random free host port
		pgImage,
	)
	if err != nil {
		t.Skipf("docker run %s failed, skipping: %v: %s", pgImage, err, out)
	}
	lines := strings.Split(out, "\n")
	containerID := strings.TrimSpace(lines[len(lines)-1])
	t.Cleanup(func() {
		if _, err := run("rm", "-f", containerID); err != nil {
			t.Logf("docker rm -f %s: %v", containerID, err)
		}
	})

	// Resolve the mapped host port: "0.0.0.0:49154" (possibly several lines).
	var hostPort string
	deadline := time.Now().Add(30 * time.Second)
	for time.Now().Before(deadline) {
		portOut, err := run("port", containerID, "5432/tcp")
		if err == nil && portOut != "" {
			for _, line := range strings.Split(portOut, "\n") {
				line = strings.TrimSpace(line)
				if idx := strings.LastIndex(line, ":"); idx >= 0 && !strings.HasPrefix(line, "[") {
					hostPort = line[idx+1:]
					break
				}
			}
		}
		if hostPort != "" {
			break
		}
		time.Sleep(500 * time.Millisecond)
	}
	if hostPort == "" {
		t.Fatalf("could not resolve host port for container %s", containerID)
	}

	// Wait for the server to accept connections.
	ready := false
	deadline = time.Now().Add(90 * time.Second)
	for time.Now().Before(deadline) {
		if _, err := run("exec", containerID, "pg_isready", "-U", "game", "-d", "gamestate"); err == nil {
			ready = true
			break
		}
		time.Sleep(time.Second)
	}
	if !ready {
		logs, _ := run("logs", containerID)
		t.Fatalf("postgres container never became ready; logs:\n%s", logs)
	}

	return fmt.Sprintf("postgres://game:localdev@127.0.0.1:%s/gamestate?sslmode=disable", hostPort)
}

// newTestStore returns a migrated store backed by a fresh container.
func newTestStore(t *testing.T) (*PostgresPlayerStore, context.Context) {
	t.Helper()
	dsn := startPostgres(t)
	ctx := context.Background()

	store, err := NewPlayerStore(ctx, dsn)
	if err != nil {
		t.Fatalf("NewPlayerStore() error: %v", err)
	}
	t.Cleanup(store.Close)

	if err := store.Migrate(ctx); err != nil {
		t.Fatalf("Migrate() error: %v", err)
	}
	return store, ctx
}

func TestPostgresPlayerStore_SaveLoadRoundtrip(t *testing.T) {
	store, ctx := newTestStore(t)

	tests := []struct {
		name  string
		state storage.PlayerState
	}{
		{"basic", storage.PlayerState{UserID: "user1", X: 10.5, Y: -3.25, HP: 80, MaxHP: 100, MapID: "map_01"}},
		{"zero values", storage.PlayerState{UserID: "user2"}},
		{"dungeon map", storage.PlayerState{UserID: "user3", X: 0.125, Y: 999.5, HP: 1, MaxHP: 1, MapID: "dungeon_ruins_01"}},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if err := store.SavePlayer(ctx, &tt.state); err != nil {
				t.Fatalf("SavePlayer() error: %v", err)
			}
			got, err := store.LoadPlayer(ctx, tt.state.UserID)
			if err != nil {
				t.Fatalf("LoadPlayer() error: %v", err)
			}
			if *got != tt.state {
				t.Errorf("LoadPlayer() = %+v, want %+v", *got, tt.state)
			}
		})
	}
}

func TestPostgresPlayerStore_SaveOverwrites(t *testing.T) {
	store, ctx := newTestStore(t)

	first := &storage.PlayerState{UserID: "hero", X: 1, Y: 2, HP: 100, MaxHP: 100, MapID: "map_01"}
	if err := store.SavePlayer(ctx, first); err != nil {
		t.Fatalf("SavePlayer() error: %v", err)
	}
	second := &storage.PlayerState{UserID: "hero", X: 42, Y: -7.5, HP: 33, MaxHP: 120, MapID: "map_02"}
	if err := store.SavePlayer(ctx, second); err != nil {
		t.Fatalf("SavePlayer() overwrite error: %v", err)
	}

	got, err := store.LoadPlayer(ctx, "hero")
	if err != nil {
		t.Fatalf("LoadPlayer() error: %v", err)
	}
	if *got != *second {
		t.Errorf("LoadPlayer() = %+v, want %+v", *got, *second)
	}

	// Upsert must not create a second row.
	var count int
	if err := store.Pool().QueryRow(ctx, `SELECT count(*) FROM player_states WHERE user_id = $1`, "hero").Scan(&count); err != nil {
		t.Fatalf("count query error: %v", err)
	}
	if count != 1 {
		t.Errorf("row count = %d, want 1", count)
	}
}

func TestPostgresPlayerStore_LoadMissing(t *testing.T) {
	store, ctx := newTestStore(t)

	if _, err := store.LoadPlayer(ctx, "nobody"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("LoadPlayer() error = %v, want storage.ErrNotFound", err)
	}
}

func TestPostgresPlayerStore_Delete(t *testing.T) {
	store, ctx := newTestStore(t)

	state := &storage.PlayerState{UserID: "gone", HP: 5, MaxHP: 5, MapID: "map_01"}
	if err := store.SavePlayer(ctx, state); err != nil {
		t.Fatalf("SavePlayer() error: %v", err)
	}
	if err := store.DeletePlayer(ctx, "gone"); err != nil {
		t.Fatalf("DeletePlayer() error: %v", err)
	}
	if _, err := store.LoadPlayer(ctx, "gone"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("LoadPlayer() after delete error = %v, want storage.ErrNotFound", err)
	}
	// Deleting a missing row is a no-op.
	if err := store.DeletePlayer(ctx, "gone"); err != nil {
		t.Errorf("DeletePlayer() on missing row error: %v", err)
	}
}

func TestPostgresPlayerStore_MigrateIdempotent(t *testing.T) {
	store, ctx := newTestStore(t) // already migrated once

	if err := store.Migrate(ctx); err != nil {
		t.Fatalf("second Migrate() error: %v", err)
	}
	if err := store.Migrate(ctx); err != nil {
		t.Fatalf("third Migrate() error: %v", err)
	}

	// Existing data must survive a re-run.
	state := &storage.PlayerState{UserID: "survivor", X: 3, Y: 4, HP: 7, MaxHP: 9, MapID: "map_01"}
	if err := store.SavePlayer(ctx, state); err != nil {
		t.Fatalf("SavePlayer() error: %v", err)
	}
	if err := store.Migrate(ctx); err != nil {
		t.Fatalf("Migrate() after insert error: %v", err)
	}
	got, err := store.LoadPlayer(ctx, "survivor")
	if err != nil {
		t.Fatalf("LoadPlayer() error: %v", err)
	}
	if *got != *state {
		t.Errorf("LoadPlayer() = %+v, want %+v", *got, *state)
	}
}

func TestPostgresPlayerStore_BadDSN(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if _, err := NewPlayerStore(ctx, ""); err == nil {
		t.Error("NewPlayerStore(\"\") error = nil, want error")
	}
	if _, err := NewPlayerStore(ctx, "://not-a-dsn"); err == nil {
		t.Error("NewPlayerStore(bad dsn) error = nil, want error")
	}
}

// The embedded schema and the compose init script must not drift apart.
func TestSchemaMatchesDeployInitScript(t *testing.T) {
	const deployPath = "../../../deploy/db/init-gamestate.sql"

	onDisk, err := os.ReadFile(deployPath)
	if err != nil {
		t.Fatalf("read %s: %v", deployPath, err)
	}
	if strings.TrimSpace(string(onDisk)) != strings.TrimSpace(SchemaSQL()) {
		t.Errorf("%s differs from the embedded schema.sql — keep them byte-identical", deployPath)
	}
}
