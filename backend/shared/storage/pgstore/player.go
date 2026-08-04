package pgstore

import (
	"context"
	"errors"
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// Compile-time check that the Postgres implementation satisfies the interface.
var _ storage.PlayerStore = (*PostgresPlayerStore)(nil)

// PostgresPlayerStore is a PostgreSQL-backed storage.PlayerStore.
//
// It writes to the player_states table of the game-state database. Saves are
// upserts keyed by user_id, so the async batch saver in gameserver can call
// SavePlayer unconditionally without a prior existence check.
type PostgresPlayerStore struct {
	pool  *pgxpool.Pool
	owned bool // true when the pool was created by this store (Close shuts it down)
}

// NewPlayerStore connects to dsn (e.g. postgres://game:localdev@localhost:5433/gamestate?sslmode=disable),
// pings the server and returns a store. The caller must Close it.
//
// It does NOT run migrations — call Migrate explicitly after construction so
// the caller controls whether a process is allowed to touch the schema.
func NewPlayerStore(ctx context.Context, dsn string) (*PostgresPlayerStore, error) {
	if dsn == "" {
		return nil, errors.New("pgstore: empty DSN")
	}
	cfg, err := pgxpool.ParseConfig(dsn)
	if err != nil {
		return nil, fmt.Errorf("pgstore parse dsn: %w", err)
	}
	pool, err := pgxpool.NewWithConfig(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("pgstore connect: %w", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("pgstore ping: %w", err)
	}
	return &PostgresPlayerStore{pool: pool, owned: true}, nil
}

// NewPlayerStoreWithPool wraps an existing pool (shared pool, tests).
// Close does not shut the pool down.
func NewPlayerStoreWithPool(pool *pgxpool.Pool) *PostgresPlayerStore {
	return &PostgresPlayerStore{pool: pool}
}

// Pool exposes the underlying connection pool for callers that need to run
// their own queries against the same database.
func (s *PostgresPlayerStore) Pool() *pgxpool.Pool { return s.pool }

// Ping verifies the connection is usable (health endpoints, readiness probes).
func (s *PostgresPlayerStore) Ping(ctx context.Context) error {
	if err := s.pool.Ping(ctx); err != nil {
		return fmt.Errorf("pgstore ping: %w", err)
	}
	return nil
}

// Close releases the pool when this store owns it.
func (s *PostgresPlayerStore) Close() {
	if s.owned {
		s.pool.Close()
	}
}

const upsertPlayerSQL = `
INSERT INTO player_states (user_id, map_id, x, y, hp, max_hp, updated_at)
VALUES ($1, $2, $3, $4, $5, $6, now())
ON CONFLICT (user_id) DO UPDATE SET
    map_id     = EXCLUDED.map_id,
    x          = EXCLUDED.x,
    y          = EXCLUDED.y,
    hp         = EXCLUDED.hp,
    max_hp     = EXCLUDED.max_hp,
    updated_at = now()`

// SavePlayer upserts the player row.
func (s *PostgresPlayerStore) SavePlayer(ctx context.Context, state *storage.PlayerState) error {
	if state == nil {
		return errors.New("pgstore: nil player state")
	}
	if state.UserID == "" {
		return errors.New("pgstore: empty user id")
	}
	_, err := s.pool.Exec(ctx, upsertPlayerSQL,
		state.UserID, state.MapID, state.X, state.Y, state.HP, state.MaxHP)
	if err != nil {
		return fmt.Errorf("pgstore save player %s: %w", state.UserID, err)
	}
	return nil
}

const loadPlayerSQL = `
SELECT user_id, map_id, x, y, hp, max_hp
FROM player_states
WHERE user_id = $1`

// LoadPlayer reads a player row. Returns storage.ErrNotFound when absent.
func (s *PostgresPlayerStore) LoadPlayer(ctx context.Context, userID string) (*storage.PlayerState, error) {
	var st storage.PlayerState
	err := s.pool.QueryRow(ctx, loadPlayerSQL, userID).
		Scan(&st.UserID, &st.MapID, &st.X, &st.Y, &st.HP, &st.MaxHP)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, fmt.Errorf("pgstore load player %s: %w", userID, storage.ErrNotFound)
	}
	if err != nil {
		return nil, fmt.Errorf("pgstore load player %s: %w", userID, err)
	}
	return &st, nil
}

// DeletePlayer removes a player row. Deleting a missing player is a no-op,
// matching MemoryPlayerStore semantics.
func (s *PostgresPlayerStore) DeletePlayer(ctx context.Context, userID string) error {
	if _, err := s.pool.Exec(ctx, `DELETE FROM player_states WHERE user_id = $1`, userID); err != nil {
		return fmt.Errorf("pgstore delete player %s: %w", userID, err)
	}
	return nil
}
